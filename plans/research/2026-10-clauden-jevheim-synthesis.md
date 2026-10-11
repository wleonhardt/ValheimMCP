# ClaudeNJevHeim: synthesis of the 12 module reviews

Date: 2026-10-11. Source: twelve subagent reports in `plans/research/modules-2026-10/` (one per package of `2026-10-clauden-jevheim-inspection-plan.md`), on the friend's clone at commit 11dc174. The friend's repository has no licence: ideas and game facts only, no code was copied.

## How far to trust this

- Every game fact comes from the friend's own decompile notes (1.0.16 / 1.0.17), his live measurements, or wiki figures. **None was checked against our decompile.** Before an idea is built, its fact is checked with ilspycmd (set up earlier in the session; `ext/lib` holds only stubs). A contradicted fact is dropped.
- Tags used below: `V` verified in our code by grep this session, `R` reported by a reviewer from reading our code (not yet verified), `F` fact to check in the decompile first.
- The reviewers disagreed with each other in places (day/night fractions, swim drain, creature opening doors). Those are listed under "Check first".

## A. Defects and gaps in our own code

Found while comparing. Fix these first: they are cheaper than new features and each has a test.

| # | Finding | Status | Where |
|---|---|---|---|
| A1 | `bed_check.canSleep` is cover and roof only. The game also needs a Heat area at the bed (`Bed.CheckFire`), no Wet status, the time window, and no sensed enemy; claiming a bed needs only the exposure check. | R, 5 reviewers | `ext/Geo.cs` |
| A2 | `Neighbours.LocationAt` passes `noBuild=true` and override 0 for every location, so `noBuildAdvice` over-reports. Read `m_noBuild` and `m_noBuildRadiusOverride` from the location prefab. | V (`ext/Neighbours.cs:80`) | `ext/Neighbours.cs`, `Rules.cs` |
| A3 | `Verify.Plan` accepts yaw+180 for every prefab, so a flipped faced piece passes. | V (`ext/Shared.cs:413`) | `ext/Shared.cs` |
| A4 | `plan_apply` item without `y` spawns at y 0 while `plan_check` uses the ground; unknown item keys (typo `rotY`) are silently dropped. | V for `y` default (`ext/Plans.cs:294`) | `ext/Plans.cs` |
| A5 | `paint_at` reads `GetPixelBilinear`; the game tests the nearest pixel, channel > 0.5. "Cultivated?" can be wrong by a metre. | V (`ext/Geo.cs:292`) | `ext/Geo.cs` |
| A6 | `portals` text says "Every portal in the world". A client holds only portals of sectors it has been near. | V (`ext/Shared.cs:143`) | `ext/Shared.cs` |
| A7 | `Env` time/weather hold is not released on hot reload: `Ext.Unload` calls no `Env` release and the new load starts with `_timeHeld=false`. | V (no Env call on unload) | `ext/Env.cs`, `ext/Shared.cs` |
| A8 | Walker arrival and last-metre aim use x,z only: a stand 5.5 m below the bed counts as arrived and the view spins. Arrival needs a height check. | R | `ext/Control.cs`, `WalkRules.cs` |
| A9 | Walker `RiseAhead` has no slope correction (a plain 30 degree bank reads as a 0.58 rise) and its mask `~0` counts saplings, drops, tame animals as ledges. Hop can win over RunUp on a 49 degree bank. | R | `ext/Control.cs` |
| A10 | Walker side step and `HazardAhead` ignore trigger colliders, so a portal trigger is invisible and can teleport the body; `walk_check` also ignores triggers, treats every collider under a `Door` as passable (windows, locked doors) and excludes doors from ground (a lowered drawbridge reads "no ground"). | R | `ext/Control.cs`, `ClientTools.cs` |
| A11 | Walker stall `Reroute` uses the `Humanoid` agent (swims, steep slopes) for the whole remaining route. | R | `ext/Control.cs` |
| A12 | `WalkFacts.StepUp = 0.45` and `HopMax = 1.2` came from the friend's constants that his own attributes mark unmeasured / planning figures. Treat as assumptions; document, and measure (idea D4). | V (`WalkRules.cs:11,14`) | `ext/WalkRules.cs` |
| A13 | Walker has no encumbrance rule: encumbered at 0 stamina cannot move and never regenerates, so the stall ladder `Rest` loops until give-up. | R | `ext/Control.cs` |
| A14 | `plan_check` ground is the generator height (no edits, own-biome base, +-1 m normal); the game uses the heightmap collider triangles and a corner-biome blend as base. | R, documented in `groundNote` | `ext/PlanCheck.cs` |
| A15 | `plan_check` treats rocks and trees as nothing; a piece resting on a boulder is reported unsupported although the game grounds it. | R | `ext/PlanCheck.cs` |
| A16 | `plan_check` takes every non-trigger collider, including nav-only ones (pathblocker, 0.28 m error on a brazier), uses `col.bounds` for mesh colliders, and errors on a `NoClipping` overlap from 0.05 m while the game allows 0.2 m. | R | `ext/PlanCheck.cs`, `PlanRules.cs` |
| A17 | `FireClearance`: a cauldron or cooking station standing on a hearth overlaps the fire box and is a hard error. `IsStation` makes it hard. Riders on the host must be exempt. The 1 m clearance itself is a heuristic, not a game rule: make it a warning. | R | `ext/PlanRules.cs` |
| A18 | `PlanCheck.Geo` classifies by name: `piece_brazierfloor01` contains "floor", `piece_walltorch` contains "wall". Classify by component and snap data. | R | `ext/PlanCheck.cs` |
| A19 | `PieceGeo.NotOnWood/NotOnFloor/GroundOnly` are loaded and never used by any rule. | R | `ext/PlanRules.cs` |
| A20 | `weak` threshold is a flat 1.5 x min; per material it is 1.67 wood, 1.5 core wood, 1.18 iron, 3.0 grausten, 1.33 stone/marble. | R | `ext/PlanCheck.cs` |
| A21 | `fires.burning = fuel > 0`; the game also needs not blocked above, not wet, not submerged, not switched off (`m_canTurnOff`), and plain E toggles a switchable fire off. | R (+ `Neighbours.cs:205`) | `ext/Neighbours.cs` |
| A22 | Journal has no world key: after joining another world, `Identity.Find` could match an unrelated object by prefab and position. | R | `ext/Common.cs` |
| A23 | Chat: speaker id comes from the RPC payload (may be forgeable); name-only trust is spoofable (the real friend offline, an impostor trusted); `chat_tail` rows carry the platform `id` while `players` hides hosts; names are not run through `Hints`; injections split across two rows are missed. | R | `ext/Rules.cs`, `Audit.cs` |
| A24 | `world_state` prints `m_position` for every list entry with no source or age (the list holds a real position only for players who share it). | R | `ext/Shared.cs:243` |
| A25 | `Physics.RaycastAll` allocates in `Control.cs` lines 500 and 512; check whether they run every tick. | V (present), R (per tick) | `ext/Control.cs` |
| A26 | `Neighbours.Wards()`: confirm Dvergr wards report `enabled` (the ZDO flag may be absent). | R | `ext/Neighbours.cs` |

## B. Adoption candidates, ranked

Each is a work item. "Pure" means a game-free class under `ext/` that the unit tests link. Effort S/M/L. Source packages in brackets.

### Tier 1: small, high value, mostly pure (do next, one commit each)

1. **Fix A1-A7, A17-A20** as a batch of small corrections with tests. S each. [p01, p05, p08, p09, p10, p12]
2. **Hard/soft rule registry**: every `plan_check` problem and every safety refusal gets `{id, origin: hard|ghost|taste, source, code, numbers}`; add a `plan_rules` list tool. Taste rules are warnings only. `PlanProblem` gains `Code`, `Hard`, `Source`. S-M. [p06, p08, p09]
3. **Removal safety**: run `SupportModel` with and without the pieces a delete, sweep or `deleteExtra` would remove; refuse or report "N pieces lose support". S-M, pure. [p09]
4. **plan_check station and placement-status rules** with the game's status names (NoBuildZone, PrivateZone, MoreSpace, ExtensionMissingStation, WrongBiome, NeedCultivated, NotInDungeon, notOnWood, notOnFloor, inCeilingOnly): call `LocationRules` and `Ownership.WardCovers` per planned item; extension distance and spacing; station build range measured flat from the builder. M, pure. [p03, p08, p09, p12]
5. **Fire/cover rules in plan_check**: blocked-above ray, roofed-or-wet, station roof (0.7) and bed exposure (0.8) via one pure 17-ray `CoverModel`. M. [p05, p08, p09, p12]
6. **Plan format hardening**: report `unknownKeys`, require `y` unless `seat:'ground'`, `schema` tag, finite `rot`/`yaw`, global-closest matcher for `verify_plan` with symmetry-aware yaw and a `blocked` class. S-M, pure. [p09]
7. **Support-ordered spawning** in the sliced `plan_apply` (grounded first, then by neighbour), so a cancelled job never leaves unsupported pieces for the owner's sweep. S-M. [p09]
8. **Chat hardening round 2** (pure `ChatRules`): id from the connection not the payload (`id_mismatch`), name trust pinned to a first-seen id, hints on speaker name and across split lines, tool-name hint, wider manipulation patterns, flood guard, echo tag, bot-to-bot loop budget, `SayVerdict` output guard, `to` addressing tag. Plus a corpus with a holdout split. S each. [p07, p10]
9. **Digest mode** for list readers (`Band`, `Count`, `Readable`, `Group`, budget golden tests). S-M, pure. [p06]
10. **Log line collapse** in `log_tail` / `get_log` (digits folded, `x95, last 12 s ago`). S, pure. [p06]
11. **`tool_stats`** built-in (calls, refusals, errors, p50/p95/max ms, slow slices) and slow-slice attribution in `MainWatch`. S-M. [p06]
12. **World key** on journal, snapshots and ledger (`WorldKey` pure, per-world folder); undo `NotFound` split into gone / unloaded / ambiguous. S-M. [p06, p11]

### Tier 2: new readers (server-capable where noted)

13. **Recipes and bill of materials**: `recipe_info`, `missing_for` / `can_craft`, `plan_materials` (pieces, stations, free flags), `item_info`, `carry_check`, plan tier. Pure `Bill` over a recipe table. M. [p03, p06, p08, p09]
14. **`station_levels` / `stations`** reader: level from linked extensions, usability (roof, fire), build range, repair stations. M, server via ZDO + prefab. [p01, p03, p09]
15. **`smelters`** reader and forecast; `cookers`; upkeep forecast (fuel per hour for the base). M, pure maths. [p01, p03, p12]
16. **`creature_info`, `threats_near`, `biome_threats`, `raid_state`** and a pure `CombatRules` (armour, resistance, star factor, contact time, hits-to-die). M. [p02]
17. **Husbandry blockers** (why a tame cannot breed, egg state, species facts); **`farm_state`** and **`plant_check`** (grow space, sun, biome, cultivated). M. [p01, p04, p11]
18. **`base_value` / `base_areas`**: PlayerBase EffectArea count for raid eligibility and spawn-proofing. S-M. [p02, p11]
19. **`world_clock` / sleep rules**: day fraction (rescaled), time to night, sleep window, who blocks the skip; server-capable. S, pure. [p01, p02, p06, p11]
20. **Terrain**: `level_check` (hoe op model, +-8 / +-1 clamps), `terrain_deltas`, `baseHeight`, triangle-accurate ground and slide, `approach_check`, cultivated/cleared verdicts, ground-float reader. M. [p04, p08]
21. **`situation`** one-call digest (word fields, bands). M. [p06]
22. `portals` facing, partner, exit and `portal_check {carried}`; portal-trigger spacing rule in `plan_check`. S. [p05, p11, p12]
23. `explored_at`, `containers` audit / kind, `loot_near`, `piece_health`, `players` extras (carry weight, hp, inBed already). S each. [p01, p03, p06, p11]

### Tier 3: large or speculative

- Reachability grid in `plan_check` (sealed pockets, use access, door approach, trap/pit report). L. [p05, p09, p11]. Biggest single value left; needs `ReachRules` over `Obb`.
- Smoke / vent simulation. L. [p09, p12]
- `site_scan` ranked sites, `terrace_plan`, `pad_grade_plan`. L. [p04]
- `sea_route`, ship state, `sail_calc`. M-L, low priority. [p11, p12]
- Terrain write tool (`terrain_level`). L, feasibility unverified (TerrainComp ownership). [p04]
- Roof closure, comfort planner, wall-mount model, storey headroom on real undersides. M each. [p08, p09, p11]

### Tier 4: test infrastructure (see section D)

## C. Check first (conflicting or unverified facts)

Resolve each in the decompile before coding anything that depends on it.

- Night range: raw day fraction 0.15-0.85 rescaled to 0.25-0.75 vs the friend's `BiomeRisk` using 0.25..0.75 raw. Use `EnvMan.IsDay/IsNight` and `SpawnSystem`'s flags.
- Stamina: run drain 8 vs 10 per s, swim drain 5 vs 6 per s (2 at skill 100), base stamina 50 vs 75, `CanMove` false at 0 stamina when encumbered.
- `NoBuildCost` key: does it skip only the check or also consumption? Does the no-cost cheat consume on placement? Does `NoCraftCost` drop the station-level check? (his 1.0.16 notes vs older behaviour.)
- Armour order: armour applied once to the total after per-type resistance; resistance multipliers; star factor `1 + 0.5 (level - 1)` vs `2^(level-1)`.
- `m_requireOnlyOneIngredient` recipes; first enabled recipe rule; `Pickable` yield.
- Station range is measured flat from the player; extension link by pivot distance vs placement ray hit; comfort radius 10 m 3D strict.
- `Fireplace.CheckUnderTerrain`: does it test terrain or any collider above the pivot; wet and wind rules.
- `Heightmap.GetHeight` nearest vs bilinear (irrelevant while we use `GetGroundHeight`).
- Whether creatures open doors (50-75 % belief that they do not).
- Interact reach 3.5 vs 5, `Rested` base 300 vs 480, whether an unlit fire counts for `CheckFire`.
- Piece usage-tag fields (`Floor`, `Stairs`) may exist only in his build; check with `inspect_type Piece`.
- `Module.ResolveMember` on Mono (still open from 0.6.0).

## D. Test infrastructure to build

From p10. None of it needs the game except capture.

1. `python/live/accept.py`: a case table with verdicts pass / fail / skipped / blocked, `--list`, `--dry-run`, `--only`, a report; cases are the manual items of `plans/next-up.md`. A precondition that fails gives "skipped with a reason", never "failed". Assertions must be able to fail on an empty world (a case passes only against a known scenario).
2. `live_guard()` in `python/hubner.py`: refuse unless the endpoint is explicit, `ping.inGame` is true and the world is on an allow-list; every `python/live` script calls it.
3. `smoke.py` port (initialize, tools/list, notification without id returns nothing, unknown tool errors, build stamp equals the DLL's metadata).
4. `scene_capture` tool writing a `hubner.scene/1` fixture (objects with colliders, terrain grid, player, zdo session, dead ids) and a pure loader; a live bug becomes a fixture and a failing test. Schema notes are in `modules-2026-10/p10-live.md`.
5. Differential test of `SupportModel` against the game's recorded support values from captured structures (`stability_scan` already reads them).
6. Replay of walks over scenes (pure capsule-vs-boxes model, slide 38, run 60, step, door and portal triggers) in xunit; later.
7. `scripts/lint_docs.py` (links, backticked tool names registered or retired, every registered tool named in a doc) and `scripts/check.py` (one command running the seven checks of AGENTS.md).
8. Chat corpus with a holdout file never used while tuning.
9. Per-case live asserts: `ext_status` stalls, dropped calls and `status.journalError` are zero after every case; cleanup invariants (`plans`, `journal_groups` counts equal before and after).
10. Do not port: text-pin gates (regex on source statements; he deleted several), the section result cache and sharded workers, the game lease mutex, the Cecil IL audits.

## E. Do not adopt

- Jev decision code, Questions framers' model transport, `DecideClaude`, anything that needs a third-party service (decision 0002).
- The friend's attack windup table (his tag says unmeasured), the "targets us" heuristic for creatures another client owns, the bot's food/hunt/loot/expedition loops, the design generator and palette, work fronts, stands, scaffold bots, his `Teleportable` tool-tier rule (not vanilla as far as the reviewer knows).
- Numbers tagged `[W]` or `[U]` in his doc as hard checks (raid radii, per-station ranges, ship drafts other than the Karve). His design numbers (lip 0.15, station corridor, 4 m storey, walkway 1.5 m, 2 m use zone) are his taste: warnings at most.

## F. Mistakes in the friend's repo worth knowing (do not copy)

State files written with plain `File.WriteAllText` (a torn write loses all goals), event recognition by message text, bytes vs characters in log rotation, unbounded repeat table, a sconce height test that never reads its own parameter, a door symmetry check that silently skips every door but one, docs disagreeing on swim and run drain, presence-of-a-file as state, escape-hatch flags (`force`, `only`, `Admit`) as the root of regressions, nine hard-coded drive-letter paths. We have none of these in the same shape, but `dry` skipping the write gate once did (fixed in 0.5.0).

## G. Suggested order

1. Check-first facts for the items below in the decompile (one sitting, record results in `plans/decisions/0003-…`).
2. Tier 1 items 1-3, then 4-5, then 6-7 (plan side); 8-12 (host and chat side) in parallel commits.
3. Tier 4 items 1-3 (runner, guard, smoke) so the next live session is a script, not prose.
4. Tier 2 readers in the order 19, 13, 14, 16, 15, 17, 20.
5. Re-assess Tier 3 after a live session has tested the plan checks against real collapses.
