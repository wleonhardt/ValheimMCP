# 0001 Review fixes (2026-10)

## Context

A full review of the repository found gates that did not gate (the server write gate was skipped by `dry:true`, `tools.write` did not cover `run_command`, `teleport` was not a write tool), a path traversal in `snapshot`, an undo journal that could lose entries, unbounded zone scans, and a stack overflow reachable through JSON nesting. The game libraries were decompiled to settle three open points (`skipAllowedCheck`, the sector index of out-of-grid positions, how `ZDO.SetPosition` re-files an object).

## Decisions

- Gates are decided in pure code with tests (`WriteGate`, `CommandPolicy`, `RequestPolicy`, `SafeNames`, `WorldBounds`, `JsonRpc`). Handlers call them; they do not re-implement them.
- `tools.write: false` is the master switch for anything that changes the world, including the console (`run_command`, `POST /command`). `tools.deny` applies to built-in tools too. `tools.profile` stays a convenience for `tools/list`, not access control.
- `dry:true` skips the server gate only for handlers that declare they honour it. Others refuse without the flag.
- Fail closed: an unreadable config stops the listener; a non-loopback bind without a token is refused.
- God mode is for `hubner.sandboxNames` only, and the default list is empty.
- Secrets are not tool arguments (server password comes from `HUBNER_AUTOJOIN_PASSWORD`). Player IPs and whispers are off by default.
- Undo never discards an entry it could not undo. Undoing a delete rewrites ids in older entries (`remap` line in the journal).
- Sector lists are repaired by re-filing the object and marking its chunk dirty (the world is saved per dirty chunk), never by dropping the entry.
- A sliced job is the only writer while it runs; `job_cancel` stops it.
- Behaviour changes are listed first in `CHANGELOG.md`.

## Why

Each change closes a way for a tool call, a typo or a hostile local process to damage a world or the game process without the operator having asked for it, or removes a way the tools could lose the record needed to undo their own work.

## What would change it

A real use case for a read-only console, or a game update that changes the sector or save internals (the repair code reads private fields by reflection and logs once when a lookup fails; `selftest` shows it).
