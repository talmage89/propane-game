#!/usr/bin/env python3
"""Validate the source asset inventory and hat catalog; budgets warn, correctness fails."""
import argparse
import json
from pathlib import Path
from library import ROOT, validate


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT / 'assets')
    parser.add_argument('--inventory', type=Path, default=ROOT / 'tools/assets/inventory.json')
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    result = validate(args.root.resolve(), json.loads(args.inventory.read_text()), json.loads((ROOT / 'tools/assets/budgets.json').read_text()))
    for level in ['warnings', 'errors']:
        for entry in result[level]: print(f'{level[:-1].upper()}: {entry}')
    for item in result['items']: print(json.dumps(item, sort_keys=True))
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, indent=2) + '\n')
    print(f'{"FAIL" if result["errors"] else "PASS"}: asset inventory; {len(result["items"])} hats, {len(result["errors"])} errors, {len(result["warnings"])} budget warnings')
    return bool(result['errors'])


if __name__ == '__main__':
    raise SystemExit(main())
