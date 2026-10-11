# 0002 Adopting from ClaudeNJevHeim (2026-10)

## Context

A friend's repository (ChuMicro/ClaudeNJevHeim) is a client-side player bot with a large rule layer. Its review (plans/research/2026-10-clauden-jevheim-review.md) produced a ranked list. The friend's repository has no licence, so nothing of it was copied: every item was written from a description of the idea or from the decompiled game.

## Decisions

- Every rule that can be game-free is a pure class that the unit tests link: `Identity` (journal resolution), `WalkFacts`/`StallLadder`/`DoorRules`/`UserOverride`/`Pursuit` (walker), `Ownership`/`LocationRules` (what a tool may touch), `StructureDetector`/`EventLog`/`Presence` (world events), `ChatRules`/`ChatLimiter`, `JobCodes`/`ReloadLedger`/`AtomicFile`/`JsonOut`, `SupportModel`/`Obb`/`PlanChecks` (plans), `BindCheck`, `StallDetector`, the dispatcher itself. The game-side files only read the game for their facts.
- The structure model is a port of the game's own `WearNTear.UpdateSupport` (decompiled), not of the friend's re-implementation, and a test pins the known 16 m limit of a wooden column.
- ZDO ids are never an identity across a restart; `hub_plan` + `hub_key` are. An ambiguous match is refused.
- Hints on chat text warn and never decide; the model reading the text decides what to do, and trust comes from the platform id.
- The lints use a baseline (the count may only go down) instead of requiring the 78 existing silent catches to be fixed at once.
- Skipped on purpose: the Jev judge (a third-party service in a harness whose tools are deterministic and undoable), the design generator, work fronts, stands, scaffolds, the lease.

## Why

These close the failure modes the review found (an undo that silently does nothing after a restart, a walker that loops beside a locked door, a gate that did not gate) and give the host the checks a hot-reloading plugin needs (bind check, retired-call drop, stall watchdog) without a new dependency.

## What would change it

Live results in `plans/next-up.md`: the bind check relies on `Module.ResolveMember` behaving on Valheim's Mono as it does on .NET, `world_events` on the private `ZDOMan.CreateNewZDO` overload keeping its shape, `plan_check` on how close the support model stays to the game on real builds.
