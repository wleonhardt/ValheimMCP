#!/usr/bin/env python3
"""Run every check AGENTS.md asks for, in one go.

  python3 scripts/check.py                run all
  python3 scripts/check.py --changed      only what the files changed since HEAD (and untracked ones) can affect
  python3 scripts/check.py --only lint    only checks whose key starts with this (build, test, py, lint)
  python3 scripts/check.py --list

Ends with `== ALL OK ==` or `== N FAILED ==` and, for each failure, the exact command to run again. Exit code = number of failures.
"""
import os, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

CHECKS = [
    ('build.core', ['dotnet', 'build', '-c', 'Release', 'ValheimMCP.csproj', '--nologo', '-v', 'q']),
    ('build.ext', ['dotnet', 'build', '-c', 'Release', 'ext/HubnerExt.csproj', '--nologo', '-v', 'q']),
    ('build.server', ['dotnet', 'build', '-c', 'Release', 'ext/HubnerExtServer.csproj', '--nologo', '-v', 'q']),
    ('test.dotnet', ['dotnet', 'test', 'tests/ValheimMCP.Tests', '--nologo', '-v', 'q']),
    ('py.client', [sys.executable, '-m', 'unittest', 'discover', '-s', 'python/tests']),
    ('py.scripts', [sys.executable, '-m', 'unittest', 'discover', '-s', 'scripts/tests']),
    ('lint.catch', [sys.executable, 'scripts/lint_catch.py']),
    ('lint.writes', [sys.executable, 'scripts/lint_writes.py']),
    ('lint.tools', [sys.executable, 'scripts/lint_tools.py']),
    ('lint.docs', [sys.executable, 'scripts/lint_docs.py']),
]


def affected(changed):
    """The check keys that files changed can break. Unknown files run everything (a rule nobody wrote down should not silently skip a check)."""
    keys = set()
    for f in changed:
        if f.startswith('src/') or f == 'ValheimMCP.csproj' or f.startswith('build/'): keys |= {'build.core', 'build.ext', 'build.server', 'test.dotnet', 'lint.catch', 'lint.writes', 'lint.tools'}
        elif f.startswith('ext/'): keys |= {'build.ext', 'build.server', 'test.dotnet', 'lint.catch', 'lint.writes', 'lint.tools'}
        elif f.startswith('tests/'): keys |= {'test.dotnet'}
        elif f.startswith('python/'): keys |= {'py.client'}
        elif f.startswith('scripts/'): keys |= {'py.scripts', 'lint.catch', 'lint.writes', 'lint.tools', 'lint.docs'}
        elif f.endswith('.md') or f.startswith('docs/') or f.startswith('plans/'): keys |= {'lint.docs'}
        elif f.startswith('.github/') or f.startswith('config/') or f.startswith('Thunderstore/'): pass
        else: return {k for k, _ in CHECKS}
    return keys


def changed_files():
    a = subprocess.run(['git', 'diff', '--name-only', 'HEAD'], cwd=ROOT, capture_output=True, text=True).stdout.split()
    b = subprocess.run(['git', 'ls-files', '--others', '--exclude-standard'], cwd=ROOT, capture_output=True, text=True).stdout.split()
    return sorted(set(a + b))


def run(checks):
    failed = []
    for key, cmd in checks:
        t = time.time()
        r = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True)
        ok = r.returncode == 0
        print(f"{'ok  ' if ok else 'FAIL'} {key:14} {time.time() - t:5.1f}s")
        if not ok:
            failed.append((key, cmd)); out = (r.stdout + r.stderr).strip().splitlines()
            print('\n'.join('     ' + l for l in out[-15:]))
    return failed


def main(argv):
    if '--list' in argv: print('\n'.join(k for k, _ in CHECKS)); return 0
    checks = CHECKS
    if '--changed' in argv:
        keys = affected(changed_files()); checks = [c for c in checks if c[0] in keys]
        if not checks: print('nothing to check'); return 0
    if '--only' in argv:
        pre = argv[argv.index('--only') + 1]; checks = [c for c in checks if c[0].startswith(pre)]
    failed = run(checks)
    if not failed: print('== ALL OK =='); return 0
    print(f'== {len(failed)} FAILED ==')
    for key, cmd in failed: print(f'  {key}: ' + ' '.join(cmd))
    return len(failed)


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
