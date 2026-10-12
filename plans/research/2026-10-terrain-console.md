# Terrain editing through World Edit Commands (option C), analysis

Status: analysed from the installed mod's decompiled code (WorldEditCommands.dll read from the sandbox client). The wrapper is designed but not built. Nothing here has run.

## Question

World Edit Commands (WEC) adds a `terrain` console command. Can ValheimMCP offer it safely, so a level can be exact and leave no objects behind?

## What the command does (decompiled `TerrainCommand`, `Terrain`, `TerrainParameters`)

- It is WEC's, not vanilla: the vanilla console has no `terrain` command (only `optterrain`). It is registered through ServerDevcommands' `Helper.Command`.
- It edits the `TerrainComp` arrays of every compiler in range directly (`m_modifiedHeight`, `m_levelDelta`, `m_smoothDelta`, `m_modifiedPaint`, `m_paintMask`), then `m_operations++`, `compiler.Save(false)` and `m_hmap.Poke`. So every change lands in the zone's TCData, exactly where `terrain_paint` snapshots. Undo by restoring the TCData bytes is therefore complete for everything except `reset` (below). The heights change at once, so a before and after TCData diff works in the same call (unlike the legacy `digg` strokes).
- Syntax is `terrain from=x,z circle=r | rect=w,d [angle=0] level=y | raise=m | lower=m  [paint=name] [smooth=s]`. `level=y` is an absolute height. Also `reset`, `void`, `slope`, `min`, `max`, `chance`, `blockcheck`, `offset`, `within`, `id`, `ignore`.
- `reset` also destroys every older terrain modifier object within radius + 1 (`ZNetScene.Destroy`). Undo cannot recreate those, so a wrapper must not offer it. `void` makes holes; not offered either.
- It checks nothing about guard stones, no-build locations or built pieces, and it keeps its own undo history in memory only.

## Safe wrapper (designed)

A tool `terrain_wec` that builds the command line itself from validated numbers and a fixed paint list, never from caller text:
1. Refuses unless the console's `terrain` handler belongs to the assembly `WorldEditCommands` (the command table is a plain dictionary any mod can write to).
2. Offers only circle or axis-aligned rect, `level` or `raise`, `paint` from the list, and `smooth` 0 to 20. Limits: 40 m radius, 80 x 80 m rect, level 30.4 to 2000 m.
3. Same refusals as `terrain_paint`: enabled guard stone area, no-build location, no loaded heightmap, built pieces on the area for height changes unless `allowPieces`.
4. Snapshots the TCData of every zone the area plus smooth can reach, journals a kind `terrain` entry (the existing `TerrainEdit.Undo` restores it), runs the line, diffs TCData and reports it.
5. Runs the line through the core plugin's own `ConsoleBridge.Run`, so `commands.allow` / `deny` still apply and `terrain` stays an explicit entry the user controls. It does not widen any list.

Why it is worth building: `terrain_level` (decision 0006) has to use the older `digg` prefabs in this game, which leave persistent objects and cannot level a vertex past what a stroke reaches, and its edge is a cliff. WEC levels exactly with a smoothed edge and no objects.

## Why it is not built yet

The session's automatic permission check refused the edit that adds the call into the core plugin's `ConsoleBridge` by reflection, as weakening a security setting (the command allow list). The wrapper does not change the allow list, but the call path reaches the console, so it needs the owner's explicit go-ahead. See `plans/next-up.md`.

## Open

- Whether `terrain` needs cheats (`devcommands`) enabled at the moment of the call.
- Whether WEC is installed on the dedicated server; the wrapper is client-only until that is known.
- The live config's `commands.allow` already lists `terrain` (example client config). Confirm on the sandbox before relying on it.
