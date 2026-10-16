#!/usr/bin/env python3
"""Compare bot difficulties in the same Lab rounds; require an aggregate retained-bank advantage."""
import argparse
import os
from pathlib import Path
import re
import subprocess

from check_lab import ROOT, run_scene


def compare_easy(args):
    failed = []
    for actors in (args.actors,) if args.actors else (4, 8):
        totals = [0.0, 0.0]
        for seed in range(args.first_seed, args.first_seed + args.seeds):
            for offset in (0, actors // 2):
                command = [os.environ.get('GODOT', 'godot-mono'), '--headless', '--fixed-fps', '60', '--path', str(ROOT),
                           'res://scenes/dev/lab_test.tscn', '--', '--scenario=soak', '--compare-skills',
                           f'--bots={actors - 1}', f'--environment={args.environment}', f'--seconds={args.seconds}',
                           f'--duel-seconds={args.seconds}', f'--tune.LabHardBots={actors // 2}', f'--tune.LabHardOffset={offset}',
                           '--tune.BotHardChasesPoints=false', '--tune.BotHardSkill=0.6', '--bot-skill=0.2',
                           f'--tune.FixedSeed={seed}', f'--effects-seed={seed}']
                if args.replay_random:
                    command += ['--replay-random', '--tune.HitstopDuration=0']
                log = args.output / f'easy-{actors}-{seed}-{offset}.log'
                try:
                    code = run_scene(command, log, 900)
                except subprocess.TimeoutExpired:
                    raise SystemExit(f'FAIL skill comparison timed out: {log}') from None
                text = log.read_text()
                match = re.search(r'\[lab skills\].*normal=(\d+) easy=(\d+) normal_score=(\d+) easy_score=(\d+)', text)
                if code or '[lab] PASS' not in text or not match or re.search(r'^ERROR:|^WARNING:|\bFAIL\b', text, re.M):
                    raise SystemExit(f'FAIL skill comparison: {log}\n{text[-4000:]}')
                normal, easy, normal_score, easy_score = map(int, match.groups())
                if normal != actors // 2 or easy != actors // 2:
                    raise SystemExit(f'FAIL skill comparison roster: {log}')
                totals[0] += normal_score / normal
                totals[1] += easy_score / easy
                print(f'bank actors={actors} seed={seed} offset={offset}: Normal {normal_score}/{normal}, Easy {easy_score}/{easy}', flush=True)
        print(f'bank actors={actors}: summed mean final banks Normal={totals[0]:.2f} Easy={totals[1]:.2f}', flush=True)
        if totals[0] <= totals[1]:
            failed.append(actors)
    if failed:
        raise SystemExit(f'FAIL Normal did not outscore Easy across paired seeds at actor counts {failed}')
    print('PASS Normal outscored Easy at every tested actor count.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seeds', type=int, default=5)
    parser.add_argument('--first-seed', type=int, default=21)
    parser.add_argument('--seconds', type=int, default=120)
    parser.add_argument('--environment', choices=('void', 'suburb'), default='suburb')
    parser.add_argument('--output', type=Path, default=ROOT / 'build' / 'bot-points')
    parser.add_argument('--policy-only', action='store_true', help='give both groups Normal aim skill')
    parser.add_argument('--easy', action='store_true', help='compare Normal and Easy in the same Bank rounds at four/eight actors')
    parser.add_argument('--actors', type=int, choices=(4, 8), help='select one actor count within --easy')
    parser.add_argument('--replay-random', action='store_true', help='seed physical events and disable wall-clock hitstop for repeatable comparisons')
    args = parser.parse_args()
    if args.seeds < 1 or args.seconds < 30:
        parser.error('positive seeds and at least 30 seconds required')
    if args.easy and args.policy_only:
        parser.error('--easy compares the actual profiles and cannot use --policy-only')
    if args.actors and not args.easy:
        parser.error('--actors requires --easy')
    args.output.mkdir(parents=True, exist_ok=True)
    if args.easy:
        compare_easy(args)
        return
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
                if args.replay_random:
                    command += ['--replay-random', '--tune.HitstopDuration=0']
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
