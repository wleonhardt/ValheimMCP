# AGENTS.md

Operations file for coding agents working in this repository.

## Instruction priority

1. The user's request in the current conversation.
2. This file.
3. `plans/decisions/` (settled choices: do not reopen them without a reason) and `plans/next-up.md`.
4. Defaults of the tools in use.

## Non-negotiable rules

- Build, test and lint must pass before work is done:
  - `dotnet build -c Release ValheimMCP.csproj`
  - `dotnet build -c Release ext/HubnerExt.csproj`
  - `dotnet build -c Release ext/HubnerExtServer.csproj`
  - `dotnet test tests/ValheimMCP.Tests`
  - `python3 -m unittest discover -s python/tests`
  - `python3 -m unittest discover -s scripts/tests` and `python3 scripts/lint_catch.py && python3 scripts/lint_writes.py && python3 scripts/lint_tools.py` (after adding or changing a tool: `python3 scripts/lint_tools.py --write`)
  The game libraries are read from `ext/lib` (git-ignored; copy or link them, or pass `-p:LibDir=...`).
- `python3 scripts/check.py` runs all of the above and ends with `== ALL OK ==` or the failures with the command to repeat each (`--changed` runs only what the changed files can break). It also runs `python3 scripts/lint_docs.py` (links, paths and tool names in the documents must exist).
- Check `plans/decisions/` before proposing structural changes.
- Project knowledge belongs in `plans/`, not in agent memory.
- Every security-relevant rule gets a pure, game-free implementation that the unit tests link (see `RequestPolicy`, `CommandPolicy`, `JsonRpc`, `WriteGate`, `SafeNames`, `WorldBounds`). Do not bury a gate inside code that needs the game to run.
- A tool that writes to the world must be registered as a write tool, go through `Plans.Guard` on the server, respect `Plans.Protected`, and be journaled. A new argument named `dry` only skips the server gate if the handler really only reports (`honoursDry`).
- A new catch block logs (`U.Once`, `Plugin.Log`) or rethrows, or carries `// allow: why`: the silent-catch count may only go down (`scripts/lint-baseline.json`). Creating, destroying or moving objects is allowed only in the files `scripts/lint_writes.py` lists.
- ZDO ids are valid for one world load only. Never persist one as the identity of an object: use `hub_plan` + `hub_key` (see `Identity` in `ext/Pure.cs`).
- A `plan_check` rule is added to `PlanRuleBook` (`ext/PlanRules.cs`) with its origin (hard, ghost or taste) and the decompiled source it comes from; a taste rule is never an error. Check a game fact in the decompile before a rule depends on it and record the result in `plans/decisions/`.
- Build every project and run the tests before committing: `dotnet build -c Release ValheimMCP.csproj ext/HubnerExt.csproj ext/HubnerExtServer.csproj` (one per call) and `dotnet test`.
- Never commit build output (`out/`, `*.pdb`, `*.dll`), symlinks, passwords, tokens, IP addresses or host names. CI rejects them.
- Live testing needs the game and a world; mark what was not exercised live in the commit message and in `plans/next-up.md`.

## Before done

1. All commands above pass.
2. `CHANGELOG.md` has an entry for any behaviour change; the README matches the code (config keys, tool names, safety rules).
3. New or changed plans/decisions are written down in `plans/`.
4. Anything verified only by reading code is listed as unverified.

## Context recovery

Read, in this order: `plans/README.md`, `plans/next-up.md`, `plans/open-questions.md`, the newest file in `plans/decisions/`, then `CHANGELOG.md` (top section) and `git log --oneline -20`.

## Project overview

Fork of `myrcutio/ValheimMCP`: a BepInEx 5 plugin that exposes a loopback HTTP/MCP endpoint for Valheim. The core (`src/ValheimMCP`) runs console commands, renders views and tails the log. A hot-reloadable extension (`ext/`, loaded from `BepInEx/hubner-ext/`) adds about 140 tools for reading and building a world, in a client build and a dedicated-server build. `python/` holds a client library.

Stack: C# (net472, LangVersion 9, Unity Mono, Harmony), Python 3, xunit tests on net10.0.

## Key commands

See the build/test commands above. `scripts/package.sh` builds the release zips into `dist/` (needs the game libraries). `config/` has example client and server configs.

## Workspace structure

| Path | What |
|---|---|
| `src/ValheimMCP/` | Core plugin: HTTP server, MCP protocol, config, log watch, extension host |
| `ext/` | Hubner extension (tools). Shared source files build into both `HubnerExt.csproj` (client) and `HubnerExtServer.csproj` (server). `Pure.cs` and `FlatJson.cs` are game-free |
| `tests/ValheimMCP.Tests/` | xunit tests; they link the game-free sources instead of referencing the plugin |
| `python/` | Client library (`hubner.py`, `hubner_mcp.py`), offline tests in `tests/`, scripts that need a live game in `live/` (`accept.py` runs the acceptance cases; every live script starts with `hubner.live_guard()`) |
| `config/` | Example `valheimmcp.yml` files |
| `scripts/`, `.github/` | Packaging and CI |
| `plans/` | Plans, decisions, open questions |
