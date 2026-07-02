#include "native_hook.hpp"

#include "build_edition.hpp"
#include "logger.hpp"
#include "natives.hpp"
#include "natives_legacy.hpp"

#include "MinHook.h"

#include <cstdint>

namespace {

// Native handlers on Enhanced are short thunks that jmp to the real body; MinHook can't relocate
// those (MH_ERROR_UNSUPPORTED_FUNCTION). Follow a leading jmp. Handles E9 (rel32) and FF 25 (jmp
// qword [rip+disp32]). Legacy handlers are direct functions, so this is a no-op there (the leading
// byte won't be a jmp).
void* ResolveThunk(void* fn) {
	uint8_t* p = reinterpret_cast<uint8_t*>(fn);
	if (p[0] == 0xE9) {
		int32_t rel = *reinterpret_cast<int32_t*>(p + 1);
		return p + 5 + rel;
	}
	if (p[0] == 0xFF && p[1] == 0x25) {
		int32_t disp = *reinterpret_cast<int32_t*>(p + 2);
		return *reinterpret_cast<void**>(p + 6 + disp);
	}
	return fn;
}

// Resolve a native handler for the running edition. Both editions key on the SAME translated hash;
// only the resolver differs — Legacy walks the scrNativeRegistration table, Enhanced uses the
// InitNativeTables trick.
rage::scrNativeHandler ResolveHandler(uint64_t hash) {
	return BuildEdition::IsLegacy() ? NativesLegacy::GetHandler(hash) : Natives::GetHandler(hash);
}

} // namespace

namespace NativeHook {

bool Install(uint64_t translatedHash, void* detour, void** trampoline, const char* name) {
	rage::scrNativeHandler handler = ResolveHandler(translatedHash);
	if (!handler) {
		Logger::Logf("shim: could not resolve handler for %s (%016llX)", name, translatedHash);
		return false;
	}
	// Legacy handlers are direct functions; Enhanced ones are jmp thunks MinHook can't relocate.
	void* target = BuildEdition::IsLegacy()
	                   ? reinterpret_cast<void*>(handler)
	                   : ResolveThunk(reinterpret_cast<void*>(handler));
	if (MH_CreateHook(target, detour, trampoline) != MH_OK) {
		Logger::Logf("shim: MH_CreateHook(%s) failed", name);
		return false;
	}
	if (MH_EnableHook(target) != MH_OK) {
		Logger::Logf("shim: MH_EnableHook(%s) failed", name);
		return false;
	}
	Logger::Logf("shim: hooked %s @ %p", name, target);
	return true;
}

} // namespace NativeHook
