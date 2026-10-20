#!/usr/bin/env python3
"""Native resource gate plus temporary scaffold syntax proof. Retain logs, never fixture assets."""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from hat import scaffold

ROOT = Path(__file__).resolve().parents[2]


def run(name, arguments=(), marker='[assets] PASS native'):
    log = ROOT / ('build/' + name + '.log')
    result = subprocess.run([os.environ.get('GODOT', 'godot-mono'), '--headless', '--path', str(ROOT),
                             'res://scenes/dev/asset_validation.tscn', '--', *arguments], cwd=ROOT, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)
    log.write_text(result.stdout)
    print(result.stdout, end='')
    if result.returncode or re.search(r'(?m)^\s*(?:ERROR:|SCRIPT ERROR:|Unhandled exception)', result.stdout) or marker not in result.stdout:
        sys.exit('FAIL native asset validation (' + str(log.relative_to(ROOT)) + ')')


run('asset-validation')
with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='al-scaffold-native-') as directory:
    assets = Path(directory) / 'assets'
    shutil.copytree(ROOT / 'assets', assets)
    inventory = json.loads((ROOT / 'tools/assets/inventory.json').read_text())
    model = assets / 'generated/props/cooler.glb'
    scaffold(assets, 'dev_probe', 'Temporary existing prop', model, inventory)
    prefix = 'res://' + assets.relative_to(ROOT).as_posix() + '/'
    # Relocate copied resource addresses. Reuse the already-imported original prop: this tests the scaffold's
    # resource syntax and typed properties, without importing/caching another copy or creating shipped fixtures.
    library = assets / 'library.tres'
    library.write_text(library.read_text().replace('res://assets/authored/', prefix + 'authored/'))
    item = assets / 'authored/hats/dev_probe/item.tres'
    item.write_text(item.read_text().replace('res://assets/authored/hats/dev_probe/model.glb', 'res://assets/generated/props/cooler.glb'))
    run('asset-scaffold-native', ['--fixture-library=' + prefix + 'library.tres'], '[assets] PASS scaffold hats=1')
