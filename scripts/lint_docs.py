#!/usr/bin/env python3
"""Documentation lint: what the docs say must exist.

  * every relative markdown link points at a file that exists,
  * every backticked repository path (src/..., ext/..., plans/...) exists,
  * every backticked name that looks like one of our tools (snake_case, first word shared with a registered tool) is registered, is listed in
    scripts/doc-words.txt (names that are not tools) or sits on a line that says it is retired/removed/gone/renamed.

  python3 scripts/lint_docs.py
"""
import os, re, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lint_tools

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOP = {'src', 'ext', 'tests', 'python', 'scripts', 'plans', 'docs', 'config', 'build', 'Thunderstore', '.github'}
LINK = re.compile(r'\[[^\]]*\]\(([^)\s]+)\)')
TICK = re.compile(r'`([^`\n]+)`')
PATH = re.compile(r'^[A-Za-z0-9_./-]+\.(cs|py|md|yml|yaml|json|csproj|props|sh|targets|txt)$')
TOOLISH = re.compile(r'^[a-z][a-z0-9]*(_[a-z0-9]+)+$')
GONE = re.compile(r'retired|removed|gone|renamed|replaced|no longer|used to|old name', re.I)


def doc_files():
    out = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '*.md'], cwd=ROOT, text=True).split()
    return [f for f in out if not f.startswith('plans/research/modules-')]            # the module reports quote another project's names


def doc_words():
    p = os.path.join(ROOT, 'scripts', 'doc-words.txt')
    return {l.strip() for l in open(p, encoding='utf-8') if l.strip() and not l.startswith('#')} if os.path.exists(p) else set()


def check(path, text, tools, prefixes, words):
    errs = []
    base = os.path.dirname(os.path.join(ROOT, path))
    research = path.startswith('plans/research/')           # notes on another project and on tools not built yet: only their links are checked
    for n, line in enumerate(text.splitlines(), 1):
        for m in LINK.finditer(line):
            t = m.group(1).split('#')[0]
            if not t or re.match(r'^[a-z]+:', t) or t.startswith('mailto'): continue
            if not os.path.exists(os.path.normpath(os.path.join(base, t))): errs.append(f'{path}:{n}: link to {t} does not exist')
        for m in TICK.finditer('' if research else line):
            tok = m.group(1).strip()
            if PATH.match(tok) and '/' in tok and tok.split('/')[0] in TOP:
                if not (os.path.exists(os.path.join(ROOT, tok)) or os.path.exists(os.path.normpath(os.path.join(base, tok)))) and '*' not in tok and not GONE.search(line):
                    errs.append(f'{path}:{n}: path {tok} does not exist')
            elif TOOLISH.match(tok) and tok.split('_')[0] in prefixes and tok not in tools and tok not in words and not GONE.search(line):
                errs.append(f'{path}:{n}: `{tok}` looks like a tool but none is registered by that name (add it to scripts/doc-words.txt if it is not one)')
    return errs


def main():
    tools = {t['name'] for t in lint_tools.collect()}
    prefixes = {n.split('_')[0] for n in tools if '_' in n}
    words = doc_words(); errs = []
    for f in doc_files():
        errs += check(f, open(os.path.join(ROOT, f), encoding='utf-8').read(), tools, prefixes, words)
    if errs:
        print('\n'.join(errs)); return 1
    print(f'ok: {len(doc_files())} documents'); return 0


if __name__ == '__main__':
    sys.exit(main())
