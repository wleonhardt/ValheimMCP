"""Live acceptance runner: a table of cases that run against a real game and say pass / fail / skipped / blocked.

  python3 python/live/accept.py --list [--json]          the cases, what each needs and whether it acts
  python3 python/live/accept.py --dry-run                read-only cases run; acting cases only check their preconditions
  python3 python/live/accept.py --only smoke,plan-rules  run these cases (a prefix selects every case that starts with it)
  HUBNER_URL=... HUBNER_LIVE_WORLDS=Test python3 python/live/accept.py

Rules the cases follow (they are why a pass means something):
  * A case that cannot run here (no beds, no server twin, a prefab missing) is SKIPPED with the reason. It never fails and it never passes by default.
  * A case that acts builds its own known scenario and removes it again; the runner checks the journal and the plan list are as they were.
  * After every acting case the runner checks that the extension dropped no calls, reported no main-thread stall and has no journal error.
  * Exit code = number of failed cases. A guard (live_guard) that refuses gives BLOCKED for every case and exit code 1.
"""
import argparse, json, os, subprocess, sys, time
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import hubner  # noqa: E402

PASS, FAIL, SKIPPED, BLOCKED = 'pass', 'fail', 'skipped', 'blocked'


class Skip(Exception):
    """Raised by a case (or returned by its pre) when it cannot run in this world: the reason is the message."""


class Ctx:
    """What a case sees: the clients, a place to record checks, and a scratch plan name."""
    def __init__(self, sandbox=None, twin=None):
        self.sandbox, self.twin = sandbox, twin
        self.checks = []
        self.test_xz = (float(os.environ.get('HUBNER_TEST_X', '0')), float(os.environ.get('HUBNER_TEST_Z', '0')))
        self.test_y = os.environ.get('HUBNER_TEST_Y')

    def client(self, needs):
        c = self.twin if needs == 'server' else self.sandbox if needs == 'sandbox' else (self.sandbox or self.twin)
        if c is None: raise Skip(f'needs the {needs} endpoint, which is not configured')
        return c

    def check(self, name, cond, detail=''):
        self.checks.append({'name': name, 'ok': bool(cond), 'detail': str(detail)})
        return bool(cond)

    @property
    def failed(self): return [c for c in self.checks if not c['ok']]


class Case:
    def __init__(self, key, doc, run, needs='any', acts=False, pre=None, clean=None):
        self.key, self.doc, self.run, self.needs, self.acts, self.pre, self.clean = key, doc, run, needs, acts, pre, clean


def select(cases, only):
    if not only: return list(cases)
    prefixes = [p.strip() for p in only.split(',') if p.strip()]
    return [c for c in cases if any(c.key == p or c.key.startswith(p) for p in prefixes)]


def _health(c):
    """The numbers that must not move during an acting case."""
    try:
        e = c.tool('ext_status'); s = c.tool('status'); pl = c.tool('plans')
        return {'dropped': e.get('droppedCalls', 0), 'stalls': e.get('mainThreadStallReports', 0), 'journalError': s.get('journalError', ''), 'plans': len(pl.get('plans', [])) if isinstance(pl, dict) else None}
    except Exception as e:
        raise RuntimeError(f'the health check of the extension failed ({type(e).__name__}: {e}): an acting case cannot be judged without it')


def run_case(case, ctx, dry_run=False):
    ctx.checks = []
    try:
        client = ctx.client(case.needs)
        if case.pre:
            why = case.pre(ctx)
            if why: return result(case, SKIPPED, why, ctx)
        if case.acts and dry_run: return result(case, SKIPPED, 'dry run: this case acts, its preconditions hold', ctx)
        before = _health(client) if case.acts else None
        try:
            case.run(ctx)
        finally:
            if case.clean:
                try: case.clean(ctx)
                except Exception as e: ctx.check('cleanup', False, f'{type(e).__name__}: {e}')
        if case.acts and before is not None:
            after = _health(client)
            if after is not None:
                ctx.check('no calls dropped', after['dropped'] == before['dropped'], f"{before['dropped']} -> {after['dropped']}")
                ctx.check('no main-thread stall reported', after['stalls'] == before['stalls'], f"{before['stalls']} -> {after['stalls']}")
                ctx.check('journal has no error', not after['journalError'], after['journalError'])
                if before['plans'] is not None: ctx.check('plans as before (cleanup)', after['plans'] == before['plans'], f"{before['plans']} -> {after['plans']}")
        if not ctx.checks: return result(case, FAIL, 'the case recorded no check: a case that asserts nothing proves nothing', ctx)
        return result(case, FAIL if ctx.failed else PASS, '; '.join(f"{c['name']}: {c['detail']}" for c in ctx.failed), ctx)
    except Skip as e:
        if ctx.failed: return result(case, FAIL, '; '.join(f"{c['name']}: {c['detail']}" for c in ctx.failed) + f' (then skipped: {e})', ctx)          # a skip never hides a check that already failed
        return result(case, SKIPPED, str(e), ctx)
    except hubner.GuardError as e:
        return result(case, BLOCKED, str(e), ctx)
    except Exception as e:
        return result(case, FAIL, f'{type(e).__name__}: {e}', ctx)


def result(case, verdict, reason, ctx):
    return {'key': case.key, 'doc': case.doc, 'verdict': verdict, 'reason': reason, 'acts': case.acts, 'needs': case.needs, 'checks': list(ctx.checks)}


def run_all(cases, ctx, dry_run=False, only=None, guard=True, worlds=None):
    chosen = select(cases, only)
    if guard:
        try:
            for c in (ctx.sandbox, ctx.twin):
                if c is not None: hubner.live_guard(c, worlds)
        except hubner.GuardError as e:
            return [{'key': k.key, 'doc': k.doc, 'verdict': BLOCKED, 'reason': str(e), 'acts': k.acts, 'needs': k.needs, 'checks': []} for k in chosen]
    return [run_case(k, ctx, dry_run) for k in chosen]


def summary(results):
    n = {v: sum(1 for r in results if r['verdict'] == v) for v in (PASS, FAIL, SKIPPED, BLOCKED)}
    return n


def render_text(results):
    out = []
    for r in results:
        out.append(f"{r['verdict'].upper():8} {r['key']:28} {r['reason']}".rstrip())
    n = summary(results)
    out.append(f"== {'ALL OK' if not n[FAIL] and not n[BLOCKED] else str(n[FAIL] + n[BLOCKED]) + ' FAILED'} == pass {n[PASS]}, fail {n[FAIL]}, skipped {n[SKIPPED]}, blocked {n[BLOCKED]}")
    return '\n'.join(out)


def render_markdown(results, sha=''):
    n = summary(results)
    out = [f'# Live acceptance {sha}'.rstrip(), '', f"pass {n[PASS]}, fail {n[FAIL]}, skipped {n[SKIPPED]}, blocked {n[BLOCKED]}", '', '| case | verdict | why / what failed |', '|---|---|---|']
    for r in results: out.append(f"| {r['key']} | {r['verdict']} | {r['reason'].replace('|', '/')} |")
    return '\n'.join(out) + '\n'


def git_sha():
    try: return subprocess.run(['git', 'rev-parse', '--short', 'HEAD'], capture_output=True, text=True, cwd=os.path.dirname(os.path.abspath(__file__))).stdout.strip()
    except Exception: return ''


def main(argv=None):
    import cases as case_table
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--list', action='store_true'); ap.add_argument('--json', action='store_true'); ap.add_argument('--dry-run', action='store_true'); ap.add_argument('--only')
    ap.add_argument('--sandbox', default=os.environ.get('HUBNER_URL')); ap.add_argument('--server', default=os.environ.get('HUBNER_SERVER_URL_LIVE'))
    ap.add_argument('--worlds'); ap.add_argument('--no-report', action='store_true')
    a = ap.parse_args(argv)
    cases = case_table.CASES
    if a.list:
        rows = [{'key': c.key, 'needs': c.needs, 'acts': c.acts, 'doc': c.doc} for c in select(cases, a.only)]
        print(json.dumps(rows, indent=1) if a.json else '\n'.join(f"{r['key']:28} {r['needs']:8} {'acts' if r['acts'] else 'reads':5} {r['doc']}" for r in rows)); return 0
    ctx = Ctx(hubner.Client(a.sandbox) if a.sandbox else None, hubner.Client(a.server, server=True) if a.server else None)
    results = run_all(cases, ctx, dry_run=a.dry_run, only=a.only, worlds=[w for w in (a.worlds or '').split(',') if w] or None)
    print(render_text(results))
    if not a.no_report:
        d = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'reports'); os.makedirs(d, exist_ok=True); sha = git_sha() or time.strftime('%Y%m%d-%H%M%S')
        with open(os.path.join(d, f'accept-{sha}.json'), 'w') as f: json.dump(results, f, indent=1)
        with open(os.path.join(d, f'accept-{sha}.md'), 'w') as f: f.write(render_markdown(results, sha))
    n = summary(results)
    return n[FAIL] + (1 if n[BLOCKED] else 0)


if __name__ == '__main__':
    sys.exit(main())
