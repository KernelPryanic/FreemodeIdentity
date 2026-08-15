using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace FreemodeIdentity {
	// Reads CPedHeadBlendData out of the live ped's memory to recover head data the game
	// exposes no getter native for: head-overlay opacity, the 20 face micro-morphs, the
	// overlay tint colour ids and the hair tint ids. The native capture path (PedAppearance)
	// handles everything the game DOES expose; this fills the rest.
	//
	// HOW the struct is found:
	// The Enhanced exe is packed/encrypted on disk, so the FiveM/Legacy byte patterns
	// (fwExtensionList::Get + the extension-id global) cannot be derived statically and do
	// not exist in the decrypted runtime layout either — Game.FindPattern fails for them.
	// Nor can the struct be reached from the ped: full-memory sweeps on three characters found
	// NOTHING in CPed pointing at it, so it is not an extension a pointer walk can follow.
	// So we locate it BY CONTENT: GET_PED_HEAD_BLEND_DATA already returns the ped's three
	// heritage MIX floats (shape/skin/third), whose exact bit patterns, together with the
	// ped's overlay indices and eye colour, are a fingerprint precise enough that it passed
	// exactly once in 8GB on each of those three characters. We scan memory around the ped
	// for it; all other fields are read at offsets relative to that mix-start, derived
	// empirically from live samples + the native getters as ground truth.
	//
	// SAFETY: every memory access goes through MemScan, which VirtualQuery-gates each read
	// (an unmapped pointer would otherwise raise an uncatchable access violation that kills
	// the game). If the struct can't be found, or a read looks out of range, the whole path
	// disables itself / returns false and callers keep their native-captured defaults.
	static class PedHeadBlendMemory {
		// Field offsets RELATIVE TO THE MIX-START (the ShapeMix float = the first of the
		// three consecutive mix floats we locate). Enhanced 1013.34. All locked against
		// ground truth (native getters + a Menyoo outfit XML carrying the authored tint
		// values, used only to interpret the probe dump). The overlay highlight (secondary
		// tint) array sits directly after the primary array and is read the same way; in
		// every sample it was 0, the universal default, so a wrong stride there can only
		// drop a highlight, never miscolour a face.
		const int OffShapeMix = 0;          // f32 (the located anchor)
		const int OffOverlayAlpha = 0x10;   // f32[13] overlay opacities
		const int OffFaceFeature = 0x78;    // f32[20] micro-morphs
		const int OffOverlayColorId = 0xC8; // u8[13] overlay tint primary palette id
		const int OffOverlayHighlightId = 0xD5; // u8[13] overlay tint secondary palette id
		const int OffOverlayValue = 0xF5;   // u8[13] overlay drawable index (255 = none)
		const int OffEyeColour = 0x10E;     // u16 eye colour palette index
		const int OffHairColour = 0x110;    // u8 hair tint primary palette id
		const int OffHairHighlight = 0x111; // u8 hair tint secondary palette id

		// Highest eye-colour palette index a real ped can hold. SET_PED_EYE_COLOR takes 0-31; the
		// bound is doubled so a future palette can grow without rejecting a genuine struct, and it
		// is still nowhere near the 0xFFFF a fill region reads as. Used as a find-time sanity gate,
		// never to clamp a captured value.
		const int MaxEyeColourIndex = 63;

		// How far past the mix-start the struct extends (hair highlight at 0x111 + margin);
		// the snapshot we read must cover it. A small fixed window — the struct is contiguous.
		const int StructSpan = 0x120;

		// ---- How the struct is located ---------------------------------------------------
		// By scanning memory NEAR THE PED for the fingerprint — not by walking the ped's pointer
		// graph, which is what earlier builds did and what produced every wrong capture in this
		// subsystem's history.
		//
		// Measured, three characters, full 8GB sweeps: the fingerprint passed exactly ONCE each
		// time, on the right struct (hair tints matched the saved slots), and NOTHING in the ped
		// pointed at it — CPedHeadBlendData lives in its own pool that CPed doesn't reference in
		// any way a pointer walk can follow. So the walk could only ever find the struct by luck,
		// and on a ped whose fields are all defaults it instead found zero-filled regions and saved
		// those: EyeColor=65535, a black hair tint, a face that wouldn't round-trip.
		//
		// The pool sits close to the ped in absolute terms (~299MB in the samples, all three within
		// 680KB of each other), so a bounded window around the ped finds it in a fraction of a
		// second while a full sweep takes minutes. The radius is far wider than any observed delta
		// because it's cheap to scan and the cost of missing is a lost face.
		const long FindRadius = 0x20000000; // ±512MB around the ped
		const long RegionChunk = 0x100000;

		// The mix anchor sits 0x28 into CPedHeadBlendData (six u32 blend ids at 0x10-0x27, then the
		// shape/skin/third mix floats). Used to report the struct base, and to check its alignment.
		const int MixOffsetInStruct = 0x28;

		// The struct comes out of a pool that aligns it to 16 bytes: every anchor ever located here —
		// four by full-memory sweep, four more by a successful find — had a 16-aligned base. A run of
		// memory that merely looks like the struct has no reason to, so the base's alignment is a free
		// filter, and it is exactly what separated the real struct from the zero region that saved
		// black hair over a real colour (base ...004, 4-aligned).
		const int StructAlign = 0x10;

		// DEAD END — don't re-attempt: the six shape/skin parent ids the native reports are NOT at
		// mix-0x18 (struct+0x10), whatever the documented layout says. Dumped mix-0x40..mix on three
		// characters and the ids appear nowhere in it: native [37,25,0,12,14,1] against sixteen
		// int32s of unrelated data. As a fingerprint gate they never matched, which cost every save
		// its early exit — the full 834MB radius instead of the first few MB.


		static bool Initialized;
		static bool available;

		// The memory read is "available" once we know it can run at all. Unlike the old
		// pattern path there is nothing to resolve up front — availability is proven per
		// ped when we actually find the struct — so we report true on a platform where the
		// primitives work and let TryFill no-op safely if a given ped can't be resolved.
		public static bool Available {
			get {
				EnsureInit();
				return available;
			}
		}

		static void EnsureInit() {
			if (Initialized) {
				return;
			}
			Initialized = true;
			// No patterns to resolve any more. The content-based locator works as long as
			// we can read process memory, which MemScan always can on a live game. Kept as
			// a flag so callers and the startup log have a single yes/no to report.
			available = true;
			// Layout-agnostic by construction: the anchor is located by CONTENT (the heritage mix
			// triple + the ped's overlay fingerprint), and every field is read relative to it, so
			// the same offsets serve both editions. Nothing here is edition-specific to report.
			Logger.Log("PedHeadBlendMemory: content-based head-blend read armed.");
		}

		// ---- Tick-driven mix-start finder ------------------------------------------------
		// Scanning tens of megabytes must NOT run synchronously inside a snapshot — a long
		// synchronous scan trips SHVDN's 5s tick watchdog, and racing the ped's churn while reading
		// its memory is how this subsystem has faulted the game before. So the find runs tick-driven
		// and time-sliced, off the snapshot hot path, the same way mood and the tattoo base are
		// found. BeginFind starts it; TryFill consumes the result once FindRunning is false.
		const long FindBudgetMs = 300;

		static bool findRunning;
		static long findBytes;  // scanned this find — for the FOUND/NOT-found diagnostic log
		// A not-found needs to say WHICH failure it was, or the next report can't be acted on:
		// zero triple hits = the scan never covered the struct (a reach problem), hits that all got
		// rejected = we covered it and the fingerprint disagreed (a layout problem).
		static int findTripleHits, findRejects;
		static int findStartMs; // Game.GameTime the scan armed — the FOUND log reports the real cost
		// Where a scan's milliseconds actually went. This subsystem has now been "optimised" twice
		// against a guessed bottleneck; splitting region enumeration from the gated copy from the
		// buffer scan means the next slow report names the culprit instead of inviting another guess.
		static long findReadTicks, findScanTicks, findEnumMs;
		static int findRegionCount;
		static int findMisaligned;

		// Take a located struct: cache it, persist the hint, and keep the bytes the fingerprint
		// passed on. Re-reading the address later (in TryFill) would hit a stale one — the deferred
		// decoration capture that runs after this find churns the ped and can relocate the struct.
		static void Accept(IntPtr cand, byte[] structBytes) {
			SaveDeltaHint(cand.ToInt64() - findPed);
			mixResult = cand;
			cachedPed = findPed;   // cache for cheap reuse on later snapshots
			cachedMix = cand;
			foundStruct = structBytes;
			findRunning = false;
			findRegions = null;
			Logger.LogDebug($"PedHeadBlendMemory: mix FOUND after {findBytes / 0x100000}MB in {Game.GameTime - findStartMs}ms " +
				$"({Cost()}, {findMisaligned} misaligned, mix={cand.ToInt64():X}, ped+0x{cand.ToInt64() - findPed:X}, " +
				$"eye={findEyeColour}, hairTint={structBytes[OffHairColour]}/{structBytes[OffHairHighlight]}).");
		}

		static string Cost() {
			long perMs = System.Diagnostics.Stopwatch.Frequency / 1000;
			if (perMs <= 0) {
				perMs = 1;
			}
			return $"{findRegionCount} regions: enum {findEnumMs}ms, read {findReadTicks / perMs}ms, scan {findScanTicks / perMs}ms";
		}
		static IEnumerator<MemScan.Region> findRegions;
		static float findShape, findSkin, findThird;
		// The ped's real per-slot overlay drawable indices, read from the native getter
		// (GET_PED_HEAD_OVERLAY, 255 = none). Used as a STRONG, ped-specific fingerprint at find
		// time: the heritage triple alone is 0,0,0 on a default ped and matches everywhere, but the
		// overlay-value array at OffOverlayValue must equal exactly these natively-read values — a
		// 13-byte ped-specific signature that random memory will not reproduce.
		static readonly byte[] findOverlayValues = new byte[PedAppearance.OverlayCount];
		// This ped's eye colour from GET_HEAD_BLEND_EYE_COLOR — the one head-blend field that has
		// both a getter AND a value that varies per ped, so it discriminates where the overlay array
		// can't. -1 = the getter gave nothing usable this pass.
		static int findEyeColour = -1;
		static long findPed;
		static IntPtr mixResult;

		// A fingerprint is WEAK when every byte it compares is ALSO the default value: zero heritage,
		// no overlays, and eye colour 0. Nothing in it is specific to this ped, so any suitably shaped
		// run of zeros passes and the first pass wins by position alone — measured, a zero region 1MB
		// into the scan beat the real struct and wrote a black hair tint over a real colour. For these
		// peds we scan the whole radius and accept only a UNIQUE pass; ambiguity keeps the defaults.
		static bool weakScan;
		static readonly List<IntPtr> weakHits = new List<IntPtr>();
		static readonly List<byte[]> weakHitStructs = new List<byte[]>();
		// Past two look-alikes the answer is already ambiguous; more can't make it decisive, so stop.
		const int WeakHitCap = 8;

		// Session cache: the CPedHeadBlendData doesn't move while the ped/model is unchanged, so a
		// found mix-start is reused on later snapshots after a cheap re-validation (read the three
		// heritage floats at the cached address — still match → skip the whole pointer-graph walk).
		// Keyed by ped memory address; a model swap gives a new address so the cache misses safely.
		static long cachedPed;
		static IntPtr cachedMix;

		// The struct bytes captured AT FIND TIME. Critical: between locating the struct and the
		// deferred DoSnapshot/TryFill the ped can churn (a spoof re-engage / model-info write can
		// RELOCATE the head-blend struct), so the found address may be stale by the time TryFill
		// reads it (observed: "mix anchor mismatch" 5s later). We snapshot the struct the instant we
		// find it (the user isn't editing mid-save, so the contents are final) and TryFill consumes
		// THIS buffer, immune to the later relocation.
		static byte[] foundStruct;

		public static bool FindRunning => findRunning;
		public static IntPtr MixResult => mixResult;
		public static byte[] StructSnapshot => foundStruct;

		// How long to wait for a freshly-switched ped's head blend to become readable before giving up.
		// GET_PED_HEAD_BLEND_DATA returns garbage right after a model switch/spawn and usually settles
		// within a second, but a snapshot taken immediately after switching can need longer — 6s covers
		// the slow case so the face still gets captured. WALL-CLOCK (Game.GameTime ms), NOT a tick
		// count: a tick budget is frame-rate dependent (observed: 360 ticks is ~6s at 60fps but only
		// ~2.2s at 160fps, so high-FPS machines gave up early and lost the head blend — the lost
		// hair-tint/eye/morph-on-custom-ped bug). Still bounded so a genuinely blend-less ped (e.g. a
		// non-freemode model) can't hold the snapshot forever.
		const int SettleBudgetMs = 6000;

		static Ped findTargetPed;
		static int settleStartMs; // Game.GameTime at which the settle wait began; -1 = not started
		static string lastRejectedMix; // last mix triple IsValidMix rejected, for the give-up diagnostic

		// Start a tick-driven search for the ped's CPedHeadBlendData mix-start. The heritage triple
		// (the native already round-trips it) is the locator fingerprint. If a previous snapshot
		// already located it for this ped and the cached address still reads the same triple, reuse
		// it instantly (no walk, no per-frame slicing) — that's what makes consequent saves smooth.
		//
		// The heritage may not be readable yet if the ped was just switched/spawned (the blend data
		// settles a beat later), so this enters a tick-driven SETTLE wait first; the actual walk is
		// armed by ArmWalk() once the heritage reads valid. FindRunning stays true throughout, so the
		// deferred snapshot correctly waits for the whole thing.
		public static void BeginFind(Ped ped) {
			mixResult = IntPtr.Zero;
			foundStruct = null;
			findRegions = null;
			findRunning = false;
			findTargetPed = null;
			settleStartMs = -1;
			if (!Available || ped == null || !ped.Exists() || ped.MemoryAddress == IntPtr.Zero) {
				return;
			}
			findTargetPed = ped;
			findRunning = true; // hold the deferred snapshot until settle+walk resolve (or time out)
			if (!ArmWalk()) {
				// Heritage not readable yet — wait for the blend to settle (driven by TickFind).
				Logger.LogDebug("PedHeadBlendMemory: head blend not ready — waiting for it to settle.");
			}
		}

		// Read everything about this ped that identifies its head blend: the heritage triple, the
		// overlay drawable indices and the eye colour. Returns false — with lastRejectedMix set to
		// why — when the blend isn't readable yet, so callers keep settling instead of searching for
		// something that isn't there.
		static bool PrepareFingerprint(Ped ped) {
			OutputArgument arg = OutputArgument.AllocForType<HeadBlendData>();
			// The RETURN value is the question we've been searching memory to answer: whether this ped
			// has a CPedHeadBlendData at all. A freemode ped only gets one once a blend is applied to
			// it, so a ped built outside the head-blend system has no struct to find — and searching
			// for one that doesn't exist is exactly how a zero-filled region ends up in a saved slot.
			bool hasBlend = Function.Call<bool>(Hash.GET_PED_HEAD_BLEND_DATA, ped, arg);
			HeadBlendData d = arg.GetResultAsBlittableStruct<HeadBlendData>();
			if (!hasBlend) {
				// Treated as not-ready rather than a hard stop: right after a model switch the ped is
				// still initialising and this flips true a beat later. The settle budget bounds it, and
				// its give-up log names this as the reason.
				lastRejectedMix = "no head-blend data on this ped (GET_PED_HEAD_BLEND_DATA returned false)";
				return false;
			}
			findShape = d.ShapeMix;
			findSkin = d.SkinMix;
			findThird = d.ThirdMix;

			// The heritage mix floats are blend WEIGHTS — always in [0,1] for a valid freemode head.
			// Right after a model switch/spawn GET_PED_HEAD_BLEND_DATA hands back garbage (observed
			// e.g. 4.6E+24, 8.4E-45) until the blend settles. Not-ready → caller keeps settling.
			if (!IsValidMix(findShape) || !IsValidMix(findSkin) || !IsValidMix(findThird)) {
				// Remember the last rejected triple so the give-up log can show WHY (distinguishes
				// real-but-out-of-range, e.g. a Menyoo overshoot, from just-switched garbage).
				lastRejectedMix = $"shape={findShape:G9} skin={findSkin:G9} third={findThird:G9}";
				return false;
			}

			// Read the real overlay drawable indices now (native getter) to fingerprint the struct
			// location precisely. GET_PED_HEAD_OVERLAY returns the slot's drawable index, 255 = none.
			for (int slot = 0; slot < PedAppearance.OverlayCount; slot++) {
				int idx = Function.Call<int>(Hash.GET_PED_HEAD_OVERLAY, ped, slot);
				findOverlayValues[slot] = (byte)(idx < 0 || idx > 255 ? 255 : idx);
			}
			findEyeColour = Function.Call<int>(Hash.GET_HEAD_BLEND_EYE_COLOR, ped);
			return true;
		}

		// Read the heritage + overlay fingerprint and, if the heritage is valid, either consume the
		// session cache or arm the pointer-graph walk. Returns true when the find is resolved or armed
		// (caller stops waiting), false when the heritage isn't readable yet (keep settling).
		static bool ArmWalk() {
			Ped ped = findTargetPed;
			if (ped == null || !ped.Exists() || ped.MemoryAddress == IntPtr.Zero) {
				findRunning = false;
				return true;
			}
			if (!PrepareFingerprint(ped)) {
				return false;
			}

			findPed = ped.MemoryAddress.ToInt64();
			if (cachedPed == findPed && cachedMix != IntPtr.Zero && MixMatches(cachedMix)) {
				mixResult = cachedMix; // cache hit — reuse, no walk
				// Snapshot now, before any later ped churn (e.g. the tattoo capture) can relocate it.
				foundStruct = MemScan.Snapshot(cachedMix, StructSpan);
				findRunning = false;
				Logger.LogDebug($"PedHeadBlendMemory: mix cache HIT (mix={cachedMix.ToInt64():X}).");
				return true;
			}
			// A ped with nothing specific about it can't be found by content — but it CAN still be
			// found by elimination, if only one region in range looks like its struct at all. So
			// rather than give up, scan the whole radius and demand a unique pass (see weakScan).
			weakScan = WeakFingerprint();
			weakHits.Clear();
			weakHitStructs.Clear();
			findBytes = 0;
			findTripleHits = 0;
			findRejects = 0;
			findMisaligned = 0;
			findReadTicks = 0;
			findScanTicks = 0;
			var enumSw = System.Diagnostics.Stopwatch.StartNew();
			List<MemScan.Region> regions = RegionsNear(ped.MemoryAddress, FindRadius);
			findEnumMs = enumSw.ElapsedMilliseconds;
			findRegionCount = regions.Count;
			findRegions = regions.GetEnumerator();
			findStartMs = Game.GameTime;
			Logger.LogDebug($"PedHeadBlendMemory: scanning near the ped (heritage={findShape:G9},{findSkin:G9},{findThird:G9}, " +
				$"eye={findEyeColour}, hint={(DeltaHint() == 0 ? "none" : "ped+0x" + DeltaHint().ToString("X"))}, " +
				$"{findRegionCount} regions in {findEnumMs}ms{(weakScan ? ", WEAK fingerprint - scanning all of it for a unique match" : "")}).");
			return true;
		}

		// Does this ped carry NOTHING a content search can identify its head blend by? Heritage that
		// is the default triple, no overlay set, and eye colour 0 — note eye colour ZERO, not the
		// getter's -1: 0 is the default palette index, so it discriminates nothing and treating it as
		// content is what let a zero region pass for Test's struct.
		static bool WeakFingerprint() {
			if (findShape != 0f || findSkin != 0f || findThird != 0f) {
				return false;
			}
			if (findEyeColour > 0 && findEyeColour <= MaxEyeColourIndex) {
				return false;
			}
			for (int i = 0; i < PedAppearance.OverlayCount; i++) {
				if (findOverlayValues[i] != 255) {
					return false;
				}
			}
			return true;
		}

		// ---- Full-memory sweep (diagnostic, user-invoked) --------------------------------
		// The probe can only report who points at a struct we already found, so on a ped the finder
		// misses it tells us nothing — exactly the ped we need the path for. This sweeps every
		// committed private read-write region for the heritage triple instead, which does not depend
		// on the struct being reachable from the ped at all, then probes each hit. Slow and
		// deliberately manual: it exists to derive the offsets, not to run during a save.
		//
		// Only meaningful on a DISTINCTIVE character. With default heritage the triple is 0,0,0 and
		// the sweep would report thousands of zero runs.
		const long SweepBudgetMs = 200;
		// Counts only hits that PASS the fingerprint. The bare triple matches in creator buffers,
		// script globals and save data — capping on raw hits stopped two sweeps inside the first
		// 40MB and reported "done" without ever reaching the struct.
		const int SweepMaxPasses = 4;

		static bool sweepRunning;
		static IEnumerator<MemScan.Region> sweepRegions;
		static long sweepBytes;
		static int sweepHits;
		static Ped sweepPed;

		public static bool SweepRunning => sweepRunning;

		public static void BeginSweep(Ped ped) {
			sweepRunning = false;
			sweepRegions = null;
			sweepBytes = 0;
			sweepHits = 0;
			sweepPed = ped;
			if (ped == null || !ped.Exists() || ped.MemoryAddress == IntPtr.Zero) {
				Logger.Log("PedHeadBlendMemory: sweep needs a live ped.");
				return;
			}
			findTargetPed = ped;
			if (!PrepareFingerprint(ped)) {
				Logger.Log($"PedHeadBlendMemory: sweep aborted — head blend not readable ({lastRejectedMix}).");
				return;
			}
			if (WeakFingerprint()) {
				Logger.Log("PedHeadBlendMemory: sweep aborted — this ped has default heritage, no overlays and eye " +
					"colour 0, so the triple matches everywhere. Run it on a distinctive character.");
				return;
			}
			weakScan = false; // the sweep reports every pass itself; it never wants the collect path
			// Unbounded (radius 0) but still nearest-first, so a sweep that does find something finds
			// it early and its ped-delta is comparable with the finder's radius.
			findTripleHits = 0;
			findRejects = 0;
			sweepRegions = RegionsNear(ped.MemoryAddress, 0).GetEnumerator();
			sweepRunning = true;
			Logger.Log($"PedHeadBlendMemory: sweep started (heritage={findShape:G9},{findSkin:G9},{findThird:G9}, " +
				$"eye={findEyeColour}, ped={ped.MemoryAddress.ToInt64():X}).");
		}

		public static void TickSweep() {
			if (!sweepRunning) {
				return;
			}
			try {
				var sw = System.Diagnostics.Stopwatch.StartNew();
				while (sw.ElapsedMilliseconds < SweepBudgetMs && sweepRegions.MoveNext()) {
					MemScan.Region r = sweepRegions.Current;
					int len = MemScan.SnapshotInto(r.Base, scanBytes, (int)Math.Min(r.Size, RegionChunk));
					sweepBytes += len;
					IntPtr cand = ScanBuffer(r.Base, scanBytes, len);
					if (cand == IntPtr.Zero) {
						continue;
					}
					SaveDeltaHint(cand.ToInt64() - sweepPed.MemoryAddress.ToInt64());
					// Where the struct sits relative to the ped — the number the per-save finder's
					// search radius is sized from.
					long delta = cand.ToInt64() - sweepPed.MemoryAddress.ToInt64();
					Logger.Log($"PedHeadBlendMemory: sweep PASS mix={cand.ToInt64():X} " +
						$"(base {cand.ToInt64() - MixOffsetInStruct:X}, ped{(delta < 0 ? "-" : "+")}0x{Math.Abs(delta):X} = {delta / 0x100000}MB, " +
						$"hairTint={scanHitStruct[OffHairColour]}).");
					ProbePointerPath(sweepPed, cand);
					if (++sweepHits >= SweepMaxPasses) {
						Logger.Log($"PedHeadBlendMemory: sweep stopped at {SweepMaxPasses} passes after {sweepBytes / 0x100000}MB.");
						sweepRunning = false;
						sweepRegions = null;
						return;
					}
				}
				if (sw.ElapsedMilliseconds < SweepBudgetMs) {
					Logger.Log($"PedHeadBlendMemory: sweep finished — {sweepHits} pass(es) across {sweepBytes / 0x100000}MB.");
					sweepRunning = false;
					sweepRegions = null;
				}
			} catch (Exception e) {
				Logger.LogError("PedHeadBlendMemory.TickSweep: " + e);
				sweepRunning = false;
				sweepRegions = null;
			}
		}

		// Cheap revalidation: does the cached mix address still look like a real CPedHeadBlendData?
		// The heritage triple alone is NOT enough — when a ped has default heritage the triple is
		// 0,0,0, which matches countless unrelated zero runs in memory. So require BOTH the triple
		// AND the morph-range fingerprint (LooksLikeStruct) here, same as at find time.
		static bool MixMatches(IntPtr mix) {
			return LooksLikeStruct(MemScan.Snapshot(mix, StructSpan), 0);
		}

		// Is `mix` the real CPedHeadBlendData anchor, or just a coincidental run of floats that
		// happen to equal the heritage triple? The heritage mix is the locator, but a DEFAULT
		// freemode ped has mix = 0,0,0, and three consecutive zero-floats occur all over process
		// memory — so the bare triple is a false-positive magnet. Two earlier weak filters (morphs
		// in [-1.5,1.5]; any empty overlay slot) both still admitted garbage: a block of denormal
		// floats (4.4E-42, etc.) is numerically in range yet is not a real morph array.
		//
		// The reliable discriminator is a PED-SPECIFIC content match: the overlay drawable-value
		// array at OffOverlayValue must equal, byte-for-byte, the 13 values the native getter
		// returned for THIS ped (findOverlayValues). That is a precise fingerprint a random region
		// won't reproduce. We additionally reject morph arrays that are denormal/garbage so a near-
		// zero region can't sneak through. The mix triple still gates first (cheap reject).
		// `s` holds the struct starting at `at`. Taking a buffer rather than an address lets the
		// scan verify a candidate inside the megabyte it has ALREADY copied: no second read, no
		// per-candidate allocation, and no VirtualQuery. On a ped whose heritage anchor recurs in
		// memory that is thousands of avoided round-trips per scan, and it fingerprints exactly
		// the bytes the triple matched on rather than whatever the address holds a moment later.
		static bool LooksLikeStruct(byte[] s, int at) {
			if (s.Length < at + StructSpan) {
				return false;
			}
			if (!FloatEq(BitConverter.ToSingle(s, at + OffShapeMix), findShape) ||
				!FloatEq(BitConverter.ToSingle(s, at + OffShapeMix + 4), findSkin) ||
				!FloatEq(BitConverter.ToSingle(s, at + OffShapeMix + 8), findThird)) {
				return false;
			}
			// Strong, ped-specific signature: the live overlay drawable indices must match exactly.
			for (int i = 0; i < PedAppearance.OverlayCount; i++) {
				if (s[at + OffOverlayValue + i] != findOverlayValues[i]) {
					return false;
				}
			}
			// That signature collapses on the ped it matters most for: a DEFAULT-heritage ped with no
			// overlays fingerprints as twelve zero bytes then thirteen 0xFF bytes, which any zero or
			// fill region reproduces. Observed live — a block passed every check above and captured
			// EyeColor=65535 with a hair tint to match, which is how a wrong colour reached a saved
			// slot. Eye colour is the field that separates them, and unlike the hair tint the game
			// DOES expose a getter for it, so it can be matched against this ped exactly.
			// Only when the getter actually returned a palette index. A ped whose eye colour was never
			// set reads -1 from the getter AND 0xFFFF in the struct — both are "unset", so rejecting
			// an out-of-range field would throw away the REAL struct on exactly the ped that has
			// nothing else to identify it by. Discriminating that ped is HasDistinguishingContent's
			// job, not this check's.
			if (findEyeColour >= 0 && findEyeColour <= MaxEyeColourIndex &&
				BitConverter.ToUInt16(s, at + OffEyeColour) != findEyeColour) {
				return false;
			}
			// And the morph array must be PLAUSIBLE morph data, not denormal noise that merely falls
			// in range. A real morph is 0 or a normal float in [-1.5,1.5]; reject NaN, out-of-range,
			// and sub-normal tiny magnitudes (|v| < 1e-6 but nonzero) that signal reinterpreted bytes.
			for (int i = 0; i < PedAppearance.FaceFeatureCount; i++) {
				float v = BitConverter.ToSingle(s, at + OffFaceFeature + i * 4);
				if (float.IsNaN(v) || v < -1.5f || v > 1.5f) {
					return false;
				}
				if (v != 0f && Math.Abs(v) < 1e-6f) {
					return false; // denormal / garbage byte pattern, not an authored morph
				}
			}
			return true;
		}

		// Advance the search by one time-bounded slice. Sets MixResult + stops when found or the
		// pointer graph is exhausted. Call once per tick while FindRunning.
		public static void TickFind() {
			if (!findRunning) {
				return;
			}
			// SETTLE phase: the scan isn't armed yet because the heritage wasn't readable (ped just
			// switched/spawned). Retry reading it each tick until it's valid, then ArmWalk() either
			// resolves from cache or arms the region scan below. Bounded so a blend-less ped gives up
			// instead of holding the snapshot forever.
			if (findRegions == null && mixResult == IntPtr.Zero) {
				if (ArmWalk()) {
					// Resolved (cache hit) or armed the walk. If it cleared findRunning (cache hit /
					// dead ped), we're done; otherwise fall through next tick into the walk.
					return;
				}
				// Wall-clock settle budget (frame-rate independent — see SettleBudgetMs). Stamp the
				// start on the first not-ready tick.
				if (settleStartMs < 0) {
					settleStartMs = Game.GameTime;
				}
				if (Game.GameTime - settleStartMs >= SettleBudgetMs) {
					Logger.Log($"PedHeadBlendMemory: head blend never became readable (last rejected mix: {lastRejectedMix}); skipping memory read (keeping defaults).");
					findRunning = false;
				}
				return;
			}
			try {
				var sw = System.Diagnostics.Stopwatch.StartNew();
				bool exhausted = false;
				while (sw.ElapsedMilliseconds < FindBudgetMs) {
					// Already ambiguous — more look-alikes can't make it decisive, so stop paying for them.
					if (weakScan && weakHits.Count >= WeakHitCap) {
						exhausted = true;
						break;
					}
					if (!findRegions.MoveNext()) {
						exhausted = true;
						break;
					}
					MemScan.Region r = findRegions.Current;
					long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
					int len = MemScan.SnapshotInto(r.Base, scanBytes, (int)Math.Min(r.Size, RegionChunk));
					long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
					findBytes += len;
					IntPtr cand = ScanBuffer(r.Base, scanBytes, len);
					findReadTicks += t1 - t0;
					findScanTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
					// A weak scan never returns a candidate here — it collects them and is judged below.
					if (cand != IntPtr.Zero) {
						Accept(cand, scanHitStruct);
						return;
					}
				}
				if (!exhausted) {
					return; // budget spent for this tick; resume next one
				}
				// Every region in range scanned. A weak fingerprint earns the answer only if exactly
				// one region in the whole radius could have been it.
				if (weakScan && weakHits.Count == 1) {
					Accept(weakHits[0], weakHitStructs[0]);
					return;
				}
				findRunning = false;
				findRegions = null;
				if (weakScan && weakHits.Count > 1) {
					Logger.Log($"PedHeadBlendMemory: {weakHits.Count} regions look equally like this ped's head blend " +
						"(default heritage, no overlays, eye colour 0 — nothing identifies it), so any pick would be a " +
						"guess; keeping defaults. Give the character a distinguishing feature (eye colour, an eyebrow, " +
						"or a parent mix) and save again.");
					return;
				}
				Logger.Log($"PedHeadBlendMemory: mix NOT found in {findBytes / 0x100000}MB around the ped " +
					$"(heritage triple hit {findTripleHits}x, {findMisaligned} misaligned, {findRejects} rejected by " +
					$"fingerprint; {Cost()}). Run Debug > Find Head-Blend Path to search all of memory.");
			} catch (Exception e) {
				Logger.LogError("PedHeadBlendMemory.TickFind: " + e);
				findRunning = false;
				findRegions = null;
			}
		}

		// Fills the fields no native exposes — overlay opacity, the 20 micro-morphs — and
		// refines overlay drawable indices and eye colour from the live struct. Returns
		// false (touching nothing) if the struct can't be located so the caller keeps its
		// native-captured / default values. Anything unexpected is treated as unavailable —
		// a bad read must never wreck a face.
		public static bool TryFill(Ped ped, AppearanceData ad) {
			if (!Available || ped == null || !ped.Exists()) {
				return false;
			}

			// Consume the struct bytes snapshotted AT FIND TIME (TickFind/BeginFind), not the live
			// address: the deferred tattoo capture that runs between find and here churns the ped and
			// can relocate the struct, so re-reading MixResult now would hit stale memory (that was
			// the "mix anchor mismatch" 5s later). The find-time bytes are the ped's final state for
			// this save — the user isn't editing mid-snapshot.
			byte[] s = StructSnapshot;
			if (s == null || s.Length < StructSpan) {
				Logger.Log("PedHeadBlendMemory: could not locate CPedHeadBlendData for this ped; keeping defaults.");
				return false;
			}

			// Micro-morphs: 20 floats. The find-time fingerprint already validated these, so this is
			// a belt-and-suspenders sanity gate — a failure here means the layout shifted under a
			// game patch; abort rather than write garbage.
			for (int i = 0; i < PedAppearance.FaceFeatureCount; i++) {
				float v = BitConverter.ToSingle(s, OffFaceFeature + i * 4);
				if (v < -1.5f || v > 1.5f || float.IsNaN(v)) {
					Logger.LogError($"PedHeadBlendMemory: faceFeature[{i}]={v} out of range; aborting memory fill.");
					return false;
				}
				ad.FaceFeatures[i] = v;
			}

			// Eye colour as the live palette index (the native getter agrees with this, but
			// reading it here keeps everything from one consistent snapshot). Only when it IS a
			// palette index: the native capture already put a good value in, so an out-of-range
			// read must not replace it — that is what wrote EyeColor=65535 into a saved slot.
			int eye = BitConverter.ToUInt16(s, OffEyeColour);
			if (eye <= MaxEyeColourIndex) {
				ad.EyeColor = eye;
			} else {
				Logger.LogError($"PedHeadBlendMemory: eye colour {eye} out of range; keeping the natively captured {ad.EyeColor}.");
			}

			// Hair tint palette ids — no native getter exposes these (only RGB, which the
			// setter can't take), so memory is the only source. They sit immediately after
			// eye colour.
			ad.HairColor = s[OffHairColour];
			ad.HairHighlightColor = s[OffHairHighlight];

			// Overlay opacity + tint: enrich the overlays the native pass already found. The
			// tint colorId/highlightId arrays are per-slot bytes; reading them lets apply
			// restore eyebrow/eye-shadow/blush/lipstick colours instead of skipping them.
			foreach (HeadOverlayData o in ad.Overlays) {
				if (o.Slot < 0 || o.Slot >= PedAppearance.OverlayCount) {
					continue;
				}
				float opacity = BitConverter.ToSingle(s, OffOverlayAlpha + o.Slot * 4);
				if (opacity >= 0f && opacity <= 1f) {
					o.Opacity = opacity;
				}
				// Refine the drawable index from the struct (255 = none) as a cross-check;
				// the native pass already set it, so only overwrite a sane value.
				byte value = s[OffOverlayValue + o.Slot];
				if (value != 255) {
					o.Index = value;
				}
				o.FirstColor = s[OffOverlayColorId + o.Slot];
				o.SecondColor = s[OffOverlayHighlightId + o.Slot];
			}

			// The overlay tint colours and hair tint were genuinely read this pass, so apply
			// may now tint the tintable slots (eyebrows/makeup/blush/lipstick) instead of
			// skipping them. See AppearanceData.OverlayTintFromMemory.
			ad.OverlayTintFromMemory = true;
			return true;
		}

		// ---- Pointer-path probe (diagnostic) ---------------------------------------------
		// The content search exists because CPedHeadBlendData is a RAGE extension: separately
		// allocated, its pointer parked in the list at ped+0x10, so there is no fixed ped offset
		// to read. That is only true as far as we've looked, though — if the pointer turns out to
		// sit somewhere STABLE relative to the ped, reading it directly replaces the whole search
		// and with it the false-positive lottery on a default-heritage ped.
		//
		// So: having located the struct the slow way on a ped whose fingerprint we trust, look for
		// who POINTS at it, directly in the ped and one hop out, and log the offsets. An offset
		// that repeats across different characters is the static path we're after. Read-only,
		// Debug-only, once per successful find — it costs nothing in normal play.
		// Reach: a probe that reports "0 references" is only meaningful if it looked far enough, and
		// the first pass didn't — it found the reference on one ped instance and none on another.
		const int ProbePedBytes = 0x8000;   // how far into CPed to look for the pointer
		const int ProbeChildBytes = 0x800;  // how far into each child block to look
		const int ProbeMaxChildren = 1024;  // bound the one-hop sweep
		const int ProbeMaxHits = 24;        // don't flood the log if the struct is widely referenced
		// A pointer to the struct won't point at the mix float itself but at the struct BASE, some
		// way above it. Accept anything landing in a window around the anchor and report the delta.
		const int ProbeWindowBefore = 0x400;

		static void ProbePointerPath(Ped ped, IntPtr mix) {
			if (Logger.Threshold > LogLevel.Debug || ped == null || !ped.Exists()) {
				return;
			}
			try {
				IntPtr pedAddr = ped.MemoryAddress;
				if (pedAddr == IntPtr.Zero) {
					return;
				}
				long lo = mix.ToInt64() - ProbeWindowBefore;
				long hi = mix.ToInt64() + StructSpan;
				long pedBase = pedAddr.ToInt64();
				int hits = 0;

				byte[] pedBuf = MemScan.Snapshot(pedAddr, ProbePedBytes);
				var children = new List<KeyValuePair<int, IntPtr>>();
				for (int off = 0; off + 8 <= pedBuf.Length; off += 8) {
					long raw = BitConverter.ToInt64(pedBuf, off);
					if (raw >= lo && raw <= hi) {
						Logger.LogDebug($"PedHeadBlendMemory: probe — ped+0x{off:X} -> struct ({DeltaToMix(raw, mix)}), ped={pedBase:X}");
						hits++;
					} else if ((raw & 7) == 0 && raw > 0x10000 && raw < 0x7FFFFFFFFFFF && children.Count < ProbeMaxChildren) {
						children.Add(new KeyValuePair<int, IntPtr>(off, (IntPtr)raw));
					}
				}

				// One hop out: the extension list is exactly this shape — a pointer in the ped to a
				// node that holds the pointer we want. Report the full two-step path.
				foreach (KeyValuePair<int, IntPtr> child in children) {
					if (hits >= ProbeMaxHits) {
						break;
					}
					byte[] childBuf = MemScan.Snapshot(child.Value, ProbeChildBytes);
					for (int off = 0; off + 8 <= childBuf.Length; off += 8) {
						long raw = BitConverter.ToInt64(childBuf, off);
						if (raw < lo || raw > hi) {
							continue;
						}
						Logger.LogDebug($"PedHeadBlendMemory: probe — ped+0x{child.Key:X} -> +0x{off:X} -> struct ({DeltaToMix(raw, mix)})");
						if (++hits >= ProbeMaxHits) {
							break;
						}
					}
				}
				Logger.LogDebug($"PedHeadBlendMemory: probe — {hits} reference(s) to the struct found (ped={pedBase:X}, mix={mix.ToInt64():X}).");
			} catch (Exception e) {
				Logger.LogError("PedHeadBlendMemory.ProbePointerPath: " + e);
			}
		}

		// Where a referenced address sits relative to the mix anchor, in HEX — the number is compared
		// against struct offsets, and a decimal one reads as a different value entirely.
		static string DeltaToMix(long raw, IntPtr mix) {
			long delta = raw - mix.ToInt64();
			return delta < 0 ? $"mix-0x{-delta:X}" : $"mix+0x{delta:X}";
		}

		// ---- Buffer scanning -------------------------------------------------------------
		// One buffer pair for every scan, allocated once. Both are large-object-heap sized, so
		// allocating them per region — which is what a nearest-first scan does thousands of times —
		// cost more than the scanning did (measured 5MB/s that way, against 56MB/s over big regions).
		static readonly byte[] scanBytes = new byte[RegionChunk];
		static readonly int[] scanInts = new int[RegionChunk / 4];

		// The struct bytes behind the candidate ScanBuffer last returned, taken at the moment the
		// fingerprint passed. Callers consume this instead of re-reading the address.
		static byte[] scanHitStruct;

		// Which of the three mix floats to match on. Matching on a zero component would hit every
		// zero run in memory and turn the scan into a fingerprint-check grind, so anchor on a
		// non-zero one and verify the other two around it.
		static int AnchorIndex() {
			if (findSkin != 0f) return 1;
			if (findShape != 0f) return 0;
			return 2;
		}

		// Find the mix anchor in `buf` (a snapshot taken at `origin`), or Zero. Exact bit match on
		// the anchor float — these floats come out of the same struct the native read them from, so
		// they match bit-for-bit — then FloatEq on its neighbours, then the full fingerprint.
		static IntPtr ScanBuffer(IntPtr origin, byte[] buf, int length) {
			int anchor = AnchorIndex();
			float anchorValue = anchor == 0 ? findShape : anchor == 1 ? findSkin : findThird;
			int anchorBits = BitConverter.ToInt32(BitConverter.GetBytes(anchorValue), 0);
			int n = length / 4;
			Buffer.BlockCopy(buf, 0, scanInts, 0, n * 4);
			for (int i = anchor; i + (2 - anchor) < n; i++) {
				if (scanInts[i] != anchorBits) {
					continue;
				}
				int off = (i - anchor) * 4;
				if (!FloatEq(BitConverter.ToSingle(buf, off), findShape) ||
					!FloatEq(BitConverter.ToSingle(buf, off + 4), findSkin) ||
					!FloatEq(BitConverter.ToSingle(buf, off + 8), findThird)) {
					continue;
				}
				findTripleHits++;
				IntPtr cand = origin + off;
				if (((cand.ToInt64() - MixOffsetInStruct) & (StructAlign - 1)) != 0) {
					findMisaligned++;
					continue;
				}
				// Verify against the buffer we already hold; only fall back to a live read when the
				// struct runs off the end of this chunk (a 1MB chunk boundary can split it).
				bool inBuf = off + StructSpan <= length;
				byte[] s = inBuf ? buf : MemScan.Snapshot(cand, StructSpan);
				int at = inBuf ? off : 0;
				if (!LooksLikeStruct(s, at)) {
					findRejects++;
					continue;
				}
				var hit = new byte[StructSpan];
				Buffer.BlockCopy(s, at, hit, 0, StructSpan);
				if (!weakScan) {
					scanHitStruct = hit;
					return cand;
				}
				// Weak fingerprint: collect instead of returning, so the caller can insist the answer
				// is the only one in range before trusting it.
				if (weakHits.Count < WeakHitCap) {
					weakHits.Add(cand);
					weakHitStructs.Add(hit);
				}
			}
			return IntPtr.Zero;
		}

		// Where the head blend sat relative to its ped, last time we found one. Purely an ORDERING
		// hint — the fingerprint still decides — so a stale or wrong hint costs a little scanning,
		// never a wrong answer. Persisted because the pool has landed in the same band across
		// sessions (0x12Bxxxxx / 0x12Dxxxxx observed), which turns the first save of a session from
		// hundreds of megabytes of scanning into a few.
		static long deltaHint;
		static bool deltaHintLoaded;
		static string HintPath => ScriptPaths.For("headblend.hint");

		static long DeltaHint() {
			if (!deltaHintLoaded) {
				deltaHintLoaded = true;
				try {
					string text = System.IO.File.Exists(HintPath) ? System.IO.File.ReadAllText(HintPath).Trim() : null;
					long parsed;
					if (!string.IsNullOrEmpty(text) && long.TryParse(text, System.Globalization.NumberStyles.HexNumber,
							System.Globalization.CultureInfo.InvariantCulture, out parsed)) {
						deltaHint = parsed;
					}
				} catch {
					// A missing or unreadable hint just means an unordered first scan.
				}
			}
			return deltaHint;
		}

		static void SaveDeltaHint(long delta) {
			if (delta == deltaHint) {
				return;
			}
			deltaHint = delta;
			try {
				System.IO.File.WriteAllText(HintPath, delta.ToString("X"));
			} catch {
				// Only an optimisation; never let it break a save.
			}
		}

		// Committed private read-write regions worth scanning for this ped, ordered by how close
		// they are to where the struct was last found (or to the ped itself, with no hint). The
		// radius bounds a miss.
		static List<MemScan.Region> RegionsNear(IntPtr pedAddr, long radius) {
			long origin = pedAddr.ToInt64() + DeltaHint();
			long ped = pedAddr.ToInt64();
			var regions = new List<MemScan.Region>();
			// Bound the walk itself, not just what we keep: outside the radius there is nothing to
			// collect, and crawling the rest of the address space costs a VirtualQuery per region
			// before the first byte is ever read.
			long from = radius > 0 ? Math.Max(0, ped - radius) : 0;
			long to = radius > 0 ? ped + radius : 0;
			foreach (MemScan.Region r in MemScan.EnumerateRegions(RegionChunk, writableOnly: true, privateOnly: true, startAddr: from, endAddr: to)) {
				if (radius <= 0 || Math.Abs(r.Base.ToInt64() - ped) <= radius) {
					regions.Add(r);
				}
			}
			regions.Sort((a, b) => Math.Abs(a.Base.ToInt64() - origin).CompareTo(Math.Abs(b.Base.ToInt64() - origin)));
			return regions;
		}

		static bool FloatEq(float a, float b) {
			return Math.Abs(a - b) < 0.0005f;
		}

		// Smallest normal float; anything nonzero below it is an IEEE subnormal. See IsValidMix.
		const float MinNormalFloat = 1.17549435E-38f;

		// Is a heritage mix weight a real, settled value rather than just-switched garbage? A valid
		// weight is a NORMAL float in [0,1] (with ±0.1 slack: Menyoo-loaded peds write a hair outside,
		// e.g. skin mix 1.0087 — real, stable data). Right after a model switch the getter hands back
		// garbage until the blend settles, and that garbage is NaN, wildly out of range (4.6E+24), or a
		// subnormal (8.4E-45) — all rejected here so the finder keeps waiting.
		//
		// The distinction is normal-vs-subnormal, NOT a magnitude floor: a genuinely near-zero mix can
		// be a small NORMAL float (observed 7.45E-08 on a Menyoo-edited default-heritage ped) that a
		// 1e-6 floor wrongly rejected as garbage, wasting the whole settle budget and losing the face.
		// Real near-zero mixes are normal floats and pass; only subnormals — the settling artefact — fail.
		static bool IsValidMix(float v) {
			if (float.IsNaN(v) || v < -0.1f || v > 1.1f) {
				return false;
			}
			return v == 0f || Math.Abs(v) >= MinNormalFloat;
		}
	}
}
