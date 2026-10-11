# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).


## [Unreleased] - Hubner fork

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
