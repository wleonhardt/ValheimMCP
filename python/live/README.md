# Live scripts

These run against a real game: the sandbox client in a world (port 8731) and, for `plan_extras_test.py`, the dedicated-server twin with its write gate. They teleport the character and
write scratch objects, so they are not part of CI. The offline tests are in `../tests` (`python3 -m unittest discover -s python/tests`).

| Script | Needs |
|---|---|
| `env_test.py` | sandbox client (time/weather override round trip) |
| `plan_extras_test.py` | server twin + `HUBNER_SERVER_SSH`, `HUBNER_GATE_FLAG`; optional `HUBNER_TEST_X/Z/Y` (a quiet spot) |
| `teleport_stress.py` | sandbox client; `HUBNER_TEST_SPOTS='[[x, z, y|null], ...]'` |
| `chat_watch.py [minutes]` | any endpoint that has `chat_tail` |
