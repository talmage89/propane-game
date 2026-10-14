#!/usr/bin/env python3
"""Prove the full Lab sessions and every dial fixture occur exactly once across the Makefile shards."""
import argparse
import collections
import os
import re
import shlex
import subprocess

import check_lab


def fixture_cases(scenario, options):
    case_kind = "dial" if scenario == "dials" else "lifetime"
    command = [os.environ.get('GODOT', 'godot-mono'), '--headless', '--path', str(check_lab.ROOT),
               'res://scenes/dev/lab_test.tscn', '--', f'--scenario={scenario}', *options, f'--list-{scenario}']
    result = subprocess.run(command, capture_output=True, text=True, timeout=120)
    text = result.stdout + result.stderr
    cases = [line.removeprefix(f'[lab {case_kind}-case] ') for line in text.splitlines() if line.startswith(f'[lab {case_kind}-case] ')]
    if result.returncode != 0 or f'[lab {scenario}] listed ' not in text or '[lab] PASS' not in text or not cases or re.search(r'^ERROR:|^WARNING:|\bFAIL\b', text, re.M):
        raise ValueError(f'{scenario} listing failed ({result.returncode}): {text[-4000:]}')
    return cases


def cases(arguments):
    parser = check_lab.argument_parser()
    args = parser.parse_args(shlex.split(arguments))
    if args.lifetime_shards < 1 or not 1 <= args.lifetime_shard <= args.lifetime_shards:
        raise ValueError('invalid lifetime shard')
    if args.dial_shards < 1 or not 1 <= args.dial_shard <= args.dial_shards:
        raise ValueError('invalid dial shard')
    if not args.full:
        raise ValueError('coverage shards must use --full')
    selected = args.only.split(',') if args.only else [name for name, _, _ in check_lab.sessions(True)]
    if len(set(selected)) != len(selected):
        raise ValueError('duplicate session selection')
    sessions = {name: (scenario, options) for name, scenario, options in check_lab.sessions(True)}
    result = []
    for name in selected:
        if name not in sessions:
            raise ValueError(f'unknown session {name}')
        scenario, options = sessions[name]
        options = check_lab.shard_options(name, options, args, parser)
        if name in ('dials', 'lifetime'):
            fixed = tuple(option for option in options if not option.startswith(('--dial-shard', '--lifetime-shard')))
            result.extend((name, fixed, case) for case in fixture_cases(name, options))
        else:
            # A seeded session consists of the same scenario and non-seed options once for each seed.
            settings = dict(option[2:].split('=', 1) for option in options if '=' in option)
            fixed = tuple(option for option in options if not option.startswith(('--seeds=', '--first-seed=')))
            first = int(settings.get('first-seed', 1))
            for seed in range(first, first + int(settings.get('seeds', 1))):
                result.append((name, scenario, fixed, seed))
    return result


def check(full, shards):
    expected = collections.Counter(full)
    actual = collections.Counter(case for shard in shards for case in shard)
    if not expected or any(count != 1 for count in expected.values()) or any(not shard for shard in shards):
        raise ValueError('empty or duplicate full coverage, or empty shard')
    if expected != actual:
        raise ValueError(f'Lab shards differ: missing {list((expected - actual).items())[:5]}, '
                         f'extra/duplicate {list((actual - expected).items())[:5]}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--shard', required=True, action='append', help='check_lab.py arguments; repeat per Makefile shard')
    args = parser.parse_args()
    try:
        full = cases('--full')
        shards = [cases(shard) for shard in args.shard]
        check(full, shards)
    except ValueError as error:
        raise SystemExit(f'FAIL: {error}') from None
    print(f'Lab shards cover exactly {len(full)} cases, including {sum(case[0] == "dials" for case in full)} '
          f'dial fixtures, once each in {len(shards)} shards: ' + ', '.join(str(len(shard)) for shard in shards))


if __name__ == '__main__':
    main()
