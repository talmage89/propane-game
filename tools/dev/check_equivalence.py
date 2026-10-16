#!/usr/bin/env python3
"""Frozen v0.4.0 behavior, with explicit refresh and two-checkout field diagnosis."""
import argparse
import gzip
import hashlib
import itertools
import json
import os
from pathlib import Path
import re
import subprocess
import time

from check_lab import run_scene

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = Path('src/Dev/Equivalence/Fixtures')
CASES = {
    'bank': ['--scenario=soak', '--environment=suburb', '--bots=8', '--seconds=40', '--max-idle=0.12', '--max-idle-worst=0.3'],
    'king': ['--scenario=prototype', '--mode=king', '--environment=suburb', '--bots=15', '--quick', '--seeds=1',
             '--first-seed=11', '--tune.KingRoundSeconds=45', '--max-idle=0.15', '--max-idle-worst=0.4'],
    'spill-chain': ['--scenario=rules'],
    'shortfuse': ['--scenario=shortfuse-combo'],
    'dissolve': ['--scenario=reference-retirement'],
}
REPLAY = ['--replay-random', '--tune.HitstopDuration=0']


def first_difference(expected, actual, path='$'):
    if type(expected) is not type(actual):
        return f'{path}: expected {expected!r}, actual {actual!r}'
    if isinstance(expected, dict):
        if list(expected) != list(actual):
            return f'{path}: field inventory/order {list(expected)} -> {list(actual)}'
        for key in expected:
            difference = first_difference(expected[key], actual[key], f'{path}.{key}')
            if difference: return difference
    elif isinstance(expected, list):
        for index, (left, right) in enumerate(zip(expected, actual)):
            difference = first_difference(left, right, f'{path}[{index}]')
            if difference: return difference
        if len(expected) != len(actual):
            return f'{path}.length: expected {len(expected)}, actual {len(actual)}'
    elif expected != actual:
        return f'{path}: expected {expected!r}, actual {actual!r}'
    return None


def read_json(path):
    opener = gzip.open if path.suffix == '.gz' else open
    with opener(path, 'rt') as source:
        return json.load(source)


def new_output_directory(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    for attempt in itertools.count(1):
        candidate = path if attempt == 1 else path.with_name(f'{path.name}-{attempt}')
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            continue


def run(root, arguments, log):
    log.parent.mkdir(parents=True, exist_ok=True)
    command = [os.environ.get('GODOT', 'godot-mono'), '--headless', '--fixed-fps', '60', '--path', str(root), *arguments]
    start = time.monotonic()
    code = run_scene(command, log, 900)
    text = log.read_text()
    if code or re.search(r'^ERROR:|^WARNING:|\bFAIL\b', text, re.M):
        raise RuntimeError(f'{log}: engine failed ({code})\n{text[-6000:]}')
    if '[lab] PASS' not in text and not re.search(r'\[unit\] \d+ tests, 0 failures', text):
        raise RuntimeError(f'{log}: missing completion marker')
    return command, time.monotonic() - start


def trace(root, name, directory, detail=False):
    output = directory / f'{name}.json'
    arguments = ['res://scenes/dev/lab_test.tscn', '--', *CASES[name], *REPLAY, f'--equivalence-trace={output}']
    if detail: arguments.append(f'--equivalence-detail={directory / (name + ".detail.jsonl.gz")}')
    command, elapsed = run(root, arguments, directory / f'{name}.log')
    data = read_json(output)
    if data['ticks'] < (8 if name == 'dissolve' else 100) or not data['windows'] or data['summary']['tanks'] < 1:
        raise RuntimeError(f'{name}: incomplete trace')
    return data, command, elapsed


def diagnose(left, right):
    with gzip.open(left, 'rt') as a, gzip.open(right, 'rt') as b:
        for line, (old, new) in enumerate(itertools.zip_longest(a, b), 1):
            if old != new:
                expected = json.loads(old) if old else None
                actual = json.loads(new) if new else None
                difference = first_difference(expected, actual)
                observation = expected if expected is not None else actual
                context = f", tick {observation['tick']}, {observation['field']}" if isinstance(observation, dict) else ''
                print(f'First divergent observation (line {line}{context}): {difference}')
                return False
    print('All detailed observations agree.')
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--refresh', action='store_true', help='deliberately replace references; never used by checks')
    parser.add_argument('--production-revision', help='required with --refresh; reviewed production revision being frozen')
    parser.add_argument('--only', help='comma-separated Lab cases (default all)')
    parser.add_argument('--repeat', type=int, default=1)
    parser.add_argument('--baseline', type=Path, help='instrumented checkout to run for first-field diagnosis')
    parser.add_argument('--detail', action='store_true', help='retain exact per-tick fields for diagnosis')
    parser.add_argument('--compare-details', nargs=2, type=Path, metavar=('EXPECTED', 'ACTUAL'))
    parser.add_argument('--output', type=Path, default=ROOT / 'build/rfp0-equivalence')
    parser.add_argument('--skip-unit', action='store_true', help='iterate on Lab observations only')
    args = parser.parse_args()
    if args.compare_details:
        raise SystemExit(0 if diagnose(*args.compare_details) else 1)
    if args.refresh and (not args.production_revision or args.baseline or args.repeat != 1 or args.only or args.skip_unit):
        parser.error('--refresh requires --production-revision and a complete, single invocation')
    if args.repeat < 1: parser.error('--repeat must be positive')
    names = args.only.split(',') if args.only else list(CASES)
    if set(names) - CASES.keys() or len(set(names)) != len(names): parser.error('unknown or duplicate case')
    destination = ROOT / FIXTURES
    commands = []
    output = new_output_directory(args.output.resolve())
    print(f'Evidence directory: {output}', flush=True)
    if not args.skip_unit:
        arguments = ['res://scenes/dev/unit_tests.tscn', '--', '--suite=ReferenceTests']
        if args.refresh: arguments.append(f'--write-reference={destination}')
        command, elapsed = run(ROOT, arguments, output / 'unit.log')
        commands.append(command)
        print(f'Release wire/schema/world/publication: PASS ({elapsed:.2f}s)', flush=True)
    failed = False
    for iteration in range(args.repeat):
        for name in names:
            directory = output / f'run-{iteration + 1}'
            actual, command, elapsed = trace(ROOT, name, directory, args.detail or args.baseline is not None)
            commands.append(command)
            reference = destination / f'lab-{name}.json.gz'
            if args.refresh:
                with gzip.GzipFile(filename=str(reference), mode='wb', mtime=0) as target:
                    target.write(json.dumps(actual, separators=(',', ':')).encode())
                difference = None
            else:
                expected = read_json(reference)
                difference = first_difference(expected, actual, name)
            print(f'Lab {name} run {iteration + 1}: {"FAIL " + difference if difference else "PASS"} ({elapsed:.2f}s)', flush=True)
            failed |= difference is not None
            if args.baseline:
                baseline, _, _ = trace(args.baseline.resolve(), name, directory / 'baseline', True)
                paired = first_difference(baseline, actual, name)
                print(f'Baseline/candidate: {paired or "agree"}')
                if paired:
                    diagnose(directory / 'baseline' / f'{name}.detail.jsonl.gz', directory / f'{name}.detail.jsonl.gz')
                    failed = True
    if args.refresh:
        instrumentation_files = sorted([*ROOT.glob('src/Dev/Equivalence/*.cs'), *ROOT.glob('src/Dev/LabTest*.cs'),
                                       *ROOT.glob('src/Lab/*.cs'), ROOT / 'src/Dev/UnitTests/WorldBuildTests.cs', Path(__file__)])
        instrumentation_hash = hashlib.sha256()
        for path in instrumentation_files:
            instrumentation_hash.update(str(path.relative_to(ROOT)).encode() + b'\0' + path.read_bytes())
        production_revision = subprocess.check_output(['git', 'rev-parse', args.production_revision + '^{commit}'], cwd=ROOT, text=True).strip()
        metadata = {
            'format': 1, 'production_revision': production_revision,
            'instrumentation_revision': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
            'instrumentation_sources_sha256': instrumentation_hash.hexdigest(),
            'engine': subprocess.check_output([os.environ.get('GODOT', 'godot-mono'), '--version'], text=True).strip(),
            'dotnet': subprocess.check_output(['dotnet', '--version'], text=True).strip(),
            'configuration': 'Debug, Linux x86_64, headless, physics 120 Hz, process fixed 60 FPS',
            'tuners': 'Baked release defaults; full shared/local schema in tuners.json.gz; overrides below',
            'world': 'seed 23, four forced formations, four actors; segment runs 1..5, indices 11..0',
            'lab': CASES, 'replay': REPLAY, 'commands': commands,
        }
        # Public provenance must not contain machine paths.
        metadata['commands'] = [[part.replace(str(ROOT), '$CHECKOUT') for part in command] for command in commands]
        (destination / 'provenance.json').write_text(json.dumps(metadata, indent=2) + '\n')
        files = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(destination.iterdir())
                 if path.suffix == '.gz' or path.name == 'provenance.json'}
        (destination / 'manifest.json').write_text(json.dumps(files, indent=2) + '\n')
    raise SystemExit(1 if failed else 0)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as error:
        raise SystemExit(f'FAIL equivalence: {error}') from None
