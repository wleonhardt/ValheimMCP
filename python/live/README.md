# Live scripts

These run against a real game: the sandbox client in a world (port 8731) and, for `plan_extras_test.py`, the dedicated-server twin with its write gate. They teleport the character and
write scratch objects, so they are not part of CI. The offline tests are in `../tests` (`python3 -m unittest discover -s python/tests`).

| Script | Needs |
|---|---|
| `env_test.py` | sandbox client (time/weather override round trip) |
| `plan_extras_test.py` | server twin + `HUBNER_SERVER_SSH`, `HUBNER_GATE_FLAG`; optional `HUBNER_TEST_X/Z/Y` (a quiet spot) |
| `teleport_stress.py` | sandbox client; `HUBNER_TEST_SPOTS='[[x, z, y|null], ...]'` |
| `chat_watch.py [minutes]` | any endpoint that has `chat_tail` |

## Acceptance runner

`accept.py` is the one command for "does the build we deployed work": a table of cases (`cases.py`), each pass / fail / skipped / blocked.

```
HUBNER_URL=http://127.0.0.1:8731/mcp HUBNER_LIVE_WORLDS=TestWorld python3 python/live/accept.py --list
HUBNER_URL=... HUBNER_LIVE_WORLDS=TestWorld python3 python/live/accept.py --dry-run      # read-only cases run, acting cases check their preconditions
HUBNER_URL=... HUBNER_LIVE_WORLDS=TestWorld HUBNER_TEST_X=120 HUBNER_TEST_Z=-340 python3 python/live/accept.py
python3 python/live/accept.py --only plan,remove-collapse
```

- `live_guard()` (in `hubner.py`) refuses to run unless the endpoint is explicit (`HUBNER_URL` or `--sandbox`), the game is in a world and the world's name is on `HUBNER_LIVE_WORLDS`. Every script that acts on a game starts with it.
- A case that cannot run here (no bed, no chat yet, a prefab missing, no `HUBNER_TEST_X/Z`) is SKIPPED with the reason: it never passes by default and never fails.
- A case that acts builds its own scenario at `HUBNER_TEST_X/Z` (a quiet, flat spot) and removes it; the runner then checks that the extension dropped no calls, reported no main-thread stall, has no journal error and the plan list is as before.
- Reports go to `python/live/reports/accept-<commit>.md` and `.json` (git-ignored). Exit code = number of failed cases (a blocked guard counts as one).
- The offline tests of the runner and the cases are in `../tests/test_accept.py`.

## Scene capture

`capture_scene.py NAME X Z [RADIUS] [NOTE]` saves the world round a point as a unit-test fixture (`tests/ValheimMCP.Tests/fixtures/scenes/NAME.scene.json`). Capture a collapse, a surprise from `plan_check`, or a stable build: `dotnet test` then checks our support model against the support values the game recorded for the same pieces. When they disagree the scene is the bug report: fix the model and commit the scene with the fix. Pieces within 12 m of the capture edge are not judged (their supporters may lie outside it), so capture with a radius at least 12 m bigger than what you care about.
