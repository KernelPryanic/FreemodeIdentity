#pragma once

#include <cstdint>

// Resolves the global ped-decoration array base by scanning the live (decrypted) .text — the one
// thing C# can't do on Enhanced (encrypted .text). See decoration.cpp. C# reads the result from
// the shared block; a 0 just means tattoos are skipped this snapshot.
namespace Decoration {

// The decoration array base, or 0 if the pattern didn't match / the slot is null (C# skips tattoos).
uintptr_t ResolveArrayBase();

} // namespace Decoration
