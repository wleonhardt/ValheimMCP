# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).


## [Unreleased] - Hubner fork

### Cover model and base-keeping readers (0.14.0 extension)
Ported from the decompiled game and unit-tested; none of it has run in the game yet.
- `plan_check` judges cover: a bed (roof and 80% cover at its spawn point), a roofed crafting station (70%), a smelter (a roof), a fermenter barrel (roof and 70%), and a beehive (under 25% cover). It is a model of the game's 17 cover rays and its roof cast on the pieces' boxes: near the limit it can be a little off, so a close miss is a warning. Rules `bed-cover`, `station-cover`, `smelter-roof`, `fermenter-cover`, `beehive-cover`.
- New readers, all from the ZDO table and the prefabs, so they work on the dedicated server: `net_peers` (ping, quality, send queue), `active_area` (is a place simulated), `producers` (barrels, hives, sap collectors), `loot_near` (floor items and the one-hour despawn), `harvest_near` (pickables and their respawn), `resource_nodes` (mine rocks, trees, logs: what is left and which tool tier works), `spawners_near`, `wear_risk` (rain, ash, wrong biome, roof state), `absence_forecast` (what fires, smelters, barrels, hives, pickables and floor items do while nobody is there).

### Decompile review (0.13.0 extension)
Three reviewers read the decompiled game against our rules (record: `plans/research/2026-10-decompile-review.md`). Compile-checked and unit-tested only.
- New tool `world_rules`: the server options (global keys), rates as multipliers, what a death costs (tombstone, gear, bag, skills), whether portals are blocked now.
- `plan_check`: overlap is an error only when the piece being placed cannot clip (not when an existing chair is in the way); a fire's cover test reads the nearest terrain vertex; tilt and water are not judged for a piece standing on another piece; world trees and rocks use their stored scale; pieces that never wear from missing support (`m_noSupportWear` false) keep full support; `NoBuildingFall` worlds get a warning, not an error; a new guard stone is checked with its own radius (also in `plan_apply`); new `snap-seam` warning where the hammer would have snapped a piece to its neighbour.
- Ground contact of a piece is tested over its real footprint, so thin pieces on slopes are no longer reported as floating.
- `menu_join`: the `auto` mode is removed. The game's `AutoJoinServer` takes a join code and quits the game when it cannot resolve one.
- Walker: the default agent is `HumanoidNoSwim`; an encumbered character no longer tries to hop or run up; no resting while swimming off the ground.
- `plan_materials` / `missing_for`: an "any one ingredient" recipe takes the first ingredient in recipe order that the stock covers. `smelters` reports the 3600 s catch-up cap.

### Second batch of review fixes (0.12.2 extension)
Compile-checked and unit-tested only; none of it has run in the game.
- `walk_to`: `autoRun:false` keeps the walk on foot unless `run:true` or a steep bank; a tired caller's `run` drops to a walk under half stamina; a re-route after a stall keeps the remaining waypoints and the agent you chose; a portal trigger on the route stops the walk with `blocked` (`throughPortals:true` walks through on purpose); debug fly is cleared through the game's own toggle.
- Journal: a modify entry records where the object stands after the edit, so an untagged object can be found again after a restart; undoing a delete renumbers only the entries of the current world load.
- `clear_overlaps`: footprints of any radius (0..500 m) are matched, inputs must be finite and in the world; `plans`, `plan_stats` and the undo scan cover the same +-11000 m as `InWorld`. The no-build location check uses a bucketed table instead of a scan per item.
- `verify_plan`: `yawTol` exists (default 2), an item without prefab, x, y or z is an error instead of position 0, `extraPlayerBuilt` counts player-placed extras next to creator-0 ones. `paint_at` accepts `[x,z]` or `[x,y,z]`. `objects` builds a full record only for rows it returns.
- `time_set` answers with the time asked for (and says the game applies it next frame); time and weather release on their own timers; a debug time a person had set before is put back; both release on leaving the world, driven by a hook on `EnvMan.Update` instead of the walker's patches.
- `plan_remove` / `zdo_delete` collapse check stands on the terrain as it is now (edits included), like `plan_check`. The chat sent-log is locked.
- Live cases: `world_clock` skips a phase boundary; `level_check` rounds the spot to whole metres and tests reach on a 3 x 3 pad.

### Review fixes after the offline code review (0.9.1 core / 0.12.1 extension)
Three reviewers read the new code against the decompiled game (record: `plans/research/2026-10-offline-code-review.md`). Behaviour you may notice:
- **Chest contents were read with the wrong type**: a current world keeps them as a byte array. `containers`, `missing_for`, `plan_materials`, `fuel_forecast`, `neighbours`, `death_state` and the journal's chest contents now see real chests; `container_set` writes the key the game reads.
- `chat_tail`: the sender id is checked against the connection (`id_mismatch`), odd ids are dropped, invisible Unicode (Tag block, bidi isolates) is removed, `chat_send` refuses a position in public chat (whisper to a trusted online player only), canonicalises `to` and has a global budget.
- `plan_apply` and `plan_check`: a `y` that is not a number counts as missing; `seat:"ground"` uses the world generator's height when no terrain is loaded; error numbers are the caller's. `plan_remove` and `zdo_delete` refuse when their collapse check could not run.
- Undo refuses entries of another world for every kind; `world_save` works only on a server; `zdo_audit fix` no longer drops portals from the save.
- Readers: `bed_check` names `$msg_bedtooexposed`; creatures use level, not star, labels and count random weapon sets; cookers say whether they run; smelters say whether the fuel covers the queue.
- Walker: `stuckSeconds` works; a goal without y stays without it through `nav`; doors that consume their key are never pressed.

### 0.9.0 core / 0.12.0 extension (more readers from the ClaudeNJevHeim reviews)
All on the server and the client; each checked against the decompile first; none has run in the game yet.
- `plant_check` (biome, cultivated ground, heat, cold, shade, space, in the game's order) and `farm_state` (age, grow range, ready times, verdict).
- `raid_state` (running event, roll timer and chance, every event with its keys and whether they are set) and `threats_near` (ignore / watch / now from the synced alert and target flags, hardest hit after armour, seconds to contact).
- `level_check` (can level strokes bring a pad to a height: 8 m from the base at most, fill and cut, edge step, strokes per terrain tool by simulation) on a pure `TerrainGrid` that ports TerrainComp's level, raise and smooth.
- `cookers` (slot states, seconds to done and to burnt) and `fuel_forecast` (what fireplaces, smelters and cookers burn per hour against what they and the chests hold).
- `scene_capture` / `scene_get`: world fixtures with the support the game recorded, checked against our support model by `dotnet test`.

### 0.8.0 core / 0.11.0 extension (Tier 2 readers and Tier 4 test tooling of the ClaudeNJevHeim review)
New readers, all on the server and the client, all checked against the decompile first (nothing has run in the game yet):
- `world_clock`: the game's day fraction (raw 0.15..0.85 is rescaled to 0.25..0.75), seconds to dusk and dawn, the sleep window, who holds up the night skip (it needs EVERY character in bed).
- `recipe_info`, `item_info`, `missing_for`, `plan_materials`: every recipe (not only the first), upgrade costs and station levels, the recipe chain from what the chests hold (whole crafts, surplus reused), the bill of a plan with free pieces and stations.
- `stations`: level (1 + the extensions that count), build range, what the next level unlocks, extensions that count for nothing and why. `smelters`: queue, fuel and the presses the game still accepts, fuel needed, time to finish, why one stands still.
- `creature_info`: faction, health and damage by star, damage modifiers, senses, attacks, taming, drops, and what a hit does to a player of given armour. `base_value`: PlayerBase areas in reach of a point for raids.
- `husbandry_state` says why an animal is not breeding (hungry, alerted, population cap, ready partners) or when young are due.
- `plan_check` takes its ground from the terrain collider's triangles where the zone is loaded (edits included) and says where each height came from; the tilt is the triangle's normal.

Test tooling
- Scene fixtures: `scene_capture` / `scene_get` write a piece of the world as `hubner.scene/1` (prefab geometry once per prefab, objects, terrain vertex heights, the support and health the GAME recorded); `python/live/capture_scene.py` saves it into `tests/ValheimMCP.Tests/fixtures/scenes/`, where `dotnet test` runs our support model on it and compares it with the recorded values. A hand-built tower pins the known wood series (100, 73.75, 54.39).
- `python/live/accept.py` with 24 cases (`--list`, `--dry-run`, `--only`; pass / fail / skipped / blocked; acting cases clean up and are checked for dropped calls, stalls and journal errors) and `hubner.live_guard()` (endpoint explicit, game in a world, world on `HUBNER_LIVE_WORLDS`).
- `scripts/check.py` (all checks, `--changed`), `scripts/lint_docs.py` (links, paths and tool names in the documents), a chat corpus with a holdout set.

### 0.7.0 core / 0.10.0 extension (decompile check and Tier 1 of the ClaudeNJevHeim module reviews)
The facts behind these changes were checked in our own decompile first: `plans/decisions/0003-decompile-check-results.md`. None of the game-touching code has run in the game yet (see the live checklist in `plans/next-up.md`).

Behaviour you may notice
- `plan_check` follows the game: every problem carries `origin` (`hard` = the world enforces it after a spawn, `ghost` = only the hammer's placement check, `taste` = our preference, never an error) and the game's status `code`; `plan_rules` lists the rule book. An overlap of a piece that cannot clip is an error only past the game's 0.2 m (it was 0.05 m). Fire clearance is a warning. `weak` uses the per-material ratio (wood 1.67, core wood 1.5, iron 1.18, grausten 3.0, stone and marble 1.33). Colliders are filtered by the layers the game's checks use, so nav blockers no longer count, and a rock, tree or bush a piece touches gives it full support, as in the game.
- New `plan_check` rules: `fire-blocked` (a solid within 0.5 m above the fire, or terrain above it: the fire does not burn), `extension-no-station`, `extension-spacing`, `station-out-of-range`, `not-on-wood`, `no-build-zone` (exact: the location prefab's own `m_noBuild` and radius override), `private-zone`. `plan_check` also reports `unknownKeys` and `missingY`.
- `plan_apply` refuses an item with no `y` (it used to build at y 0, under the sea) unless it says `seat:"ground"`, reports `unknownKeys`, and builds lowest first (`order:"plan"` keeps the caller's order) so a slow sliced job never leaves a piece standing on nothing for the owner's support sweep to destroy.
- `plan_remove` and `zdo_delete` refuse a removal that would bring down pieces resting on the removed ones (`allowCollapse:true` overrides; `plan_remove` dry runs report `wouldCollapse`).
- `bed_check` follows `Bed.Interact`: spawn point, heat area (`$msg_bednofire`), the time window, owner; `canClaim` (exposure only), `canSleep` (exposure and heat), `sleepNow`, `firstFailure`.
- `paint_at` reads the nearest paint pixel like the game and gives `isCultivated` / `isCleared`. `fires` uses the on/off state and fuel item. `portals` says whether its list is complete (a joined client holds only portals near it). `verify_plan` reports `flipped180`.
- Walker: the last point counts as reached only within `arriveHeight` (default 2 m) of its y when one was given (`wrong_storey` ends a walk a storey off); ledge probes take the slope off and ignore saplings, drops and animals; a side step avoids a portal trigger or lit fire first; an encumbered character with no stamina gives up (it cannot regenerate) instead of resting for ever.
- Chat: hints for tool names, machine-shaped text, injections cut over two lines, odd speaker names, flooding; rows carry `ours` and `to`; a name on `hubner.trustedPlayers` without a platform id is `name_only` (still untrusted); `chat_send` refuses a repeated injection, a position for an untrusted listener and an echoed untrusted line, and allows 6 lines per listener per 10 minutes (`to`).
- Journal entries carry the world (`name#uid`): an entry of another world is not touched; an object not found in an area the client has not loaded is "cannot tell" and the entry is kept. The extension releases a held time or weather when it unloads.
- `log_tail` folds lines that differ only in numbers (`collapse:false` for the raw tail); `objects digest:true` gives kinds with counts and boxes.

New
- Core: `tool_stats` (calls, errors, threw, timeouts, slow calls, p50/p95/max handler time per tool).
- Extension: `plan_rules`.
- Research: `plans/research/2026-10-clauden-jevheim-synthesis.md` and the 12 module reports; `plans/decisions/0003-decompile-check-results.md`.

### 0.6.0 core / 0.9.0 extension (what the ClaudeNJevHeim review suggested)
Behaviour you may notice
- A JSON-RPC message without an id is a notification and is never executed (a `tools/call` without an id used to run).
- Undo after a restart works by what survives a restart. ZDO ids are valid for one world load only (the game renumbers every ZDO when it loads a save), so journal entries now carry the id of the world load and the plan identity (`hub_plan` + `hub_key`); an entry from an earlier run is found by plan tag, or for a plain spawn by prefab and position, and an ambiguous match is refused instead of guessed. A modify of an untagged object after a restart is reported as gone.
- `Plans.Protected` also refuses (without `force` + `forceConfirm`) a dead player's tombstone, anything inside an enabled guard stone's area, a container that holds items, and a piece of a build of creator-0 pieces (two creator-0 pieces within 6 m). `plan_apply` refuses to place or move a piece inside an enabled ward and counts pieces inside a known location's radius as `noBuildAdvice`.
- The walker yields to the human for 1.5 s after any input and stops its clocks meanwhile; it no longer turns the view every tick; it does not press ward-locked or keyed doors (`Door.Interact` returns true for those and opens nothing), only doors the route crosses; stalls follow a ladder (measured hop, run up banks of 38-60 degrees, rest, step aside, re-route, give up); a teleport ends the walk. `walk_check` uses the player's capsule (0.49 x 1.85, step 0.45, slide limit 38 degrees, `run:true` for 60).
- `chat_tail` rows are tagged `untrusted` (unless the speaker's platform id is in `hubner.trustedPlayers`) with `hints`, and the text is cleaned; `chat_send` allows one line per 6 s and no repeats within 5 minutes.
- `job_status` has a `code` (done, cancelled, error, crash, unloaded, bad_args, refused). A sliced job interrupted by an extension reload is resubmitted by the next load (same game process, under 2 minutes).

New
- `ext_status` (core): loaded extensions with build stamps, reload count (Mono cannot unload assemblies: the log warns at 50), dropped calls, stall reports. Every assembly carries `hubner.commit/branch/dirty/worktree`; untracked files count as dirty.
- Main-thread watchdog: work that runs longer than 2 s is logged with its name; a frame stall is logged too.
- Bind check: an extension built against a newer core is refused before the old one is touched. Queued calls of a reloaded extension are dropped and their callers fail at once.
- Tools: `plan_check` (structure model ported from WearNTear.UpdateSupport, overlaps with real collider boxes, tilt, water, yaw grid, fire clearance), `world_events`, `neighbours`, `locations_near`, `biome_at`, `fires`, `husbandry_state`, `death_state`, `cost_mode`, `job_cancel`; `players` reports inBed and dead.
- CI: silent-catch lint with a baseline, write-API allowlist, generated `docs/TOOLS.md`.

### 0.5.0 core / 0.8.0 extension (review fixes)
**Behaviour changes to know about**
- `run_command` and `POST /command` now need `tools.write: true` (they used to run whenever the listener was up). `tools.deny` also blocks the built-in tools.
- The plugin does not start when `valheimmcp.yml` cannot be read, or when `server.host` is not loopback and `server.token` is empty.
- `teleport` is a write tool. God mode is given only to characters named in `hubner.sandboxNames` (default: nobody); it used to be forced on whichever character loaded the extension.
- `menu_join` and `menu_password` take no password argument; set `HUBNER_AUTOJOIN_PASSWORD` in the game's environment.
- Client `modify`, `delete` (ids and box), `spawn` and `container_set` refuse players, creatures, ships, carts, dropped items and player-built objects without `force` + `forceConfirm:'player-built'`, like `zdo_set`.
- `players` omits IPs / Steam ids and `chat_tail` skips whispers unless `hubner.exposePeerHosts` / `hubner.captureWhispers` are true.
- Snapshot names must be 1-64 characters of `A-Za-z0-9_.-`. `plan_apply` skips its delete sweep when an item had an error (`sweepOnErrors:true` overrides).
- Python: `mcp.py` is now `hubner_mcp.py`; scripts that need a live game moved to `python/live/`.

**Fixes**
- Server write gate: `dry:true` bypassed it for `zdo_set`, `zdo_delete`, `container_set`, `doors_set`, `undo_group` and `zdo_audit fix`, which do not honour `dry`. Now only handlers that really only report skip the gate (`WriteGate.Check`, tested).
- `snapshot` built a file path from its `name` (`../journal` overwrote the undo journal).
- `walk_check` never reported steep slopes: a `//` comment had swallowed the braces of the check.
- Zone scans with a huge box looped over millions of empty zones on the main thread; boxes are clamped to the world and sector index 0 (outside the grid) is no longer scanned as a zone. `terraform_map` and `terrain_grid` computed their sample cap in `int` (wrapped for a huge box).
- `MiniJson` has a nesting limit (a stack overflow would end the game process), rejects trailing characters and non-finite numbers.
- A failed undo no longer drops its journal entry. Undoing a delete remaps the ids in older entries, so a later undo of the original spawn still finds the object. The journal also records scale and chest contents of deleted objects, compacts atomically and reports write errors in `status` / `selftest`.
- `container_set` is journaled and refuses non-containers, open chests and player-built chests without force.
- Log tools: user regexes have a 100 ms match timeout, the log filter runs outside the ring lock, and `wait_for_log` callbacks run on a worker thread instead of whichever thread logged.
- Requests use their own capped threads instead of the thread pool; the body cap counts bytes (413); the accept loop does not spin on errors.
- MCP: `initialize` negotiates a supported version, an unsupported `MCP-Protocol-Version` is a 400, a client JSON-RPC response gets 202. The timeout message says whether the call had already started.
- `RequestPolicy`: Host comparison is case-insensitive, token comparison is constant-time, 413 for oversize bodies. `MiniYaml` keeps `#` inside quotes and reads booleans in any case (bad values are logged).
- Extension host: a failed `Register` removes its tools, several extensions per DLL are tracked, built-in tool names cannot be shadowed, plugin shutdown unloads extensions. `VirtualPeers` is unpatched on hot reload (a stale copy used to keep running).
- Sector-list repair re-files an object (portals keep their old list when moved) instead of dropping the entry, marks the chunk dirty, and runs before a save only when the journal changed.
- `plan_apply`: scans go zone by zone with yields and the frame budget is checked every 8 items; adoption picks the nearest piece; positions must be finite and inside the world; dry runs report unknown prefabs. Jobs: `job_cancel`; only finished jobs are forgotten; a sliced job blocks other writers while it runs.
- `render_ex` always switches hidden renderers back on and uses the `render.*` size limits; `validate_placement` does not cache "unknown prefab" before the world is ready; `chat_send` rejects an unknown type instead of whispering; `terrain_grid` no longer looks every height up twice.
- Repo: build output (`out/*.pdb`) and the `ext/lib` symlink are no longer tracked (the CI "no binaries" check failed on the pdbs); `scripts/package.sh` works on macOS and also builds `dist/HubnerExt-v<ext>.zip`; the release workflow checks the tag against the project version; the extension version lives in `ext/Version.props`; example configs no longer carry host names.
- Tests: command policy, protocol negotiation, request policy, write gate, name and bounds rules, YAML quoting, JSON depth limit, regex timeout; Python client tests against a fake endpoint (both run in CI).

### 0.4.1 core / 0.7.1 extension
- `tools.profile` (all | observe | build | control) and `tools.terse` filter `tools/list`; tools carry a category (extension table in `Reg.cs`, handed to the core reflectively so older cores keep working).
- `journal_compact {keepDays, dry}`: prune old journal entries and rewrite the file.

### 0.4.0 core / 0.7.0 extension (review fixes)
- Live-server safety: `plan_apply` is an iterator sliced by the job runner (`hubner.jobBudgetMs` per frame); a tool call that times out is cancelled instead of running late; `vpeer_add` is gated, releases ownership once per frame and refuses spots near real players; server chat capture peeks the RPC hash instead of deserializing every routed RPC (and no longer double-captures).
- Protection by component: creatures, tamed animals, ships, carts and dropped items are never adopted, cleared or box-deleted; forced deletes/edits need `forceConfirm:'player-built'` and are logged.
- Plans: stable `key` per item (moves are moves), world-wide sweep of a plan's objects (no stale pieces after a shrink), `floats` extras, fireplaces spawn fuelled.
- Journal: batched writes per call, compaction, `ints`/`floats`/`strings` journaled and restored by undo; the client's `undo` marks entries undone on disk.
- Fixes: `Zdos.NameOf` no longer freezes a one-entry table when called before the world loads; `stability_scan` skips non-supporting pieces and reports the game's own `minSupport`/`atRisk`; `sign_check` ignores chest plaques; `prefab_info` validates its argument; number arguments are checked (no raw `InvalidCastException` stacks); tool exceptions return one line, the stack goes to the log.
- Config: `hubner:` section (`sandboxNames`, `writeFlagMinutes`, `jobBudgetMs`); versions come from the csproj files; the auto-join password can come from `HUBNER_AUTOJOIN_PASSWORD`.
- Python: `Client` class with `sandbox` / `twin` instances, one `goto`, automatic sliced jobs for big plans; `mcp.call` takes `url`.
- Tests: `tests/ValheimMCP.Tests` (xunit, game-free sources linked in) run in CI.

- Hardening: Origin/Host validation, 8 MB body cap, optional bearer token (`server.token`).
- Server twin: virtual peers (`vpeer_*`) give the dedicated server real colliders and physics; whitelisted physics tools run there.
- Chat: server-side capture (`Say` and `ChatMessage`), `chat_send`, `chat_bubble`.
- New tools: `prefab_find`, `visible_objects`, `interact`, `probe_fan`, `base_survey`, `player_status`.

- Core: tool registry (`ToolRegistry.cs`) and hot-reloadable extension host (`ExtensionHost.cs`); per-tool write gating.
- `ext/`: the Hubner extension (world reading, idempotent plan building, walking and rendering verification, environment control, chat capture) for client and dedicated server.
- `python/`: MCP client and helpers; `config/`: example client and server configs.

## [0.2.2] - 2026-09-25

- Bump bepinex dep for 5.4.2351

## [0.2.1] - 2026-09-09

- Bump bepinex dependencies for Valheim 1.0 release


## [0.2.0] - 2026-06-19

### Added
- `get_log` MCP tool (and `GET /log` route): fetch recent lines from an in-memory
  log ring buffer on demand — tail the log, including on a dedicated server whose
  `LogOutput.log` isn't directly accessible. Supports incremental polling via a
  `cursor`/`since` and filtering via `contains`/`regex`. Complements the blocking
  `wait_for_log`.
- `LogWatch` now retains a bounded ring buffer of recent formatted log lines
  (configurable via `log.bufferCapacity` / `log.defaultLines` / `log.maxLines`).

## [0.1.0] - 2026-05-29

### Added
- Standalone BepInEx plugin exposing a localhost HTTP endpoint for driving
  Valheim's in-game console.
- Plain HTTP routes for scripting: `GET /health`, `GET /commands`,
  `POST /command`.
- Native in-process MCP transport (`POST /mcp`, Streamable-HTTP / JSON-RPC 2.0)
  with tools: `run_command`, `list_commands`, `health`, `render_view`.
- Off-screen camera rendering (`render_view`) returning a PNG inline, without
  touching the player's view.
- Dependency-free config at `BepInEx/config/valheimmcp.yml`, including an
  `allow`/`deny` command access-control list.

### Known limitations
- Output from commands that print asynchronously (coroutines, e.g. screenshot
  capture) is not captured — only output printed synchronously during the call.
