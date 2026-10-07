"""plan_apply extras (ints, floats, ctag, label, stable keys), sliced jobs, undo of ints, and plan_remove filters, on a scratch plan at a quiet spot.
Run with the server twin tunnel up and the sandbox client in the world:  HUBNER_SERVER_SSH=user@host HUBNER_GATE_FLAG=/path/ALLOW_SERVER_WRITES python3 plan_extras_test.py"""
import sys, os, time; sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
from hubner import twin, sandbox
X, Z, Y = -250.0, -380.0, 45.0
items = [{'prefab': 'darkwood_gate', 'x': X, 'y': Y, 'z': Z, 'yaw': 0, 'ctag': 'scratch:gate', 'ints': {'state': 1}, 'key': 'scratch:gate:1'},
         {'prefab': 'piece_chest_blackmetal', 'x': X + 6, 'y': Y, 'z': Z, 'yaw': 0, 'ctag': 'scratch:chest', 'label': 'TEST\nLABEL', 'key': 'scratch:chest:1'},
         {'prefab': 'piece_groundtorch', 'x': X + 3, 'y': Y, 'z': Z + 2, 'yaw': 0, 'ctag': 'scratch:torch', 'key': 'scratch:torch:1'}]
def apply(its, **kw): return twin.plan_apply('scratch-b', its, job=False, **kw)
def find(prefab): return twin.objects(prefab=prefab, x=X, z=Z, radius=15, limit=20)['objects']
ok = True
def check(name, cond, extra=''):
    global ok; ok &= bool(cond); print('OK  ' if cond else 'FAIL', name, extra, flush=True)
with twin.writes():
    r = apply(items); check('spawn', r['spawned'] == 3 and r['errors'] == 0, {k: r[k] for k in ('spawned', 'updated', 'labelPending', 'sweep')})
    g = find('darkwood_gate')[0]; d = twin.zdo_dump(g['id'])['fields']
    check('ints state=1 on spawn', d.get('state') == 1, d.get('state'))
    t = find('piece_groundtorch')[0]; check('fireplace gets fuel on spawn', (twin.zdo_dump(t['id'])['fields'].get('fuel') or 0) > 0)
    items[0]['ints'] = {'state': 0}
    r = apply(items); check('state change counts as update', r['updated'] == 1, r['updated'])
    check('ints state=0 after update', twin.zdo_dump(g['id'])['fields'].get('state') == 0)
    r = apply(items); check('idempotent', r['unchanged'] == 3 and r['updated'] == 0, {k: r[k] for k in ('unchanged', 'updated')})
    items[0]['y'] = Y + 1.0                                                   # stable key: a height change is a MOVE, not delete+spawn
    r = apply(items); check('height change with key = moved', r['moved'] == 1 and r['spawned'] == 0 and r['deleted'] == 0, {k: r[k] for k in ('moved', 'spawned', 'deleted')})
    items[0]['y'] = Y; apply(items)
    r = twin.undo_group(group='scratch-b'); check('undo whole group', r['undone'] >= 1 and not [e for e in r['errors'] if 'gone' not in e], r)     # 'gone' = an older op of this group whose object was removed since: expected on a reused scratch plan
    check('gone after undo', not find('darkwood_gate') and not find('piece_chest_blackmetal'))
    r = twin.plan_apply('scratch-b', items, job=True); check('sliced job applies', r['spawned'] == 3, r.get('spawned'))
    r = twin.doors_set(1, x=X, z=Z, radius=5); r2 = twin.undo_group(group='doors_set'); g = find('darkwood_gate')[0]
    check('doors_set undo restores state', r['changed'] == 1 and twin.zdo_dump(g['id'])['fields'].get('state') == 0)
print('moving the sandbox client next to the scratch chest so the game creates its plaque ...')
sandbox.goto(X + 6, Z + 4, y=Y + 6, snap='none'); time.sleep(8)
with twin.writes():
    r = apply(items); check('plaque labelled (client nearby)', r['labelled'] >= 1 or r['labelPending'] == 0, {k: r[k] for k in ('labelled', 'labelPending')})
    signs = twin.objects(prefab='sign', x=X + 6, z=Z, radius=3, limit=10)['objects']
    check('plaque text', any(s.get('text') == 'TEST\nLABEL' for s in signs), [s.get('text') for s in signs])
    r = twin.plan_remove('scratch-b', ctag='scratch:chest', dry=True); check('remove dry by ctag', r['removed'] == 1, r)
    r = twin.tool('plan_remove', all=True, ctag='scratch:', confirm='server-write'); check('remove all plans by ctag', r['removed'] == 3 and 'scratch-b' in r['byPlan'], r)
    check('gone', not find('darkwood_gate') and not find('piece_chest_blackmetal'))
    for s in twin.objects(prefab='sign', x=X + 6, z=Z, radius=3, limit=10)['objects']:
        twin.zdo_delete([s['id']], force=True, forceConfirm='player-built')
print('ALL OK' if ok else 'FAILURES')
