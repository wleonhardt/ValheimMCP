# Second decompile review (2026-10-11)

Three read-only reviewers read the decompiled game (assembly_valheim.dll and assembly_utils.dll, ilspy) against our code: placement rules, game systems we do not expose, and the constants and reflection targets. Their reports came back as text; this file keeps what was acted on and what was left. Everything below comes from reading code. Nothing ran in the game.

## Done (commit "Decompile review")

- `world_rules`: server options from the global keys, rates as multipliers, the death rule from `Player.CreateTombStone` (verified), portals blocked from `TeleportWorld.Teleport`. Pure `WorldRules`.
- `plan_check`: an overlap error only when the piece being placed has `m_noClipping` (the game tests only the ghost); fire-blocked reads the nearest terrain vertex like `Heightmap.GetHeight`; tilt and water are not judged for a piece standing above the terrain (the game reads the first surface under the piece); world pieces keep their ZDO `scale`; a piece with `m_noSupportWear == false` keeps full support and carries others; `m_forceCorrectCOMCalculation` read; `NoBuildingFall` turns "unsupported" into a warning; a ward being placed is checked with its own radius (also in `plan_apply`); snap seams (`snap-seam` rule, pure `SnapSeams`, snap points read from the prefab).
- `SupportModel.TouchesGround`: the footprint is sampled in the box's own axes and the test is lowest-ground <= top and highest-ground >= bottom.
- `menu_join`: the `auto` mode is gone. `FejdStartup.AutoJoinServer` takes a PlayFab join code and calls `Application.Quit()` when it cannot resolve one; a host:port is not a join code.
- Walker: default agent `HumanoidNoSwim`; no hop or run-up while encumbered; no rest while swimming off the ground (no regeneration there).
- Bill: an "any one ingredient" recipe takes the first ingredient in recipe order that the stock covers (`Player.GetFirstRequiredItem`).
- `smelters`: `catchUpCapSeconds` (3600; `Smelter.UpdateSmelter`), and the note that a windmill's output is not read.
- `PlanOrder` comment: the support sweep waits 30 s after a piece appears, not a few seconds.

## Left, in order of value

1. Cover model for plan_check (beds, stations, smelters need a roof and cover): needs the 17-ray model from decision 0003 as a pure function, then a live comparison with `bed_check`.
2. Systems readers: `net_peers` (server), `producers` (fermenter, beehive, sap collector), `loot_near` (floor items and the 3600 s despawn), `harvest_near` (pickables), `active_area`, `absence_forecast`, `wear_risk`, `spawners_near`, `resource_nodes`. Details and ZDO keys are in the reviewer's list kept below.
3. Non-box colliders: the game uses the world AABB of the rotated collider plus 0.3 m; we rotate the prefab-frame bounds.
4. Walker: stamina test is a fraction (0.15) where the game tests an absolute 10; nav agent radius 0.4 against our capsule 0.49; the player is a Rigidbody with a capsule, so `StepUp 0.45` is not a game number (needs a live probe before it changes).
5. `m_onlyInBiome`, `m_blockRadius`, `m_mustConnectTo`, smoke-blocked fires, biome wear (rain, ash, lava, deep north), `m_nonPlayer` damage and NG+ flat damage in `Combat`, plant status inside a shield dome, night skip needs "not already skipping" and 10 s since the last sleep, spawn rule conditions (biome area, alt biomes, persistent events).
6. Live checks still open: `Game.m_*Rate` on a server; the prefab `m_environmentDuration` and `Game.m_ashDamage`; `Cover` on server colliders; whether a shut door is a wall in the nav mesh.

## Corrections to our notes

- Tool-tier items (`m_toolTier >= 1000`) are not teleportable in vanilla (`Inventory.IsTeleportable`); the synthesis was wrong to call it modded.
- Dropped items are safe from despawn only above y 28 inside a PlayerBase area (`ItemDrop.IsInsideBase`).
- Walker constants that match the decompile: slide angle 38, run and jump stamina 10, encumbered speed. The support material table, 0.15 contact growth and the 100 degree rule all match.
- Every reflection target we use still exists with the shape we assume (checked list: Character, Player, FejdStartup, ZNet, Door, EnvMan, ZNetScene, ZDOMan, ZoneSystem, Chat, Cover, SE_Rested, Heightmap, WearNTear, Terminal).

## Systems list kept from the reviewer (unverified beyond the quoted lines)

- Fermenter ZDO `Content`, `StartTime`; ready after `m_fermentationDuration` (2400 default); a cover under 0.7 or no roof resets the timer.
- Beehive ZDO `level` (max 4), `product`, `lastTime`; sap collector `level`, `product`, `lastTime`.
- Floor items: `spawntime`, `itemData` (byte array), `piece`; destroyed at 3600 s unless inside a base above y 28, a player within 25 m, in tar, or a piece.
- Pickable: `picked`, `picked_time` (only when it respawns), `enabled`; checked every 60 s.
- `ZNetScene.PointInsideActiveArea`: Chebyshev distance of 1.5 zones of 64 m (plus a Euclidean limit for the non-classic setting).
- MineRock5 ZDO `health` is a base64 `ZPackage`: an int count, one float per hit area.
