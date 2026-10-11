#!/usr/bin/env python3
"""Write-API allowlist. The AGENTS rule says anything that writes to the world goes through Plans.Guard, respects Plans.Protected and is journaled. This makes the rule structural:
each risky game call may appear only in the files that are known to apply those rules, so a new file cannot grow a second way to create, destroy or move objects unnoticed.

  python3 scripts/lint_writes.py
Add a file to a rule only after it goes through the guards (and say so in the commit).
"""
import os, re, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from csscan import mask, line_of

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

RULES = [
    (r'\bCreateNewZDO\s*\(',                 {'ext/Plans.cs'}, 'creating a ZDO'),
    (r'\bDestroyZDO\s*\(',                  {'ext/Plans.cs', 'ext/Audit.cs'}, 'destroying a ZDO'),
    (r'ZNetScene\.instance\.Destroy\s*\(',  {'ext/Plans.cs', 'ext/ClientTools.cs'}, 'destroying an object'),
    (r'\.SetPosition\s*\(',                 {'ext/Plans.cs', 'ext/ClientTools.cs'}, 'moving a ZDO'),
    (r'\bObject\.Instantiate\s*\(|\bInstantiate\s*\(', {'ext/ClientTools.cs', 'ext/Place.cs', 'ext/PlanCheck.cs'}, 'instantiating a prefab'),
    (r'\bSetGodMode\s*\(',                  {'ext/ClientTools.cs', 'ext/Control.cs'}, 'god mode'),
    (r'\bTryRunCommand\s*\(',               {'src/ValheimMCP/ConsoleBridge.cs'}, 'running console commands'),
    (r'\bKick\s*\(|\bBan\s*\(|AddToBanList|ZNet\.instance\.Kick', set(), 'kicking or banning players'),
    (r'\bSendText\s*\(',                    {'ext/Insight.cs'}, 'sending chat as the player'),
]

def files():
    out = subprocess.check_output(['git', 'ls-files', 'src/*.cs', 'ext/*.cs'], cwd=ROOT, text=True).split()
    return [f for f in out if f.endswith('.cs')]

def check(path, text):
    m = mask(text); bad = []
    for pat, allowed, what in RULES:
        if path in allowed: continue
        for mt in re.finditer(pat, m):
            bad.append((line_of(text, mt.start()), what, pat))
    return bad

def main():
    problems = []
    for f in files():
        with open(os.path.join(ROOT, f), encoding='utf-8') as fh:
            for ln, what, pat in check(f, fh.read()):
                problems.append(f'{f}:{ln}: {what} outside its allowed files')
    if problems:
        print('write APIs used outside the files that apply the guards:')
        for p in problems: print('  ' + p)
        return 1
    print('ok: write APIs only in their allowed files')
    return 0

if __name__ == '__main__':
    sys.exit(main())
