#!/usr/bin/env python3
"""Inventory network harness commands and its assertion/capture/log expressions; compare a parent without fixtures."""
import argparse
from collections import Counter
import json
from pathlib import Path
import re
import subprocess


def git_text(revision, path):
    return subprocess.check_output(['git', 'show', f'{revision}:{path}'], text=True)


def paths_at(revision):
    return subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', revision], text=True).splitlines()


def invocations(text, names):
    """Retain entire C# expressions, including multiline conditions. Ignore declarations and comments."""
    result = []
    for match in re.finditer(r'\b(' + '|'.join(names) + r')\(', text):
        before = text[text.rfind('\n', 0, match.start()) + 1:match.start()]
        if re.search(r'\b(?:void|bool|Action|Color|string|double)\s+$', before) or before.lstrip().startswith(('//', '///')):
            continue
        depth = 1
        quoted = False
        escaped = False
        end = match.end()
        while end < len(text) and depth:
            char = text[end]
            if escaped:
                escaped = False
            elif char == '\\' and quoted:
                escaped = True
            elif char == '"':
                quoted = not quoted
            elif not quoted:
                if char == '(':
                    depth += 1
                elif char == ')':
                    depth -= 1
            end += 1
        if depth:
            raise ValueError(f'unterminated {match.group(1)} expression')
        expression = re.sub(r'\s+', ' ', text[match.start():end]).replace('NetScenarioTools.ParseAppearance(', 'ParseAppearance(')
        if expression != 'Log(text)' and not re.search(r'\bvoid\s+\w+\.$', before):
            result.append(expression)
    return sorted(result)


def inventory(revision):
    paths = paths_at(revision) if revision else [p for p in subprocess.check_output(
        ['git', 'ls-files', '--cached', '--others', '--exclude-standard'], text=True).splitlines() if Path(p).is_file()]
    read = (lambda p: git_text(revision, p)) if revision else (lambda p: Path(p).read_text())
    harness = [p for p in paths if (p.startswith('src/Dev/NetTest') or p.startswith('src/Dev/NetScenarios/')) and p.endswith('.cs')]
    source = '\n'.join(read(p) for p in sorted(harness))
    references = []
    for path in sorted(paths):
        if not (path in ('Makefile', 'README.md') or path.startswith(('docs/', 'tools/'))) or Path(path).suffix not in ('', '.md', '.sh', '.py'):
            continue
        lines = read(path).splitlines()
        for number, line in enumerate(lines):
            if 'net_bots.sh' not in line and 'net_test' not in line and 'scenario=capacity' not in line:
                continue
            end = number + 1
            while end < len(lines) and (lines[end - 1].rstrip().endswith('\\') or end < number + 4 and '--' in lines[end]):
                end += 1
            start = number
            while start > 0 and lines[start - 1].strip():
                start -= 1
            stop = end
            while stop < len(lines) and lines[stop].strip():
                stop += 1
            references.append({'path': path, 'line': number + 1, 'text': '\n'.join(lines[number:end]),
                               'context': '\n'.join(lines[start:stop])})
    return {'references': references,
            'options': sorted(set(re.findall(r'DevArgs.Get(?:Float)?\("([^"]+)"', source))),
            'assertions': invocations(source, ['ScenarioAssert']),
            'captures': invocations(source, ['Screenshot', 'ScreenshotAsync']),
            'logs': invocations(source, ['Log'])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision', help='read tracked files from this revision instead of the working tree')
    parser.add_argument('--compare', help='compare assertions/options/capture/log expressions with this revision')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    current = inventory(args.revision)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(current, indent=2) + '\n')
    for kind, records in current.items():
        print(f'{kind}: {len(records)}')
    if args.compare:
        baseline = inventory(args.compare)
        failed = False
        for kind in ('options', 'assertions', 'captures', 'logs'):
            before, after = Counter(baseline[kind]), Counter(current[kind])
            for label, changes in (('removed', before - after), ('added', after - before)):
                for expression, count in changes.items():
                    print(f'{kind} {label} ({count}): {expression}')
                    failed = True
        if failed:
            raise SystemExit(1)
        print('PASS unchanged network assertion, option, capture and log expression inventory')


if __name__ == '__main__':
    main()
