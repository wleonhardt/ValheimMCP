# 0006 Terrain editing (2026-10-12)

Earlier decisions and the README said "no terrain-editing tools by design". That is reopened for two tools, with the safeguards below, because building plans depend on the ground and the rest of the pipeline (`level_check`, `plan_check`, `terrain_grid`) already models it.

How the game edits terrain (decompiled `TerrainOp`, `TerrainComp`, `Attack.SpawnOnHitTerrain`): a hoe stroke instantiates a terrain-op prefab (`ObjectDB.m_terrainOps`: `cultivate_v2`, `mud_road_v2`, `paved_road_v2`, `raise_v2`, `digg_v3`, and the older `TerrainModifier` prefabs `digg`, `digg_v2`, `raise`, `path` ...) at a point. Its `Awake` hands the op to the `TerrainComp` ("_TerrainCompiler" ZDO) of every zone it touches; the owner applies it (`DoOperation`) and saves the zone's whole terrain delta as the compressed byte array "TCData" in that ZDO. Other clients and the save follow the ZDO. The RPC carries only the prefab name hash, so only registered ops can be applied remotely: no custom settings.

What the tools do and do not do:
- They instantiate the same registered op prefabs the player's tools use, at points the tool computes, so the result is the game's own. No height or paint data is written by hand.
- Before any op, the whole TCData of every zone the edit can reach is saved to `hubner-ext/terrain/` and a journal entry (kind `terrain`) names the files. `undo` and `undo_group` write the bytes back into the ZDOs (a missing compiler is restored to "no edits"). Undo works on the server without any zone loaded.
- Write tools: behind the write gate (`tools.write`, the server flag file), `dry` honoured (a dry run reports points, zones, refusals and, for a pad, the `level_check` figures and the pieces it would disturb), journaled, `Plans.Guard`.
- Refused: any point inside an enabled guard stone's area (needs `force` + `forceConfirm`), inside a known no-build location, in a zone the process has no heightmap for (a client near it, or a virtual peer on the server).
- Limits: 300 ops and a 40 m radius per call; a level pad up to 40 x 40 m; the game's own limits (8 m from the base height) apply.
- `terrain_level` also refuses when player-built or plan-tagged pieces stand on the pad's ground (they would be left floating or buried) unless `allowPieces` is passed; the pieces' ground contact is judged with the support model.

Found live (2026-10-11, client, scratch spot) and changed the design:
- Two kinds of prefab exist. The `TerrainOp` ones (`cultivate_v2`, `mud_road_v2`, `paved_road_v2`, `raise_v2`, `digg_v3` ...) write into TCData, and the snapshot-and-restore design above is right for them: a paint of 91 paint nodes and 45 height nodes was undone to the same heights, and a second paint gave identical counts. The older `TerrainModifier` prefabs (`digg`, `digg_v2`, `raise`) are the only ones that level, and they do not touch TCData: each stroke leaves a persistent object (a ZDO) in the world and the heightmap applies it. The first `terrain_level` build used one and left 25 `digg` objects that a TCData restore could not remove. They were removed with a temporary tool and the terrain came back to the heights measured before.
- So `terrain_level` records the position of every stroke in the journal entry (key `p:<prefab>@x,y,z`, because ZDO ids do not survive a reload) and undo destroys the objects at exactly those positions. A new modifier also removes older modifiers within a quarter of its radius (`TerrainModifier.RemoveOthers`) and undo cannot bring those back, so `terrain_level` refuses when an older modifier object stands on the pad or within the stroke radius of it.
- The stroke plan comes from `PadPlanner` (the `TerrainComp` maths). Measured against the real `digg` strokes: a 9 x 9 pad planned with 25 strokes at spacing 2 gave all 81 vertices exactly at the target. The pad edge is a vertical step (no apron), as `level_check` reports in `edgeStep`.
- The heightmap rebuilds one to two seconds after the strokes, so the tool does not read the heights back itself: use `terrain_grid` afterwards.
- `digg` and `digg_v2` also paint the ground (cleared dirt); the result says `alsoPaints`.

Not done (option C, calling the World Edit Commands `terrain` console command): see `plans/research/2026-10-terrain-console.md`.
