#!/usr/bin/env python3
"""Build and run deep probes as optimized Debug, with separate assemblies and the same imported resources."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / 'build' / 'test-full-engine' / 'project'


def prepare(godot):
    PROJECT.mkdir(parents=True, exist_ok=True)
    # This is a runtime view, not another checkout/import. C# still compiles the real sources from ROOT.
    # Keep all writable engine state private; assets and their already-imported binary resources are read-only.
    for name in ('assets', 'scenes', 'src', 'shaders', 'icon.svg', 'icon.svg.import', 'tuning.tres'):
        target = PROJECT / name
        if not target.is_symlink():
            target.symlink_to(ROOT / name, target_is_directory=(ROOT / name).is_dir())
    for name in ('project.godot', 'override.cfg'):
        shutil.copyfile(ROOT / name, PROJECT / name)
    cache = PROJECT / '.godot'
    cache.mkdir(exist_ok=True)
    imported = cache / 'imported'
    if not imported.is_symlink():
        imported.symlink_to(ROOT / '.godot' / 'imported', target_is_directory=True)
    for name in ('uid_cache.bin', 'global_script_class_cache.cfg'):
        shutil.copyfile(ROOT / '.godot' / name, cache / name)
    subprocess.run(['dotnet', 'build', 'Propane.csproj', '-c', 'Debug', '-nologo', '-v', 'quiet', '-warnaserror',
                    '-p:Optimize=true', f'-p:BaseIntermediateOutputPath={cache / "mono/temp/obj"}/',
                    f'-p:IntermediateOutputPath={cache / "mono/temp/obj/Debug"}/',
                    f'-p:OutputPath={cache / "mono/temp/bin/Debug"}/'], cwd=ROOT, check=True)
    (PROJECT.parent / 'engine.json').write_text(json.dumps({'godot': godot}) + '\n')


def run(arguments):
    engine = json.loads((PROJECT.parent / 'engine.json').read_text())['godot']
    arguments = list(arguments)
    if '--path' not in arguments:
        raise ValueError('deep probes must supply --path')
    arguments[arguments.index('--path') + 1] = str(PROJECT)
    if '--' not in arguments:
        arguments.append('--')
    arguments.append('--full-engine-optimized')
    os.execvp(engine, [engine, *arguments])


def main():
    if sys.argv[1:2] == ['--prepare']:
        parser = argparse.ArgumentParser(description=__doc__)
        parser.add_argument('--prepare', action='store_true')
        parser.add_argument('--godot', required=True)
        args = parser.parse_args()
        prepare(args.godot)
    else:
        run(sys.argv[1:])


if __name__ == '__main__':
    main()
