#!/usr/bin/env python3
"""Prove that generator shards cover exactly the seeds of the whole sweep, from the sweep's own --list-seeds listing.

Usage: check_generator_shards.py --full='--seeds=200 ...' --shard='--first-seed=1 ...' [--shard=...]
Every seed of every sweep (single-player, each match size, each forced formation and size) must be in exactly one shard.
"""
import argparse
import collections
import os
from pathlib import Path
import shlex
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def listing(arguments):
    command = [os.environ.get('GODOT', 'godot-mono'), '--headless', '--path', str(ROOT),
               'res://scenes/dev/generator_tests.tscn', '--', *shlex.split(arguments), '--list-seeds']
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=300)
    lines = [line.split()[1:] for line in result.stdout.splitlines() if line.startswith('[seeds] ')]
    if result.returncode != 0 or lines[-1:] != [['listed']]:
        raise SystemExit(f'FAIL: generator seed listing for {arguments!r} exited {result.returncode}\n{result.stdout[-4000:]}')
    seeds = [(sweep, int(seed)) for sweep, seed in lines[:-1]]
    repeated = [seed for seed, count in collections.Counter(seeds).items() if count > 1]
    if repeated:
        raise SystemExit(f'FAIL: {arguments!r} lists seeds twice: {repeated[:5]}')
    return set(seeds)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--full', required=True, help='arguments of the whole sweep')
    parser.add_argument('--shard', action='append', required=True, help='arguments of one shard; repeat for each')
    args = parser.parse_args()
    full = listing(args.full)
    shards = [listing(shard) for shard in args.shard]
    failures = []
    for number, seeds in enumerate(shards, 1):
        if not seeds:
            failures.append(f'shard {number} lists no seeds')
        for other in range(number, len(shards)):
            if shared := seeds & shards[other]:
                failures.append(f'shards {number} and {other + 1} share {len(shared)} seeds, e.g. {sorted(shared)[:3]}')
    union = set().union(*shards)
    if missing := full - union:
        failures.append(f'shards miss {len(missing)} seeds, e.g. {sorted(missing)[:3]}')
    if extra := union - full:
        failures.append(f'shards add {len(extra)} seeds the whole sweep lacks, e.g. {sorted(extra)[:3]}')
    if failures:
        raise SystemExit('FAIL: generator shards differ from the whole sweep:\n  ' + '\n  '.join(failures))
    sweeps = collections.Counter(sweep for sweep, _ in full)
    print(f'Generator shards cover exactly the {len(full)} seeds of {args.full!r}, each once, in {len(shards)} shards '
          f'of {", ".join(str(len(seeds)) for seeds in shards)}; {len(sweeps)} sweeps: ' +
          ', '.join(f'{sweep} {count}' for sweep, count in sorted(sweeps.items())))


if __name__ == '__main__':
    main()
