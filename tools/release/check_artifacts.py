#!/usr/bin/env python3
"""Inventory the three packaged exports and execute the Linux archive, without source-tree fallback.

Default smoke is headless (also runs on stock ubuntu-24.04). --rendered uses the local Godot wrapper
with PROPANE_REAL_GODOT pointed at the exported executable, so invisible local GPU runs use the same script.
Never publishes. Output must be new so failed evidence cannot be overwritten accidentally.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / 'build/architecture/bin/Debug/net10.0/Architecture.dll'


def run(command, log, cwd, env, marker=None, timeout=90):
    with log.open('w') as stream:
        with subprocess.Popen(command, cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT, start_new_session=True) as process:
            try:
                process.wait(timeout=timeout)
            except BaseException as error:
                # Give the invisible wrapper time to stop/reap its compositor and exported child too.
                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    try:
                        os.killpg(process.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                    process.wait()
                if isinstance(error, subprocess.TimeoutExpired):
                    raise RuntimeError(f'Artifact step timed out ({log})') from None
                raise
            status = process.returncode
    output = log.read_text(errors='replace')
    if status or re.search(r'(?m)^\s*(?:ERROR:|SCRIPT ERROR:|Unhandled exception)', output) or (marker and marker not in output):
        raise RuntimeError(f'Artifact step failed (exit {status}; {log}):\n{output[-8000:]}')


def unpack(archive, destination):
    destination.mkdir()
    if archive.name.endswith('.tar.gz'):
        with tarfile.open(archive) as source:
            source.extractall(destination, filter='data')
    else:
        with zipfile.ZipFile(archive) as source:
            source.extractall(destination)
    assemblies = list(destination.rglob('Propane.dll'))
    packs = list(destination.rglob('*.pck'))
    if len(assemblies) != 1 or len(packs) != 1:
        raise ValueError(f'{archive}: expected one game assembly and pack, found {len(assemblies)}/{len(packs)}')
    return assemblies[0], packs[0]


def native_entry(platform, pack):
    entry = {'linux': pack.parent / 'Propane.x86_64', 'windows': pack.parent / 'Propane.exe',
             'macos': pack.parent.parent / 'MacOS/Propane'}[platform]
    if not entry.is_file() or entry.stat().st_size == 0:
        raise ValueError(f'Missing or empty {platform} executable: {entry}')
    return entry


def main():
    signal.signal(signal.SIGTERM, interrupted)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', type=Path, default=ROOT / 'build/dist')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--rendered', action='store_true')
    parser.add_argument('--inventory-only', action='store_true')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True)  # Refuse to replace earlier logs or archive copies.
    env = {**os.environ, 'TMPDIR': str(output)}
    run(['dotnet', 'build', str(ROOT / 'tools/architecture/Architecture.csproj'), '-nologo', '-v', 'quiet'],
        output / 'checker-build.log', ROOT, env)
    run(['python3', '-B', str(ROOT / 'tools/assets/check.py'), '--report', str(output / 'asset-inventory.json')],
        output / 'asset-source.log', ROOT, env)
    source_assets = json.loads((output / 'asset-inventory.json').read_text())
    expected_hats = ','.join(item['id'] for item in source_assets['items'])
    policy = output / 'policy'
    policy.mkdir()
    shutil.copyfile(ROOT / 'tools/architecture/modules.json', policy / 'modules.json')
    artifact_policy = json.loads((ROOT / 'tools/architecture/artifact.json').read_text())
    artifact_policy['AssetRoots'] += [path for item in source_assets['items'] for path in ['assets/' + item['item'], *('assets/' + p for p in item['dependencies'])]]
    (policy / 'artifact.json').write_text(json.dumps(artifact_policy, indent=2) + '\n')
    linux = None
    for platform, suffix in [('linux', 'linux-x86_64.tar.gz'), ('windows', 'windows-x86_64.zip'), ('macos', 'macos-arm64.zip')]:
        archives = list(args.dist.glob('Propane-*-' + suffix))
        if len(archives) != 1:
            raise ValueError(f'Expected one {platform} archive, found {len(archives)}')
        assembly, pack = unpack(archives[0], output / platform)
        entry = native_entry(platform, pack)
        run(['dotnet', str(TOOL), 'inventory', str(assembly), str(pack), str(policy)],
            output / f'{platform}-inventory.log', output, env)
        if platform == 'linux':
            linux = entry
    if args.inventory_only:
        print(f'PASS three artifact inventories ({output})')
        return
    assert linux is not None
    # No --path and no editor: execute exactly what was unpacked. Working directory has no project.godot.
    if args.rendered:
        wrapper = shutil.which('godot-mono')
        if not wrapper:
            raise ValueError('--rendered needs godot-mono on PATH')
        with open(wrapper, 'rb') as script:
            if b'PROPANE_REAL_GODOT' not in script.read(16384):
                raise ValueError('--rendered needs the invisible wrapper that honors PROPANE_REAL_GODOT; refusing an editor fallback')
        command = [wrapper, '--resolution', '960x540', '--disable-vsync']
        env['PROPANE_REAL_GODOT'] = str(linux)
    else:
        command = [str(linux), '--headless']
    for name, extra in [('ordinary', []), ('classic', ['--classic-lobby'])]:
        run([*command, '--quit-after', '120', '--', '--windowed', *extra], output / f'{name}.log', output, env)
    run([str(linux), '--headless', '--quit-after', '120', '--', '--server', '--port=0'],
        output / 'server.log', output, env, marker='listening on UDP')
    capture = ['--capture-dir=' + str(output / 'screenshots')] if args.rendered else []
    run([*command, '--', '--windowed', '--release-test', '--expected-hats=' + expected_hats, *capture], output / 'release-test.log', output, env,
        marker='[releasetest] PASS main menu and single player')
    smoke_output = (output / 'release-test.log').read_text()
    if '[assets export] PASS ids=' + expected_hats + ' roots=' not in smoke_output:
        raise RuntimeError('Missing packaged catalog/default dependency proof')
    print(f'PASS three inventories; Linux ordinary, classic, server and release-test ({output})')


def interrupted(signum, _frame):
    raise SystemExit(128 + signum)


if __name__ == '__main__':
    main()
