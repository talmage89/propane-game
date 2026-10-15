#!/usr/bin/env python3
"""Measure King crowd holds and audited mints across actor counts and difficulties."""
import argparse
import csv
import os
from pathlib import Path
import re
import subprocess
import time

from check_lab import ROOT, run_scene


def numbers(value):
    return [int(part) for part in value.split(',')]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--actors', type=numbers, default=[4, 8, 16])
    parser.add_argument('--difficulties', type=numbers, default=[0, 1, 2])
    parser.add_argument('--environments', default='void,suburb')
    parser.add_argument('--first-seed', type=int, default=23)
    parser.add_argument('--seeds', type=int, default=1)
    parser.add_argument('--seconds', type=int, default=90)
    parser.add_argument('--network', action='store_true', help='real-time clients instead of the Lab')
    parser.add_argument('--supplies', type=numbers, default=[1, 4])
    parser.add_argument('--net-seed', type=int, default=2)
    parser.add_argument('--faults', action='store_true', help='also run faulted four-client matches')
    parser.add_argument('--minimum-mints', type=int, default=1)
    parser.add_argument('--minimum-minters', type=int, default=1)
    parser.add_argument('--project', type=Path, default=ROOT, help='prepared source checkout, including its compiled assembly')
    parser.add_argument('--output', type=Path, default=ROOT / 'build' / 'king-crowd')
    parser.add_argument('--hold-file', type=Path, help='wait in bounded intervals while this file exists')
    args = parser.parse_args()
    environments = args.environments.split(',')
    if (not args.actors or any(n not in (4, 8, 16) for n in args.actors) or
            not args.difficulties or any(d not in (0, 1, 2) for d in args.difficulties) or
            any(e not in ('void', 'suburb') for e in environments) or
            not args.supplies or any(s not in (1, 4) for s in args.supplies) or
            args.seconds < 30 or args.seeds < 1 or args.minimum_mints < 0 or args.minimum_minters < 0):
        parser.error('use 4/8/16 actors, difficulty 0/1/2, void/suburb, supply 1/4 and at least 30 seconds')
    args.output = args.output.resolve()
    args.project = args.project.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    rows, failures = [], []
    for seed in range(args.first_seed, args.first_seed + args.seeds):
        for actors in args.actors:
            for difficulty in args.difficulties:
                skill = (0.2, 0.6, 1)[difficulty]
                cases = [(e, 1, False) for e in environments] if not args.network else [
                    ('network', supply, faulted) for supply in args.supplies
                    for faulted in ((False, True) if args.faults and actors == 4 else (False,))]
                for environment, supply, faulted in cases:
                    name = f'{environment}-{actors}-{difficulty}-{seed}-s{supply}-' + ('faults' if faulted else 'clean')
                    log = args.output / f'{name}.log'
                    while args.hold_file and args.hold_file.exists():
                        print(f'Waiting for hold: {args.hold_file}', flush=True)
                        time.sleep(30)
                    if args.network:
                        directory = args.output / name
                        command = [str(args.project / 'tools/dev/net_bots.sh'), '-n', str(4 if actors == 4 else 2),
                                   '-l', str(args.seconds), '-t', 'faults' if faulted else 'clean', '-o', str(directory),
                                   '-s', f'--match-seed={seed}', '--', '--mode=king', '--melee-start', '--bot-log',
                                   f'--bot-skill={skill}', f'--tank-supply={supply}', f'--net-seed={args.net_seed}']
                        if actors > 4:
                            command += [f'--lobby-bots={actors - 2},0', f'--lobby-bot-difficulty={difficulty}']
                        if faulted:
                            command += ['--net-lag=120', '--net-jitter=30', '--net-loss=3']
                    else:
                        command = [os.environ.get('GODOT', 'godot-mono'), '--headless', '--fixed-fps', '60', '--path',
                                   str(args.project), 'res://scenes/dev/lab_test.tscn', '--', '--scenario=prototype',
                                   '--mode=king', '--seeds=1', f'--first-seed={seed}', f'--bots={actors - 1}',
                                   f'--bot-skill={skill}', f'--environment={environment}', f'--tune.FixedSeed={seed}',
                                   f'--effects-seed={seed}', f'--tune.KingRoundSeconds={args.seconds}', '--trace',
                                   '--min-king-mints=0']
                    print(' '.join(command), flush=True)
                    try:
                        code = run_scene(command, log, 900)
                    except subprocess.TimeoutExpired:
                        failures.append(name + ': timeout')
                        continue
                    text = log.read_text()
                    if args.network:
                        server = (directory / 'server.log').read_text()
                        clients = sorted(directory.glob('bot*.log'))
                        views = [p.read_text() for p in clients]
                        audit = re.search(r'king audit .*?minted=(\d+).*?ok=True', server)
                        observed = [re.search(r'king crowd: seconds=([\d.]+) lone_share=([\d.]+) minters=(\d+)', v) for v in views]
                        minted = int(audit[1]) if audit else -1
                        lone = sum(float(m[2]) for m in observed if m) / max(1, len(observed))
                        minters = min((int(m[3]) for m in observed if m), default=-1)
                        valid = code == 0 and audit and len(clients) == (4 if actors == 4 else 2) and all(observed)
                        # net_bots checks every client's per-actor mints, scores, engine log and tank agreement.
                    else:
                        metric = re.search(r'\[king crowd\].*?minted=(\d+).*?lone_share=([\d.]+) minters=(\d+)', text)
                        minted, lone, minters = (int(metric[1]), float(metric[2]), int(metric[3])) if metric else (-1, 0, -1)
                        valid = code == 0 and metric and '[lab] PASS' in text and 'ok=True' in text
                    valid = valid and not re.search(r'^ERROR:|^WARNING:|\bFAIL\b', text, re.M)
                    passed = bool(valid and minted >= args.minimum_mints and minters >= args.minimum_minters)
                    row = dict(case=name, seed=seed, actors=actors, difficulty=difficulty, environment=environment,
                               supply=supply, faulted=faulted, seconds=args.seconds, minted=minted,
                               per_minute=minted * 60 / args.seconds, lone_share=lone, minters=minters, passed=passed)
                    rows.append(row)
                    print(row, flush=True)
                    if not passed:
                        failures.append(name)
                    with (args.output / 'summary.csv').open('w', newline='') as stream:
                        writer = csv.DictWriter(stream, fieldnames=row.keys())
                        writer.writeheader()
                        writer.writerows(rows)
    if failures:
        raise SystemExit('FAIL King crowd: ' + ', '.join(failures))
    print(f'PASS {len(rows)} King crowd rounds; summary: {args.output / "summary.csv"}')


if __name__ == '__main__':
    main()
