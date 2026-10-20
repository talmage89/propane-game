#!/usr/bin/env python3
"""Run native asset validation, rejecting Godot diagnostics even when the engine exits zero."""
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
log = ROOT / 'build/asset-validation.log'
result = subprocess.run([os.environ.get('GODOT', 'godot-mono'), '--headless', '--path', str(ROOT),
                         'res://scenes/dev/asset_validation.tscn'], cwd=ROOT, text=True,
                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)
log.write_text(result.stdout)
print(result.stdout, end='')
if result.returncode or re.search(r'(?m)^\s*(?:ERROR:|SCRIPT ERROR:|Unhandled exception)', result.stdout) or '[assets] PASS native' not in result.stdout:
    sys.exit('FAIL native asset validation (build/asset-validation.log)')
