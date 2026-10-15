#!/usr/bin/env python3
"""Require assertions, successful exit and clean engine logs from the real offline Lab."""
import argparse
import os
from pathlib import Path
import re
import signal
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]


def argument_parser():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--full', action='store_true', help='ten lifecycle cycles, every dial at both slider ends, both three-minute bot soaks '
                        'and full prototype rounds across seeds')
    parser.add_argument('--lifetime-shard', type=int, default=1)
    parser.add_argument('--lifetime-shards', type=int, default=1)
    parser.add_argument('--dial-shard', type=int, default=1)
    parser.add_argument('--dial-shards', type=int, default=1)
    parser.add_argument('--first-seed', type=int, help='first seed within ringout-full, shortfuse-full or dissolve-full')
    parser.add_argument('--seeds', type=int, help='number of consecutive seeds within that session')
    parser.add_argument('--only', help='comma-separated sessions to run, so make -j can split the list: '
                        'lifetime, rules, aimstall, duel, ringout, king, shortfuse, shortfuse-combo, dissolve, idle-bank, idle-king, and with --full dials, soak-void, '
                        'soak-suburb, ringout-full, king-full, king-suburb, shortfuse-full, dissolve-full, dissolve-pace')
    return parser


def sessions(full):
    # Each session is named, and its log keeps its number in the whole list, so split runs never share a log.
    scenarios = [('lifetime', 'lifetime', [f'--cycles={10 if full else 1}']), ('rules', 'rules', []),
                 ('aimstall', 'aimstall', []), ('duel', 'duel', ['--seconds=15']),
                 # Prototype rounds play to their end with a bot on every actor: shortened dials here, the real ones in --full.
                 ('ringout', 'prototype', ['--mode=ringout', '--quick', '--seeds=2']),
                 ('king', 'prototype', ['--mode=king', '--quick', '--seeds=1']),
                 # A whole Short Fuse match, a new suburb each round: shortened rounds, first to two.
                 ('shortfuse', 'shortfuse', ['--quick', '--seeds=1']),
                 # The scripted round: a player shot dead spectates, then a blast-then-bullet kill decides it.
                 ('shortfuse-combo', 'shortfuse-combo', []),
                 # A bot runs down Dissolve Run's street, then stops and must be caught; segments build and retire.
                 ('dissolve', 'dissolve', ['--seconds=45']),
                 # Crowds of bots keep moving: few seconds of play may pass without travel, a shot or a zone held.
                 ('idle-bank', 'soak', ['--environment=suburb', '--bots=8', '--seconds=40', '--max-idle=0.12', '--max-idle-worst=0.3']),
                 ('idle-king', 'prototype', ['--mode=king', '--environment=suburb', '--bots=15', '--quick', '--seeds=1', '--first-seed=11',
                                             '--tune.KingRoundSeconds=45', '--max-idle=0.15', '--max-idle-worst=0.4'])]
    if full:
        scenarios += [('dials', 'dials', []), ('soak-void', 'soak', ['--environment=void']),
                      ('soak-suburb', 'soak', ['--environment=suburb']),
                      ('ringout-full', 'prototype', ['--mode=ringout', '--seeds=3', '--first-seed=11']),
                      ('king-full', 'prototype', ['--mode=king', '--seeds=3', '--first-seed=11']),
                      ('king-suburb', 'prototype', ['--mode=king', '--environment=suburb', '--seeds=1', '--first-seed=11']),
                      ('shortfuse-full', 'shortfuse', ['--seeds=2', '--first-seed=11']),
                      # Five-minute runs: the bot on two seeds, and a pacer without bot navigation for the stream's own frame times.
                      ('dissolve-full', 'dissolve', ['--seconds=300', '--seeds=2', '--first-seed=11', '--census-every=2']),
                      ('dissolve-pace', 'dissolve', ['--driver=pace', '--seconds=300', '--first-seed=11', '--census-every=2'])]
    return scenarios


def shard_options(name, options, args, parser):
    options = list(options)
    if name == 'lifetime':
        options += [f'--lifetime-shard={args.lifetime_shard}', f'--lifetime-shards={args.lifetime_shards}']
    if name == 'dials':
        options += [f'--dial-shard={args.dial_shard}', f'--dial-shards={args.dial_shards}']
    if args.first_seed is not None or args.seeds is not None:
        if name not in ('ringout-full', 'shortfuse-full', 'dissolve-full'):
            parser.error(f'{name} cannot be split by seed')
        original = dict(option[2:].split('=', 1) for option in options if '=' in option)
        first = int(original['first-seed'])
        count = int(original.get('seeds', 1))
        begin = first if args.first_seed is None else args.first_seed
        length = count if args.seeds is None else args.seeds
        if length < 1 or begin < first or begin + length > first + count:
            parser.error(f'{name}: seed range must stay inside {first}..{first + count - 1}')
        options = [option for option in options if not option.startswith(('--seeds=', '--first-seed='))]
        options += [f'--seeds={length}', f'--first-seed={begin}']
    return options


def run_scene(command, log, budget):
    with log.open('w') as output, subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT,
                                                 start_new_session=True) as process:
        try:
            return process.wait(timeout=budget)
        except subprocess.TimeoutExpired:
            # GODOT may be a timing wrapper. Kill its whole group, including the engine, before reporting a hang.
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
            raise


def main():
    parser = argument_parser()
    args = parser.parse_args()
    scenarios = sessions(args.full)
    selected = [name for name, _, _ in scenarios]
    if args.lifetime_shards < 1 or not 1 <= args.lifetime_shard <= args.lifetime_shards:
        parser.error('lifetime shard must be in 1..lifetime-shards')
    if args.dial_shards < 1 or not 1 <= args.dial_shard <= args.dial_shards:
        parser.error('dial shard must be in 1..dial-shards')
    if args.only:
        selected = args.only.split(',')
        unknown = sorted(set(selected) - {name for name, _, _ in scenarios})
        if len(selected) != len(set(selected)):
            parser.error('duplicate sessions')
        if unknown:
            parser.error(f'unknown sessions: {", ".join(unknown)}')
    logs = ROOT / 'build' / ('lab-check-full' if args.full else 'lab-check')
    logs.mkdir(parents=True, exist_ok=True)
    for number, (name, scenario, options) in enumerate(scenarios):
        if name not in selected:
            continue
        options = shard_options(name, options, args, parser)
        start = time.monotonic()
        command = [os.environ.get('GODOT', 'godot-mono'), '--headless', '--fixed-fps', '60', '--path', str(ROOT),
                   'res://scenes/dev/lab_test.tscn', '--', f'--scenario={scenario}', *options]
        suffix = f'-{args.dial_shard}-of-{args.dial_shards}' if name == 'dials' and args.dial_shards > 1 else ''
        if name == 'lifetime' and args.lifetime_shards > 1: suffix += f'-{args.lifetime_shard}-of-{args.lifetime_shards}'
        if args.first_seed is not None: suffix += f'-seed-{args.first_seed}'
        log = logs / f'{number}-{name}{suffix}.log'
        # Full sessions get at least three times their measured loaded cost. An unsharded dial sweep can exceed
        # 600 s; make's eight shards each retain a 600 s floor. The fast gate keeps its existing 600 s limit.
        budget = 600
        if args.full:
            budget = max(600, (2400 + args.dial_shards - 1) // args.dial_shards) if name == 'dials' else 1800
        try:
            code = run_scene(command, log, budget)
        except subprocess.TimeoutExpired:
            raise SystemExit(f'FAIL Lab {name}: timed out after {budget} s; {log}') from None
        text = log.read_text()
        if code != 0 or '[lab] PASS' not in text or re.search(r'^ERROR:|^WARNING:|\bFAIL\b', text, re.M):
            raise SystemExit(f'FAIL Lab {name} ({code}); {log}\n{text[-12000:]}')
        print('\n'.join(line for line in text.splitlines() if line.startswith('[lab')), flush=True)
        print(f'Lab {name} passed in {time.monotonic() - start:.1f} s.', flush=True)
    print(f'Lab sessions passed with clean engine logs: {", ".join(selected)}.')


if __name__ == '__main__':
    main()
