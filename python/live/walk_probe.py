"""Measures what the character really climbs: the lip height it walks over, the lip it hops, the slope it walks and runs up. Builds floating wooden platforms at a spot of your choice (they are removed again) and walks the sandbox character over them with the walker.
  HUBNER_URL=http://127.0.0.1:8731/mcp HUBNER_LIVE_WORLDS=World HUBNER_TEST_X=-280 HUBNER_TEST_Z=-300 python3 python/live/walk_probe.py [lip|hop|slope|all|bench]
Floors are 2 x 2 m wood_floor pieces (collider 0.13 m thick, top 0.097 m over the pivot); they float, so the result does not depend on the terrain."""
import sys, os, time, math, json
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import hubner
hubner.live_guard()
t = hubner.tool
X0 = float(os.environ.get('HUBNER_TEST_X', -280)); Z0 = float(os.environ.get('HUBNER_TEST_Z', -300))
TOP = 0.097                                                           # a wood_floor's top over its pivot
PLAN = 'walk-probe'


def ground_max():
    return max(t('terrain_info', x=X0 + dx, z=Z0 + dz)['ground'] for dx in (-6, 0, 6, 12) for dz in (-8, 0, 8))


def floor(x, y, z, key, rot=None):
    it = {'prefab': 'wood_floor', 'x': round(x, 3), 'y': round(y, 3), 'z': round(z, 3), 'key': key}
    if rot: it['rot'] = rot
    return it


def apply(items):
    r = t('plan_apply', plan=PLAN, items=items, order='plan')
    if r.get('errors'): raise RuntimeError(r)
    return r


def cleanup():
    try: t('plan_remove', plan=PLAN, allowCollapse=True)
    except Exception: pass


def put_player(x, y, z):
    t('teleport', x=x, y=y, z=z, snap='none'); time.sleep(2.5)


def walk(goal, timeout=14, **kw):
    t('walk_to', points=[goal], arrive=1.0, timeout=timeout, nav=False, stuckSeconds=2, **kw)
    t0 = time.time(); s = {}
    while time.time() - t0 < timeout + 4:
        time.sleep(0.4); s = t('walk_status')
        if not s.get('running'): break
    return s


def rest():
    """A jump costs stamina and a tired character jumps a weaker jump: wait until it is full before a trial."""
    for _ in range(40):
        st = t('player_status')
        if st['stamina'] >= st.get('maxStamina', st['stamina']) - 0.5 or st['stamina'] >= 49.5: return
        time.sleep(1)


def platform(y0, cols, key='p'):
    items = []
    for i in range(cols):
        for j in range(-3, 3):
            items.append(floor(X0 + 2 * i + 1, y0, Z0 + 2 * j + 1, f'{key}{i}_{j}'))
    return items


def lip_trial(y0, h, **kw):
    items = platform(y0, 3) + [floor(X0 + 6 + 2 * i + 1, y0 + h, Z0 + 2 * j + 1, f's{i}_{j}') for i in range(2) for j in range(-3, 3)]
    apply(items); time.sleep(1.0)
    put_player(X0 + 0.8, y0 + TOP + 0.1, Z0)
    s = walk([X0 + 9.0, Z0], **kw)
    return s['result'], round(s['pos'][0] - X0, 1), s.get('lastStall', ''), round(s['pos'][1] - (y0 + TOP), 2)


def lip():
    y0 = ground_max() + 2.5; out = []
    for run in (False, True):
        print('LIP, walking' if not run else 'LIP, running', flush=True)
        for h in (0.35, 0.36, 0.37, 0.38, 0.39, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0):
            r = lip_trial(y0, h, jump=False, run=run); ok = r[0] == 'arrived' and r[1] > 7
            out.append({'kind': 'lip', 'run': run, 'h': h, 'ok': ok, 'result': r}); print(f'  h={h:.2f} {"PASS" if ok else "stop"} {r}', flush=True)
    return out


def hop():
    y0 = ground_max() + 2.5; out = []
    print('HOP (jump allowed)', flush=True)
    for h in (1.4, 1.45, 1.5, 1.55, 1.6, 1.7, 1.8, 1.9, 2.0, 2.2):
        r = lip_trial(y0, h, jump=True, run=False, hopMax=3.0); ok = r[0] == 'arrived' and r[1] > 7
        out.append({'kind': 'hop', 'h': h, 'ok': ok, 'result': r}); print(f'  h={h:.2f} {"PASS" if ok else "stop"} {r}', flush=True)
    return out


def ramp_items(y0, deg, n=3):
    a = math.radians(deg); items = platform(y0, 2)
    # a ramp of n floors tilted about the z axis, running up from the platform's east edge (x = X0 + 4); the pivot of a tilted floor is its centre
    for i in range(n):
        cx = X0 + 4 + (1 + 2 * i) * math.cos(a); cy = y0 + (1 + 2 * i) * math.sin(a)
        for j in range(-3, 3): items.append(floor(cx, cy, Z0 + 2 * j + 1, f'r{i}_{j}', rot=[0, 0, deg]))
    top_x = X0 + 4 + 2 * n * math.cos(a); top_y = y0 + 2 * n * math.sin(a)
    for i in range(2):
        for j in range(-3, 3): items.append(floor(top_x + 2 * i + 1, top_y, Z0 + 2 * j + 1, f't{i}_{j}'))
    return items, top_x, top_y


def slope():
    y0 = ground_max() + 2.5; out = []
    for run in (False, True):
        print('SLOPE, walking' if not run else 'SLOPE, running', flush=True)
        for deg in (30, 35, 36, 37, 38, 40, 60, 65, 70, 75, 80, 85):
            cleanup(); time.sleep(0.5)
            items, tx, ty = ramp_items(y0, deg); apply(items); time.sleep(1.0)
            put_player(X0 + 1.0, y0 + TOP + 0.1, Z0)
            s = walk([tx + 2.0, Z0], timeout=16, jump=False, run=run); ok = s['result'] == 'arrived'
            r = (s['result'], round(s['pos'][0] - X0, 1), s.get('lastStall', ''), round(s['pos'][1] - y0, 2))
            out.append({'kind': 'slope', 'run': run, 'deg': deg, 'ok': ok, 'result': r}); print(f'  {deg} deg {"PASS" if ok else "stop"} {r}', flush=True)
    return out


# What the walker with its default settings must do over each obstacle: a regression bench for the walker's constants (run it after changing walk_to or WalkRules).
# (kind, size, must arrive, walker options). The slope rows run: on a floating ramp the walker's 'route goes up' test reads the terrain, so it never chooses a run-up by itself there.
BENCH = [('lip', 0.30, True, {}), ('lip', 0.37, True, {}), ('lip', 0.9, True, {}), ('lip', 1.3, True, {}), ('lip', 1.8, False, {}), ('slope', 30, True, {}), ('slope', 37, True, {}), ('slope', 60, True, {'run': True}), ('slope', 85, False, {'run': True})]


def bench():
    y0 = ground_max() + 2.5; out = []; bad = 0
    print('BENCH, default walker (jump on, no run)', flush=True)
    for kind, v, expect, opts in BENCH:
        t0 = time.time(); rest()
        if kind == 'lip':
            r = lip_trial(y0, v, **opts); ok = r[0] == 'arrived' and r[1] > 7
        else:
            cleanup(); time.sleep(0.5)
            items, tx, ty = ramp_items(y0, v); apply(items); time.sleep(1.0)
            put_player(X0 + 1.0, y0 + TOP + 0.1, Z0)
            s = walk([tx + 2.0, Z0], timeout=16, **opts); ok = s['result'] == 'arrived'; r = (s['result'], round(s['pos'][0] - X0, 1), s.get('lastStall', ''), 0)
        good = ok == expect; bad += not good
        out.append({'kind': kind, 'v': v, 'expect': expect, 'ok': ok, 'seconds': round(time.time() - t0, 1), 'result': r}); print(f'  {kind} {v}: {"arrived" if ok else "blocked"} (expected {"arrive" if expect else "block"}) {"OK" if good else "MISMATCH"} {r}', flush=True)
    print('bench:', 'ALL OK' if not bad else f'{bad} mismatches')
    return out


if __name__ == '__main__':
    what = sys.argv[1] if len(sys.argv) > 1 else 'all'
    res = []
    hubner.go_to(X0, Z0 - 4, snap='ground')                         # the terrain around the spot has to be loaded
    try:
        if what in ('lip', 'all'): res += lip()
        if what in ('hop', 'all'): res += hop()
        if what in ('slope', 'all'): res += slope()
        if what == 'bench': res += bench()
    finally:
        cleanup()
    os.makedirs(os.path.join(os.path.dirname(__file__), 'reports'), exist_ok=True)
    json.dump(res, open(os.path.join(os.path.dirname(__file__), 'reports', 'walk-probe.json'), 'w'), indent=1)
    print('done', len(res), 'trials')
