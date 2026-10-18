#!/usr/bin/env python3
"""Compare full prototype HUD values against the independent parent presenters and an immediate-parent build."""
import argparse
from pathlib import Path

from check_equivalence import ROOT, first_difference, new_output_directory, run


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=ROOT / 'build/prototype-views')
    args = parser.parse_args()
    output = new_output_directory(args.output.resolve())
    values = []
    for label, root in [('parent', args.baseline.resolve()), ('candidate', ROOT)]:
        log = output / f'{label}.log'
        run(root, ['res://scenes/dev/unit_tests.tscn', '--', '--suite=PrototypeViewTests'], log)
        observations = [line for line in log.read_text().splitlines() if line.startswith('[prototype view]')]
        if len(observations) != 92:
            raise RuntimeError(f'{label}: expected 92 full HUD observations, got {len(observations)}')
        values.append(observations)
    difference = first_difference(*values)
    if difference:
        raise RuntimeError(difference)
    print('PASS 92 byte-identical full HUD observations; independent presenter assertions passed on both builds')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, RuntimeError) as error:
        raise SystemExit(f'FAIL prototype views: {error}') from None
