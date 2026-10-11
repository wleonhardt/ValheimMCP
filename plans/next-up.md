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

### Live verification of the Tier 1 work (0.7.0 core / 0.10.0 extension)

Everything below compiled and passed game-free tests; none of it has run in the game.

11. `plan_check` on a real build: the tower of 9 posts again, plus a hearth with a cauldron on it (no fire-clearance warning), a brazier on a floor stand (the pathblocker collider no longer counts), a campfire under a beam 0.8 m up (`fire-blocked`, and in game the fire really goes out), a workbench with extensions at 4 and 6 m (`extension-no-station`), two extensions 1 m apart (`extension-spacing`), a piece on a rock (grounded).
12. `bed_check`: a bed with a roof and no fire (`canClaim` true, `canSleep` false, `firstFailure` `$msg_bednofire`; the game says the same when you try), then with a fire; `sleepNow` outside the afternoon/night window.
13. `plan_apply`: an item with no `y` is refused; a 400-piece roof-and-walls plan as a sliced job builds floor first (watch `job_status`), cancel it half way and check nothing stands on nothing 60 s later.
14. `plan_remove dry:true` on a foundation under a built floor: `wouldCollapse` lists the pieces; the real call is refused; `allowCollapse:true` removes them and the floor really falls. `zdo_delete` the same.
15. Location prefab flags on the dedicated server: `plan_check` over a ruin or a start temple. A `?` after the location name means the prefab could not be read there (the answer is then an assumption) and the extension's `selftest`/log carries the reason.
16. Walker: `walk_to` a bed on an upper floor with a y while standing below: ends `wrong_storey` after 3 s; `arriveHeight:0` arrives. A bank of 30 degrees is not hopped as a ledge. An overloaded character at 0 stamina ends `blocked` (encumbered).
17. Journal: undo in the same world still works; join a different world and undo an old group (refused: other world); on a client far from a spawned piece, `undo` says "cannot tell" and keeps the entry.
18. Chat: type an injection split over two lines, a line naming `zdo_delete`, four lines in 10 s, a line with your own name in it; read `chat_tail` for `split_manipulation`, `tool_talk`, `spam`, `to:me`. `chat_send` with `to` of an untrusted player and a coordinate pair is refused.
19. `tool_stats` after a session: the slow tools come first; `log_tail` folds repeats; `objects digest:true` over a base.
20. `Env` hold released on hot reload: `time_set` then rebuild the extension: the time is free again.

### Live verification of the Tier 2 readers and the acceptance runner (0.8.0 core / 0.11.0 extension)

Run `HUBNER_URL=... HUBNER_LIVE_WORLDS=<test world> HUBNER_TEST_X/Z=<quiet spot> python3 python/live/accept.py` first: it covers most of the below. What it cannot judge:

21. The runner itself: `--dry-run` skips the acting cases and says why; a wrong world name gives BLOCKED; every case that skips says what it needs. Fix the cases, not the expectations, when one fails.
22. `world_clock` on the dedicated server against the client's `env_state` at dusk; sleep with one of two players in bed (`nightSkips` stays false and names who is out).
23. `recipe_info`/`missing_for` for Bronze and a bronze axe with real chests: compare the steps with what the crafting UI asks. `plan_materials` against the build UI for a small house. The chest decode (`Inventory.Load`) on a chest with every kind of item.
24. `stations`: a workbench with 2 extensions at 4 and 6 m: the second does not count. Compare the level with the crafting UI.
25. `smelters`: queue 10 ore, watch `secondsToEmpty` and `fuelNeeded` against the real smelter; a kiln burns no fuel; `fuelPressesAllowed` against how many presses the game accepts.
26. `creature_info` for Boar, Troll, Greydwarf: modifiers and attack reach against the in-game wiki; the `againstPlayer` hit against a real hit with known armour.
27. `base_value` beside a fire, a workbench and a ward: compare `value` with the base value the character reports (`charactersNearby`); the bounding-sphere approximation for box areas.
28. `husbandry_state` with two tame boars: a breeding blocker disappears when the partner comes within 3 m; the expected time against a stopwatch.
29. `plan_check` ground: over an edited pad (flattened by a hoe) the heights are the pad's, not the generator's; tilt on a steep bank uses the triangle.

30. Scene capture: `capture_scene.py` over a real base (radius 40): the saved scene has `withRecordedSupport` > 0 on a server that owns the pieces; `dotnet test` on it. Every disagreement is a defect in the support model or in our collider reading (mesh colliders are bounding boxes): decide which, fix, keep the scene.

31. `plant_check` over a prepared carrot patch: close plants block each other, a roof above gives NoSun, an uncultivated patch gives NotCultivated; compare with what the plants do in game (`farm_state` status vs the plant's tooltip). The sun ray starts at the plant's origin: a plant under a thin eave in game may differ.
32. `raid_state` at a quiet moment: the roll timer counts down; force an event with the console and read `current`; `keysOkNow` against the boss kills of the world. `threats_near` with a troll chasing: `now` and `secondsToContact`; with PassiveMobs on nothing is `now`.
33. `level_check` over a hillside, then really level it with the hoe and compare the fill and cut and the stroke count; a pad 10 m under the base reports `unreachable`. Compare `levelTools` with the prefabs' real radii.
34. `cookers` with a grill of meat: seconds to done and to burnt against the real timer; `fuel_forecast` with two fires and a chest of wood: the hours of cover against a stopwatch.

35. Containers: `containers` over a base with a chest of known contents; `missing_for` and `fuel_forecast` count it; `container_set` puts an item in a chest and the game shows it in the UI (the old code wrote a key the game never read); `undo_group` of that edit and of a chest delete restores the contents.
36. Chat identity: from a second account type a line; `chat_tail` has your `id` and no `id_mismatch`. A modified client cannot be tested cheaply: read `Audit.RelayedChat2` once more against a captured RPC if one is available.
37. Walker: `walk_to` with `autoRun:false` over 50 m stays on foot; a route that passes a portal stops with `blocked` and a message; with `throughPortals:true` it walks through. After a forced stall the re-route still visits the waypoints you gave.
38. Journal: `modify` an untagged piece, restart the game, `undo`: the piece goes back (no "gone"). Undo a `delete`, restart, undo an older entry of the same piece: still found.
39. Env: `time_set` then `env_state` in the same call chain reports the asked time with `pending`; `weather_set` released after ten minutes leaves a time you set stays until its own timer; leave the world: both are released.
40. `clear_overlaps` with a footprint `r` 12 over a tree stand: the dry count matches what stands there.

### Next candidates (not done)

- Fix the 78 silent catches over time (`python3 scripts/lint_catch.py --list`), lowering the baseline as you go.
- From the review but skipped on purpose: the design generator, work fronts and stands, the lease, the Jev judge (see `research/2026-10-clauden-jevheim-review.md`).
- What the offline review left unfixed is listed in `research/2026-10-offline-code-review.md` ("Not fixed"): accepted limits and items that need the game.
- Tier 1 of `research/2026-10-clauden-jevheim-synthesis.md` is done (decompile check in `decisions/0003-decompile-check-results.md`). Not done from it: A14 (plan_check on edited ground), A16 (mesh colliders), A23 (speaker id check against the connection), A24 (`world_state` positions without a source), A26 (Dvergr wards enabled); Tier 2 not yet built: biome-spawn reader, terrain deltas, `explored_at`, `loot_near`, `situation`; Tier 4 not yet built: walk replay over scenes (held back on purpose: it would encode the walker's assumed numbers, StepUp 0.45 and the hop height, which nobody has measured; measure them first with a slope and step probe), walk bench, chat injection hook (scene capture and the support differential test exist now).

### Other

- Decide whether `log_tail` (file) and `get_log` (ring buffer) should merge, and whether `Render.Ex` should share the core's camera (see open questions).

## Done

- 2026-10-11: review fixes applied in code, docs, CI, Python (branch `review-fixes`); see `decisions/0001-review-fixes.md` and the top section of `CHANGELOG.md`.
- 2026-10-11: the 14 items of the ClaudeNJevHeim adoption list implemented with tests (see `decisions/0002-adopting-from-clauden-jevheim.md`); live verification above.
