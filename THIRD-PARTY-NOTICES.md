# Third-party notices

Freemode Identity itself is MIT licensed (see [LICENSE](LICENSE)). It also contains code
derived from the project below.

## ScriptHookVDotNet Enhanced

<https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced> — zlib licence, Copyright (C)
2015 crosire, kagikn and (C) 2025 Chiheb-Bacha. See that repository for the licence text.

Freemode Identity is built against this fork, and the following derive from its
`NativeMemory` implementation. They are adapted, not verbatim copies, and any error in
them is ours:

- `native/src/waypoint.cpp` — the Enhanced `WaypointInfoArray` entry patterns.
- `WaypointKeeper.cs` — the Legacy `WaypointInfoArray` start/end patterns and the entry
  layout (`modelHash` at +0x00, `blipHandle` at +0x04, stride 0x18).
- `Joaat.cs` — the `atStringHash` (Jenkins one-at-a-time) implementation, mirroring
  `StringHash.AtStringHash`.
