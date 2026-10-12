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

Not done (option C, calling the World Edit Commands `terrain` console command): see `plans/research/2026-10-terrain-console.md`.
