#!/usr/bin/env python3
"""Print the Lab CLI, assertion, log and capture inventory for differential review."""
import argparse
from collections import Counter
import json
import subprocess

from net_scenario_inventory import invocations
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
# Independent of the dispatch table, just like check_lab's session/shard coverage.
CASES = ('lifetime', 'frames', 'rules', 'duel', 'soak', 'effects', 'measure', 'measure-play',
         'stackfire', 'aimstall', 'dials', 'prototype', 'runner-sampling', 'bot-cost', 'shortfuse', 'shortfuse-combo',
         'tracking', 'reference-retirement', 'dissolve')


def inventory(revision=None):
    paths = subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', revision] if revision else
                                    ['git', 'ls-files', '--cached', '--others', '--exclude-standard'],
                                    text=True).splitlines()
    paths = sorted(set(path for path in paths if revision or (ROOT / path).is_file()))
    read = (lambda path: subprocess.check_output(['git', 'show', f'{revision}:{path}'], text=True)) if revision else \
        (lambda path: (ROOT / path).read_text())
    harness = sorted(path for path in paths if path.endswith('.cs') and
                     (path.startswith('src/Dev/LabTest') or path.startswith('src/Dev/LabScenarios/')))
    source = '\n'.join(read(path) for path in harness)
    records = {name: invocations(source, calls) for name, calls in
               (('assertions', ['Assert']), ('logs', ['Print', 'PushError']), ('captures', ['Screenshot']))}
    records['cases'] = CASES
    records['options'] = sorted(set(re.findall(r'DevArgs\.(?:Get|GetFloat)\("([^"]+)"', source)))
    # Include historical runbooks as well as executable callers; retain entire lines so value combinations survive.
    callers = [path for path in paths if (path in ('Makefile', 'README.md') or path.startswith(('tools/', 'docs/')))
               and Path(path).suffix in ('', '.py', '.sh', '.md') and path != 'tools/dev/lab_scenario_inventory.py']
    references = []
    for path in sorted(callers):
        lines = read(path).splitlines()
        for number, line in enumerate(lines):
            if not re.search(r'lab_test|--(?:lifetime|dial)-shards?=|--list-(?:lifetime|dials)', line):
                continue
            start, end = number, number + 1
            while start > 0 and lines[start - 1].strip():
                start -= 1
            while end < len(lines) and lines[end].strip():
                end += 1
            references.append({'path': path, 'line': number + 1, 'text': line,
                               'context': '\n'.join(lines[start:end])})
    records['callers'] = references
    return records


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision', help='read tracked files at this revision')
    parser.add_argument('--compare', help='require unchanged case/options/assertion/log/capture expressions')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    current = inventory(args.revision)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(current, indent=2) + '\n')
    for kind, records in current.items():
        print(f'{kind}: {len(records)}')
    if args.compare:
        parent = inventory(args.compare)
        changed = False
        for kind in ('cases', 'options', 'assertions', 'logs', 'captures'):
            before, after = Counter(parent[kind]), Counter(current[kind])
            for label, changes in (('removed', before - after), ('added', after - before)):
                for expression, count in changes.items():
                    print(f'{kind} {label} ({count}): {expression}')
                    changed = True
        if changed:
            raise SystemExit(1)
        print('PASS unchanged Lab case, option, assertion, log and capture inventory')
