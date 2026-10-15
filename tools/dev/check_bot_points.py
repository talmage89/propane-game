#!/usr/bin/env python3
"""Compare Hard and Normal in the same Lab rounds; require an aggregate final-bank advantage per mode."""
import argparse
import os
from pathlib import Path
import re
import subprocess

from check_lab import ROOT, run_scene


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seeds', type=int, default=5)
    parser.add_argument('--first-seed', type=int, default=21)
    parser.add_argument('--seconds', type=int, default=120)
    parser.add_argument('--environment', choices=('void', 'suburb'), default='suburb')
    parser.add_argument('--output', type=Path, default=ROOT / 'build' / 'bot-points')
    parser.add_argument('--policy-only', action='store_true', help='give both groups Normal aim skill')
    args = parser.parse_args()
    if args.seeds < 1 or args.seconds < 30:
        parser.error('positive seeds and at least 30 seconds required')
    args.output.mkdir(parents=True, exist_ok=True)
    minted = 0
    totals = {mode: [0.0, 0.0] for mode in ('bank', 'king')}
    for seed in range(args.first_seed, args.first_seed + args.seeds):
        for mode in totals:
            # Alternate the groups across spawn slots as well as varying the world and brain random streams.
            for offset in (0, 4):
                command = [os.environ.get('GODOT', 'godot-mono'), '--headless', '--fixed-fps', '60', '--path', str(ROOT),
                           'res://scenes/dev/lab_test.tscn', '--', '--bots=7', '--compare-points',
                           f'--environment={args.environment}', '--tune.LabHardBots=4', f'--tune.LabHardOffset={offset}',
                           f'--tune.FixedSeed={seed}', f'--effects-seed={seed}', '--bot-skill=0.6']
                if args.policy_only:
                    command.append('--tune.BotHardSkill=0.6')
                if mode == 'bank':
                    command += ['--scenario=soak', f'--seconds={args.seconds}', f'--duel-seconds={args.seconds}']
                else:
                    command += ['--scenario=prototype', '--mode=king', '--seeds=1', f'--first-seed={seed}',
                                f'--tune.KingRoundSeconds={args.seconds}']
                log = args.output / f'{mode}-{seed}-{offset}.log'
                try:
                    code = run_scene(command, log, 900)
                except subprocess.TimeoutExpired:
                    raise SystemExit(f'FAIL points comparison timed out: {log}') from None
                text = log.read_text()
                match = re.search(r'\[lab points\].*hard=(\d+) normal=(\d+) hard_score=(\d+) normal_score=(\d+)', text)
                if code or '[lab] PASS' not in text or not match or re.search(r'^ERROR:|^WARNING:|\bFAIL\b', text, re.M):
                    raise SystemExit(f'FAIL points comparison: {log}\n{text[-4000:]}')
                if mode == 'king':
                    audit = re.search(r'king audit .*?minted=(\d+)', text)
                    if not audit:
                        raise SystemExit(f'FAIL missing King audit: {log}')
                    minted += int(audit[1])
                hard, normal, hard_score, normal_score = map(int, match.groups())
                totals[mode][0] += hard_score / hard
                totals[mode][1] += normal_score / normal
                print(f'{mode} seed={seed} offset={offset}: Hard {hard_score}/{hard}, Normal {normal_score}/{normal}', flush=True)
    for mode, (hard, normal) in totals.items():
        print(f'{mode}: summed mean final banks Hard={hard:.2f} Normal={normal:.2f}', flush=True)
        if hard <= normal:
            raise SystemExit(f'FAIL {mode}: Hard did not outscore Normal across paired seeds')
    if minted == 0:
        raise SystemExit('FAIL King: nobody minted across the strength sweep')
    print(f'PASS Hard outscored Normal in both modes; King minted {minted}.')


if __name__ == '__main__':
    main()
