#!/usr/bin/env python3
"""Final-pose review metrics; standard library only. Calibrated bounds supplement rendered owner review."""
import argparse
import csv
import json
import math
from pathlib import Path
import statistics

CALIBRATION = Path(__file__).with_name('animation_calibration.json')


def correlation(left, right):
    a, b = statistics.mean(left), statistics.mean(right)
    covariance = sum((x - a) * (y - b) for x, y in zip(left, right))
    variance = sum((x - a) ** 2 for x in left) * sum((y - b) ** 2 for y in right)
    if variance < 1e-12:
        raise ValueError('ankles did not move enough to measure alternation')
    return covariance / math.sqrt(variance)


def measure(path):
    groups = {}
    with path.open() as stream:
        for row in csv.DictReader(stream):
            case = row.pop('case')
            values = {key: float(value) for key, value in row.items()}
            if not all(math.isfinite(value) for value in values.values()):
                raise ValueError(f'{case}: non-finite sample')
            groups.setdefault(case, []).append(values)
    result = {}
    for case, rows in groups.items():
        if case == 'startup':
            continue
        frames = [r for r in rows if 0.8 <= r['localTime'] < 2.5]
        if len(frames) < 95 or len(frames) > 110:
            raise ValueError(f'{case}: need 60 Hz settled coverage, got {len(frames)}')
        speed = statistics.mean(r['speed'] for r in frames)
        gait = case.split('_')[0]
        expected_speed = {'walk': 1.5, 'jog': 6, 'creep': 1.6, 'sprint': 9.5}.get(gait)
        if expected_speed is not None and abs(speed - expected_speed) > .02:
            raise ValueError(f'{case}: controller speed changed: {speed}')
        if gait == 'creep' and min(r['aim'] for r in frames) < .99:
            raise ValueError(f'{case}: not effectively aiming')
        entry = {'samples': len(frames), 'speed': speed,
                 'head_span': max(r['headY'] for r in frames) - min(r['headY'] for r in frames),
                 'pelvis_span': max(r['pelvisY'] for r in frames) - min(r['pelvisY'] for r in frames),
                 'knee_gap': min(r['kneeGap'] for r in frames),
                 'thigh_gap': min(r['thighGap'] for r in frames),
                 'stock_head': min(r['stockHead'] for r in frames),
                 'stock_shoulder': min(r['stockShoulder'] for r in frames),
                 'hip_aim_yaw': max(abs(r['hipAimYaw']) for r in frames)}
        if case.startswith(('walk_', 'jog_', 'creep_')) or case == 'sprint':
            entry['alternation'] = correlation([r['leftY'] for r in frames], [r['rightY'] for r in frames])
            drift = []
            for side in ('left', 'right'):
                floor = min(r[side + 'Y'] for r in frames)
                for a, b in zip(frames, frames[1:]):
                    dt = b['time'] - a['time']
                    if dt <= 0 or dt > .025:
                        raise ValueError(f'{case}: non-consecutive sampling')
                    if max(a[side + 'Y'], b[side + 'Y']) <= floor + .03:
                        drift.append(math.hypot(b[side + 'X'] - a[side + 'X'], b[side + 'Z'] - a[side + 'Z']) / dt)
            if len(drift) < 12:
                raise ValueError(f'{case}: insufficient planted-foot proxy coverage')
            entry['foot_sliding'] = statistics.median(drift)
            entry['contact_pairs'] = len(drift)
        result[case] = entry
    if not result:
        raise ValueError('no measured cases')
    return result


def evaluate(result, calibration):
    checks = []
    for case, values in result.items():
        gait = case.split('_')[0]
        limits = calibration['gaits'].get(gait)
        if limits:
            for metric, (low, high) in limits.items():
                value = values[metric]
                checks.append({'case': case, 'metric': metric, 'value': value, 'minimum': low, 'maximum': high,
                               'pass': low <= value <= high, 'status': 'pass' if low <= value <= high else 'fail'})
        if case.startswith('creep_') or case == 'aim_idle':
            for metric in ('stock_head', 'stock_shoulder'):
                low, high = calibration['clearance'][metric]
                checks.append({'case': case, 'metric': metric, 'value': values[metric], 'minimum': low,
                               'maximum': high, 'pass': low <= values[metric] <= high, 'status': 'pass' if low <= values[metric] <= high else 'fail'})
        if case in ('creep_180', 'creep_090', 'creep_270'):
            high = calibration['hip_aim_yaw']
            checks.append({'case': case, 'metric': 'hip_aim_yaw', 'value': values['hip_aim_yaw'], 'maximum': high,
                           'pass': values['hip_aim_yaw'] <= high, 'status': 'pass' if values['hip_aim_yaw'] <= high else 'fail'})
    return checks


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('csv', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--strict', action='store_true', help='make visual failures return nonzero')
    args = parser.parse_args()
    measured = measure(args.csv)
    calibration = json.loads(CALIBRATION.read_text())
    missing = set(calibration['gate_cases']) - measured.keys()
    if missing:
        raise ValueError('missing required review cases: ' + ', '.join(sorted(missing)))
    checks = evaluate(measured, calibration)
    report = {'metrics': measured, 'checks': checks, 'calibration': calibration['provenance']}
    if args.output:
        args.output.write_text(json.dumps(report, indent=2))
    failed = [c for c in checks if not c['pass']]
    for check in failed:
        print(f"FAIL {check['case']}: {check['metric']}={check['value']:.5f}")
    print(f'PASS data coverage; {len(checks) - len(failed)}/{len(checks)} visual checks pass, {len(failed)} failures')
    if args.strict and failed:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
