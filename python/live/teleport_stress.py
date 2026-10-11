"""Teleport reliability test: alternate between far castle spots, assert the player is really there each time (the old teleport_and_wait could return with the player elsewhere)."""
import json, os, sys, time; sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
from hubner import *
import hubner
# HUBNER_TEST_SPOTS='[[x, z, y_or_null], ...]': far-apart spots in YOUR world (the character really is teleported there)
spots = [tuple(q) for q in json.loads(os.environ.get('HUBNER_TEST_SPOTS') or 'null') or sys.exit('set HUBNER_TEST_SPOTS to a JSON list of [x, z, y|null] spots')]
t0 = time.time(); bad = 0
for i in range(2):
    for x, z, y in spots:
        teleport_and_wait(x, z, y=y, timeout=120, radius=40, min_instances=0)
        ok = hubner._arrived(x, z, y); bad += not ok
        print('OK  ' if ok else 'FAIL', (x, z), [round(v, 1) for v in player_state()['pos']], flush=True)
print('teleports', 2 * len(spots), 'failed', bad, f'{time.time() - t0:.0f}s')
