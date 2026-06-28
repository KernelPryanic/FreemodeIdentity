#include "decoration.hpp"

#include "build_edition.hpp"
#include "logger.hpp"
#include "pattern.hpp"

#include <cstdint>

// Resolve the global ped-decoration (tattoo) array base. SHVDN/C# can find this by pattern on
// Legacy (plaintext .text via Game.FindPattern), but NOT on Enhanced, whose .text is encrypted on
// disk and only decrypted in memory — exactly what this native scanner reads. So the shim does the
// scan and hands the base to C# through the shared block. A miss just means C# skips tattoos this
// snapshot (it never touches the ped to go looking), so it can't wipe a real tattoo.
//
// Both editions resolve a `mov/add reg, [rip+disp32]` that loads the array-base global, then deref
// once: base = *(match + instrLen + disp32). pedEntry = base + bufferIndex*0x7D8 (C# side).
namespace {

struct Sig {
	const char* pattern;
	int dispOffset; // byte offset of the rip-relative disp32 within the match
	int instrLen;   // length of the loading instruction (disp = at +dispOffset, target = match+instrLen+disp)
};

// LEGACY (FiveM GET_PED_DECORATIONS, EntityExtraNatives.cpp PR #1467): `add r8,[rip+disp32]`
// (4C 03 05 …), one pattern for the whole legacy line (b1604..b3xxx). disp at +3, instr len 7.
constexpr Sig kLegacySig = { "4C 03 05 ?? ?? ?? ?? EB 03 4D 8B C3", 3, 7 };

// ENHANCED (~1013.x, derived in-game via DecorationProbe): the decoration indexing sites load the
// global with `mov rax,[rip+disp32]; test rax,rax; jz; mov ecx,ecx; imul rcx,rcx,0x7D8` — the
// 0x7D8 (PedEntryStride) imul is the unmistakable tell. `48 8B 05` = mov rax,[rip]; disp at +3,
// instr len 7. The trailing test/jz/imul-by-0x7D8 makes it specific to this array. Multiple call
// sites share this exact shape; the scanner returns the first.
constexpr Sig kEnhancedSig = {
	"48 8B 05 ?? ?? ?? ?? 48 85 C0 74 ?? 89 C9 48 69 C9 D8 07 00 00", 3, 7
};

uintptr_t Resolve(const Sig& sig, const char* edition) {
	uint8_t* hit = Pattern::Scan(sig.pattern);
	if (!hit) {
		Logger::Logf("Decoration: %s global-ptr pattern not found in .text — C# skips tattoos this snapshot.", edition);
		return 0;
	}
	int32_t disp = *reinterpret_cast<int32_t*>(hit + sig.dispOffset);
	auto* globalSlot = reinterpret_cast<uintptr_t*>(hit + sig.instrLen + disp);
	uintptr_t base = *globalSlot; // one deref → the decoration array base
	if (!base) {
		Logger::Logf("Decoration: %s global slot is null (no array yet) — C# skips tattoos this snapshot.", edition);
		return 0;
	}
	Logger::Logf("Decoration: %s array base @ %p (pattern @ %p).", edition, (void*)base, (void*)hit);
	return base;
}

} // namespace

namespace Decoration {

uintptr_t ResolveArrayBase() {
	return BuildEdition::IsLegacy() ? Resolve(kLegacySig, "Legacy") : Resolve(kEnhancedSig, "Enhanced");
}

} // namespace Decoration
