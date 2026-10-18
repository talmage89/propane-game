#!/usr/bin/env python3
"""Explain the unchanged tank checker's final grid and optional FU2a native traces.

This is a diagnostic sidecar, not an alternative acceptance checker. The original
compare() determines the verdict and validates coverage before any alignment report.
"""
import argparse
import bisect
import gzip
from itertools import combinations
import json
import math
from pathlib import Path

from compare_tanks import STEP, PROFILES, compare, interpolate, load, observed


def bracket(samples, time):
    times = [s.time for s in samples]
    index = bisect.bisect_right(times, time) - 1
    a = samples[index]
    b = samples[min(index + 1, len(samples) - 1)]
    return a, b, 0. if a.time == b.time else (time - a.time) / (b.time - a.time)


def alignment(logs, limits):
    verdict = compare(logs, limits)
    matches = []
    for match_id in sorted(logs[0]):
        complete = [log[match_id][-1] for log in logs]
        series = [log[match_id][:-1] if end.kind == 'completion' else log[match_id] for log, end in zip(logs, complete)]
        times = [[s.time for s in samples] for samples in series]
        settled = max(next(s.time for s in samples if s.phase == 'E') for samples in series) + limits.settle
        tick = math.floor(min(t[-1] for t in times) / STEP)
        while tick * STEP >= settled and not all(observed(ts, tick * STEP, limits) for ts in times):
            tick -= 1
        time = tick * STEP
        frames = [interpolate(samples, ts, time)[1] for samples, ts in zip(series, times)]
        pairs = []
        for pa, pb in combinations(range(len(logs)), 2):
            for body in sorted(frames[pa].keys() & frames[pb].keys()):
                a, b = frames[pa][body], frames[pb][body]
                if a[0] != b[0]:
                    continue
                distance = math.dist(a[2], b[2])
                endpoints = []
                for peer in (pa, pb):
                    left, right, weight = bracket(series[peer], time)
                    endpoints.append(dict(peer=peer + 1, left=left.time, right=right.time, span=right.time - left.time,
                                          weight=weight, left_body=left.tanks.get(body), right_body=right.tanks.get(body),
                                          interpolated=frames[peer][body], final=series[peer][-1].time,
                                          completion=complete[peer].time))
                pairs.append(dict(body=body, peers=[pa + 1, pb + 1], distance=distance, endpoints=endpoints))
        pairs.sort(key=lambda p: p['distance'], reverse=True)
        matches.append(dict(match=match_id, grid=time, settled=settled, pairs=pairs))
    return dict(verdict=verdict, alignment=matches)


def trace_window(paths, match_id, body, time, window):
    records = []
    for path in paths:
        opener = gzip.open if path.suffix == '.gz' else open
        with opener(path, 'rt', encoding='utf-8-sig') as stream:
            for line in stream:
                if f'"{body}"' not in line:
                    continue
                row = json.loads(line)
                identity = row.get('id', row.get('sample', {}).get('id'))
                if identity == body and row.get('match', match_id) == match_id and abs(row['time'] - time) <= window:
                    records.append(row)
    return sorted(records, key=lambda r: (r['time'], r['peer']))


def trace_metadata(paths, match_id):
    records = []
    for path in paths:
        opener = gzip.open if path.suffix == '.gz' else open
        with opener(path, 'rt', encoding='utf-8-sig') as stream:
            for line in stream:
                row = json.loads(line)
                if row.get('match') == match_id and row['kind'] in ('start', 'configuration'):
                    records.append(row)
                if row['kind'] == 'configuration' or row['kind'].startswith('measurement-'):
                    break
    return records


def rotated_y(q, v):
    # Y component of q * v * q^-1. Trace quaternions come from orthonormalized bases.
    x, y, z, w = q
    return 2 * (x * y + z * w) * v[0] + (1 - 2 * (x * x + z * z)) * v[1] + 2 * (y * z - x * w) * v[2]


def support(row, points):
    def bottom(pose):
        return min(pose['p'][1] + rotated_y(pose['q'], point) for point in points)
    result = dict(time=row['time'], current_bottom=bottom(row['pose']))
    if row.get('target'):
        result['sample_bottom'] = bottom(row['target'])
    detail = row.get('detail')
    if isinstance(detail, dict) and 'goalCenter' in detail:
        result['predicted_bottom'] = min(detail['goalCenter'][1] + rotated_y(detail['goalRotation'],
            [p - c for p, c in zip(point, row['center'])]) for point in points)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('logs', nargs='+', type=Path)
    parser.add_argument('--profile', choices=PROFILES, default='faults')
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--traces', type=Path)
    parser.add_argument('--body', help='otherwise the largest final pair')
    parser.add_argument('--window', type=float, default=2.)
    args = parser.parse_args()
    result = alignment([load(p) for p in args.logs], PROFILES[args.profile])
    if args.traces:
        paths = sorted(args.traces.glob('peer-*.jsonl*'))
        if not paths:
            parser.error('no peer traces')
        for match in result['alignment']:
            body = args.body or match['pairs'][0]['body']
            match['trace_body'] = body
            match['trace_window'] = trace_window(paths, match['match'], body, match['grid'], args.window)
            match['trace_metadata'] = trace_metadata(paths, match['match'])
            match['support_at_nearest_decision'] = {}
            for config in match['trace_metadata']:
                if config['kind'] != 'configuration' or not config.get('hull'):
                    continue
                decisions = [r for r in match['trace_window'] if r['peer'] == config['peer'] and r['kind'].startswith('decision-')]
                if decisions:
                    row = min(decisions, key=lambda r: abs(r['time'] - match['grid']))
                    match['support_at_nearest_decision'][config['peer']] = support(row, config['hull'])
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(dict(failures=result['verdict']['failures'], final_moving=result['verdict']['final_moving_peak'],
                          grids=[m['grid'] for m in result['alignment']])))


if __name__ == '__main__':
    main()
