#!/usr/bin/env python3
"""Immediate-parent B4b differentials beyond the immutable release fixture inventory."""
import argparse
from pathlib import Path

from check_equivalence import ROOT, REPLAY, diagnose, first_difference, new_output_directory, read_json, run

CASES = {
    'ringout': ['--scenario=prototype', '--mode=ringout', '--quick', '--seeds=1', '--first-seed=23'],
    'bank-mixed': ['--scenario=soak', '--environment=suburb', '--bots=4', '--seconds=40',
                   '--tune.LabHardBots=2', '--tune.LabHardOffset=1'],
    'king-mixed': ['--scenario=prototype', '--mode=king', '--quick', '--seeds=1', '--first-seed=23', '--bots=4',
                   '--tune.LabHardBots=2', '--tune.LabHardOffset=1'],
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=ROOT / 'build/b4b-mechanics')
    args = parser.parse_args()
    output = new_output_directory(args.output.resolve())
    baseline = args.baseline.resolve()
    print(f'Evidence directory: {output}', flush=True)
    for suite, prefix, expected in [('LabCreditTests', ('[lab credit]', '[lab spill]'), 4),
                                     ('LabMechanicsTests', ('[lab mechanics]',), 7)]:
        observations = []
        for label, root in [('parent', baseline), ('candidate', ROOT)]:
            log = output / f'{suite}-{label}.log'
            run(root, ['res://scenes/dev/unit_tests.tscn', '--', f'--suite={suite}'], log)
            selected = [line for line in log.read_text().splitlines() if line.startswith(prefix)]
            if len(selected) != expected:
                raise RuntimeError(f'{suite}/{label}: expected {expected} observations, found {len(selected)}')
            observations.append(selected)
        difference = first_difference(*observations, suite)
        if difference:
            raise RuntimeError(difference)
        print(f'{suite}: {expected} exact observations agree; all assertions passed', flush=True)
    for name, options in CASES.items():
        summaries, details = [], []
        for label, root in [('parent', baseline), ('candidate', ROOT)]:
            summary = output / f'{name}-{label}.json'
            detail = output / f'{name}-{label}.jsonl.gz'
            run(root, ['res://scenes/dev/lab_test.tscn', '--', *options, *REPLAY,
                       f'--equivalence-trace={summary}', f'--equivalence-detail={detail}'], output / f'{name}-{label}.log')
            data = read_json(summary)
            if data['ticks'] < 100 or not data['windows'] or data['summary']['tanks'] < 1:
                raise RuntimeError(f'{name}/{label}: incomplete trace')
            summaries.append(data)
            details.append(detail)
        difference = first_difference(*summaries, name)
        equal = diagnose(*details)
        if difference or not equal:
            raise RuntimeError(difference or f'{name}: detailed trace differs')
        print(f'{name}: summary and every detailed observation agree', flush=True)
    print('PASS Lab mechanics parent/candidate differentials', flush=True)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, RuntimeError) as error:
        raise SystemExit(f'FAIL Lab mechanics: {error}') from None
