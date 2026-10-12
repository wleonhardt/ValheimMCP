# Terrain editing through World Edit Commands (option C), analysis

Status: built as `terrain_wec` and verified on the sandbox client (2026-10-12). The mod's code was read from the installed WorldEditCommands.dll.

## Question

World Edit Commands (WEC) adds a `terrain` console command. Can ValheimMCP offer it safely, so a level can be exact and leave no objects behind?

## What the command does (decompiled `TerrainCommand`, `Terrain`, `TerrainParameters`)

- It is WEC's, not vanilla: the vanilla console has no `terrain` command (only `optterrain`). It is registered through ServerDevcommands' `Helper.Command`.
- It edits the `TerrainComp` arrays of every compiler in range directly (`m_modifiedHeight`, `m_levelDelta`, `m_smoothDelta`, `m_modifiedPaint`, `m_paintMask`), then `m_operations++`, `compiler.Save(false)` and `m_hmap.Poke`. So every change lands in the zone's TCData, exactly where `terrain_paint` snapshots. Undo by restoring the TCData bytes is therefore complete for everything except `reset` (below). The heights change at once, so a before and after TCData diff works in the same call (unlike the legacy `digg` strokes).
- Syntax is `terrain from=x,z circle=r | rect=w,d [angle=0] level=y | raise=m | lower=m  [paint=name] [smooth=s]`. `level=y` is an absolute height. Also `reset`, `void`, `slope`, `min`, `max`, `chance`, `blockcheck`, `offset`, `within`, `id`, `ignore`.
- `reset` also destroys every older terrain modifier object within radius + 1 (`ZNetScene.Destroy`). Undo cannot recreate those, so a wrapper must not offer it. `void` makes holes; not offered either.
- It checks nothing about guard stones, no-build locations or built pieces, and it keeps its own undo history in memory only.

## Safe wrapper (built: `terrain_wec`)

The tool builds the command line itself from validated numbers and a fixed paint list, never from caller text:
1. Refuses unless the console's `terrain` handler belongs to the assembly `WorldEditCommands` (the command table is a plain dictionary any mod can write to).
2. Offers only circle or axis-aligned rect, `level` or `raise`, `paint` from the list, and `smooth` 0 to 20. Limits: 40 m radius, 80 x 80 m rect, level 30.4 to 2000 m.
3. Same refusals as `terrain_paint`: enabled guard stone area, no-build location, no loaded heightmap, built pieces on the area for height changes unless `allowPieces`.
4. Snapshots the TCData of every zone the area plus smooth can reach, journals a kind `terrain` entry (the existing `TerrainEdit.Undo` restores it), runs the line, diffs TCData and reports it.
5. Runs the line through the core plugin's own `ConsoleBridge.Run`, so `commands.allow` / `deny` still apply and `terrain` stays an explicit entry the user controls. It does not widen any list.

Why it is worth building: `terrain_level` (decision 0006) has to use the older `digg` prefabs in this game, which leave persistent objects and cannot level a vertex past what a stroke reaches, and its edge is a cliff. WEC levels exactly with a smoothed edge and no objects.

## Findings from building it

- `Helper.Command` (ServerDevcommands) registers the command, so the handler in the console table is a lambda from ServerDevcommands whose closure holds the WEC delegate. The source check walks the closure up to three levels for a delegate from the `WorldEditCommands` assembly.
- A rectangle takes half extents: `rect=4,4` is an 8 x 8 m area. The tool takes full width and depth and sends the halves.
- `smooth` is a fraction of the shape (0 to 1), not metres: nodes closer to the edge than that fraction get a linear fade from no change to full change, inside the shape. The tool takes metres and divides by the smaller half extent. A value over 1 would make even the centre partial.
- `level` is absolute and adds to the node's level delta, so, from the code, a second call to the same height changes nothing (not tested).
- The heights change at once (no one to two second wait as with the older `digg` strokes), so the before and after TCData diff is in the tool's reply.

## Open

- WEC is not installed on the dedicated server (its console has no `terrain` command), so the tool is client-only; on the server it answers that the command is missing.
- Whether the command needs cheats (`devcommands`) enabled: it ran on the sandbox client with the existing session state, `consoleOutput` was empty.
- Pieces and wards are checked by this tool, not by the command, and the paint list excludes the lava colours.
