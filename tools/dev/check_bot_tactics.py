#!/usr/bin/env python3
"""Compare controlled Banking/King decisions, reporting the first differing tick and field."""
import argparse
import itertools
import json
from pathlib import Path

from check_equivalence import ROOT, first_difference, new_output_directory, run


def compare(parent, candidate):
    with parent.open() as old, candidate.open() as new:
        count = 0
        for line, (left, right) in enumerate(itertools.zip_longest(old, new), 1):
            if left != right:
                expected = json.loads(left) if left else None
                actual = json.loads(right) if right else None
                context = expected or actual
                raise ValueError(f"first divergent decision: {context['name']} tick {context['tick']} "
                                 f"(line {line}): {first_difference(expected, actual)}")
            count += 1
    if count != 24 * 960:
        raise ValueError(f'incomplete transcript: {count} ticks, expected {24 * 960}')
    print(f'PASS {count} byte-identical intent/decision observations (24 cases)')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path, help='independently built parent with identical probe source')
    parser.add_argument('--output', type=Path, default=ROOT / 'build/bot-tactics')
    parser.add_argument('--compare', type=Path, nargs=2, metavar=('PARENT', 'CANDIDATE'))
    parser.add_argument('--bench', action='store_true', help='three alternating parent/candidate cost pairs, no observer')
    args = parser.parse_args()
    if args.compare:
        compare(*args.compare)
        return
    if not args.baseline:
        parser.error('--baseline is required unless --compare is used')
    output = new_output_directory(args.output.resolve())
    roots = [('parent', args.baseline.resolve()), ('candidate', ROOT)]
    scene = ['res://scenes/dev/unit_tests.tscn', '--', '--suite=BotTacticsTests']
    for label, root in roots:
        run(root, scene + [f'--tactics-transcript={output / (label + ".jsonl")}'], output / f'{label}.log')
    compare(output / 'parent.jsonl', output / 'candidate.jsonl')
    if args.bench:
        for pair in range(1, 4):
            for label, root in roots:
                run(root, scene + ['--tactics-bench'], output / f'cost-{pair}-{label}.log')
        print('Three alternating cost pairs in', output)


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, ValueError) as error:
        raise SystemExit(f'FAIL tactics: {error}') from None
