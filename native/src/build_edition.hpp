#pragma once

// Which GTA V edition this .asi is running under. One binary serves both; the native-handler
// resolution MECHANISM differs (Enhanced: InitNativeTables trick + jmp thunks; Legacy: classic
// scrNativeRegistration walk + direct handlers) — but BOTH key on the same translated hashes, so
// the shim detects the edition once and branches only the resolver. Mirrors the C# GameBuild
// detection (host module name GTA5_Enhanced.exe vs GTA5.exe).
namespace BuildEdition {

enum class Edition { Enhanced, Legacy };

// Detected once on first call (host module name). Cached thereafter.
Edition Current();

bool IsEnhanced();
bool IsLegacy();

const char* Name();

} // namespace BuildEdition
