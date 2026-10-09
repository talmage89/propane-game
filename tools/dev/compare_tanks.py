#!/usr/bin/env python3
"""Compares where several net_test bots had each tank at the same match times: counts, states and drift.
Usage: tools/dev/compare_tanks.py bot1.log bot2.log [...]"""
import re, sys, math
def load(path):
    snaps = {}
    for line in open(path, errors='replace'):
        m = re.search(r'tanks clock (\S+) n (\d+)(.*)', line)
        if not m: continue
        clock = float(m.group(1))
        tanks = {}
        for tok in m.group(3).split():
            id_, st, pos = tok.split(':')
            tanks[id_] = (st, tuple(map(float, pos.split(','))))
        snaps.setdefault(clock, []).append(tanks)
    return snaps
logs = [load(p) for p in sys.argv[1:]]
base = logs[0]
for clock in sorted(base.keys(), reverse=True):
    a = base[clock][-1]
    for other in logs[1:]:
        near = min(other.keys(), key=lambda c: abs(c-clock)) if other else None
        if near is None or abs(near-clock) > 0.6: continue
        b = other[near][-1]
        common = set(a) & set(b)
        only_a = set(a) - set(b); only_b = set(b) - set(a)
        dists = sorted(((math.dist(a[i][1], b[i][1]), i, a[i][0], b[i][0]) for i in common), reverse=True)
        state_diff = sum(1 for i in common if a[i][0] != b[i][0])
        worst = dists[:3]
        print(f"clock {clock:6.1f}: {len(a)} vs {len(b)} tanks, only-first {sorted(only_a)[:6]} only-second {sorted(only_b)[:6]}, state diffs {state_diff}, "
              f"max drift {worst[0][0] if worst else 0:.3f} " + " ".join(f"{i}:{d:.2f}{sa}{sb}" for d,i,sa,sb in worst))
