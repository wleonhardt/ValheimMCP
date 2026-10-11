"""The live acceptance cases (see accept.py). Each is a function of a Ctx; it records checks with ctx.check(...) and raises Skip when the world cannot answer it."""
import json, os, urllib.request
from accept import Case, Skip
import hubner_mcp

POLE = 'wood_pole2'


def _c(ctx, case): return ctx.client(case.needs)


# ---------------------------------------------------------------- read-only
def smoke(ctx):
    c = ctx.client('any'); url = c.url or hubner_mcp.URL
    init = hubner_mcp.rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'accept', 'version': '1'}}, url=url, token=c.token)
    ctx.check('initialize names the server', bool(init.get('result', {}).get('serverInfo', {}).get('name')), init.get('result', {}).get('serverInfo'))
    tools = hubner_mcp.rpc('tools/list', url=url, token=c.token)['result']['tools']
    names = {t['name'] for t in tools}
    ctx.check('tools/list has the built-ins', {'health', 'ext_status', 'tool_stats'} <= names, sorted({'health', 'ext_status', 'tool_stats'} - names))
    ctx.check('tools/list has the extension tools', {'plan_check', 'plan_rules', 'status'} <= names, sorted({'plan_check', 'plan_rules', 'status'} - names))
    # a message without an id is a notification: 202 and nothing runs
    body = json.dumps({'jsonrpc': '2.0', 'method': 'tools/call', 'params': {'name': 'tool_stats', 'arguments': {}}}).encode()
    hdr = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'}
    if c.token: hdr['Authorization'] = 'Bearer ' + c.token
    with urllib.request.urlopen(urllib.request.Request(url, body, hdr), timeout=30) as r:
        ctx.check('a call without an id is a notification (202, empty)', r.status == 202 and not r.read(), r.status)
    bad = hubner_mcp.rpc('tools/call', {'name': 'no_such_tool', 'arguments': {}}, url=url, token=c.token)
    ctx.check('an unknown tool is an error', 'error' in bad or bad.get('result', {}).get('isError'), str(bad)[:120])
    h = c.tool('health'); ctx.check('health says the game is in a world', isinstance(h, dict) and h.get('inGame'), h)


def build_stamp(ctx):
    e = ctx.client('any').tool('ext_status')
    ctx.check('the core has a build stamp', bool(e.get('build')), e.get('build'))
    exts = e.get('extensions', [])
    ctx.check('an extension is loaded', len(exts) > 0, len(exts))
    for x in exts: ctx.check(f"{x['name']} has a build stamp", bool(x.get('build')), x.get('build'))
    ctx.check('reloads are counted', isinstance(e.get('reloads'), int), e.get('reloads'))


def selftest(ctx):
    r = ctx.client('any').tool('selftest')
    for ch in r['checks']: ctx.check(ch['name'], ch['ok'], ch['detail'])
    ctx.check('selftest ok', r['ok'])


def tool_stats(ctx):
    c = ctx.client('any'); c.tool('status')
    s = c.tool('tool_stats')
    row = [t for t in s['tools'] if t['tool'] == 'status']
    ctx.check('the status call was counted', bool(row) and row[0]['calls'] >= 1, row)
    ctx.check('times are reported', bool(row) and 'p95Ms' in row[0] and 'maxMs' in row[0])


def plan_rules(ctx):
    r = ctx.client('any').tool('plan_rules')
    origins = {x['origin'] for x in r['rules']}
    ctx.check('all three origins exist', origins == {'hard', 'ghost', 'taste'}, origins)
    codes = {x['code'] for x in r['rules']}
    ctx.check('the game status codes are there', {'MoreSpace', 'NoBuildZone', 'PrivateZone', 'ExtensionMissingStation'} <= codes, codes)


def _has_prefab(c, name):
    r = c.tool('prefab_search', contains=name, limit=20)
    return name in r.get('names', [])


def _ground(c, x, z):
    t = c.tool('terrain_info', x=x, z=z)
    return t.get('groundY', t.get('y', t.get('height'))) if isinstance(t, dict) else None


def tower_pre(ctx):
    if not _has_prefab(ctx.client('any'), POLE): return f'prefab {POLE} not in this game'


def plan_check_tower(ctx):
    c = ctx.client('any')
    items = [{'prefab': POLE, 'x': 0, 'y': 2.0 * i, 'z': 0, 'key': f'p{i}'} for i in range(9)]
    r = c.tool('plan_check', items=items, groundY=0)
    ctx.check('the model checked all nine poles', r['planned'] == 9, r['planned'])
    ctx.check('the top of a 9-pole tower is unsupported (a wooden column holds about 16 m)', r['unsupported'] >= 1, r['unsupported'])
    short = c.tool('plan_check', items=items[:4], groundY=0)
    ctx.check('a 4-pole tower stands', short['unsupported'] == 0, short['unsupported'])
    ctx.check('problems carry an origin', all(p.get('origin') for p in r['problems']), [p.get('rule') for p in r['problems'] if not p.get('origin')])


def plan_check_hearth(ctx):
    c = ctx.client('any')
    for n in ('hearth', 'piece_cauldron'):
        if not _has_prefab(c, n): raise Skip(f'prefab {n} not in this game')
    r = c.tool('plan_check', items=[{'prefab': 'hearth', 'x': 0, 'y': 0, 'z': 0, 'key': 'h'}, {'prefab': 'piece_cauldron', 'x': 0, 'y': 0.5, 'z': 0, 'key': 'c'}], groundY=0)
    fc = [p for p in r['problems'] if p['rule'] == 'fire-clearance']
    ctx.check('a cauldron on a hearth is not a fire-clearance problem', not fc, fc)
    ctx.check('no fire-clearance rule is ever an error', all(p['severity'] != 'error' for p in r['problems'] if p['rule'] == 'fire-clearance'))


def plan_check_missing_y(ctx):
    r = ctx.client('any').tool('plan_check', items=[{'prefab': POLE, 'x': 0, 'z': 0, 'rotY': 90}], groundY=0)
    ctx.check('an item without y is counted', r.get('missingY') == 1, r.get('missingY'))
    ctx.check('an unknown key is listed', 'rotY' in r.get('unknownKeys', {}), r.get('unknownKeys'))


def plan_apply_refuses_missing_y(ctx):
    c = ctx.client('any')
    r = c.tool('plan_apply', plan='accept-noy', items=[{'prefab': POLE, 'x': ctx.test_xz[0], 'z': ctx.test_xz[1]}], dry=True)
    ctx.check('a dry run reports the item as an error', r['errors'] == 1, r['errors'])
    ctx.check('the sweep is skipped because of the error', r['sweepSkipped'])


def bed_check_consistency(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    r = c.tool('bed_check', x=x, z=z, radius=300)
    if r['beds'] == 0: raise Skip(f'no bed within 300 m of {x},{z} (set HUBNER_TEST_X/Z near one)')
    for b in r['rows']:
        ctx.check(f"bed {b['pos']}: canSleep = exposure and heat", b['canSleep'] == (b['exposureOk'] and b['heat']), b)
        ctx.check(f"bed {b['pos']}: canClaim = exposure", b['canClaim'] == b['exposureOk'], b)
        if b['exposureOk'] and not b['heat']: ctx.check(f"bed {b['pos']}: no fire is the reason given", b['firstFailure'] in ('$msg_cantsleep', '$msg_bednofire'), b['firstFailure'])


def chat_tags(ctx):
    r = ctx.client('any').tool('chat_tail', limit=50)
    rows = r.get('messages') or []
    if not rows: raise Skip('no chat rows captured yet: say something in game (or have someone type) and run again')
    for row in rows[-10:]:
        ctx.check('a row has trust, hints, ours and to', all(k in row for k in ('trust', 'hints', 'ours', 'to')), list(row))
    ctx.check('a trust value is one of the known ones', all(r_['trust'] in ('trusted', 'name_only', 'other') for r_ in rows))


def log_collapse(ctx):
    c = ctx.client('any')
    raw = c.tool('log_tail', lines=200, collapse=False); folded = c.tool('log_tail', lines=200)
    ctx.check('the folded tail never has more lines than the raw one', len(folded['lines']) <= len(raw['lines']), (len(folded['lines']), len(raw['lines'])))
    ctx.check('it reports how many distinct lines it kept', folded.get('distinct') == len(folded['lines']), folded.get('distinct'))


def objects_digest(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    r = c.tool('objects', x=x, z=z, radius=60, digest=True, groups=5)
    if r['total'] == 0: raise Skip(f'no objects within 60 m of {x},{z}')
    ctx.check('at most the asked number of groups', len(r['groups']) <= 5, len(r['groups']))
    ctx.check('the groups and the rest add up to the total', sum(g['count'] for g in r['groups']) + r['inOtherKinds'] == r['total'], r)


def portals_complete(ctx):
    r = ctx.client('any').tool('portals')
    ctx.check('the list says whether it is complete', 'complete' in r and 'source' in r, list(r))


# ---------------------------------------------------------------- acting (build their own scenario, remove it again)
def _spot_pre(ctx):
    if ctx.test_xz == (0.0, 0.0): return 'set HUBNER_TEST_X / HUBNER_TEST_Z to a quiet spot (the case builds three poles there and removes them)'
    if not _has_prefab(ctx.client('any'), POLE): return f'prefab {POLE} not in this game'


def _stack(ctx, plan, ctags=('base', 'mid', 'top'), top_first=False, **kw):
    c = ctx.client('any'); x, z = ctx.test_xz
    g = _ground(c, x, z)
    if g is None: raise Skip('terrain_info gave no ground height (area not loaded?)')
    items = [{'prefab': POLE, 'x': x, 'y': round(g + 2.0 * i - (0.05 if i == 0 else 0), 2), 'z': z, 'key': f'{plan}-{i}', 'ctag': ctags[i]} for i in range(3)]
    if top_first: items.reverse()
    return c, c.plan_apply(plan, items, **kw)


def order_case(ctx):
    plan = 'accept-order'
    c, r = _stack(ctx, plan, top_first=True, order='plan')
    ctx.check('three poles spawned', r['spawned'] == 3, r)
    # sent top first with order:"plan" they are built in that order; sent top first with the default order they must be built lowest first
    c.plan_remove(plan)
    c2, r2 = _stack(ctx, plan, top_first=True)
    j = c2.tool('journal', limit=3)['recent']
    ys = [e['pos'][1] for e in reversed(j) if e['kind'] == 'spawn']
    ctx.check('the default order spawns the lowest pole first', ys == sorted(ys) and len(ys) == 3, ys)


def order_clean(ctx):
    try: ctx.client('any').plan_remove('accept-order', allowCollapse=True)
    except Exception: pass


def collapse_case(ctx):
    plan = 'accept-collapse'
    c, r = _stack(ctx, plan)
    ctx.check('three poles spawned', r['spawned'] == 3, r)
    d = c.plan_remove(plan, ctag='base', dry=True)
    ctx.check('a dry run says what would fall', (d.get('wouldCollapse') or 0) >= 1, d)
    try:
        c.plan_remove(plan, ctag='base'); ctx.check('removing the foot is refused', False, 'it was removed')
    except RuntimeError as e:
        ctx.check('removing the foot is refused', 'refused' in str(e) and 'bring down' in str(e), str(e)[:160])
    left = c.objects(x=ctx.test_xz[0], z=ctx.test_xz[1], radius=3, prefab=POLE)
    ctx.check('all three poles still stand', left['total'] >= 3, left['total'])


def collapse_clean(ctx):
    try: ctx.client('any').plan_remove('accept-collapse', allowCollapse=True)
    except Exception: pass


CASES = [
    Case('smoke', 'initialize, tools/list, notification without id, unknown tool, health', smoke),
    Case('build-stamp', 'core and extensions carry a build stamp; reloads are counted', build_stamp),
    Case('selftest', 'the extension\'s own health checks all pass', selftest),
    Case('tool-stats', 'tool_stats counts a call and gives times', tool_stats),
    Case('plan-rules', 'plan_rules lists hard, ghost and taste rules with the game codes', plan_rules),
    Case('plan-check-tower', 'a 9-pole wooden tower: top unsupported; 4 poles stand', plan_check_tower, pre=tower_pre),
    Case('plan-check-hearth', 'a cauldron on a hearth is no fire-clearance problem', plan_check_hearth),
    Case('plan-check-missing-y', 'plan_check counts a missing y and lists unknown keys', plan_check_missing_y),
    Case('plan-apply-no-y', 'plan_apply (dry) refuses an item without y', plan_apply_refuses_missing_y),
    Case('bed-check', 'bed_check agrees with itself: canSleep = exposure and heat', bed_check_consistency),
    Case('chat-tags', 'chat_tail rows carry trust, hints, ours and to', chat_tags),
    Case('log-collapse', 'log_tail folds repeats and says how many are distinct', log_collapse),
    Case('objects-digest', 'objects digest:true adds up', objects_digest),
    Case('portals-complete', 'portals says whether its list is complete', portals_complete),
    Case('plan-order', 'plan_apply builds lowest first by default', order_case, acts=True, pre=_spot_pre, clean=order_clean),
    Case('remove-collapse', 'plan_remove refuses to take a foot from under a tower', collapse_case, acts=True, pre=_spot_pre, clean=collapse_clean),
]
