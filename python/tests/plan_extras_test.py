"""plan_apply extras (ints, ctag, label) and plan_remove filters, on a scratch plan at a quiet spot. Run with the server twin tunnel up and the sandbox client in the world."""
import sys, os, time, json; sys.path.insert(0, '..')
os.environ.setdefault('HUBNER_URL', 'http://127.0.0.1:8741/mcp')
import hubner; from hubner import *
X, Z, Y = -250.0, -380.0, 45.0
items = [{'prefab': 'darkwood_gate', 'x': X, 'y': Y, 'z': Z, 'yaw': 0, 'ctag': 'scratch:gate', 'ints': {'state': 1}},
         {'prefab': 'piece_chest_blackmetal', 'x': X + 6, 'y': Y, 'z': Z, 'yaw': 0, 'ctag': 'scratch:chest', 'label': 'TEST\nLABEL'}]
def apply(its, **kw): return tool('plan_apply', plan='scratch-b', items=its, confirm='server-write', **kw)
def find(prefab): return [o for o in tool('objects', prefab=prefab, x=X, z=Z, radius=15, limit=20)['objects']]
ok = True
def check(name, cond, extra=''):
    global ok; ok &= bool(cond); print('OK  ' if cond else 'FAIL', name, extra, flush=True)
with server_writes():
    r = apply(items); check('spawn', r['spawned'] == 2 and r['errors'] == 0, {k: r[k] for k in ('spawned', 'updated', 'labelPending')})
    g = find('darkwood_gate')[0]; d = tool('zdo_dump', id=g['id'])['fields']
    check('ints state=1 on spawn', d.get('state') == 1, d.get('state'))
    items[0]['ints'] = {'state': 0}
    r = apply(items); check('state change counts as update', r['updated'] == 1, r['updated'])
    check('ints state=0 after update', tool('zdo_dump', id=g['id'])['fields'].get('state') == 0)
    r = apply(items); check('idempotent', r['unchanged'] == 2 and r['updated'] == 0, {k: r[k] for k in ('unchanged', 'updated')})
    print('moving the sandbox client next to the scratch chest so the game creates its plaque ...')
    try: go_to_ = None
    except Exception: pass
os.environ['HUBNER_URL'] = 'http://127.0.0.1:8731/mcp'
import importlib, mcp; importlib.reload(mcp); importlib.reload(hubner)
hubner.go_to(X + 6, Z + 4, y=Y + 6, snap='none'); time.sleep(8)
os.environ['HUBNER_URL'] = 'http://127.0.0.1:8741/mcp'; importlib.reload(mcp); importlib.reload(hubner)
from hubner import *
with server_writes():
    r = apply(items); check('plaque labelled (client nearby)', r['labelled'] >= 1 or r['labelPending'] == 0, {k: r[k] for k in ('labelled', 'labelPending')})
    signs = [o for o in tool('objects', prefab='sign', x=X + 6, z=Z, radius=3, limit=10)['objects']]
    check('plaque text', any(s.get('text') == 'TEST\nLABEL' for s in signs), [s.get('text') for s in signs])
    r = tool('plan_remove', plan='scratch-b', ctag='scratch:chest', dry=True); check('remove dry by ctag', r['removed'] == 1, r)
    r = tool('plan_remove', all=True, ctag='scratch:', confirm='server-write'); check('remove all plans by ctag', r['removed'] == 2 and 'scratch-b' in r['byPlan'], r)
    check('gone', not find('darkwood_gate') and not find('piece_chest_blackmetal'))
    for s in tool('objects', prefab='sign', x=X + 6, z=Z, radius=3, limit=10)['objects']:
        tool('zdo_delete', ids=[s['id']], force=True, confirm='server-write')
print('ALL OK' if ok else 'FAILURES')
