# ValheimMCP

A standalone BepInEx plugin that exposes a **localhost HTTP endpoint** for driving
Valheim's in-game console remotely and returning the console output directly in the
response. Built to give an agent (Claude Code) reproducible, scriptable access to game
state — e.g. running console commands like `pos` and reading what they print, without
typing into the in-game console or round-tripping through dump files.

## ⚠️ Security

This mod opens a local HTTP endpoint that can run **arbitrary Valheim console commands** (including anything `devcommands` unlocks) and, with the extension, change the world.

- It binds **IPv4 loopback (`127.0.0.1`) only** by default. The plugin **refuses to start** with a non-loopback `server.host` unless `server.token` is set.
- Requests with an `Origin` header (cross-site browser requests), a `Host` that is not a loopback name (DNS rebinding) or a body over 8 MB are refused. With `server.token` set, every request needs `Authorization: Bearer <token>` (or `X-Hubner-Token`). Without a token, any process on your machine can drive the game while the listener is up: set one on a shared host, and reach a remote server through an SSH tunnel, never by exposing the port.
- **`tools.write: false` (the default) turns off `run_command` / `POST /command` and every tool that changes the world.** `tools.deny` blocks any tool by name, built-in ones included. `commands.allow` / `commands.deny` filter console commands (below). A config file that cannot be read leaves the plugin stopped, not open.
- `tools.profile` only trims what `tools/list` advertises; it is **not** access control.
- This is a development/automation tool. Don't run it on a shared or public host without a token.

## Compatibility

Built and tested against **Valheim** (BepInEx pack `denikson-BepInExPack_Valheim-5.4.2351`, BepInEx 5.4.x). The core only depends on Valheim's own `Console`/`Terminal`, so it should be resilient across game patches. The extension reads private game fields and methods by reflection (sector lists, save hooks, menu and chat internals); a game update that renames one shows up as a one-time warning in the log and a failing `selftest`, not as a crash.

## Design

- Ships into `BepInEx/plugins/ValheimMCP/` (loaded once, **not** hot-reloaded), so the
  listener stays up across F6 reloads of the mods you're iterating on and never fights
  for its port.
- A background `HttpListener` thread accepts requests and marshals each onto Unity's
  main thread (`MainThreadDispatcher`) before touching any Valheim API.
- Console output is captured with a Harmony postfix on `Terminal.AddString` while a
  command runs — no private-field reflection.
- Zero dependency on any other mod: it drives Valheim's `Console`/`Terminal`, which
  already has every registered command.

## Routes

| Method | Path        | Body / Query              | Returns |
|--------|-------------|---------------------------|---------|
| POST   | `/mcp`      | JSON-RPC 2.0 (MCP)        | MCP response (`application/json`, or `text/event-stream` for a `wait_for_log` with a progress token) |
| GET    | `/health`   | —                         | `{ok, inGame}` |
| GET    | `/commands` | —                         | `{ok, commands:[{name,description}]}` |
| POST   | `/command`  | raw command line, or `?text=` | `{ok, ran, output:[...], error?}` (needs `tools.write: true`) |
| GET    | `/log`      | `?since=` `?maxLines=` `?contains=` `?regex=` | `{ok, cursor, matching, dropped, lines:[...]}` |
| GET    | `/sse`      | —                         | `501 Not Implemented` (no SSE transport) |

The plain `/health`, `/commands`, `/command` routes are for `curl`/scripting. At most 24 requests are served at once (503 beyond that); a body over 8 MB is a 413.
`/mcp` speaks the protocol Claude Code consumes.

## MCP (native, no bridge)

The plugin implements the MCP Streamable-HTTP transport (JSON-RPC 2.0) directly, with no external dependencies — a hand-rolled JSON parser/writer. Responses are single `application/json` bodies; a `wait_for_log` call that carries a progress token and accepts `text/event-stream` streams its progress as SSE. Protocol versions 2025-11-25, 2025-06-18, 2025-03-26 and 2024-11-05 are answered (the client's version when supported, otherwise the newest); an unsupported `MCP-Protocol-Version` header is a 400. Tools:

- `run_command(text)` — run a console command, return captured output. **Needs `tools.write: true`.**
- `list_commands()` — all registered console commands.
- `health()` — is a world loaded.
- `render_view(x, z, [y, yaw, pitch, dist, size])` — render the location with an
  **independent off-screen camera** (never touches the player's view) and return
  the PNG inline. The off-screen camera is named `valheimmcp_render_cam`, so a mod
  that can draw debug overlays into a named camera can target it to render those
  overlays into *only* this view, leaving the player's screen untouched.
- `wait_for_log(pattern, [regex, timeoutMs])` — block until a log line matches,
  then return it (streams observed lines as progress when the client sends a
  progressToken). For waiting on an async event (e.g. a mod hot-reload finishing).
- `get_log([since, maxLines, contains, regex])` — tail recent log lines from an
  in-memory ring buffer (the MCP server, the game, and every other mod), returning
  immediately. Omit `since` for the latest lines; pass back the `cursor` from the
  result header to poll only what's new. Filter with `contains`/`regex` (a regex
  has a 100 ms match timeout per line). Lets you read logs even on a dedicated
  server whose `LogOutput.log` isn't reachable.

Register it with Claude Code (game can be launched after; the connector reconnects):

```sh
claude mcp add --transport http valheim http://127.0.0.1:8731/mcp
```

### Examples

Use `127.0.0.1`, not `localhost` — the server binds IPv4 loopback only, and
`localhost` may resolve to IPv6 `::1` first (which isn't bound). This IPv4-only
bind is intentional: it keeps the endpoint strictly local.

```sh
curl -s 127.0.0.1:8731/health
curl -s 127.0.0.1:8731/commands
curl -s -X POST 127.0.0.1:8731/command --data 'pos'     # needs tools.write: true
```

## Config

`BepInEx/config/valheimmcp.yml` (written with defaults on first run; parsed by the dependency-free `MiniYaml` reader — a value that contains ` #` must be quoted; booleans accept `true`/`false`/`yes`/`no`/`on`/`off` in any case, anything else is logged and ignored). Example configs for a game client and a dedicated server are in `config/`.

```yaml
server:
  host: 127.0.0.1          # loopback only; anything else is refused unless token is set
  token: ''                # optional shared secret, sent as 'Authorization: Bearer <token>'
  port: 8731
  commandTimeoutMs: 15000

render:
  defaultSize: 768         # render_view / render_ex size when 'size' is omitted
  minSize: 256
  maxSize: 1280

wait:
  defaultTimeoutMs: 120000 # wait_for_log timeout when 'timeoutMs' is omitted
  maxTimeoutMs: 600000
  heartbeatMs: 5000        # SSE progress heartbeat while a wait is pending

log:
  bufferCapacity: 2000     # in-memory ring of recent log lines retained for get_log
  defaultLines: 200        # get_log lines returned when 'maxLines' is omitted
  maxLines: 1000

# Access control for run_command (and POST /command). 'deny' always wins; if
# 'allow' is non-empty, ONLY matching commands run. Match by command name (first
# token, case-insensitive); trailing '*' is a prefix wildcard (e.g. "spawn*").
commands:
  allow: []
  deny: []

tools:
  write: false             # true enables run_command and every tool that changes the world
  timeoutMs: 30000
  deny: []                 # tool names to block, built-in ones included
  profile: all             # what tools/list advertises: all | observe | build | control (not access control)
  terse: false             # true = first sentence of each description only

hubner:                    # read by the extension
  sandboxNames: []         # characters that get god mode from teleport / walk_to / auto-join; empty = nobody
  exposePeerHosts: false   # true: the players tool returns each peer's IP / Steam id
  captureWhispers: false   # true: chat_tail also records whispers
  writeFlagMinutes: 30     # server: how long ALLOW_SERVER_WRITES stays valid after it was touched
  jobBudgetMs: 8           # main-thread time a sliced job (plan_apply) may use per frame
```

Note: `ConsoleBridge` runs commands with the game's `skipAllowedCheck`, which skips only the per-session allowed-command check (admin rights on a server). Cheat commands still need `devcommands`, as in the in-game console.

## Install

**From Thunderstore (recommended):** install with [r2modman](https://thunderstore.io/c/valheim/p/ebkr/r2modman/)
or the Thunderstore Mod Manager. It pulls in the BepInEx pack automatically.

**Manual:** install [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/),
then drop `ValheimMCP.dll` into `BepInEx/plugins/ValheimMCP/`. Launch the game once
to generate `BepInEx/config/valheimmcp.yml`, then edit it if needed.

## Build

The game's managed assemblies and the BepInEx core libraries are not in this repository (they are Iron Gate's and BepInEx's files). Copy them to `ext/lib/` (`core/0Harmony.dll`, `core/BepInEx.dll`, `Managed/assembly_valheim.dll`, `assembly_utils.dll`, `UnityEngine*.dll`) or pass `-p:LibDir=/path/to/lib`. `ext/lib` is git-ignored: link or copy it on your machine.

```bash
dotnet build -c Release ValheimMCP.csproj              # core plugin -> ./out/ValheimMCP.dll
dotnet build -c Release ext/HubnerExt.csproj           # client extension -> ./out/HubnerExt.dll (reads ./out/ValheimMCP.dll and ext/lib)
dotnet build -c Release ext/HubnerExtServer.csproj     # server extension -> ./out/server/HubnerExt.dll
dotnet test tests/ValheimMCP.Tests                     # unit tests of the game-free parts (no game DLLs needed; this is what CI runs)
python3 -m unittest discover -s python/tests           # offline tests of the Python client (also CI)
python3 -m unittest discover -s scripts/tests          # the lints and their tests (also CI)
python3 scripts/lint_catch.py && python3 scripts/lint_writes.py && python3 scripts/lint_tools.py     # silent catches vs baseline, write APIs only where guarded, docs/TOOLS.md current (--write regenerates it)
```
Override any reference location with `-p:McpDll=/path/ValheimMCP.dll` or `-p:LibDir=/path/lib`. Versions: core in `ValheimMCP.csproj`, extension in `ext/Version.props` (`ext_info` reports it). Copy `out/ValheimMCP.dll` to `BepInEx/plugins/`, the extension DLL to `BepInEx/hubner-ext/`, and `config/valheimmcp.client.yml` or `config/valheimmcp.server.yml` to `BepInEx/config/valheimmcp.yml`.

## Releasing

CI can't compile this — it needs Valheim's (non-redistributable) managed assemblies — so releases are built locally. Versions live in **one place each**: `<Version>` in `ValheimMCP.csproj` (core; flows into the assembly version, the generated `PluginInfo` constants and, via `scripts/package.sh`, the packaged `manifest.json`) and in `ext/Version.props` (extension). Bump them, add a `CHANGELOG.md` entry, then:

```sh
./scripts/package.sh        # -> dist/ValheimMCP-v<core>.zip (Thunderstore) and dist/HubnerExt-v<ext>.zip (extension, configs, Python client)
git add -f dist/*.zip && git commit && git tag v<core> && git push --tags
```

The `release` workflow checks that the tag equals the core `<Version>` and attaches the zips from the tagged commit. Upload the `ValheimMCP` zip to [Thunderstore](https://thunderstore.io/c/valheim/) too. Because the binaries are built on a developer machine, they are not reproducible by CI; a community package of publicized game libraries (for example [ValheimGameLibs on NuGet](https://www.nuget.org/packages/ValheimGameLibs)) could let CI compile one day, if it tracks the current game version.

## Status / roadmap

- **Done:** plain HTTP routes + native in-process MCP (`/mcp`), capturing synchronous
  console output. Usable via `curl` or as Claude Code MCP tools.
- **Known limitation:** output from commands that print asynchronously (coroutines, e.g.
  screenshot capture) is not captured — only what's printed synchronously during the call.
- **Possible next:** a `GET /file` route (and/or inlining `written: <path>` contents) so
  commands that dump to disk return their payload too; typed introspection tools for
  live game state.

## License

[MIT](LICENSE) © 2026 myrcutio


---

# Hubner fork: tool registry, extension host and the Hubner extension

This fork keeps upstream's console bridge and camera and adds a pluggable **tool registry** and a hot-reloadable **extension host** (`BepInEx/hubner-ext/*.dll`, reloaded within about 2 seconds, no game restart), plus a large extension (`ext/`) that turns the server into an agent harness for building and inspecting a world.

## What changed in the core (`src/ValheimMCP/`)
`ToolRegistry.cs` and `ExtensionHost.cs` are new (tools are looked up in the registry, per-tool write gating and deny lists, extension loading, hot reload). The security-relevant rules live in small pure files that the unit tests link without the game: `RequestPolicy` (Origin/Host/token/size), `CommandPolicy` (allow/deny), `JsonRpc` (envelopes and protocol negotiation), `SafeRegex` (match timeouts), `ToolProfiles`, `MiniJson`, `MiniYaml`. `McpServer`, `HttpServer`, `ModConfig`, `LogWatch` and `Plugin` differ from upstream in the hardening described above.

## The extension (`ext/`)
About 93 tools (the core's six plus the extension's), in two builds: `HubnerExt.csproj` (game client) and `HubnerExtServer.csproj` (dedicated server twin). The server twin has no camera, and no physics until a virtual peer exists (see below).

| Area | Tools (examples) |
|---|---|
| Reading the world | `terrain_info`, `terrain_profile`, `surface_probe`, `raycast`, `objects`, `creatures`, `zone_state`, `prefab_info` (colliders, snap points, piece rules), `prefab_search` |
| Idempotent building | `plan_apply` (exact, journaled, `ctag`/`ints`/`label`), `plan_remove` (by tag, prefab or plan), `plan_stats`, `verify_plan`, `validate_placement`, `clear_overlaps`, `zdo_*` |
| Verification | `walk_check`, `walk_to` (real capsule movement, optional no-jump), `nav_path`, `headroom`, `stability_scan`, `bed_check`, `sign_check`, `doors` / `doors_set`, `room_view`, `render_ex` (cutaways, ortho, hide trees) |
| Environment | `time_set`, `weather_set`, `env_state` |
| Session control | `teleport` (with arrival check), `menu_join` / `menu_password` / auto-join (the password only from `HUBNER_AUTOJOIN_PASSWORD`), `player_state`, `players` |
| Operations | `world_save`, `snapshot` / `snapshot_diff`, `journal`, `job_start` / `job_status` / `job_cancel`, `log_tail`, `chat_tail` (in-game chat, client and server) |

There are **no terrain-editing tools** by design.

## Python client (`python/`)
`hubner_mcp.py` is a tiny MCP-over-HTTP client (not named `mcp.py`: that would shadow the official `mcp` package); `hubner.py` has a `Client` class with the tools as methods plus three ready instances: `default` (follows `HUBNER_URL`), `sandbox` (the game client, 8731) and `twin` (the dedicated server, `HUBNER_SERVER_URL`, default 8741, adds `confirm:'server-write'` to writes). `from hubner import *` still gives the module-level functions bound to `default`.
```python
from hubner import sandbox, twin
with twin.writes():                                   # opens ALLOW_SERVER_WRITES over ssh for the block, always removes it
    twin.plan_apply('castle-x', items)                # > 300 items: runs as a sliced job (job_start + poll), never freezes the game
sandbox.goto(-200, -300); sandbox.walk([[-200, -290]]); sandbox.render('shot.png', x=-200, z=-300, yaw=200, pitch=20, dist=30)
with twin.peer(-200, -300): twin.stability_scan(-250, -350, -150, -250)
```
Environment: `HUBNER_URL`, `HUBNER_SERVER_URL`, `HUBNER_SERVER_SSH` (user@host), `HUBNER_GATE_FLAG` (flag path on the server; both required for `writes()`), `HUBNER_SSH_AGENT_OPEN` (optional command that starts your ssh agent when ssh fails), `HUBNER_TOKEN`, `HUBNER_LAUNCH` (command that starts the sandbox client, for `ensure_client`), `HUBNER_JOB_THRESHOLD`, `HUBNER_NOJUMP`. Tests: `python3 -m unittest discover -s python/tests` (offline, fake endpoint); scripts that need a live game are in `python/live/`.

## Plans: identity, jobs, undo
- Send a stable `key` per item from your generator (for example `tower:NW:wall:c3:k5`): a moved piece is then a *move*, not a delete plus a spawn. Without a key the identity is prefab + position (0.1 m) + height (0.5 m) + yaw.
- `plan_apply` sweeps the plan's objects everywhere this process knows them (the server: the whole world); pass `box` to limit the sweep. The sweep is skipped when any item had an error (`sweepSkipped`), unless `sweepOnErrors:true`. A dry run reports unknown prefabs too.
- Big plans: `job_start {tool:'plan_apply', args}`; the job runs on the game's frames and checks `hubner.jobBudgetMs` (default 8 ms) every 8 items, so players never see a hitch. While it runs, other write tools are refused (`job_cancel` stops it). The Python client does this by itself over `HUBNER_JOB_THRESHOLD` items.
- Everything is journaled (`hubner-ext/journal.jsonl`, batched per call, compacted atomically on load; entries carry the world load they belong to and the plan identity, because ZDO ids are renumbered at every load). `undo_group` restores position, rotation, scale, text, tags, chest contents and any `ints`/`floats`/`strings` the operation changed (door states, fuel). An entry that cannot be undone stays in the journal. Undoing a delete recreates the object under a new id and the older entries about it follow. A journal that cannot be written is reported by `status` and `selftest` (`journalError`).
- Fireplace prefabs spawn with full fuel (`floats.fuel` to override).

## Tool profiles (`tools.profile`, `tools.terse`)
`tools/list` advertises about 93 tools with long descriptions; every MCP session pays for that. `tools.profile` in `valheimmcp.yml` trims the list: `observe` (reads and ops), `build` (plus plans, zdo, undo, jobs, journal), `control` (plus teleport, walk, menu, environment, chat), `all` (default). Every tool stays callable by name whatever the profile (the Python client does not read the list); the profile is a convenience, not a permission: use `tools.write` and `tools.deny` for that. `tools.terse: true` keeps only the first sentence of each description. Both are read at start.

## Journal upkeep
`journal_compact {keepDays}` drops entries older than `keepDays` (default 30; their undo is given up) and rewrites `hubner-ext/journal.jsonl` without undone markers. The journal is loaded into memory at start: a server that has done months of syncs should run it now and then (`dry:true` first).

## Safety: what the write tools refuse
- Terrain and internal prefabs, players. Creatures, tamed animals, ships, carts and dropped items are decided by the prefab's components (never adopted, never cleared as overlap, deleted or edited only forced).
- Player-built objects (`creator != 0`) need `force:true` **and** `forceConfirm:'player-built'`; every forced object is logged. The same override is needed for a tombstone, anything inside an enabled guard stone's area, a container that holds items and a build of creator-0 pieces. This holds for the plan/ZDO tools **and** the client's `spawn` / `modify` / `delete` / `container_set` (a player's chest is player-built).
- Positions must be finite and inside the world (about ±11 km); a box argument is clamped to it.
- Server writes need the gate flag file (`ALLOW_SERVER_WRITES`, valid for `hubner.writeFlagMinutes` after it was touched) and `confirm:'server-write'`. A `dry:true` run skips the gate only on tools that honour it (`plan_apply`, `clear_overlaps`, `plan_remove`, `journal_compact`); every other tool refuses without the flag even with `dry`. `vpeer_add` is gated too and refuses a spot within 200 m of a real player unless forced.
- `teleport`, `walk_to`, `menu_join`, `chat_send` and the other control tools are write tools. God mode goes only to characters listed in `hubner.sandboxNames`.
- A tool call that hits the HTTP timeout while it is still waiting in the queue is cancelled and will not run. If it had already started it finishes anyway: the error says so, and a retry of a non-idempotent write can apply it twice, so check first.

## Virtual peers: server-side physics with no player connected
A stock dedicated server never instantiates the world (no reference position), so it has no colliders, heightmaps or `WearNTear`. `vpeer_add {name,x,z,minutes}` makes the server run the game's own `CreateLocalZones` / `FindSectorObjects` / `CreateObjects` / `RemoveObjects` around positions you choose (idea from ddormer/valheim-serverside, rewritten; off by default, at most 4 peers, auto-expiring, behind the write gate, refused within 200 m of a real player unless forced). While one exists the server twin also answers `raycast`, `surface_probe`, `headroom`, `stability_scan`, `walk_check`, `terrain_info`, `sign_check` and `bed_check`. Python: `with hubner.vpeer(x, z): ...`.

## Reading the world without a player (server and client)
`net_peers`, `active_area`, `producers`, `loot_near`, `harvest_near`, `resource_nodes`, `spawners_near`, `wear_risk`, `absence_forecast` (barrels, hives, floor items, pickables, rocks, spawners and what stops when nobody is near), `world_rules` (the server options, rates, what a death costs, whether portals are blocked), `plan_check` (structure, overlap, tilt, water, yaw grid, snap seams, bed, station, smelter, barrel and hive cover, fire blocked above, station extensions, build range, not-on-wood, no-build locations and wards before placing; every problem says whether the rule is `hard`, `ghost` or `taste` and `plan_rules` lists them), `tool_stats` (per-tool calls, errors and slow calls), `world_events` (structures that appeared, players who came, went or died), `neighbours` (who owns what round a point: pieces by creator, wards, tombstones, claimed beds, containers with items), `locations_near`, `biome_at`, `fires`, `husbandry_state`, `death_state`, `cost_mode`. The full list with the code each lives in is `docs/TOOLS.md` (generated; CI keeps it current).

## Host
`ext_status` shows what is loaded, with the build stamp (commit, dirty, branch, worktree) of the core and of each extension, how many reloads this process has done (every reload stays in memory: restart the game after about 50), calls dropped, and main-thread stall reports. A main-thread watchdog logs work that runs longer than 2 s with its name. An extension built against a newer core than the one running is refused before the old extension is touched; queued calls of a reloaded extension are dropped.

## Chat
Chat text is data from people and a model reads it: `chat_tail` rows are cleaned for reading, tagged `untrusted` unless the speaker's platform id is in `hubner.trustedPlayers`, and carry `hints` (manipulation, machine, tool_talk, split_*, name_suspicious, spam, noise) that warn and never decide, plus `ours` (our own line coming back) and `to` (me, someone_else, everyone); a name on the trusted list without an id is `name_only`, still untrusted. `chat_send` cleans its text, allows one line per 6 s and 6 per listener per 10 minutes, and refuses a repeated injection, a position for an untrusted listener and an echoed untrusted line. `chat_tail` captures chat on the client (`Chat.OnNewChatMessage`) and on the server (one hook on `ZRoutedRpc.RPC_RoutedRPC` that peeks the method hash and decodes only the Talker RPC `Say` for Normal/Whisper and the routed `ChatMessage` for Shout/Ping). `chat_send` speaks as the sandbox character (180 characters, 15 m for Normal); `chat_bubble` shows a speech bubble for a named speaker.

## Other tools added from a community review
`prefab_find` (name and component search, build costs), `visible_objects` (frustum + occlusion + type taxonomy), `interact` (press E), `probe_fan` (12-heading steering probe), `base_survey` (clusters, stations, beds), `player_status`.

## Safety notes
- The MCP endpoint binds to loopback: reach a remote server through an SSH tunnel, never expose it; set `server.token` on any shared host (Python: `HUBNER_TOKEN`).
- Server writes need the gate flag file (`ALLOW_SERVER_WRITES`) next to the extension; `hubner.server_writes()` opens it and always removes it again.
- Privacy: `players` leaves out IP addresses and Steam ids, and `chat_tail` skips whispers, unless `hubner.exposePeerHosts` / `hubner.captureWhispers` are true. The log tools show the whole BepInEx log.
- Take a world backup before large write batches.
- The auto-join password is read from the environment variable `HUBNER_AUTOJOIN_PASSWORD`; it is not a tool argument and should not go on the command line.

## Licence
MIT, same as upstream (see `LICENSE`). Upstream: https://github.com/myrcutio/ValheimMCP
