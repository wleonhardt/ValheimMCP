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

### Live verification of the ClaudeNJevHeim adoption (needs live test)

Implemented with game-free tests (see CHANGELOG 0.6.0 / 0.9.0); none of it has run in the game. Check in this order:

1. `ext_status`: build stamps show the right commit and `dirty`; hot-reload the extension a few times and watch `reloads` and `droppedCalls`; build an extension against a newer core and check it is refused with the old one still loaded (**verify `Module.ResolveMember` on Valheim's Mono**: the unit test runs on .NET).
2. Watchdog: run a long `plan_apply` synchronously and look for a "main thread busy for ... ms in plan_apply" line in the BepInEx log.
3. Journal across a restart: `plan_apply`, restart the server, `undo_group` the plan; the pieces must go. Spawn a plain piece with the client `spawn`, restart, `undo`; also check an ambiguous case is refused.
4. Reload ledger: start a sliced `plan_apply` job of several hundred pieces, rebuild the extension mid-run; the new load must resume it (`status.resumeNote`), and a game restart must not.
5. Walker: walk past a ward-locked door and a keyed door (must not loop, `lockedDoor` in `walk_status`); a normal door opens only when on the route; steer by hand during a walk (the walker yields for 1.5 s); a steep bank of 40-55 degrees is climbed at a run; a portal ends the walk (`teleported`); the view no longer swings.
6. Ownership: `zdo_delete` of a tombstone, of a piece inside a ward, of a chest with items, and of one piece of a creator-0 ruin must be refused; `neighbours` lists them; `plan_apply` inside a ward reports `wardRefused`.
7. `world_events`: build something as a player on the server, wait 30 s, read `structure_appeared` with the right player; a `plan_apply` shows as "the server's own tools"; join and leave; die and read `player_died`.
8. `plan_check`: a wooden tower of 9 posts reports the top ones `unsupported`; compare with what the game really does (build it on the test world and see what falls); stone and wood differ as in the unit test; check the ground note when terrain was edited.
9. Readers: `locations_near`, `biome_at`, `fires` (fuel against an in-game fire), `husbandry_state` (feed an animal, watch `fedSecondsLeft`), `death_state`, `cost_mode` against the world options, `players` (`inBed`, `dead`).
10. Chat: type an injection attempt in game chat and read it with `chat_tail` (`untrusted`, `hints`); `chat_send` twice within 6 s must refuse the second.

### Next candidates (not done)

- Fix the 78 silent catches over time (`python3 scripts/lint_catch.py --list`), lowering the baseline as you go.
- From the review but skipped on purpose: the design generator, work fronts and stands, the lease, the Jev judge (see `research/2026-10-clauden-jevheim-review.md`).
- Plan how to read the rest of the friend's repository (`research/2026-10-clauden-jevheim-inspection-plan.md`).

### Other

- Decide whether `log_tail` (file) and `get_log` (ring buffer) should merge, and whether `Render.Ex` should share the core's camera (see open questions).

## Done

- 2026-10-11: review fixes applied in code, docs, CI, Python (branch `review-fixes`); see `decisions/0001-review-fixes.md` and the top section of `CHANGELOG.md`.
- 2026-10-11: the 14 items of the ClaudeNJevHeim adoption list implemented with tests (see `decisions/0002-adopting-from-clauden-jevheim.md`); live verification above.
