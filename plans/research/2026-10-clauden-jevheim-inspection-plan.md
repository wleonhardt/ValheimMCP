# Plan: reading the rest of ClaudeNJevHeim efficiently

Status: plan only. Nothing here has been run beyond the measurements below.

## Goal

The first review read the tool surface, the walker, the structure model, the ward and ownership rules, the chat rules and the live-test notes. It did not read most of the friend's code. This plan finds what else ValheimMCP can use, at a bounded token cost, without reading 5 million tokens.

The repository has no licence. Read it for ideas and game facts only. Do not copy code.

## Measurements (repomix 1.18.0, tiktoken counts, clone at `scratchpad/friend/repo`, 552 tracked files)

| Area | Files | Tokens | Note |
|---|---|---|---|
| `VhExt/` all | 318 | 2.75 M | 2.16 M after `--compress` |
| `VhExt/Checks/` | 82 | 0.48 M | the friend's own tests; read only to learn game facts |
| `VhExt/` without Checks, comments and empty lines removed, `--compress` | 235 | 1.13 M | the real source skeleton |
| `tools/` | 128 | 3.22 M | 2.92 M is `fixtures/` (scene captures), 0.13 M is `mcpcheck.ps1` |
| `docs/` | 70 | 0.60 M | `audit/` 0.14 M, `reference/` 0.08 M, `history/` 0.06 M |
| `plans/` | 15 | 0.57 M | generated design and plan JSON |
| `data/` | 2 | 0.34 M | `pieces-catalog.json`, `geometry.json` |
| `VhBridge/` | 12 | 0.02 M | small; read in full |

Findings that shape the plan:

- About 6.5 M of the 7.2 M tokens is data (fixtures, plans, catalogues) or the friend's own tests. None of it should be packed for reading.
- `--compress` only saves about 21 percent on this C# (it keeps most bodies). Packing is not enough; the order of reading matters.
- The biggest source files are `Level.cs` (294 KB), `World/Facts.cs` (237 KB), `Survival.cs` (187 KB), `Fight.cs` (185 KB). A model cannot read these whole inside a working context.
- `ctags` on this machine is the BSD one and has no C# support. `scc` and `tokei` are not installed. Tree-sitter is built into repomix, so repomix is the only tool needed.

## Method

1. **Index first, no bodies.** Build a map of public types and members per file with a cheap script (a regex pass over `class|struct|enum|static .*\(` lines, or reuse `scripts/csscan.py` from this repository, which already lexes C#). Output one line per member. Expected size: under 60 K tokens for all of `VhExt` outside Checks. Read this once to decide where to go deep.
2. **Docs before code.** The friend wrote design notes per module (`docs/act.md`, `actor.md`, `survival.md`, `resources.md`, `runtime.md`, `design-kernel.md`, `generator.md`, `reference/valheim-knowledge.md`). They state intent and game facts at a fraction of the code cost. About 0.25 M tokens in total; read the `valheim-knowledge.md` file whole (39 K) and the rest through a subagent each.
3. **Deep-read one module per subagent, with a fixed question list** (below). A subagent gets a repomix pack of its module only, compressed, comments kept (the comments hold the game facts), and returns a short list: idea, why it helps us, the game fact it rests on, where we would put it, test sketch. It returns no code.
4. **Verify game facts against our own decompile** before adopting anything (ilspycmd is already set up). A fact that the friend states and the decompile contradicts is dropped and noted.
5. **Merge into `plans/next-up.md`** as ranked candidates with a size estimate. Adopt in small commits with game-free tests, as in 0.6.0 / 0.9.0.

## Packing commands

Run from the clone. Output goes to the scratchpad, never into this repository.

```bash
# the per-module pack used by one subagent (example: survival + husbandry + provision)
repomix --include "VhExt/Survival.cs,VhExt/World/Husbandry*.cs,VhExt/World/Provision*.cs" \
        --compress --remove-empty-lines -o "$SCRATCH/survival.xml" --no-security-check

# the skeleton of everything outside the tests (1.13 M tokens, for grep, not for reading)
repomix --include "VhExt/**" --ignore "VhExt/Checks/**" \
        --compress --remove-comments --remove-empty-lines -o "$SCRATCH/skeleton.xml" --no-security-check

# size check before handing a pack to anyone
repomix --token-count-tree 20000 --compress -o /dev/null --no-security-check
```

Rules: always pass `--ignore` for `tools/fixtures/**`, `plans/**`, `data/**`, `docs/*.png`, `docs/history/**`. Keep each pack under 150 K tokens. If a single file is over that (`Level.cs`), split by region: read its type and member index first, then pull line ranges with `sed -n`.

## Work packages

Order is by expected value to ValheimMCP, highest first. Token figures are the compressed source of the named files.

| # | Package | Files | Tokens | Questions |
|---|---|---|---|---|
| 1 | Survival and husbandry | `Survival.cs`, `World/Husbandry*`, `Provision*` | ~0.25 M | What state does the bot read to know it is hungry, cold, wet, resting? Which game fields? Does any of it help `death_state`, `husbandry_state`, `players`? |
| 2 | Fight and threat | `Fight.cs`, `World/Threat*` | ~0.15 M | How are threats ranked? Which creature facts (reach, speed, flinch) are tabulated? Candidate for a `threats_near` reader. |
| 3 | Resources and crafting | `Resources.cs`, `World/Resource*`, `Craft*` | ~0.2 M | Station requirements, upgrade levels, recipe lookup, inventory maths. Candidate for `can_craft` / `missing_for` readers. |
| 4 | Level, landscape, terrain | `Level.cs`, `World/Landscape*`, `Site.cs` | ~0.5 M | Terrain levelling, slopes, site choice. Check against `plan_check` ground handling and `terrain_*` tools. Read as index plus ranges. |
| 5 | Navigation and grid | `Nav.cs`, `World/Grid*`, `GridCore`, `Stands*` | ~0.2 M | The walker was read; read the grid and the reachability (unreachable scenes) logic. Candidate: `reachable` reader for `plan_check`. |
| 6 | Questions and framers | `Runtime/Questions.cs`, `Live/*` | ~0.35 M | How state is framed for the model. Candidate: terse state digests for our readers. Jev-specific parts are skipped. |
| 7 | Social | `Social.cs`, `World/Social.cs` | ~0.1 M | Chat and NPC rules beyond the chat rules already adopted. |
| 8 | World facts | `World/Facts.cs` (237 KB) | ~0.06 M indexed only | Treat as a table of game facts. Extract the index of fact names; read only the facts that match packages 1-5. |
| 9 | Design kernel and validator | `Design/*` | ~0.36 M | Skipped on purpose in the first review. Read `DValidator.cs` only, for structural rules that `plan_check` lacks. |
| 10 | Live scripts and acceptance | `tools/live/*`, `tools/gates/*`, `tools/mcpcheck.ps1` (132 K, index only) | ~0.1 M | Which acceptance checks they run; steal the list of checks, not the scripts. |
| 11 | Audit notes | `docs/audit/*` | ~0.14 M | Real bugs the friend hit live. Read all through one subagent; each bug is a test case for us. Highest value per token. |

Do packages 11, 2 and 1 first: the audit notes are cheap and name real failures, and those two modules are the most likely to yield small readers.

## Subagent brief (fixed template)

- Goal: list ideas ValheimMCP can use from module X. State what ValheimMCP already has (name the tools from `docs/TOOLS.md`).
- Inputs: the pack file path, `docs/TOOLS.md`, the review file `2026-10-clauden-jevheim-review.md`.
- Output limit: 25 lines. Each line: idea; game fact it relies on; our file for it; effort S/M/L; whether it can be tested without the game.
- Forbidden: copying code, quoting more than a short phrase, listing ideas already adopted in 0.6.0 / 0.9.0.
- Must say what it did not read.

Run packages in parallel, at most four at a time, as general-purpose agents on a medium model. The main thread reads only their summaries.

## Budget

Packages 1-11 packed come to about 2.5 M tokens if all are read. Reading only the compressed packs of 1, 2, 3, 5, 7 and the audit notes (11) is about 1.0 M tokens across subagents. The main thread sees under 30 K tokens of summaries. Set a hard stop at 1.5 M tokens across subagents unless a package turns up something large.

## Stop conditions

- A package returns nothing new after reading its docs: stop that package, do not read its code.
- An idea needs a game feature we cannot read (an unexposed field): record it in `open-questions.md`, move on.
- Anything that needs a third-party service: record, do not adopt (decision 0002).

## Also worth doing once

- Run the friend's `tools/gates/*` checks only by reading them. They run against a live game and the machine is not free.
- Re-pull the repository before starting; it changes daily (fixtures are dated 2026-10-11).
- Keep the clone and the packs in the scratchpad. Never commit them here.
