"""Python client for the Hubner ValheimMCP extension tools.

Two ways to use it:
  from hubner import *                     # module functions bound to the default client (URL from HUBNER_URL / mcp.URL, 8731 = sandbox client)
  from hubner import Client, sandbox, twin # explicit clients: sandbox (client role, renders, walks) and twin (dedicated server, ZDO writes behind the gate)
  with twin.writes(): twin.plan_apply('castle-x', items)           # opens the server's ALLOW_SERVER_WRITES flag over ssh for the block, big plans run as sliced jobs

Coordinates are world x,z (y up). Tool errors raise RuntimeError with the tool's message.
"""
import json, os, subprocess, time
import mcp
from mcp import save_images

SERVER_HOST = os.environ.get('HUBNER_SERVER_SSH', '')                      # user@host of the dedicated server (needed for writes()/ensure_agent), e.g. 'root@10.0.0.5'
GATE_FLAG = os.environ.get('HUBNER_GATE_FLAG', '')                        # the flag file the server twin checks before any write, e.g. /srv/valheim/BepInEx/hubner-ext/ALLOW_SERVER_WRITES
SERVER_URL = os.environ.get('HUBNER_SERVER_URL', 'http://127.0.0.1:8741/mcp')
JOB_THRESHOLD = int(os.environ.get('HUBNER_JOB_THRESHOLD', '300'))          # plan_apply with more items than this runs as a sliced job (never freezes players)


def ensure_agent(host=None, wait=45):
    """Make sure `ssh host` works non-interactively. The Bitwarden SSH agent stops answering when its app is closed: open it and wait. Raises with a clear message."""
    host = host or SERVER_HOST
    if not host: raise RuntimeError('HUBNER_SERVER_SSH is not set (user@host of the dedicated server)')
    t0 = time.time(); opened = False
    while True:
        r = subprocess.run(['ssh', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=6', host, 'echo ok'], capture_output=True, text=True)
        if r.returncode == 0: return True
        if not opened and ('agent' in r.stderr or 'Permission denied' in r.stderr or 'publickey' in r.stderr):
            subprocess.run(['open', '-a', 'Bitwarden'], capture_output=True); opened = True
        if time.time() - t0 > wait: raise RuntimeError(f'ssh {host} not available after {wait}s (Bitwarden SSH agent?): {r.stderr.strip()[:200]}')
        time.sleep(3)


def _ssh(cmd, host=None):
    host = host or SERVER_HOST
    r = subprocess.run(['ssh', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', host, cmd], capture_output=True, text=True)
    if r.returncode != 0: raise RuntimeError(f'ssh {cmd!r} failed: {r.stderr.strip()[:200]}')
    return r.stdout


class server_writes:
    """with server_writes(): ...  opens the server write gate (flag file, hubner.writeFlagMinutes) and ALWAYS removes it again, also on errors and Ctrl-C."""
    def __enter__(self):
        if not GATE_FLAG: raise RuntimeError('HUBNER_GATE_FLAG is not set (path of ALLOW_SERVER_WRITES on the dedicated server)')
        ensure_agent(); _ssh(f'touch {GATE_FLAG}'); return self
    def __exit__(self, *exc):
        for _ in range(3):
            try: _ssh(f'rm -f {GATE_FLAG}'); break
            except Exception: ensure_agent()
        return False


class Client:
    """One MCP endpoint. url=None follows mcp.URL at call time (so the old `with on(url):` pattern keeps working)."""
    def __init__(self, url=None, token=None, timeout=60, server=False):
        self.url, self.token, self.timeout, self.server = url, token or os.environ.get('HUBNER_TOKEN'), timeout, server

    # ---- transport
    def raw(self, name, args=None):
        return mcp.call(name, args or {}, url=self.url, token=self.token, timeout=self.timeout)

    @staticmethod
    def text(r): return ''.join(c.get('text', '') for c in r.get('content', []) if c.get('type') == 'text')

    def tool(self, _name, /, **args):
        """Call a tool; returns parsed JSON when the tool returns JSON, else the text. Raises RuntimeError on isError."""
        r = self.raw(_name, args); t = self.text(r)
        if r.get('isError'): raise RuntimeError(f'{_name}: {t}')
        try: return json.loads(t)
        except ValueError: return t

    def confirm(self, args):
        if self.server: args.setdefault('confirm', 'server-write')
        return args

    def writes(self): return server_writes()

    # ---- jobs (sliced on the game's frames: the way to run anything big on a live server)
    def job(self, tool_name, args=None, poll=1.0, timeout=900):
        """job_start + poll until done. Returns the tool's parsed result; raises on error."""
        j = self.tool('job_start', tool=tool_name, args=self.confirm(dict(args or {})))
        t0 = time.time()
        while True:
            s = self.tool('job_status', id=j['job'])
            if s['state'] in ('done', 'error'):
                if s['state'] == 'error': raise RuntimeError(f"{tool_name} job {j['job']}: {s['result']}")
                return s['result']
            if time.time() - t0 > timeout: raise TimeoutError(f"{tool_name} job {j['job']} still {s['state']} after {timeout}s ({s.get('frames')} frames)")
            time.sleep(poll)

    # ---- plans
    def plan_apply(self, plan, items, job=None, **kw):
        """Idempotent placement. Big plans (over HUBNER_JOB_THRESHOLD items) run as a sliced job unless job=False."""
        args = self.confirm(dict(plan=plan, items=items, **kw))
        if job is None: job = len(items) > JOB_THRESHOLD
        return self.job('plan_apply', args) if job else self.tool('plan_apply', **args)
    def plan_remove(self, plan, **kw): return self.tool('plan_remove', **self.confirm(dict(plan=plan, **kw)))
    def plans(self, **kw): return self.tool('plans', **kw)
    def plan_stats(self, **kw): return self.tool('plan_stats', **kw)
    def zdo_set(self, edits, **kw): return self.tool('zdo_set', **self.confirm(dict(edits=edits, **kw)))
    def zdo_delete(self, ids, **kw): return self.tool('zdo_delete', **self.confirm(dict(ids=ids, **kw)))
    def zdo_dump(self, id): return self.tool('zdo_dump', id=id)
    def doors_set(self, state, **kw): return self.tool('doors_set', **self.confirm(dict(state=state, **kw)))
    def undo_group(self, group=None, count=None): return self.tool('undo_group', **self.confirm({'group': group} if group else {'count': count}))
    def journal_groups(self, limit=40): return self.tool('journal_groups', limit=limit)
    def journal(self, limit=30): return self.tool('journal', limit=limit)

    # ---- reads
    def terrain_info(self, x, z): return self.tool('terrain_info', x=x, z=z)
    def terrain_grid(self, x0, z0, x1, z1, step=4, solid=False): return self.tool('terrain_grid', x0=x0, z0=z0, x1=x1, z1=z1, step=step, solid=solid)
    def terrain_profile(self, points, step=2): return self.tool('terrain_profile', points=points, step=step)
    def surface_probe(self, points, **kw): return self.tool('surface_probe', points=points, **kw)
    def raycast(self, x, y, z, dx=0, dy=-1, dz=0, maxDist=500): return self.tool('raycast', x=x, y=y, z=z, dx=dx, dy=dy, dz=dz, maxDist=maxDist)
    def objects(self, **kw): return self.tool('objects', **kw)
    def object_info(self, id): return self.tool('object_info', id=id)
    def prefab_info(self, name): return self.tool('prefab_info', name=name)
    def prefab_search(self, contains, limit=100): return self.tool('prefab_search', contains=contains, limit=limit)
    def world_state(self): return self.tool('world_state')
    def zone_state(self, x, z, radius=30): return self.tool('zone_state', x=x, z=z, radius=radius)
    def stability_scan(self, x0, z0, x1, z1, **kw): return self.tool('stability_scan', x0=x0, z0=z0, x1=x1, z1=z1, **kw)
    def walk_check(self, points, **kw): return self.tool('walk_check', points=points, **kw)
    def terraform_map(self, x0, z0, x1, z1, cell=4, minDelta=0.15, raster=True): return self.tool('terraform_map', x0=x0, z0=z0, x1=x1, z1=z1, cell=cell, minDelta=minDelta, raster=raster)
    def snapshot(self, name, x0, z0, x1, z1): return self.tool('snapshot', name=name, x0=x0, z0=z0, x1=x1, z1=z1)
    def snapshot_diff(self, name, limit=15): return self.tool('snapshot_diff', name=name, limit=limit)
    def validate_placement(self, items, **kw): return self.tool('validate_placement', items=items, **kw)
    def verify_plan(self, items, **kw): return self.tool('verify_plan', items=items, **kw)
    def find_text(self, q, **kw): return self.tool('find_text', q=q, **kw)
    def portals(self): return self.tool('portals')
    def player_state(self): return self.tool('player_state')
    def containers(self, **kw): return self.tool('containers', **kw)
    def locations(self, name=None, **kw): return self.tool('locations', **({'name': name} if name else {}), **kw)
    def creatures(self, **kw): return self.tool('creatures', **kw)
    def paint_at(self, points): return self.tool('paint_at', points=points)
    def status(self): return self.tool('status')
    def selftest(self): return self.tool('selftest')
    def zdo_audit(self, **kw): return self.tool('zdo_audit', **kw)
    def log_tail(self, lines=60, **kw): return self.tool('log_tail', lines=lines, **kw)
    def job_start(self, tool_name, args=None): return self.tool('job_start', tool=tool_name, args=args or {})
    def job_status(self, id=None): return self.tool('job_status', **({'id': id} if id is not None else {}))
    def nav_path(self, to_x, to_z, from_x=None, from_z=None, agent='Humanoid'):
        kw = {'toX': to_x, 'toZ': to_z, 'agent': agent}
        if from_x is not None: kw.update(fromX=from_x, fromZ=from_z)
        return self.tool('nav_path', **kw)

    # ---- client-only writes
    def spawn(self, items, force=False): return self.tool('spawn', items=items, force=force)
    def modify(self, edits): return self.tool('modify', edits=edits)
    def delete(self, **kw): return self.tool('delete', **kw)
    def undo(self, count=1): return self.tool('undo', count=count)

    # ---- renders
    def render(self, path, **kw):
        """render_ex to a PNG file (kw: x,z,y,yaw,pitch,dist,size,fov,near,ortho,orthoSize,hideAboveY,hidePrefabs,cutRadius,hideTrees)."""
        r = self.raw('render_ex', kw)
        if r.get('isError'): raise RuntimeError(self.text(r))
        n = save_images(r, path.rsplit('.', 1)[0] + '_')
        return path.rsplit('.', 1)[0] + '_0.png' if n else None

    def room_view(self, path, x, z, floor_y, yaw=0, pitch=-8, **kw):
        """First-person image from inside a room with the ceiling and everything above hidden (see the room_view tool)."""
        r = self.raw('room_view', {'x': x, 'z': z, 'floorY': floor_y, 'yaw': yaw, 'pitch': pitch, **kw})
        if r.get('isError'): raise RuntimeError(self.text(r))
        n = save_images(r, path.rsplit('.', 1)[0] + '_')
        return path.rsplit('.', 1)[0] + '_0.png' if n else None

    def render_map(self, path, x, z, size=60, y=None, grid=10, hide_trees=True, **kw):
        """Top-down orthographic map centred on (x,z), half-extent `size` m, with a labelled coordinate grid (every `grid` m) drawn on top. Needs Pillow."""
        from PIL import Image, ImageDraw
        px = 1000
        p = self.render(path, x=x, z=z, y=(y if y is not None else 60), yaw=0, pitch=90, dist=200, size=px, ortho=True, orthoSize=size, hideTrees=hide_trees, far=400, **kw)
        im = Image.open(p).convert('RGB'); d = ImageDraw.Draw(im)
        scale = px / (2 * size)
        g0x = int((x - size) // grid + 1) * grid; g0z = int((z - size) // grid + 1) * grid
        for gx in range(g0x, int(x + size) + 1, grid):
            u = (gx - (x - size)) * scale; d.line([(u, 0), (u, px)], fill=(255, 255, 255), width=1); d.text((u + 2, 2), str(gx), fill=(255, 255, 0))
        for gz in range(g0z, int(z + size) + 1, grid):
            v = px - (gz - (z - size)) * scale; d.line([(0, v), (px, v)], fill=(255, 255, 255), width=1); d.text((2, v - 10), str(gz), fill=(0, 255, 255))
        im.save(p); return p

    def render_section(self, path, a, b, y0, y1, thickness=2.0, size=1000, **kw):
        """Vertical cross-section along the line a->b (x,z pairs): an orthographic side view of a slab `thickness` m deep, showing floors, walls and the ground cut."""
        import math
        (ax, az), (bx, bz) = a, b; cx, cz = (ax + bx) / 2, (az + bz) / 2; L = math.hypot(bx - ax, bz - az); cy = (y0 + y1) / 2
        ang = math.degrees(math.atan2(bx - ax, bz - az)); yaw = (ang + 90) % 360
        half = max(L, y1 - y0) / 2 + 1
        return self.render(path, x=cx, y=cy, z=cz, yaw=yaw, pitch=0, dist=50, size=size, ortho=True, orthoSize=half, near=50 - thickness / 2, far=50 + thickness / 2, hideTrees=True, **kw)

    # ---- character control (client role)
    def _teleport(self, **args):
        """teleport that survives the game's refusal: a teleport asked for within ~2 s of the previous one is ignored (accepted:false); retry until accepted"""
        t0 = time.time()
        while True:
            r = self.tool('teleport', **args)
            if not isinstance(r, dict) or r.get('accepted', True): return r
            if time.time() - t0 > 20: raise RuntimeError('teleport refused for 20 s: ' + str(r))
            time.sleep(0.5)

    def _arrived(self, x, z, y=None, tol=3.0):
        try: s = self.player_state()
        except Exception: return None
        p = s.get('pos') or [1e9, 0, 1e9]
        ok = (not s.get('teleporting')) and s.get('grounded') and abs(p[0] - x) <= tol and abs(p[2] - z) <= tol and (y is None or abs(p[1] - y) <= 4.0)
        return s if ok else None

    def goto(self, x, z, y=None, snap=None, fast=True, timeout=150, radius=30):
        """The one teleport routine. fast: poll player_state every 0.25 s for up to 25 s (hops inside the loaded area). Then, or when fast=False, the
        area-loading path: ready_state polling, a re-sent teleport when the game swallowed the first one (seen after long walks), and a ground
        re-snap once the terrain is known. snap: 'ground' (terrain, never a roof), 'solid' (highest piece), 'none' (keep altitude); default
        'ground' unless y is given. Raises TimeoutError."""
        args = {'x': x, 'z': z}
        if y is not None: args['y'] = y
        else: args['snap'] = snap or 'ground'
        if snap and y is not None: args['snap'] = snap
        t0 = time.time()
        for attempt in range(40):                                   # the character respawns now and then (death, scene fade): wait for it instead of failing
            try: self._teleport(**args); break
            except RuntimeError as e:
                if 'no local player' not in str(e) or attempt == 39: raise
                time.sleep(3)
        if fast:
            tf = time.time()
            while time.time() - tf < 25:
                s = self._arrived(x, z, y)
                if s: return s
                time.sleep(0.25)
        last = None; resent = time.time()
        while time.time() - t0 < timeout:
            last = self.tool('ready_state', x=x, z=z, radius=radius)
            if last.get('ready') and self._arrived(x, z, y, tol=6.0): return last
            if last.get('groundKnown') and not last.get('grounded') and not last.get('teleporting') and y is None: self._teleport(**args)
            elif not last.get('teleporting') and last.get('nearTarget') is False and time.time() - resent > 10:
                self._teleport(**args); resent = time.time()
            time.sleep(1.5)
        raise TimeoutError(f'goto({x},{z}) not ready after {timeout}s: {last}')

    def walk(self, points, run=False, timeout=120, poll=1.0, **kw):
        """Walk the sandbox character for real (gravity, collisions, doors). Blocks until arrived/blocked/timeout; returns the final status with a trace. HUBNER_NOJUMP=1 forbids jumping."""
        if os.environ.get('HUBNER_NOJUMP') and 'jump' not in kw: kw['jump'] = False
        self.tool('walk_to', points=points, run=run, timeout=timeout, **kw)
        time.sleep(0.5)
        while True:
            s = self.tool('walk_status', trace=True)
            if not s['running']: return s
            time.sleep(poll)

    def ensure_client(self, timeout=300):
        """Make sure the sandbox client is in the world: if the MCP endpoint is dead (the game crashed or was closed), run the script named in HUBNER_LAUNCH (default launch-client.sh next to this file) and wait for ready_state."""
        def up():
            try: return bool(self.tool('ready_state').get('player'))
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

    # ---- plan files
    @staticmethod
    def _plan_items(plan_path):
        pieces = json.load(open(plan_path))['pieces']
        return [{'prefab': q['prefab'], 'x': q['x'], 'y': q['y'], 'z': q['z'], 'yaw': q.get('yaw', 0), **({'text': q['text']} if q.get('text') else {}), **({'key': q['key']} if q.get('key') else {})} for q in pieces]

    @staticmethod
    def _tiles(pieces, size=64):
        t = {}
        for q in pieces: t.setdefault((int(q['x'] // size), int(q['z'] // size)), []).append(q)
        return t

    def verify_plan_file(self, plan_path, tile=64, **kw):
        """Verify a plan.json against the world tile by tile (teleports the sandbox character). Returns merged counts + examples."""
        items = self._plan_items(plan_path)
        tot = {'planned': 0, 'ok': 0, 'moved': 0, 'missing': 0, 'duplicates': 0, 'wrongYaw': 0, 'wrongText': 0, 'extraOfPlanPrefabs': 0}; ex = {}
        for (tx, tz), its in sorted(self._tiles(items, tile).items()):
            self.goto((tx + .5) * tile, (tz + .5) * tile, fast=False, timeout=120, radius=tile)
            r = self.verify_plan(its, **kw)
            for k in tot: tot[k] += r.get(k, 0)
            for k, v in r['examples'].items(): ex.setdefault(k, []).extend(v[:3])
        tot['examples'] = ex; return tot

    def validate_plan_file(self, plan_path, tile=64, **kw):
        """validate_placement over a whole plan.json (teleports tile by tile so zones are loaded)."""
        items = self._plan_items(plan_path)
        tot = {'checked': 0, 'clean': 0, 'overlapping': 0, 'buriedUnderGround': 0, 'thinPiecesFloating': 0, 'zoneNotLoaded': 0, 'unknownPrefab': 0}; ex = {}
        for (tx, tz), its in sorted(self._tiles(items, tile).items()):
            self.goto((tx + .5) * tile, (tz + .5) * tile, fast=False, timeout=120, radius=tile)
            r = self.validate_placement(its, **kw)
            for k in tot: tot[k] += r.get(k, 0)
            for k, v in r['examples'].items(): ex.setdefault(k, []).extend(v[:4])
        tot['examples'] = ex; return tot

    # ---- client-local environment
    class at_time:
        """with client.at_time('noon'): client.render(...)   hold the client's time of day (and optionally weather) for renders, always released afterwards"""
        def __init__(self, c, t='noon', weather=None): self.c, self.t, self.weather = c, t, weather
        def __enter__(self):
            self.c.tool('time_set', t=self.t)
            if self.weather is not None: self.c.tool('weather_set', name=self.weather)
            time.sleep(2.5); return self
        def __exit__(self, *exc):
            self.c.tool('env_release'); return False

    def hold_time(self, t='noon', weather=None): return Client.at_time(self, t, weather)

    # ---- server twin: virtual peers
    class vpeer:
        """with twin.vpeer(x, z, minutes=20): ...   make the dedicated server instantiate the world around (x, z) so its physics tools work with no client connected. Opens the write gate; always removes the peer again."""
        def __init__(self, c, x, z, minutes=20, name='py', wait=12): self.c, self.x, self.z, self.minutes, self.name, self.wait = c, x, z, minutes, name, wait
        def __enter__(self):
            with self.c.writes(): self.c.tool('vpeer_add', **self.c.confirm(dict(name=self.name, x=self.x, z=self.z, minutes=self.minutes)))
            time.sleep(self.wait); return self
        def __exit__(self, *exc):
            self.c.tool('vpeer_remove', name=self.name); return False

    def peer(self, x, z, minutes=20, name='py', wait=12): return Client.vpeer(self, x, z, minutes, name, wait)

    # ---- terrain paint through World Edit Commands (client role; paint only, never height)
    _HEIGHT_WORDS = ('level', 'raise', 'lower', 'slope', 'void', 'reset', 'delta', 'min', 'max', 'smooth', 'step', 'to=')

    def terrain_paint(self, x, z, paint='dirt', circle=None, rect=None, blockcheck=None, ids=None, ignore=None, chance=None, verify_box=None):
        """PAINT-ONLY terrain edit through World Edit Commands: refuses any height keyword, handles the devcommands toggle (authenticates when the console says Unauthorized),
        and checks afterwards that no terrain height changed (terraform_map over verify_box = (x0,z0,x1,z1), default a box around the target). Returns the console text."""
        def run(t): return self.text(self.raw('run_command', {'text': t}))
        parts = [f'terrain paint={paint}', f'from={x},{z}']
        if circle is not None: parts.append(f'circle={circle}')
        if rect is not None: parts.append(f'rect={rect}')
        if blockcheck: parts.append(f'blockcheck={blockcheck}')
        if ids: parts.append('id=' + (ids if isinstance(ids, str) else ','.join(ids)))
        if ignore: parts.append('ignore=' + (ignore if isinstance(ignore, str) else ','.join(ignore)))
        if chance is not None: parts.append(f'chance={chance}')
        cmd = ' '.join(parts)
        if any(w in cmd.replace('paint=', '') for w in self._HEIGHT_WORDS): raise ValueError('terrain_paint refuses height edits: ' + cmd)
        vb = verify_box or (x - 30, z - 30, x + 30, z + 30)
        before = self.tool('terraform_map', x0=vb[0], z0=vb[1], x1=vb[2], z1=vb[3], cell=4, minDelta=0.01, raster=False)['modifiedHeightNodes']
        out = run(cmd)
        for _ in range(2):
            if 'Unauthorized' not in out: break
            run('devcommands'); time.sleep(6); out = run(cmd)
        if 'Unauthorized' in out: raise RuntimeError('devcommands not authorized: ' + out[:120])
        time.sleep(1.0)
        after = self.tool('terraform_map', x0=vb[0], z0=vb[1], x1=vb[2], z1=vb[3], cell=4, minDelta=0.01, raster=False)['modifiedHeightNodes']
        if after != before: raise RuntimeError(f'terrain HEIGHT changed ({before} -> {after} nodes) by: {cmd}')
        return out


# ---- the three clients
default = Client()                                                           # follows mcp.URL (HUBNER_URL); what `from hubner import *` binds to
sandbox = Client('http://127.0.0.1:8731/mcp')
twin = Client(SERVER_URL, server=True)


# ---- module-level API bound to the default client (kept for every existing script)
def _raw(name, args=None): return default.raw(name, args)
def _text(r): return Client.text(r)
def tool(_name, /, **args): return default.tool(_name, **args)
for _n in ('terrain_info', 'terrain_grid', 'terrain_profile', 'surface_probe', 'raycast', 'objects', 'object_info', 'prefab_info', 'prefab_search', 'world_state', 'zone_state',
           'stability_scan', 'walk_check', 'terraform_map', 'snapshot', 'snapshot_diff', 'validate_placement', 'verify_plan', 'find_text', 'portals', 'player_state', 'containers',
           'locations', 'creatures', 'paint_at', 'status', 'selftest', 'zdo_audit', 'log_tail', 'job_start', 'job_status', 'nav_path', 'spawn', 'modify', 'delete', 'undo',
           'plan_apply', 'plan_remove', 'plans', 'plan_stats', 'zdo_dump', 'undo_group', 'journal_groups', 'journal', 'render', 'room_view', 'render_map', 'render_section',
           'walk', 'ensure_client', 'verify_plan_file', 'validate_plan_file', 'terrain_paint'):
    globals()[_n] = (lambda n: (lambda *a, **k: getattr(default, n)(*a, **k)))(_n)
del _n


def _teleport(**args): return default._teleport(**args)
def go_to(x, z, y=None, snap='ground', timeout=150, radius=30): return default.goto(x, z, y=y, snap=snap, fast=False, timeout=timeout, radius=radius)
def go_fast(x, z, y=None, timeout=25, snap=None): return default.goto(x, z, y=y, snap=snap, fast=True)
def teleport_and_wait(x, z, y=None, timeout=60, radius=30, min_instances=1): return default.goto(x, z, y=y, fast=False, timeout=max(timeout, 30), radius=radius)


class at_time(Client.at_time):
    def __init__(self, t='noon', weather=None): super().__init__(default, t, weather)


class vpeer:
    """with vpeer(x, z, minutes=20): ...   server-twin virtual peer (see Client.vpeer); uses the twin client."""
    def __init__(self, x, z, minutes=20, name='py', wait=12): self._p = Client.vpeer(twin, x, z, minutes, name, wait)
    def __enter__(self): return self._p.__enter__()
    def __exit__(self, *exc): return self._p.__exit__(*exc)
