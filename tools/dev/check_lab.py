#!/usr/bin/env python3
"""Require assertions, successful exit and clean engine logs from the real offline Lab."""
import argparse
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--full', action='store_true', help='ten lifecycle cycles and both three-minute bot soaks')
args = parser.parse_args()
scenarios = [('lifetime', [f'--cycles={10 if args.full else 1}']), ('rules', []), ('aimstall', []), ('duel', ['--seconds=15'])]
if args.full:
    scenarios += [('soak', ['--environment=void']), ('soak', ['--environment=suburb'])]
logs = ROOT / 'build' / ('lab-check-full' if args.full else 'lab-check')
logs.mkdir(parents=True, exist_ok=True)
for number, (scenario, options) in enumerate(scenarios):
    command = [os.environ.get('GODOT', 'godot-mono'), '--headless', '--fixed-fps', '60', '--path', str(ROOT),
               'res://scenes/dev/lab_test.tscn', '--', f'--scenario={scenario}', *options]
    log = logs / f'{number}-{scenario}.log'
    with log.open('w') as output:
        result = subprocess.run(command, stdout=output, stderr=subprocess.STDOUT, timeout=600)
    text = log.read_text()
    if result.returncode != 0 or '[lab] PASS' not in text or re.search(r'^ERROR:|^WARNING:|\bFAIL\b', text, re.M):
        raise SystemExit(f'FAIL Lab {scenario} ({result.returncode}); {log}\n{text[-12000:]}')
    print('\n'.join(line for line in text.splitlines() if line.startswith('[lab')), flush=True)
print('Lab sessions passed with clean engine logs.')
