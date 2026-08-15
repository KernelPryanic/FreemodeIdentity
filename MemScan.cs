using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FreemodeIdentity {
	// Crash-proof process-memory primitives. Union of the two source mods' helpers:
	// the appearance side needs the region sweep / snapshot reads;
	// the spoof + shim side needs the gated u32/i32 reads and the VirtualProtect-flipping
	// write. Kept as one class so there is a single memory-safety gate for the whole mod.
	//
	// CRITICAL: this code dereferences pointers pulled out of game memory, many of which
	// are garbage. An access violation on an unmapped address CANNOT be caught by a C#
	// try/catch — it kills the whole game process. So every read first asks the OS, via
	// VirtualQuery, whether the address range is committed and readable; an unreadable
	// address becomes a skip/zero, never a fault. Treat that VirtualQuery gate as
	// mandatory: never Marshal.Read* a game pointer without it.
	static class MemScan {
		[StructLayout(LayoutKind.Sequential)]
		struct MEMORY_BASIC_INFORMATION {
			public IntPtr BaseAddress;
			public IntPtr AllocationBase;
			public uint AllocationProtect;
			public IntPtr RegionSize;
			public uint State;
			public uint Protect;
			public uint Type;
		}

		[DllImport("kernel32.dll")]
		static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

		[DllImport("kernel32.dll")]
		static extern bool VirtualProtect(IntPtr lpAddress, IntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

		const uint MEM_COMMIT = 0x1000;
		// Any protection flag that permits reading. PAGE_NOACCESS(0x01)/PAGE_GUARD(0x100)
		// must be excluded — touching a guard page also faults.
		const uint PAGE_READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80; // R, RW, WC, EX-R, EX-RW, EX-WC
		const uint PAGE_WRITABLE = 0x04 | 0x08 | 0x40 | 0x80;              // RW, WC, EX-RW, EX-WC
		const uint PAGE_READWRITE = 0x04;
		const uint PAGE_GUARD = 0x100;

		static readonly IntPtr MbiSize = (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));

		// True only if [addr, addr+size) is entirely committed and readable per the OS.
		public static bool IsReadable(IntPtr addr, int size) {
			long start = addr.ToInt64();
			if (start <= 0x10000 || start >= 0x7FFFFFFFFFFF) {
				return false;
			}
			long end = start + size;
			long cur = start;
			while (cur < end) {
				MEMORY_BASIC_INFORMATION mbi;
				if (VirtualQuery((IntPtr)cur, out mbi, MbiSize) == IntPtr.Zero) {
					return false;
				}
				if (mbi.State != MEM_COMMIT) {
					return false;
				}
				if ((mbi.Protect & PAGE_GUARD) != 0 || (mbi.Protect & PAGE_READABLE) == 0) {
					return false;
				}
				long regionEnd = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
				if (regionEnd <= cur) {
					return false; // no forward progress; bail rather than spin
				}
				cur = regionEnd;
			}
			return true;
		}

		// A pointer worth dereferencing: canonical user range AND actually readable.
		public static bool LooksLikeHeapPtr(IntPtr p) {
			return IsReadable(p, 8);
		}

		public static IntPtr SafeReadPtr(IntPtr addr) {
			return IsReadable(addr, 8) ? Marshal.ReadIntPtr(addr) : IntPtr.Zero;
		}

		public static ushort ReadUInt16(IntPtr addr) {
			return IsReadable(addr, 2) ? unchecked((ushort)Marshal.ReadInt16(addr)) : (ushort)0;
		}

		public static uint ReadUInt32(IntPtr addr) {
			return IsReadable(addr, 4) ? unchecked((uint)Marshal.ReadInt32(addr)) : 0u;
		}

		public static int ReadInt32(IntPtr addr) {
			return IsReadable(addr, 4) ? Marshal.ReadInt32(addr) : 0;
		}

		// Ungated raw reads — NO VirtualQuery. ONLY for an address a caller already proved committed
		// this session and that can't have moved since (e.g. the spoof's held slot, re-validated
		// whenever the ped handle changes). The gated versions cost a syscall each; on a per-frame
		// re-assert of a known-good address that syscall was the dominant cost. Never call these on a
		// pointer freshly pulled out of game memory — an unmapped read here is an uncatchable access
		// violation that kills the process.
		public static uint ReadUInt32Raw(IntPtr addr) => unchecked((uint)Marshal.ReadInt32(addr));
		public static int ReadInt32Raw(IntPtr addr) => Marshal.ReadInt32(addr);

		// Write a u32, temporarily flipping the page to PAGE_READWRITE if needed and
		// restoring the original protection after. VirtualQuery-gated like the reads.
		// Returns false (writing nothing) if the page is unreadable or the flip fails.
		public static bool WriteUInt32(IntPtr addr, uint value) {
			if (!IsReadable(addr, 4)) {
				return false;
			}
			uint oldProtect;
			bool flipped = false;
			if (!IsWritable(addr, 4)) {
				if (!VirtualProtect(addr, (IntPtr)4, PAGE_READWRITE, out oldProtect)) {
					return false;
				}
				flipped = true;
			} else {
				oldProtect = 0;
			}
			Marshal.WriteInt32(addr, unchecked((int)value));
			if (flipped) {
				uint ignore;
				VirtualProtect(addr, (IntPtr)4, oldProtect, out ignore);
			}
			return true;
		}

		static bool IsWritable(IntPtr addr, int size) {
			long start = addr.ToInt64();
			long end = start + size;
			long cur = start;
			while (cur < end) {
				MEMORY_BASIC_INFORMATION mbi;
				if (VirtualQuery((IntPtr)cur, out mbi, MbiSize) == IntPtr.Zero) {
					return false;
				}
				if (mbi.State != MEM_COMMIT) {
					return false;
				}
				if ((mbi.Protect & PAGE_GUARD) != 0 || (mbi.Protect & PAGE_WRITABLE) == 0) {
					return false;
				}
				long regionEnd = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
				if (regionEnd <= cur) {
					return false;
				}
				cur = regionEnd;
			}
			return true;
		}

		// Copy as much of [addr, addr+len) as is readable into a managed array, stopping at
		// the first unreadable page so a partially-mapped block still yields its head.
		//
		// Copies ONE page at a time, re-checking IsReadable immediately before each page's
		// copy. The game runs concurrently and can unmap a page between the check and the
		// copy; doing it per-page keeps that check→copy window as small as possible (a
		// whole-span check-then-copy leaves a wide TOCTOU window where a freed page faults
		// the Marshal.Copy with an UNCATCHABLE access violation that kills the process).
		public static byte[] Snapshot(IntPtr addr, int len) {
			const int Page = 0x1000;
			var buf = new byte[len];
			int done = 0;
			while (done < len) {
				int chunk = Math.Min(Page, len - done);
				if (!IsReadable(addr + done, chunk)) {
					break;
				}
				Marshal.Copy(addr + done, buf, done, chunk);
				done += chunk;
			}
			if (done == len) {
				return buf;
			}
			// Trim to what we actually read so callers see a short buffer, not zero padding.
			var trimmed = new byte[done];
			Array.Copy(buf, trimmed, done);
			return trimmed;
		}

		// Gate granularity for SnapshotInto. Snapshot re-checks every 4KB page, which is right when
		// following a pointer of unknown provenance but is the dominant cost when sweeping hundreds
		// of megabytes: one VirtualQuery per page is ~150k syscalls per 600MB against a process with
		// a huge, churning address space, and it held the head-blend scan to ~5MB/s. 64KB keeps a
		// gate on every read while cutting those syscalls 16x.
		//
		// The trade is a wider TOCTOU window: 64KB rather than 4KB of "checked, not yet copied". The
		// callers are region sweeps over the game's own committed heap, which is not being unmapped
		// under us in normal play; a pointer chase must still use Snapshot.
		const int SweepGateBytes = 0x10000;

		// Snapshot into a caller-owned buffer, returning how many bytes were read. Exists so a scan
		// sweeping hundreds of megabytes can reuse ONE buffer — allocating a fresh 1MB array per
		// region puts every one of them on the large object heap, and over a few thousand small
		// regions that allocation cost dominated the scan itself.
		public static int SnapshotInto(IntPtr addr, byte[] dest, int len) {
			len = Math.Min(len, dest.Length);
			int done = 0;
			while (done < len) {
				int chunk = Math.Min(SweepGateBytes, len - done);
				if (!IsReadable(addr + done, chunk)) {
					break;
				}
				Marshal.Copy(addr + done, dest, done, chunk);
				done += chunk;
			}
			return done;
		}

		// One committed, readable memory region: its base and size. Used by the content scans that
		// sweep process memory — the decoration-array probe and the head-blend finder.
		public struct Region {
			public IntPtr Base;
			public long Size;
		}

		// Enumerate committed, readable memory regions across the user address space, each no
		// larger than maxRegionSize (huge regions are chunked so a caller can time-budget its
		// scan). Skips guard/no-access pages. Heap data (where the decoration array lives) is
		// in PAGE_READWRITE regions; pass writableOnly to skip read-only/image/code regions and
		// keep the sweep small.
		//
		// startAddr/endAddr bound the WALK, not just the results. A caller searching a window
		// around one address (the head-blend finder) would otherwise pay a full 128TB VirtualQuery
		// crawl — thousands of calls, and a Region list an order of magnitude longer than the
		// window — to then discard nearly all of it. endAddr 0 means "to the top".
		public static IEnumerable<Region> EnumerateRegions(long maxRegionSize = 0x100000, bool writableOnly = true, bool privateOnly = false, long startAddr = 0, long endAddr = 0) {
			const uint PAGE_WRITABLE_REGION = 0x04 | 0x08 | 0x40 | 0x80; // RW, WC, EX-RW, EX-WC
			const uint MEM_PRIVATE = 0x20000; // not a mapped file/image — the heap, where the decoration array lives
			long cur = Math.Max(0x10000, startAddr);
			long userMax = endAddr > 0 ? Math.Min(endAddr, 0x7FFFFFFFFFFF) : 0x7FFFFFFFFFFF;
			while (cur < userMax) {
				MEMORY_BASIC_INFORMATION mbi;
				if (VirtualQuery((IntPtr)cur, out mbi, MbiSize) == IntPtr.Zero) {
					break;
				}
				long regionSize = mbi.RegionSize.ToInt64();
				if (regionSize <= 0) {
					break;
				}
				bool committed = mbi.State == MEM_COMMIT;
				bool guarded = (mbi.Protect & PAGE_GUARD) != 0;
				bool readable = (mbi.Protect & PAGE_READABLE) != 0;
				bool writable = (mbi.Protect & PAGE_WRITABLE_REGION) != 0;
				bool isPrivate = mbi.Type == MEM_PRIVATE;
				if (committed && readable && !guarded && (!writableOnly || writable) && (!privateOnly || isPrivate)) {
					long off = 0;
					while (off < regionSize) {
						long chunkBase = mbi.BaseAddress.ToInt64() + off;
						if (chunkBase >= userMax) {
							break;
						}
						long chunk = Math.Min(maxRegionSize, regionSize - off);
						yield return new Region { Base = (IntPtr)chunkBase, Size = chunk };
						off += chunk;
					}
				}
				cur = mbi.BaseAddress.ToInt64() + regionSize;
			}
		}

	}
}
