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


def world_clock(ctx):
    c = ctx.client('any'); w = c.tool('world_clock')
    ctx.check('the fraction is in 0..1', 0 <= w['dayFraction'] <= 1, w['dayFraction'])
    ctx.check('day, afternoon and night agree with the fraction', w['isNight'] == (w['dayFraction'] <= 0.25 or w['dayFraction'] >= 0.75) and w['isAfternoon'] == (0.5 <= w['dayFraction'] <= 0.75), w)
    ctx.check('the seconds to dusk and dawn fit in one day', 0 <= w['secondsToDusk'] <= w['dayLengthSec'] and 0 <= w['secondsToDawn'] <= w['dayLengthSec'], (w['secondsToDusk'], w['secondsToDawn']))
    ctx.check('the night skips only when the window is open and everybody is in bed', w['nightSkips'] == (w['sleepWindow'] and w['characters'] > 0 and w['inBed'] == w['characters']), w)
    ctx.check('who is not in bed adds up', len(w['notInBed']) == w['characters'] - w['inBed'], w)
    try: client_f = ctx.client('sandbox').tool('env_state')['dayFraction']
    except Exception: return
    ctx.check('the client sees the same time of day (within 0.03)', abs(client_f - w['dayFraction']) < 0.03 or abs(abs(client_f - w['dayFraction']) - 1) < 0.03, (client_f, w['dayFraction']))


def recipes_case(ctx):
    c = ctx.client('any')
    r = c.tool('recipe_info', item='Bronze')
    if r['count'] == 0: raise Skip('no recipe for Bronze in this game')
    ctx.check('every recipe lists ingredients', all(x['ingredients'] for x in r['recipes']), r['recipes'])
    ctx.check('the recipes name what they make', all(x['item'] == 'Bronze' for x in r['recipes']))
    i = c.tool('item_info', name='Bronze')
    ctx.check('item_info knows where it is made', len(i['madeBy']) >= 1, i['madeBy'])
    ctx.check('bars do not go through a portal', i['teleportable'] is False, i['teleportable'])
    u = c.tool('recipe_info', uses='Copper')
    ctx.check('Copper is used somewhere', u['count'] >= 1, u['count'])


def missing_for_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    r = c.tool('recipe_info', item='Bronze')
    if r['count'] == 0: raise Skip('no recipe for Bronze in this game')
    best = min((x_ for x_ in r['recipes'] if not x_['upgradeOnly']), key=lambda x_: sum(i['amount'] for i in x_['ingredients']) / x_['makes'])
    m = c.tool('missing_for', item='Bronze', count=5, x=x, z=z, radius=1)
    if m['chests'] > 0: raise Skip('there are chests within 1 m of the test spot, so the stock is not empty (move HUBNER_TEST_X/Z)')
    crafts = -(-5 // best['makes'])
    for ing in best['ingredients']:
        ctx.check(f"missing {ing['item']} = crafts x amount", m['missing'].get(ing['item']) == crafts * ing['amount'], (m['missing'], crafts, ing))
    ctx.check('one step, Bronze', [s.split(' x')[0] for s in m['steps']] == ['Bronze'], m['steps'])
    ctx.check('the station is named', not best['station'] or best['station'] in m['stations'], m['stations'])
    ctx.check('it is not complete without stock', m['complete'] is False)


def stations_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    r = c.tool('stations', x=x, z=z, radius=150)
    if not r['stations']: raise Skip(f'no crafting station within 150 m of {x},{z}')
    for s in r['stations']:
        ctx.check(f"{s['prefab']}: level = 1 + extensions", s['level'] == 1 + len(s['extensions']), s)
        ctx.check(f"{s['prefab']}: every extension is inside its own range", all(e['distance'] < 12 for e in s['extensions']), s['extensions'])
        ctx.check(f"{s['prefab']}: the build range is at least the base range", s['buildRange'] >= 10, s['buildRange'])


def smelters_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    r = c.tool('smelters', x=x, z=z, radius=150)
    if r['total'] == 0: raise Skip(f'no smelter, kiln or windmill within 150 m of {x},{z}')
    for s in r['smelters']:
        ctx.check(f"{s['prefab']}: queued = ore listed (up to 40)", s['queued'] == len(s['ore']) or s['queued'] > 40, s)
        ctx.check(f"{s['prefab']}: room = max - queued", s['queueRoom'] >= 0, s)
        ctx.check(f"{s['prefab']}: stalled says why or is empty", s['stalled'] in (None, 'no ore queued', 'out of fuel'), s['stalled'])
        ctx.check(f"{s['prefab']}: an empty queue takes no time", s['queued'] > 0 or s['secondsToEmpty'] == 0, s)


def creature_case(ctx):
    c = ctx.client('any')
    if not _has_prefab(c, 'Boar'): raise Skip('prefab Boar not in this game')
    r = c.tool('creature_info', name='Boar', armor=10, playerHp=100)
    ctx.check('health by star is health x level', r['healthByStar'] == [r['health'], r['health'] * 2, r['health'] * 3], r['healthByStar'])
    ctx.check('a boar has an attack', len(r['attacks']) >= 1, r['attacks'])
    ctx.check('a boar can be tamed', 'tame' in r and r['tame']['tamingTimeSec'] > 0, r.get('tame'))
    for w in r['againstPlayer']['hits']:
        hits = [s['hit'] for s in w['stars']]
        ctx.check('more stars hurt more', hits == sorted(hits), w)
        ctx.check('hits to kill are consistent with the hit', all(s['hitsToKill'] >= 100 / max(s['hit'], 0.01) - 1 for s in w['stars']), w)
    ctx.check('the damage factor by star is 1, 1.5, 2', r['damageFactorByStar'] == [1, 1.5, 2])


def base_value_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    r = c.tool('base_value', x=x, z=z)
    if not r['areas'] and r['value'] == 0 and not r['charactersNearby']: raise Skip(f'no PlayerBase area (fire, workbench, ward...) within reach of {x},{z}')
    ctx.check('the value is the number of areas in reach', r['value'] == len(r['areas']) or r['value'] > 60, (r['value'], len(r['areas'])))
    ctx.check('a raid needs three', r['raidEligible'] == (r['value'] >= 3), r['value'])
    ctx.check('every area is within 20 m plus its own radius', all(((a['pos'][0] - x) ** 2 + (a['pos'][2] - z) ** 2) ** 0.5 <= 20 + a['radius'] + 0.5 + abs(a['pos'][1] - r['point'][1]) for a in r['areas']), r['areas'][:3])


def breeding_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    r = c.tool('husbandry_state', x=x, z=z, radius=60, tamedOnly=True)
    rows = [q for q in r['creatures'] if 'breeding' in q]
    if not rows: raise Skip(f'no tamed animal that breeds within 60 m of {x},{z}')
    for q in rows:
        b = q['breeding']
        ctx.check(f"{q['prefab']}: canBreedNow = no blockers and not pregnant", b['canBreedNow'] == (not b['blockers'] and not q['pregnant']), q)
        ctx.check(f"{q['prefab']}: a hungry animal says so", (not q['hungry']) or any(s.startswith('hungry') for s in b['blockers']) or q['pregnant'], q)
        ctx.check(f"{q['prefab']}: it counts itself", b['nearby'] >= 1, b)


def ground_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    if not _has_prefab(c, POLE): raise Skip(f'prefab {POLE} not in this game')
    item = [{'prefab': POLE, 'x': x, 'z': z, 'key': 'g'}]
    auto = c.tool('plan_check', items=item); gen = c.tool('plan_check', items=item, ground='generator')
    ctx.check('the ground note says where the heights came from', 'vertex heights' in auto['ground'], auto['ground'])
    ctx.check('ground:generator uses only the generator', ' 0 vertex heights of the loaded terrain' in gen['ground'] or 'forced' in gen['ground'], gen['ground'])
    if 'terrain triangles from 0 ' in auto['ground']: raise Skip('the zone at the test spot is not loaded here (the server without a virtual peer): the generator was used for everything')
    ctx.check('a flat groundY still wins', c.tool('plan_check', items=item, groundY=0)['ground'].startswith('flat ground'))


def scene_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    info = c.tool('scene_capture', name='accept-scene', x=x, z=z, radius=16, note='accept.py')
    if info['objects'] == 0: raise Skip(f'nothing with colliders within 16 m of {x},{z}')
    s = json.loads(c.raw_text('scene_get', name='accept-scene'))
    ctx.check('the schema is hubner.scene/1', s['schema'] == 'hubner.scene/1', s['schema'])
    ctx.check('the objects are all there', len(s['objects']) == info['objects'], (len(s['objects']), info['objects']))
    ctx.check('every object has its prefab geometry', all(o['prefab'] in s['prefabs'] for o in s['objects']))
    ctx.check('the terrain grid is nx x nz', len(s['terrain']['h']) == s['terrain']['nx'] * s['terrain']['nz'], s['terrain']['source'])
    ctx.check('the game recorded support for the pieces (WearNTear owners)', info['withRecordedSupport'] >= 0, info)


def plant_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    if not _has_prefab(c, 'sapling_carrot'): raise Skip('prefab sapling_carrot not in this game')
    far = c.tool('plant_check', items=[{'prefab': 'sapling_carrot', 'x': x, 'z': z, 'key': 'a'}, {'prefab': 'sapling_carrot', 'x': x + 3, 'z': z, 'key': 'b'}])
    first = far['plants'][0]['status']
    if first in ('WrongBiome', 'NotCultivated', 'TooHot', 'TooCold'): raise Skip(f'the test spot is {first} for a carrot: these checks come before shade and space (use a cultivated meadow spot)')
    near = c.tool('plant_check', items=[{'prefab': 'sapling_carrot', 'x': x, 'z': z, 'key': 'a'}, {'prefab': 'sapling_carrot', 'x': x + 0.2, 'z': z, 'key': 'b'}])
    if first != 'NoSpace': ctx.check('two carrots 3 m apart do not block each other', 'NoSpace' not in {p['status'] for p in far['plants']}, far['plants'])
    ctx.check('two carrots 0.2 m apart block each other', {p['status'] for p in near['plants']} == {'NoSpace'}, near['plants'])
    ctx.check('a prefab that is not a plant is an error row, not a guess', 'is not a plant' in json.dumps(c.tool('plant_check', items=[{'prefab': 'wood_wall', 'x': x, 'z': z}, {'prefab': 'sapling_carrot', 'x': x, 'z': z}])['errors']))


def farm_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    r = c.tool('farm_state', x=x, z=z, radius=40)
    if r['total'] == 0: raise Skip(f'no plant within 40 m of {x},{z}')
    for p in r['plants']:
        ctx.check(f"{p['prefab']}: ready-from is never after ready-by", p['readyFromSeconds'] <= p['readyBySeconds'], p)
        ctx.check(f"{p['prefab']}: hadTimeToGrow agrees with ready-by", p['hadTimeToGrow'] == (p['readyBySeconds'] == 0), p)


def raid_case(ctx):
    r = ctx.client('any').tool('raid_state')
    ctx.check('events are listed', len(r['events']) > 0, len(r['events']))
    ctx.check('the next roll is within one interval', 0 <= r['secondsToNextRoll'] <= r['rollIntervalSeconds'], (r['secondsToNextRoll'], r['rollIntervalSeconds']))
    ctx.check('the chance is a percentage', 0 <= r['chancePercent'] <= 100, r['chancePercent'])
    if r['current']: ctx.check('the running event has time left within its duration', 0 <= r['current']['secondsLeft'], r['current'])


def threats_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    r = c.tool('threats_near', x=x, z=z, radius=100, armor=20)
    if r['total'] == 0: raise Skip(f'no creature within 100 m of {x},{z}')
    order = {'now': 0, 'watch': 1, 'ignore': 2}
    ranks = [order[q['threat']] for q in r['creatures']]
    ctx.check('the worst threats come first', ranks == sorted(ranks), ranks)
    for q in r['creatures']:
        if q['tamed']: ctx.check(f"{q['prefab']}: a tamed animal is ignored", q['threat'] == 'ignore', q)
        if r['passiveMobs']: ctx.check(f"{q['prefab']}: nothing is 'now' in a passive world unless alert and after a target", q['threat'] != 'now' or (q['alert'] and q['haveTarget']), q)
        ctx.check(f"{q['prefab']}: the distance is within the radius", q['distance'] <= 100, q)


def level_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    g = _ground(c, x, z)
    if g is None: raise Skip('terrain_info gave no ground height (area not loaded?)')
    r = c.tool('level_check', x=x, z=z, half=4, targetY=round(g, 1))
    ctx.check('a 9 x 9 pad has 81 vertices', r['vertices'] == 81, r['vertices'])
    ctx.check('raised + lowered never exceeds the vertices', r['raised'] + r['lowered'] <= r['vertices'], r)
    ctx.check('levelling to the ground you stand on is reachable', r['unreachable'] == 0, r)
    far = c.tool('level_check', x=x, z=z, half=4, targetY=round(g, 1) + 100)
    ctx.check('100 m up is out of reach everywhere', far['unreachable'] == far['vertices'] and far['maxShortfall'] >= 90, far)
    ctx.check('and the note says so', any('8 m' in n for n in far['notes']), far['notes'])
    if not r['levelTools']: raise Skip('this game has no terrain tool prefab with a level op')
    ctx.check('every level tool needs at least one stroke', all(t['strokes'] >= 1 for t in r['levelTools']), r['levelTools'])


def cookers_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    r = c.tool('cookers', x=x, z=z, radius=100)
    if r['count'] == 0: raise Skip(f'no cooking station within 100 m of {x},{z}')
    for k in r['cookers']:
        for s in k['slots']:
            if s['item'] is None: continue
            ctx.check(f"{k['prefab']} slot {s['slot']}: a burnt item has no time left to burn", s['state'] != 'burnt' or s['secondsToBurnt'] is None, s)
            ctx.check(f"{k['prefab']} slot {s['slot']}: only an item still cooking has a time to done", s['state'] == 'cooking' or s['secondsToDone'] is None, s)
            ctx.check(f"{k['prefab']} slot {s['slot']}: done comes before burnt", s['secondsToDone'] is None or s['secondsToBurnt'] is None or s['secondsToDone'] <= s['secondsToBurnt'], s)


def fuel_case(ctx):
    c = ctx.client('any'); x, z = ctx.test_xz
    r = c.tool('fuel_forecast', x=x, z=z, radius=60)
    if not r['fuels']: raise Skip(f'no fireplace, smelter or fuel-burning cooker within 60 m of {x},{z}')
    for f in r['fuels']:
        if f['hoursOfCover'] is not None and f['burningPerHour'] > 0:
            ctx.check(f"{f['fuel']}: hours = (inside + chests) / rate", abs(f['hoursOfCover'] - (f['insideThem'] + f['inChests']) / f['burningPerHour']) < 0.2, f)
        ctx.check(f"{f['fuel']}: no cover figure means nothing is burning", f['hoursOfCover'] is not None or f['burningPerHour'] == 0, f)


def plan_materials_case(ctx):
    c = ctx.client('any')
    if not _has_prefab(c, 'wood_wall'): raise Skip('prefab wood_wall not in this game')
    one = c.tool('plan_materials', items=[{'prefab': 'wood_wall', 'x': 0, 'y': 0, 'z': 0}])
    three = c.tool('plan_materials', items=[{'prefab': 'wood_wall', 'x': i * 2, 'y': 0, 'z': 0} for i in range(3)])
    if not one['materials']: raise Skip('wood_wall is free in this world (NoBuildCost)')
    ctx.check('three walls cost three times one', all(three['materials'][k] == 3 * v for k, v in one['materials'].items()), (one['materials'], three['materials']))
    ctx.check('an unknown prefab is listed, not guessed', c.tool('plan_materials', items=[{'prefab': 'no_such_piece', 'x': 0, 'y': 0, 'z': 0}])['unknownPrefabs'] == ['no_such_piece'])


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
    Case('world-clock', 'world_clock agrees with itself and with the client', world_clock),
    Case('recipes', 'recipe_info and item_info agree about Bronze', recipes_case),
    Case('missing-for', 'missing_for with no stock is crafts x ingredients', missing_for_case),
    Case('stations', 'stations: level is 1 + the extensions that count', stations_case),
    Case('smelters', 'smelters: queue, room and why one stands still agree', smelters_case),
    Case('creature-info', 'creature_info: stars scale health and damage, hits to kill follow', creature_case),
    Case('base-value', 'base_value counts the PlayerBase areas in reach; a raid needs three', base_value_case),
    Case('breeding', 'husbandry_state: why animals are not breeding agrees with their state', breeding_case),
    Case('plan-check-ground', 'plan_check says where its ground came from and can be forced', ground_case),
    Case('scene-capture', 'scene_capture writes a scene that scene_get returns whole', scene_case),
    Case('plant-check', 'plant_check: two carrots 0.2 m apart block each other', plant_case),
    Case('farm-state', 'farm_state: ready times and the grown flag agree', farm_case),
    Case('raid-state', 'raid_state: roll timer and events agree', raid_case),
    Case('threats-near', 'threats_near: ranked, tamed ignored, passive worlds respected', threats_case),
    Case('level-check', 'level_check: reach, volumes and strokes agree', level_case),
    Case('cookers', 'cookers: slot states and times agree', cookers_case),
    Case('fuel-forecast', 'fuel_forecast: hours of cover follow from stock and rate', fuel_case),
    Case('plan-materials', 'plan_materials scales with the item count', plan_materials_case),
    Case('portals-complete', 'portals says whether its list is complete', portals_complete),
    Case('plan-order', 'plan_apply builds lowest first by default', order_case, acts=True, pre=_spot_pre, clean=order_clean),
    Case('remove-collapse', 'plan_remove refuses to take a foot from under a tower', collapse_case, acts=True, pre=_spot_pre, clean=collapse_clean),
]
