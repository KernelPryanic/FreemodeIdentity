#pragma once

#include <cstdint>

#include "rage.hpp"

// Shared MinHook plumbing for detouring a game native's HANDLER (the function the script VM calls
// for a native). SHVDN/C# can't hook these, so every shim detour goes through here. Both editions
// key on the SAME translated hash (crossmap column 27); only the handler RESOLUTION differs, which
// this hides. Kept separate from any one feature (wallet, skill-feed) because it's shim infrastructure.
namespace NativeHook {

// Resolve the running edition's handler for a translated native hash and MinHook-detour it. `detour`
// is the replacement handler; `*trampoline` receives the pointer to call the original. Returns false
// (and logs) on a resolve or hook failure. On Enhanced the handler is a jmp thunk MinHook can't
// relocate, so the leading jmp is followed to the real body first; on Legacy the handler is direct.
bool Install(uint64_t translatedHash, void* detour, void** trampoline, const char* name);

} // namespace NativeHook
