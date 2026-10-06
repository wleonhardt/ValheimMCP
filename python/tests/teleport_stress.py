"""Teleport reliability test: alternate between far castle spots, assert the player is really there each time (the old teleport_and_wait could return with the player elsewhere)."""
import sys, time; sys.path.insert(0, '..'); sys.path.insert(0, '../../valheim-castle')
from hubner import *
import hubner
spots = [(-347, -510, 49.2), (-436, -489, None), (-336, -416, 52.0), (-462, -490, 34.0), (-396, -461, 49.2), (-425, -530, None), (-330, -440, None), (-368, -489, 49.2)]
t0 = time.time(); bad = 0
for i in range(2):
    for x, z, y in spots:
        teleport_and_wait(x, z, y=y, timeout=120, radius=40, min_instances=0)
        ok = hubner._arrived(x, z, y); bad += not ok
        print('OK  ' if ok else 'FAIL', (x, z), [round(v, 1) for v in player_state()['pos']], flush=True)
print('teleports', 2 * len(spots), 'failed', bad, f'{time.time() - t0:.0f}s')
