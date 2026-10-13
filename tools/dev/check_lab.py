#!/usr/bin/env python3
"""Require assertions, successful exit and clean engine logs from the real offline Lab."""
import argparse
import os
from pathlib import Path
import re
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--full', action='store_true', help='ten lifecycle cycles, every dial at both slider ends, both three-minute bot soaks '
                    'and full prototype rounds across seeds')
parser.add_argument('--only', help='comma-separated sessions to run, so make -j can split the list: '
                    'lifetime, rules, aimstall, duel, ringout, king, and with --full dials, soak-void, soak-suburb, '
                    'ringout-full, king-full, king-suburb')
args = parser.parse_args()
# Each session is named, and its log keeps its number in the whole list, so split runs never share a log.
scenarios = [('lifetime', 'lifetime', [f'--cycles={10 if args.full else 1}']), ('rules', 'rules', []),
             ('aimstall', 'aimstall', []), ('duel', 'duel', ['--seconds=15']),
             # Prototype rounds play to their end with a bot on every actor: shortened dials here, the real ones in --full.
             ('ringout', 'prototype', ['--mode=ringout', '--quick', '--seeds=2']),
             ('king', 'prototype', ['--mode=king', '--quick', '--seeds=1'])]
if args.full:
    scenarios += [('dials', 'dials', []), ('soak-void', 'soak', ['--environment=void']),
                  ('soak-suburb', 'soak', ['--environment=suburb']),
                  ('ringout-full', 'prototype', ['--mode=ringout', '--seeds=3', '--first-seed=11']),
                  ('king-full', 'prototype', ['--mode=king', '--seeds=3', '--first-seed=11']),
                  ('king-suburb', 'prototype', ['--mode=king', '--environment=suburb', '--seeds=1', '--first-seed=11'])]
selected = [name for name, _, _ in scenarios]
if args.only:
    selected = args.only.split(',')
    unknown = sorted(set(selected) - {name for name, _, _ in scenarios})
    if unknown:
        parser.error(f'unknown sessions: {", ".join(unknown)}')
logs = ROOT / 'build' / ('lab-check-full' if args.full else 'lab-check')
logs.mkdir(parents=True, exist_ok=True)
for number, (name, scenario, options) in enumerate(scenarios):
    if name not in selected:
        continue
    start = time.monotonic()
    command = [os.environ.get('GODOT', 'godot-mono'), '--headless', '--fixed-fps', '60', '--path', str(ROOT),
               'res://scenes/dev/lab_test.tscn', '--', f'--scenario={scenario}', *options]
    log = logs / f'{number}-{name}.log'
    with log.open('w') as output:
        result = subprocess.run(command, stdout=output, stderr=subprocess.STDOUT, timeout=600)
    text = log.read_text()
    if result.returncode != 0 or '[lab] PASS' not in text or re.search(r'^ERROR:|^WARNING:|\bFAIL\b', text, re.M):
        raise SystemExit(f'FAIL Lab {name} ({result.returncode}); {log}\n{text[-12000:]}')
    print('\n'.join(line for line in text.splitlines() if line.startswith('[lab')), flush=True)
    print(f'Lab {name} passed in {time.monotonic() - start:.1f} s.', flush=True)
print(f'Lab sessions passed with clean engine logs: {", ".join(selected)}.')
