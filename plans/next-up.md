# Next up

## Queue

### Live verification of the 2026-10 review fixes (needs live test)

Everything below compiles and the game-free parts are unit-tested, but none of it ran in a real game or on the dedicated server (the account was in use on another PC). Run these on the sandbox client and the server twin, in this order, and note failures in `open-questions.md`.

1. `selftest` and `status` on both roles: hooks installed, journal ok, `journalError` empty.
2. `curl` checks: `/health` still works; `POST /command` returns 403 with `tools.write: false` and works with `true`; a request with `Origin:` returns 403; `MCP-Protocol-Version: 2030-01-01` returns 400; a body over 8 MB returns 413.
3. `wait_for_log` with a progress token streams SSE; a catastrophic regex (`(a+)+$`) returns after about 100 ms per line and the game does not stall.
4. Server write gate: with no `ALLOW_SERVER_WRITES` file, `zdo_delete {ids:[...], dry:true}` must now refuse; `plan_apply {dry:true}` must still work.
5. `plan_apply` of a few hundred pieces as a job: `job_status` frames, no visible hitch (watch FPS), `job_cancel` mid-run, a second write tool refused while it runs.
6. Undo chain: `plan_apply`, `plan_remove`, `undo_group` of the remove, `undo_group` of the plan: no piece is left behind. Repeat with a chest that has items (contents come back).
7. `container_set` on a plan chest works and undoes; on a player's chest it refuses without `force` + `forceConfirm`; on a chest a player has open it refuses.
8. Sector lists: move a portal with `zdo_set`, save the world (`world_save`), reload: the portal is still there and listed once (`zdo_audit`).
9. `vpeer_add`, then hot-reload the extension DLL: `vpeer_list` shows no stale second copy and `selftest` passes.
10. `walk_check` on a path with a known steep slope now reports `slope too steep`.
11. `teleport` with `tools.write: false` is refused; with a non-sandbox character name the character stays mortal.
12. Client auto-join: `-hubner-autojoin host:port,profile` with `HUBNER_AUTOJOIN_PASSWORD` set; IPv6 host form `[::1]:2456`.
13. `players` shows no host by default; whispers do not appear in `chat_tail`.
14. `snapshot` with name `../journal` is refused.

### Other

- Decide whether `log_tail` (file) and `get_log` (ring buffer) should merge, and whether `Render.Ex` should share the core's camera (see open questions).

## Done

- 2026-10-11: review fixes applied in code, docs, CI, Python (branch `review-fixes`); see `decisions/0001-review-fixes.md` and the top section of `CHANGELOG.md`.
