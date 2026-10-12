#!/usr/bin/env python3
"""Require the shader pressure assertions and clean engine logs; --rendered also proves native allocation."""
import argparse
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--rendered', action='store_true')
parser.add_argument('--output', type=Path, default=ROOT / 'build' / 'shader-capacity-gate')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
command = [os.environ.get('GODOT', 'godot-mono')]
command += ['--disable-vsync', '--resolution', '1280x720'] if args.rendered else ['--headless']
command += ['--fixed-fps', '60', '--path', str(ROOT), 'res://scenes/dev/shader_capacity_tests.tscn',
            '--', f'--capture-dir={args.output.resolve()}']
log = args.output / 'engine.log'
with log.open('w') as output:
    result = subprocess.run(command, stdout=output, stderr=subprocess.STDOUT, timeout=120)
text = log.read_text()
if result.returncode != 0 or '[shader capacity] PASS' not in text or re.search(r'^ERROR:|\bFAIL\b', text, re.M):
    raise SystemExit(f'FAIL shader capacity ({result.returncode}); {log}\n{text[-2000:]}')
print('\n'.join(line for line in text.splitlines() if line.startswith('[shader capacity]')))
