# Review of ChuMicro/ClaudeNJevHeim (2026-10-11)

Source: https://github.com/ChuMicro/ClaudeNJevHeim, cloned read-only at commit 11dc174 (192 commits, one author). Nothing from it was built or run. Five reviewers each read one area; their claims about game behaviour are the friend's own decompile notes unless this file says "checked here".

## What it is

A client-side player bot for Valheim 1.0.17. Claude supervises, a fast judge model called Jev (TypeSafe System One) makes the in-loop choices, and C# code owns every hard rule. About 112k lines of C# in `VhExt/` (a hot-reloaded extension), 1.7k lines in `VhBridge/` (the host), 1.2 MB of docs. It is not a server-side ZDO harness: roughly half of its 182 tools drive a body (walk, swing, place with the hammer, chat, sail). The other half are readers and rules, and those are what this repository can use.

## Licence and lineage

- The repository has no LICENSE for its own code. Only the vendored `.claude/skills/typesafe-ai/` is MIT. Without a licence nothing may be copied verbatim: ask the friend, or reimplement from the descriptions here. Game facts and formulas are not copyrightable.
- The lineage is ours: `VhBridge/ExtHost.cs` says "Adapted from wleonhardt/ValheimMCP ExtensionHost (MIT)"; `Mcp.cs` and `RequestPolicy.cs` come from upstream and this fork; `Render.cs` and several readers say "adapted from HubnerExt". His `CLAUDE.md` calls this fork one that "spawns objects and forces god mode" and says not to deploy it, which matches what the review fixes addressed.
- `data/geometry.json` (209 prefabs, schema vh.geometry/1) and `data/pieces-catalog.json` (664 pieces) are derived from Iron Gate assets, and the `tables` field of the catalog came from a 1.0.12 dump. Do not import them; regenerate from our own install.
- His repository embeds Windows user-profile paths in 24 tracked files and a default `.env` path in `VhBridge/Tools.cs`. Worth telling him before it is made public.

## Defects found in our own code because of this review

These were verified here against the decompiled game, not only reported:

1. **ZDO ids do not survive a restart.** `ZDO.cs:1298` renumbers every loaded ZDO with `m_uid.SetID(++ZDOID.m_loadID)`, which also forces user key 1. Our journal stores ids like `123:456789`. After a restart, undo of a spawn finds nothing (`FindZdo` returns null), reports success and removes the entry while the piece stays; undo of an older delete or modify can hit an unrelated object. Fix: journal the plan and key identity (`hub_plan`, `hub_key`) plus a per-boot id, and resolve by tag after a restart; refuse when ambiguous. The README claim "undo works across restarts" is wrong until this is done.
2. **`Door.Interact` returns true when nothing opened.** In the decompile it returns true for a ward-denied door and for a keyed door without the key. `Control.Walker.TryDoor` counts that as "opened", resets its stall clock, and can loop forever beside a locked door. It also opens any door within 1.6 m, not only doors on the route.
3. **`Walker.Tick` calls `SetLookDir(dir, 0f)` every tick.** With a zero transition time the game sets the look direction at once, so the view is turned every frame. The stick can be converted relative to the current look instead.
4. **`tools/call` without an id still runs** (`McpServer.HandleOne`). A message without an id is a notification and must never run a tool; his `Mcp.cs` drops it.
5. **29 empty `catch {}` and 37 bare `catch {` blocks** hide failures.
6. **Hot reload leaks.** His HANDOFF measured 19.6 GB after 103 reloads (Mono cannot unload assemblies). We have no reload counter or warning.

## Ranked adoption backlog

Value H/M/L, effort S/M/L. "Idea" means reimplement from the description.

| # | Item | Value | Effort | What |
|---|---|---|---|---|
| 1 | Journal identity across restarts | H | S-M | Defect 1 above. Add boot id and plan/key to journal entries, resolve by tag, test with a pure resolver |
| 2 | Walker door and look fixes | H | S | Defects 2 and 3. Skip ward-locked and key doors, require the leaf at rest (animator tag), open only doors on the line ahead within 2.8 m, 2 s hold before pressing again; drop `SetLookDir` |
| 3 | Notifications never run | H | S | Defect 4. In `HandleOne`, `if (!hasId) return null` before dispatch |
| 4 | Drop queued calls of a retired extension | M | S | His `MainQueue.Retire`: each queued item carries the assembly of its handler (`handler.Method.Module.Assembly`, not the lambda's); `ExtensionHost.Load` retires the old assembly; `Pump` drops and signals the waiter |
| 5 | Bind check before unloading the old extension | H | S-M | His `UnboundCoreRefs`: after `Assembly.Load(bytes)`, resolve every TypeRef (table 0x01) and MemberRef (0x0A) token against the running core and refuse the load if the core lacks a member. Fixes the hazard behind commit cc2d4d3. Verify `Module.ResolveMember` behaviour on Valheim's Mono first. Needs the load split into "poll thread checks" and "main thread swaps" |
| 6 | Server-side ownership rules for `Plans.Protected` | H | M | His `World/Neighbours.cs` decision order, idea only. Add: tombstones (owner, not creator), ward areas (enabled, radius from the prefab's `PrivateArea.m_radius`, permitted list `pu_id<i>`), another tool's creator-0 structures, private chests, tames. Our `Protected` only knows creator, living things and plan marks |
| 7 | World events from ZDOs | H | M-L | `world_events`: a ZDO is new only if its user id is an online peer's session; session 1 means "in the save when the server started". Structure appeared, player arrived/left/died, ship docked, boss killed |
| 8 | No-build location table | H | S-M | His `Live/LocationRules.cs`: per location type radius, pivot strictly inside the circle; make `plan_apply` and `verify_plan` refuse no-build circles. Check it works on a server |
| 9 | User override in the walker | H | S | His `BodyCore`: capture real input at Harmony priority First, write ours at Last, yield to the user for 1.5 s after any touch, freeze stuck and timeout clocks while yielded, re-find the place on the line afterwards |
| 10 | Silent-catch lint with a baseline | H | M | His `tools/gates/errors.ps1` idea as a Python script in CI: parse each catch, flag empty, bare, swallow, or no log/throw, allow with `// allow: why`, fail when the count exceeds a committed baseline. Pair with a rate-limited fault reporter (per call site, 30 s, with a suppressed count) |
| 11 | Reload ledger for interrupted jobs | H | S-M | His `Run.cs` resume after reload: write `{tool, args, pid}` when a sliced job is interrupted by an unload, resubmit after the new build registers if pid matches and the file is under 120 s old. Safe for `plan_apply` because it is idempotent and journaled. Add typed outcome codes (`done`, `cancelled`, `error`, `crash`, `unloaded`, `bad_args`, `refused`) and atomic state-file writes |
| 12 | Main-thread stall watchdog | H | S | His `MainWatch`: mark main-thread work with Enter/Exit, a background thread checks every 250 ms, a pure detector reports work marked longer than 2 s or no frame for 2 s, repeats every 60 s. About 120 lines |
| 13 | Build stamp | M | S | MSBuild target writes commit, branch, dirty (use `--untracked-files=normal`) and worktree into `AssemblyMetadata`; show in `ext_info` and on every journal/job row |
| 14 | Tool and docs sync lint | M | S | Collect tool names from `r.Add("x"` and `Reg.Add(r, "x"`; assert unique, snake_case, described, and present in README/AGENTS |
| 15 | Write-API allowlist lint | M | S-M | Regex to allowed files: `CreateNewZDO`, `DestroyZDO`, `ZDO.Set` only in files that go through `Plans.Guard`; every registration that writes is `write: true` |
| 16 | Chat hardening | H | S-M | For `chat_send` and `chat_tail`: reply sanitizer (one line, 120 chars, no leading `/ \ ! #`, no markup/URLs/control chars), global gap 6 s and per-speaker 15 s, echo suppression, trust by platform id never by name, tag rows `untrusted`, hint flags for manipulation/machine/spam without acting on them. NFKC and zero-width stripping before any regex |
| 17 | Readers worth adding | M | S each | `neighbours`, `players` with inBed/dead/position source and age, `locations_near` (server knows all instances), `biome_at` (seed-pure), `fires` (fuel from ZDO), `husbandry_state`, `death_state` (tombstone ZDOs), `cost_mode` (global keys), `sea_route` (pure A* on world heights) |
| 18 | Tests that need no game | M | M | A pure dispatcher test (timeout never runs late, started call finishes, cancel, retired owner, frame budget); ZDO-snapshot fixtures with pure rule functions (his `NeighbourFacts.From` is the shape) |
| 19 | Placement rules for validation | H | S-M | Overlap with oriented per-collider boxes instead of a union box with 0.9 shrink; snap-rest contact; not-on-tilting-surface (normal y < 0.8), not-on-wood/floor, no-in-water; extension spacing; the checks a ZDO spawn skips |
| 20 | Support predictor | H | M | Replay placement order; ground-touching pieces get the material maximum; otherwise take the best supporting neighbour with `S*(1 - loss*d)`, d = distance to centre of mass or pivot + 0.1; two neighbours 100 degrees apart average; break below the material minimum. Game note: placement is not a support check, the owner's sweep breaks weak pieces after about 30 s |
| 21 | Clearance rules | M | S-M | Fire 1 m free all round and 1.5 m beside a long side; walkway 1.5 m; station 2 m use zone; storey at least 4 m; door pairs mirror (yaws 180 apart, hinges outward); 22.5 degree yaw grid |
| 22 | Walker stall ladder | M-H | M | Progress means route shrank 0.2 m and feet moved 0.2 m; stall after 2 s; hop only when a probe finds a top 0.40-1.2 m, never from water, toward fire or portals; then side step 0.6 s, re-route twice, give up after 4 stalls at one spot. Run up banks of 38-60 degrees at over 50 percent stamina. End the walk on teleport (jump over 30 m) |
| 23 | Walk numbers for `walk_check` | M | S | Capsule radius 0.49, height 1.85 (ours 0.4 and 1.8); step 0.45 (ours 0.5); walk slides above 38 degrees unless running (ours 50) |
| 24 | Smoother view and pursuit | M | S-M | Ease the view with a jerk-bounded ease; pursue a point `clamp(0.9 + 0.3 v, 1.0, 2.4)` m ahead on the line instead of steering at waypoints |
| 25 | PlanSchema adapter | M | S | `vh.plan/2` steps map to plan_apply items: `piece`->`prefab`, `at`->`x,y,z`, `rot`->`yaw`; keep `action == "place"` steps that are neither `removeAfter` nor `scaffold`; do not use his ids as keys (they renumber) |
| 26 | Jev as a judge | L-M | L | A fresh dependency on a third-party service for a tool set that is deterministic and undoable. If wanted: `POST https://api.typesafe.ai/v1/systemone`, Bearer key, body `{state, model:"jev-latest", questions:{k:{type:"choice"|"noul"|"score", instructions, criteria}}}`; answers carry `probabilities` and `confidence`. His pattern: ask the same choice twice with the options in reversed order and average, treat disagreement as low confidence, code advice goes in as a `code_rule` fact. Chat lines would be sent to the service |

Not worth taking: work fronts, stands, scaffolds, the Director, the design generator (castle, longhall), the walker grid planner, terrain editing code, the lease (low value for one developer; his lease mutex ignores a failed `WaitOne`).

## Defects in his code worth telling him

- `tools/lib/vh.psm1:276` ignores the result of `WaitOne(10000)`, so after 10 s two agents can both take the lease.
- Social chat: raw chat text reaches the supervisor inbox with no "untrusted" marker (`VhExt/Social.cs:834`), the speaker name goes unquoted into a model prompt (`World/Social.cs:1240`), and the manipulation regexes run before any Unicode normalisation.
- `tools/build/vh.targets` stamps `dirty=false` when only untracked files changed.
- `Debug.get_value` invokes properties, so it can have side effects and read secrets if exposed beyond loopback.
- `ExtHost` keeps one extension per DLL path (a second type is never unloaded), and loads each refused file as an assembly before checking it.
- Hard-coded Windows user-profile fallback path in `VhExt.csproj` and `VhBridge/Tools.cs`.
- Smaller: `DecidePolicy` drops a pending HTTP request without disposing it; non-atomic `File.WriteAllText` for checkpoints, policy and orders; the event rotation compares characters with bytes.

## Not verified

No code of his was built or run. The offline harness (`tools/mcpcheck.ps1`) needs Windows PowerShell and the real game DLLs. Every "works on a dedicated server" judgement is inferred from the classes read. His own measured numbers (bank climbs, capsule size) were not reproduced.
