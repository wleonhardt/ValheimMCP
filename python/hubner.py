"""Python client for the Hubner ValheimMCP extension tools (see hubner-mcp/ext/HubnerExt.cs).
Needs the SSH tunnel (tunnel.sh) and the sandbox client in the world. All coordinates are world x,z (y up).
  from hubner import *; terrain_info(-80,-322); objects(x=-80,z=-322,radius=30); prefab_info('stone_stair')"""
import json, os
from mcp import call, save_images


def _raw(name, args=None):
    return call(name, args or {})


def _text(r):
    return ''.join(c.get('text', '') for c in r.get('content', []) if c.get('type') == 'text')


def tool(_name, /, **args):
    """Call a tool; returns parsed JSON when the tool returns JSON, else the text. Raises on isError."""
    r = _raw(_name, args)
    t = _text(r)
    if r.get('isError'):
        raise RuntimeError(f'{_name}: {t}')
    try:
        return json.loads(t)
    except ValueError:
        return t


def terrain_info(x, z): return tool('terrain_info', x=x, z=z)
def terrain_grid(x0, z0, x1, z1, step=4, solid=False): return tool('terrain_grid', x0=x0, z0=z0, x1=x1, z1=z1, step=step, solid=solid)
def raycast(x, y, z, dx=0, dy=-1, dz=0, maxDist=500): return tool('raycast', x=x, y=y, z=z, dx=dx, dy=dy, dz=dz, maxDist=maxDist)
def objects(**kw): return tool('objects', **kw)
def object_info(id): return tool('object_info', id=id)
def prefab_info(name): return tool('prefab_info', name=name)
def prefab_search(contains, limit=100): return tool('prefab_search', contains=contains, limit=limit)
def world_state(): return tool('world_state')
def zone_state(x, z, radius=30): return tool('zone_state', x=x, z=z, radius=radius)
def stability_scan(x0, z0, x1, z1, threshold=20, limit=100): return tool('stability_scan', x0=x0, z0=z0, x1=x1, z1=z1, threshold=threshold, limit=limit)
def walk_check(points, **kw): return tool('walk_check', points=points, **kw)
def spawn(items, force=False): return tool('spawn', items=items, force=force)
def modify(edits): return tool('modify', edits=edits)
def delete(**kw): return tool('delete', **kw)
def undo(count=1): return tool('undo', count=count)
def journal(limit=30): return tool('journal', limit=limit)


def _teleport(**args):
    """tool('teleport') that survives the game's refusal: a teleport asked for within ~2 s of the previous one is ignored (the tool now reports accepted:false); retry until accepted"""
    import time
    t0 = time.time()
    while True:
        r = tool('teleport', **args)
        if not isinstance(r, dict) or r.get('accepted', True): return r
        if time.time() - t0 > 20: raise RuntimeError('teleport refused for 20 s: ' + str(r))
        time.sleep(0.5)


def _arrived(x, z, y=None, tol=6.0):
    """True when the player really stands near (x, z): not mid-teleport, within tol metres in the plane (and 8 m in height when y is given)"""
    try: s = player_state()
    except Exception: return False
    p = s.get('pos') or [1e9, 0, 1e9]
    return (not s.get('teleporting')) and abs(p[0] - x) <= tol and abs(p[2] - z) <= tol and (y is None or abs(p[1] - y) <= 8.0)


def teleport_and_wait(x, z, y=None, timeout=60, radius=30, min_instances=1):
    """Teleport the sandbox character (god mode is switched on), wait until the area is loaded AND the player is really there, then re-snap onto the ground.
    A teleport the game swallowed (seen after long walks and big syncs: the tool answered, the player never moved) is re-sent every 12 s."""
    import time
    args = {'x': x, 'z': z}
    if y is not None: args['y'] = y
    else: args['snap'] = 'ground'                 # without y or snap the tool keeps the old height; below the new ground the game silently ignores the teleport (walk_ward hung 120 s on it, 2026-10-06)
    for attempt in range(40):                    # the character respawns now and then (death, scene fade): wait for it instead of failing
        try: first = _teleport(**args); break
        except RuntimeError as e:
            if 'no local player' not in str(e) or attempt == 39: raise
            time.sleep(3)
    t0 = time.time(); sent = t0
    s = None
    time.sleep(2)
    while time.time() - t0 < timeout:
        s = zone_state(x, z, radius)
        if s['zoneLoaded'] and s['groundOk'] and s['instancesWithinRadius'] >= min_instances:
            if _arrived(x, z, y):
                if y is None and not first.get('groundSnapped', True):
                    _teleport(**args)             # ground is known now: land on it instead of hovering
                    time.sleep(1.5)
                return s
            if time.time() - sent > 12:                  # loaded but the player is elsewhere: the teleport was swallowed
                try: first = _teleport(**args)
                except RuntimeError: pass
                sent = time.time()
        time.sleep(1)
    raise TimeoutError(f'area at {x},{z} not loaded / player not there after {timeout}s: {s}')


def render(path, **kw):
    """render_ex to a PNG file (kw: x,z,y,yaw,pitch,dist,size,fov,near,ortho,orthoSize,hideAboveY,hidePrefabs,cutRadius)."""
    r = _raw('render_ex', kw)
    if r.get('isError'): raise RuntimeError(_text(r))
    n = save_images(r, path.rsplit('.', 1)[0] + '_')
    return path.rsplit('.', 1)[0] + '_0.png' if n else None


# ---- v0.2 tools -------------------------------------------------------------------------------------------
def terraform_map(x0, z0, x1, z1, cell=4, minDelta=0.15, raster=True): return tool('terraform_map', x0=x0, z0=z0, x1=x1, z1=z1, cell=cell, minDelta=minDelta, raster=raster)
def snapshot(name, x0, z0, x1, z1): return tool('snapshot', name=name, x0=x0, z0=z0, x1=x1, z1=z1)
def snapshot_diff(name, limit=15): return tool('snapshot_diff', name=name, limit=limit)
def validate_placement(items, **kw): return tool('validate_placement', items=items, **kw)
def verify_plan(items, **kw): return tool('verify_plan', items=items, **kw)


def _tiles(pieces, size=64):
    t = {}
    for q in pieces:
        t.setdefault((int(q['x'] // size), int(q['z'] // size)), []).append(q)
    return t


def _plan_items(plan_path):
    pieces = json.load(open(plan_path))['pieces']
    return [{'prefab': q['prefab'], 'x': q['x'], 'y': q['y'], 'z': q['z'], 'yaw': q.get('yaw', 0), **({'text': q['text']} if q.get('text') else {})} for q in pieces]


def verify_plan_file(plan_path, tile=64, **kw):
    """Verify a plan.json against the world tile by tile (teleports the sandbox character). Returns merged counts + examples."""
    items = _plan_items(plan_path)
    tot = {'planned': 0, 'ok': 0, 'moved': 0, 'missing': 0, 'duplicates': 0, 'wrongYaw': 0, 'wrongText': 0, 'extraOfPlanPrefabs': 0}
    ex = {}
    for (tx, tz), its in sorted(_tiles(items, tile).items()):
        cx, cz = (tx + .5) * tile, (tz + .5) * tile
        teleport_and_wait(cx, cz, timeout=120, radius=tile, min_instances=0)
        r = verify_plan(its, **kw)
        for k in tot: tot[k] += r.get(k, 0)
        for k, v in r['examples'].items(): ex.setdefault(k, []).extend(v[:3])
    tot['examples'] = ex
    return tot


def validate_plan_file(plan_path, tile=64, **kw):
    """validate_placement over a whole plan.json (teleports tile by tile so zones are loaded)."""
    items = _plan_items(plan_path)
    tot = {'checked': 0, 'clean': 0, 'overlapping': 0, 'buriedUnderGround': 0, 'thinPiecesFloating': 0, 'zoneNotLoaded': 0, 'unknownPrefab': 0}
    ex = {}
    for (tx, tz), its in sorted(_tiles(items, tile).items()):
        cx, cz = (tx + .5) * tile, (tz + .5) * tile
        teleport_and_wait(cx, cz, timeout=120, radius=tile, min_instances=0)
        r = validate_placement(its, **kw)
        for k in tot: tot[k] += r.get(k, 0)
        for k, v in r['examples'].items(): ex.setdefault(k, []).extend(v[:4])
    tot['examples'] = ex
    return tot


# ---- v0.3 tools: plans, server writes, control, batch geometry, reads --------------------------------------------------------------------
def plan_apply(plan, items, **kw): return tool('plan_apply', plan=plan, items=items, **kw)
def plans(**kw): return tool('plans', **kw)
def plan_remove(plan, **kw): return tool('plan_remove', plan=plan, **kw)
def zdo_dump(id): return tool('zdo_dump', id=id)
def find_text(q, **kw): return tool('find_text', q=q, **kw)
def portals(): return tool('portals')
def undo_group(group=None, count=None): return tool('undo_group', **({'group': group} if group else {'count': count}))
def journal_groups(limit=40): return tool('journal_groups', limit=limit)
def player_state(): return tool('player_state')
def terrain_profile(points, step=2): return tool('terrain_profile', points=points, step=step)
def surface_probe(points, **kw): return tool('surface_probe', points=points, **kw)
def containers(**kw): return tool('containers', **kw)
def locations(name=None, **kw): return tool('locations', **({'name': name} if name else {}), **kw)
def creatures(**kw): return tool('creatures', **kw)
def paint_at(points): return tool('paint_at', points=points)


def walk(points, run=False, timeout=120, poll=1.0, **kw):
    """Walk the sandbox character for real (gravity, collisions, doors). Blocks until arrived/blocked/timeout; returns the final status with a trace."""
    import os, time
    if os.environ.get('HUBNER_NOJUMP') and 'jump' not in kw: kw['jump'] = False       # accessibility test: the walker may not jump (HUBNER_NOJUMP=1)
    tool('walk_to', points=points, run=run, timeout=timeout, **kw)
    time.sleep(0.5)
    while True:
        s = tool('walk_status', trace=True)
        if not s['running']: return s
        time.sleep(poll)


def server_write_tools(url='http://127.0.0.1:8741/mcp'):
    """Context for server-twin writes: HUBNER_URL points at the twin, ALLOW_SERVER_WRITES must exist on the server, every call carries confirm='server-write'."""
    import mcp
    return mcp, url


def render_map(path, x, z, size=60, y=None, grid=10, hide_trees=True, **kw):
    """Top-down orthographic map centred on (x,z), half-extent `size` m, with a labelled coordinate grid (every `grid` m) drawn on top. Needs Pillow."""
    from PIL import Image, ImageDraw
    px = 1000
    p = render(path, x=x, z=z, y=(y if y is not None else 60), yaw=0, pitch=90, dist=200, size=px, ortho=True, orthoSize=size, hideTrees=hide_trees, far=400, **kw)
    im = Image.open(p).convert('RGB'); d = ImageDraw.Draw(im)
    scale = px / (2 * size)
    g0x = int((x - size) // grid + 1) * grid; g0z = int((z - size) // grid + 1) * grid
    for gx in range(g0x, int(x + size) + 1, grid):
        u = (gx - (x - size)) * scale; d.line([(u, 0), (u, px)], fill=(255, 255, 255), width=1); d.text((u + 2, 2), str(gx), fill=(255, 255, 0))
    for gz in range(g0z, int(z + size) + 1, grid):
        v = px - (gz - (z - size)) * scale; d.line([(0, v), (px, v)], fill=(255, 255, 255), width=1); d.text((2, v - 10), str(gz), fill=(0, 255, 255))
    im.save(p); return p


def render_section(path, a, b, y0, y1, thickness=2.0, size=1000, **kw):
    """Vertical cross-section along the line a->b (x,z pairs): an orthographic side view of a slab `thickness` m deep, showing floors, walls and the ground cut."""
    import math
    from PIL import Image
    (ax, az), (bx, bz) = a, b; cx, cz = (ax + bx) / 2, (az + bz) / 2; L = math.hypot(bx - ax, bz - az); cy = (y0 + y1) / 2
    ang = math.degrees(math.atan2(bx - ax, bz - az))                       # direction of the line; the camera looks across it
    yaw = (ang + 90) % 360
    half = max(L, y1 - y0) / 2 + 1
    return render(path, x=cx, y=cy, z=cz, yaw=yaw, pitch=0, dist=50, size=size, ortho=True, orthoSize=half, near=50 - thickness / 2, far=50 + thickness / 2, hideTrees=True, **kw)


# ---- v0.4 helpers --------------------------------------------------------------------------------------------------------------
def go_to(x, z, y=None, snap='ground', timeout=150, radius=30):
    """Teleport the sandbox character and wait until it is really there: not teleporting, grounded, zone and terrain loaded. Raises TimeoutError otherwise.
    snap='ground' never lands on a roof or wall-walk (use y= or snap='solid' to stand on pieces)."""
    import time
    t0 = time.time(); args = {'x': x, 'z': z, 'snap': snap}
    if y is not None: args['y'] = y
    while True:
        try: _teleport(**args); break
        except RuntimeError as e:
            if 'no local player' not in str(e) or time.time() - t0 > timeout: raise
            time.sleep(3)
    time.sleep(1.5)
    last = None; resent = time.time()
    while time.time() - t0 < timeout:
        last = tool('ready_state', x=x, z=z, radius=radius)
        if last.get('ready') and _arrived(x, z, y): return last
        if last.get('groundKnown') and not last.get('grounded') and not last.get('teleporting') and y is None: _teleport(**args)
        elif not last.get('teleporting') and last.get('nearTarget') is False and time.time() - resent > 10:      # the first teleport was swallowed (seen after long walks): the player stood still, grounded, far from the target
            _teleport(**args); resent = time.time()
        time.sleep(1.5)
    raise TimeoutError(f'go_to({x},{z}) not ready after {timeout}s: {last}')


def nav_path(to_x, to_z, from_x=None, from_z=None, agent='Humanoid'):
    kw = {'toX': to_x, 'toZ': to_z, 'agent': agent}
    if from_x is not None: kw.update(fromX=from_x, fromZ=from_z)
    return tool('nav_path', **kw)


def room_view(path, x, z, floor_y, yaw=0, pitch=-8, **kw):
    """First-person image from inside a room with the ceiling and everything above hidden (see the room_view tool)."""
    r = _raw('room_view', {'x': x, 'z': z, 'floorY': floor_y, 'yaw': yaw, 'pitch': pitch, **kw})
    if r.get('isError'): raise RuntimeError(_text(r))
    n = save_images(r, path.rsplit('.', 1)[0] + '_')
    return path.rsplit('.', 1)[0] + '_0.png' if n else None


def status(): return tool('status')
def selftest(): return tool('selftest')
def zdo_audit(**kw): return tool('zdo_audit', **kw)
def log_tail(lines=60, **kw): return tool('log_tail', lines=lines, **kw)
def job_start(tool_name, args=None): return tool('job_start', tool=tool_name, args=args or {})
def job_status(id=None): return tool('job_status', **({'id': id} if id is not None else {}))


_HEIGHT_WORDS = ('level', 'raise', 'lower', 'slope', 'void', 'reset', 'delta', 'min', 'max', 'smooth', 'step', 'to=')


def terrain_paint(x, z, paint='dirt', circle=None, rect=None, blockcheck=None, ids=None, ignore=None, chance=None, verify_box=None):
    """PAINT-ONLY terrain edit through World Edit Commands: refuses any height keyword, handles the devcommands toggle (authenticates when the console says Unauthorized),
    and checks afterwards that no terrain height changed (terraform_map over verify_box = (x0,z0,x1,z1), default a box around the target).
    Returns the console text. Needs the client in the world with WEC + Server devcommands (see the valheim-build skill)."""
    import time
    from mcp import call
    def run(t):
        r = call('run_command', {'text': t}); return ''.join(c.get('text', '') for c in r.get('content', []))
    parts = [f'terrain paint={paint}', f'from={x},{z}']
    if circle is not None: parts.append(f'circle={circle}')
    if rect is not None: parts.append(f'rect={rect}')
    if blockcheck: parts.append(f'blockcheck={blockcheck}')
    if ids: parts.append('id=' + (ids if isinstance(ids, str) else ','.join(ids)))
    if ignore: parts.append('ignore=' + (ignore if isinstance(ignore, str) else ','.join(ignore)))
    if chance is not None: parts.append(f'chance={chance}')
    cmd = ' '.join(parts)
    if any(w in cmd.replace('paint=', '') for w in _HEIGHT_WORDS): raise ValueError('terrain_paint refuses height edits: ' + cmd)
    vb = verify_box or (x - 30, z - 30, x + 30, z + 30)
    before = tool('terraform_map', x0=vb[0], z0=vb[1], x1=vb[2], z1=vb[3], cell=4, minDelta=0.01, raster=False)['modifiedHeightNodes']
    out = run(cmd)
    if 'Unauthorized' in out:
        run('devcommands'); time.sleep(6); out = run(cmd)
        if 'Unauthorized' in out:
            run('devcommands'); time.sleep(6); out = run(cmd)             # the toggle may have switched it off the first time
    if 'Unauthorized' in out: raise RuntimeError('devcommands not authorized: ' + out[:120])
    time.sleep(1.0)
    after = tool('terraform_map', x0=vb[0], z0=vb[1], x1=vb[2], z1=vb[3], cell=4, minDelta=0.01, raster=False)['modifiedHeightNodes']
    if after != before: raise RuntimeError(f'terrain HEIGHT changed ({before} -> {after} nodes) by: {cmd}')
    return out


# ---- v0.6 helpers: ssh agent and server write gate --------------------------------------------------------------------------------------------
SERVER_HOST = os.environ.get('HUBNER_SERVER_SSH', '')              # user@host of the dedicated server (needed for server_writes / ensure_agent), e.g. 'root@10.0.0.5'
GATE_FLAG = os.environ.get('HUBNER_GATE_FLAG', '/path/to/serverfiles/BepInEx/hubner-ext/ALLOW_SERVER_WRITES')   # the flag file the server twin checks before any write


def ensure_agent(host=SERVER_HOST, wait=45):
    """Make sure `ssh host` works non-interactively. The Bitwarden SSH agent stops answering when its app is closed: open it and wait. Raises with a clear message."""
    import subprocess, time
    t0 = time.time(); opened = False
    while True:
        r = subprocess.run(['ssh', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=6', host, 'echo ok'], capture_output=True, text=True)
        if r.returncode == 0: return True
        if not opened and ('agent' in r.stderr or 'Permission denied' in r.stderr or 'publickey' in r.stderr):
            subprocess.run(['open', '-a', 'Bitwarden'], capture_output=True); opened = True
        if time.time() - t0 > wait: raise RuntimeError(f'ssh {host} not available after {wait}s (Bitwarden SSH agent?): {r.stderr.strip()[:200]}')
        time.sleep(3)


def _ssh(cmd, host=SERVER_HOST):
    import subprocess
    r = subprocess.run(['ssh', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', host, cmd], capture_output=True, text=True)
    if r.returncode != 0: raise RuntimeError(f'ssh {cmd!r} failed: {r.stderr.strip()[:200]}')
    return r.stdout


class server_writes:
    """with server_writes(): ...  opens the server write gate (flag file, 30 min) and ALWAYS removes it again, also on errors and Ctrl-C."""
    def __enter__(self):
        ensure_agent(); _ssh(f'touch {GATE_FLAG}'); return self
    def __exit__(self, *exc):
        for _ in range(3):
            try: _ssh(f'rm -f {GATE_FLAG}'); break
            except Exception: ensure_agent()
        return False


class at_time:
    """with at_time('noon'): render(...)   hold the client's time of day (and optionally weather) for renders, always released afterwards"""
    def __init__(self, t='noon', weather=None): self.t, self.weather = t, weather
    def __enter__(self):
        import time
        tool('time_set', t=self.t)
        if self.weather is not None: tool('weather_set', name=self.weather)
        time.sleep(2.5); return self
    def __exit__(self, *exc):
        tool('env_release'); return False


def go_fast(x, z, y=None, timeout=25, snap=None):
    """Teleport and poll quickly (0.25 s) until the player is there, grounded and not mid-teleport: for hops inside the loaded area (route to route in one building).
    Falls back to the slow, area-loading teleport_and_wait when that does not settle in `timeout` s."""
    import time
    args = {'x': x, 'z': z}
    if y is not None: args['y'] = y
    if snap: args['snap'] = snap
    t0 = time.time()
    try: _teleport(**args)
    except RuntimeError: return teleport_and_wait(x, z, y=y, timeout=90, radius=40, min_instances=0)
    while time.time() - t0 < timeout:
        try:
            s = player_state(); p = s.get('pos') or [1e9, 0, 1e9]
            if not s.get('teleporting') and s.get('grounded') and abs(p[0] - x) <= 3.0 and abs(p[2] - z) <= 3.0 and (y is None or abs(p[1] - y) <= 4.0): return s
        except Exception: pass
        time.sleep(0.25)
    return teleport_and_wait(x, z, y=y, timeout=90, radius=40, min_instances=0)


def ensure_client(timeout=300):
    """Make sure the sandbox client is in the world: if the MCP endpoint is dead (the game crashed or was closed), run the script named in HUBNER_LAUNCH (default launch-client.sh next to this file) and wait for ready_state."""
    import os, subprocess, time
    def up():
        try: return bool(tool('ready_state').get('player'))
        except Exception: return False
    if up(): return True
    here = os.path.dirname(os.path.abspath(__file__))
    r = subprocess.run([os.environ.get('HUBNER_LAUNCH') or os.path.join(here, 'launch-client.sh')], capture_output=True, text=True)
    if r.returncode: raise RuntimeError('client launch script failed: ' + (r.stdout + r.stderr)[-200:])
    t0 = time.time()
    while time.time() - t0 < timeout:
        if up(): return True
        time.sleep(5)
    raise RuntimeError(f'sandbox client not in the world after {timeout}s')


class vpeer:
    """with vpeer(x, z, minutes=20): ...   make the dedicated server instantiate the world around (x, z) (virtual peer) so the server twin's physics tools
    (raycast, surface_probe, stability_scan, headroom, walk_check, terrain_info) work with no client connected. Uses the server twin URL (HUBNER_SERVER_URL, default
    http://127.0.0.1:8741/mcp) and the write gate; always removes the peer again."""
    def __init__(self, x, z, minutes=20, name='py', wait=12): self.x, self.z, self.minutes, self.name, self.wait = x, z, minutes, name, wait
    def __enter__(self):
        import mcp, os, time
        self._old = mcp.URL; mcp.URL = os.environ.get('HUBNER_SERVER_URL', 'http://127.0.0.1:8741/mcp')
        with server_writes(): tool('vpeer_add', name=self.name, x=self.x, z=self.z, minutes=self.minutes)
        time.sleep(self.wait); return self
    def __exit__(self, *exc):
        import mcp
        try: tool('vpeer_remove', name=self.name)
        finally: mcp.URL = self._old
        return False
