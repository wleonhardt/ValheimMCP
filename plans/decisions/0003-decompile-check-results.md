# 0003 Decompile check of the facts behind the ClaudeNJevHeim adoption list (2026-10-11)

## Context

The module reviews (`plans/research/2026-10-clauden-jevheim-synthesis.md`) rest on the friend's decompile notes. Before building on them, the points that Tier 1 depends on were checked in our own decompile of the installed game (assembly_valheim.dll, assembly_utils.dll, 2.5 MB; decompiled with ilspycmd into the session scratchpad). Results:

## Confirmed

- **Bed** (`Bed.Interact`): claiming an unowned bed needs only `CheckExposure` (roof, cover >= 0.8 at the spawn point). Sleeping in the current bed needs, in order: `EnvMan.CanSleep()`, `CheckEnemies` (`IsSensed`), `CheckExposure`, `CheckFire` (`EffectArea.IsPointInsideArea(bed transform, Heat)`), `CheckWet`. `bed_check.canSleep` was wrong.
- **Day**: `RescaleDayFraction` maps raw 0.15..0.85 to 0.25..0.75. `IsDay` is rescaled >= 0.25 and <= 0.75; `IsAfternoon` >= 0.5 and <= 0.75; `IsNight` <= 0.25 or >= 0.75. `CanSleep` = (afternoon or night) and now > wakeupTime + `m_sleepCooldownSeconds` (30). So the friend's `BiomeRisk` 0.25..0.75 is on the rescaled fraction and his doc's "20:24-03:36" is wrong. `m_dayLengthSec` is a field (default in code 1200, live servers report 1800): always read it, never hard-code.
- **Stamina**: `m_runStaminaDrain` 10; swim drain lerps `m_swimStaminaDrainMinSkill` 5 to `m_swimStaminaDrainMaxSkill` 2. The "8 per s" and "6 per s" figures in his reference doc are wrong.
- **Build costs**: the placement check is skipped when `m_noPlacementCost` (the nocost cheat) or the piece's `FreeBuildKey` global key is set; resources are consumed after a placement unless the `FreeBuildKey` global key is set. So the cheat skips only the check and still consumes; the key skips both. `NoWorkbench` skips only the station-in-range test. Station roof and fire checks are skipped for `NoCostCheat`.
- **Placement ghost** (`UpdatePlacementGhost`): `m_noClipping` pieces are invalid when `ComputePenetration` against a placement-mask collider within 10 m exceeds **0.2** (we used 0.05). `m_notOnWood` only when the supporting piece has WearNTear of material Wood or HardWood. `m_notOnTiltingSurface` normal.y < 0.8; `m_inCeilingOnly` normal.y > -0.5 is invalid; `m_notOnFloor` normal.y > 0.1 is invalid. Status order: the last failing check wins (NoBuildZone, PrivateZone, BlockedbyPlayer, WrongBiome, noClipping). `BlockedbyPlayer` is any `Character` within 30 m, not only players. An extension needs `FindClosestStationInRange(ray hit)`; two extensions closer than `m_spaceRequirement` give MoreSpace.
- **No-build location**: `m_noBuild && DistanceXZ(location, point) < (m_noBuildRadiusOverride > 0 ? override : GetMaxRadius())`, strict; `GetMaxRadius` is the exterior radius, and max(exterior, interior) only when `m_hasInterior`. `m_noBuild` defaults true but prefabs set it, so treating every location as no-build over-reports. Our `LocationRules` also ignores `m_hasInterior`.
- **Ward** (`PrivateArea.CheckAccess`): for a plain piece one covering ward with local access is enough; a ward piece needs access in every covering ward.
- **Fireplace**: `IsBurning` = not `m_blocked`, ZDO state == 1, not under water, and (fuel > 0 or infinite). `m_blocked` = terrain above `y + m_checkTerrainOffset` (0.2), or an upward ray of 0.5 m from `y + m_coverCheckOffset` (0.5) hits Default/static_solid/Default_small/piece/terrain, or the smoke spawner is blocked. Wet (rain with no roof, wind >= 0.8 with cover < 0.7) only switches the fire off when `m_canTurnOff`; it is not part of `IsBurning`.
- **Cover** (`assembly_utils`): 17 rays (1 up, 8 horizontal incl. diagonals, 8 upward diagonals), 30 m less the start offset, mask Default/static_solid/Default_small/piece/terrain/vehicle (no `piece_nonsolid`). The ray vectors are never normalised (`Normalize` runs on a struct copy), so a diagonal starts `0.5 x |v|` out (up to 0.87 m). `IsUnderRoof` is a 0.1 m sphere cast up 100 m; a hit on a collider tagged "leaky" does not count.
- **Comfort** (`SE_Rested`): 1, +1 in shelter; pieces within 10 m sorted by group, comfort descending, name descending; a piece is skipped when the previous piece in the sorted list (counted or not) has the same non-None group or the same name; Rested = 300 + 60 per level above 1.
- **Stations**: build range is horizontal (the point's y is set to the station's y), `< m_rangeBuild + extensionCount x m_extraRangePerLevel`. An extension links when pivot-to-pivot distance < its `m_maxStationDistance` and the station name matches; one per extension name unless `m_stack`. Level = 1 + extension count.
- **Heightmap**: `IsCultivated` reads the nearest paint pixel (`WorldToVertexMask`), g > 0.5; `IsCleared` shifts the point by -0.5 and tests r, g or b > 0.5. Our bilinear read was wrong.
- **Armour** (`HitData.DamageTypes.ApplyArmor`): over the total of blunt, slash, pierce, fire, frost, lightning, poison, spirit (not chop or pickaxe); `dmg - ac` when `ac < dmg/2`, else `clamp01(dmg/(4 ac)) x dmg`. Resistance multipliers 0.75, 0.5, 0.25, 1.25, 1.5, 2.
- **Chat**: `RPC_ChatMessage(sender, pos, type, UserInfo, text)`: `UserInfo` is built by the sending client (`UserInfo.GetLocalUser()`) and the routed sender id is written by the sender; `ZRoutedRpc.RPC_RoutedRPC` on the server does not rewrite either. Both are forgeable by a modified client. A server-side check can compare the routed sender id with the peer that owns the `ZRpc`.
- **World identity**: `ZNet.World` has `m_name` and `m_uid`.
- No door-opening code exists in `BaseAI` or `MonsterAI` (the friend's "creatures do not open doors" holds for these classes).

## Not resolved

- Day length default (1200 in code vs 1800 live): read at run time.
- Anything about boats, spawners, raids and creature stats: not needed for Tier 1; check when those items are taken.

## Decision

Build Tier 1 on the confirmed facts above. Where a rule depends on a placement surface (`notOnWood`, `notOnFloor`, `inCeilingOnly`), `plan_check` has no ghost ray, so those rules warn with the game's status name instead of failing the plan.

## Addendum (2026-10-11): session-only ZDO values

`ZDOVars.s_sessionHashes` (alert, haveTarget, inUse, support, velocities, lookTarget, noise, tiltrot) are session-only: `ZDOExtraData` never writes them to the world file. Consequences: `scene_capture` finds a recorded `support` only for pieces whose owner ran its support update in this game session (the area must have been loaded by a player or a virtual peer); `threats_near` sees `alert` / `haveTarget` only for creatures active now; `containers` in use are not remembered across a restart. The ZDO keys the extension reads were all checked against `ZDOVars` and the game's own `Get*("key")` calls: no misspelled key.
