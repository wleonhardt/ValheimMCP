# 0005 Walker limits, measured live (2026-10-12)

The walker's step, hop and slope numbers were assumptions taken from another project. `python/live/walk_probe.py` builds floating wooden platforms (so the terrain does not matter) and walks the sandbox character over them with the walker. Results, world AxGardDeepNorth, sandbox character not encumbered:

| What | Passes | Stops | Constant |
|---|---|---|---|
| Lip, walking | 0.37 m | 0.38 m | `StepUp` 0.37 (was 0.45) |
| Lip, running | 0.5 m | 0.6 m | `RunStepUp` 0.5 (new) |
| Hop up a ledge (5 m run-up on the flat) | 1.3 to 1.4 m every time; 1.5 to 1.7 in one run and not in the next | 1.8 m | `HopMax` 1.4 (was 1.2), `HopMin` 0.38 (was 0.40) |
| Slope, walking | 37 degrees | 38 | `SlideDeg` 38 (confirmed) |
| Slope, running | 65 degrees | 70 | `RunClimbMaxDeg` 65 (was 60) |

The player is a rigidbody with a capsule and no step offset (decompile): the lip limit comes from the contact normal, which is why running (which pushes harder) climbs more. The nav agent's own climb is 0.3. Two traps found on the way: the walker's `RiseAhead` probe only sees tops up to 1.5 m, so `hopMax` larger than 1.3 lengthens it; and an encumbered character never hops, so a probe on an overloaded character reads as "cannot hop" (the character file is saved whenever the world is saved, so items picked up for one test persist: restore `Developer_backup_auto-*.fch` from `characters_local` to clear them).
