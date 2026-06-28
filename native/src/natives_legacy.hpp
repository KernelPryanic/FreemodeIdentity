#pragma once

#include "rage.hpp"

#include <cstdint>

// Resolves engine native-handler function pointers on classic/Legacy GTA5.exe by walking the
// obfuscated scrNativeRegistration table directly (see natives_legacy.cpp). The table is keyed
// by build-TRANSLATED hashes, so callers pass the translated hash, not the stable one. The
// Enhanced counterpart (Natives::, natives.cpp) uses a different mechanism; the shim picks one
// at runtime by detected edition.
namespace NativesLegacy {

// Locate the registration table via pattern scan. Returns false (fail-safe) if missing.
bool Init();

// Resolve one build-translated native hash to its handler. nullptr if unresolved.
rage::scrNativeHandler GetHandler(uint64_t translatedHash);

} // namespace NativesLegacy
