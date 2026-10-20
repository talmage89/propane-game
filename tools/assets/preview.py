#!/usr/bin/env python3
"""Invisible owner preview. Arguments are data, not shell text."""
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
directory = Path(os.environ.get('AL_PREVIEW_DIR') or ROOT / 'build/assets-preview').resolve()
directory.mkdir(parents=True, exist_ok=True)
command = [os.environ.get('GODOT', 'godot-mono'), '--fixed-fps', '60', '--resolution', '1280x720',
           '--path', str(ROOT), 'res://scenes/dev/asset_preview.tscn', '--', '--capture-dir=' + str(directory)]
if os.environ.get('AL_PREVIEW_ASSET'): command.append('--asset=' + os.environ['AL_PREVIEW_ASSET'])
with (directory / 'preview.log').open('w') as log:
    status = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT).returncode
output = (directory / 'preview.log').read_text()
if status or re.search(r'(?m)^\s*(?:ERROR:|SCRIPT ERROR:|Unhandled exception)', output) or '[assets preview] PASS' not in output:
    print(output)
    sys.exit('FAIL preview; see ' + str(directory / 'preview.log'))
print('PASS preview: ' + str(directory))
