#!/usr/bin/env python3
"""Silent-catch lint. A catch block that neither logs nor throws hides a failure (a game update that breaks a reflective lookup then shows as a silently wrong answer).

A catch is flagged when its body has no throw and no logging call (Log, Once, Report, Warn, Error, Fault). `// allow: why` on the catch line, inside the body or on the line above
accepts it. The count per file may not exceed scripts/lint-baseline.json (it only shrinks: fix a site, run --update-baseline, commit the lower number).

  python3 scripts/lint_catch.py            check against the baseline
  python3 scripts/lint_catch.py --list     print every flagged site
  python3 scripts/lint_catch.py --update-baseline
"""
import json, os, re, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from csscan import mask, line_of, match_brace

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASELINE = os.path.join(ROOT, 'scripts', 'lint-baseline.json')
OK_TOKENS = re.compile(r'\bthrow\b|\bLog\w*\b|\bOnce\b|\bReport\b|\bWarn\w*\b|\bError\b|\bFault\w*\b|Debug\.Log')

def sources():
    files = subprocess.check_output(['git', 'ls-files', 'src/*.cs', 'ext/*.cs'], cwd=ROOT, text=True).split()
    return [f for f in files if f.endswith('.cs')]

def flagged(path, text):
    m = mask(text)
    lines = text.split('\n')
    res = []
    for mt in re.finditer(r'\bcatch\b', m):
        pos = mt.end()
        # optional (type name) and when (...)
        k = pos
        while k < len(m) and m[k] in ' \t\r\n': k += 1
        typed = False
        if m[k:k + 1] == '(':
            typed = True
            d = 0
            while k < len(m):
                d += (m[k] == '(') - (m[k] == ')')
                k += 1
                if d == 0: break
            while k < len(m) and m[k] in ' \t\r\n': k += 1
            if m[k:k + 4] == 'when':
                k += 4
                while m[k] != '(': k += 1
                d = 0
                while k < len(m):
                    d += (m[k] == '(') - (m[k] == ')')
                    k += 1
                    if d == 0: break
                while k < len(m) and m[k] in ' \t\r\n': k += 1
        if m[k:k + 1] != '{':
            continue
        end = match_brace(m, k)
        body = m[k + 1:end]
        ln = line_of(text, mt.start())
        end_ln = line_of(text, end)
        if OK_TOKENS.search(body):
            continue
        window = '\n'.join(lines[max(0, ln - 2):end_ln])
        if re.search(r'(//|/\*)\s*allow:', window):
            continue
        kind = 'empty' if body.strip() == '' else ('bare' if not typed else 'silent')
        res.append((ln, kind))
    return res

def scan():
    out = {}
    for f in sources():
        with open(os.path.join(ROOT, f), encoding='utf-8') as fh:
            r = flagged(f, fh.read())
        if r: out[f] = r
    return out

def main(argv):
    found = scan()
    counts = {f: len(v) for f, v in found.items()}
    if '--list' in argv:
        for f, v in sorted(found.items()):
            for ln, kind in v: print(f'{f}:{ln}: {kind} catch (no log, no throw)')
        print(f'{sum(counts.values())} flagged in {len(counts)} files')
        return 0
    if '--update-baseline' in argv:
        json.dump(counts, open(BASELINE, 'w'), indent=1, sort_keys=True); open(BASELINE, 'a').write('\n')
        print(f'baseline written: {sum(counts.values())} flagged catches')
        return 0
    base = {}
    if os.path.exists(BASELINE):
        with open(BASELINE) as bf: base = json.load(bf)
    bad = []
    for f, n in sorted(counts.items()):
        if n > base.get(f, 0):
            bad.append(f'{f}: {n} silent catches, baseline {base.get(f, 0)}: ' + ', '.join(f'L{ln} {k}' for ln, k in found[f]))
    if bad:
        print('silent catch blocks above the baseline (log with U.Once / Plugin.Log, rethrow, or justify with `// allow: why`):')
        for b in bad: print('  ' + b)
        return 1
    shrunk = [f for f in base if counts.get(f, 0) < base[f]]
    if shrunk: print('note: fewer silent catches than the baseline in ' + ', '.join(shrunk) + ' -- run --update-baseline and commit the lower numbers')
    print(f'ok: {sum(counts.values())} flagged (baseline {sum(base.values())})')
    return 0

if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
