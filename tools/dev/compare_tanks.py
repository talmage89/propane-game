#!/usr/bin/env python3
"""Compare tank snapshots; --assert checks final convergence, including ids, states and bounded drift."""
import argparse
import math
import re
import sys


def load(path):
    snapshots = {}
    with open(path, errors="replace") as source:
        for line in source:
            match = re.search(r"tanks clock (\S+) n (\d+)(.*)", line)
            if not match:
                continue
            clock = float(match[1])
            if not math.isfinite(clock) or clock < 0 and clock != -1:
                raise ValueError(f"{path}: invalid snapshot clock")
            tanks = {}
            for token in match[3].split():
                identity, state, position = token.split(":")
                if not re.fullmatch(r"[0-9A-Fa-f]+", identity) or not 0 < int(identity, 16) <= 0xffffffff or state not in ("I", "V"):
                    raise ValueError(f"{path}: invalid tank id or state")
                identity = f"{int(identity, 16):x}"
                if identity in tanks:
                    raise ValueError(f"{path}: duplicate tank or invalid state")
                point = tuple(map(float, position.split(",")))
                if len(point) != 3 or not all(map(math.isfinite, point)):
                    raise ValueError(f"{path}: invalid tank position")
                tanks[identity] = state, point
            if len(tanks) != int(match[2]):
                raise ValueError(f"{path}: declared count disagrees with snapshot")
            snapshots[clock] = tanks
    return snapshots


def compare(first, second):
    common = first.keys() & second.keys()
    missing = first.keys() ^ second.keys()
    states = sum(first[key][0] != second[key][0] for key in common)
    drift = max((math.dist(first[key][1], second[key][1]) for key in common), default=0)
    return len(missing), states, drift


def check(paths, max_drift=.5):
    if not math.isfinite(max_drift) or max_drift < 0:
        raise ValueError("drift limit must be finite and nonnegative")
    if len(paths) < 2:
        raise ValueError("provide at least two client logs")
    final = []
    for path in paths:
        snapshots = load(path)
        if 0 not in snapshots:
            raise ValueError(f"{path}: no completed-match tank snapshot")
        final.append(snapshots[0])
    worst = 0
    for path, tanks in zip(paths[1:], final[1:]):
        missing, states, drift = compare(final[0], tanks)
        if missing or states or drift > max_drift:
            raise ValueError(f"{path}: final tanks disagree: ids={missing} states={states} drift={drift:.3f}m (limit {max_drift}m)")
        worst = max(worst, drift)
    print(f"PASS final tanks: {len(final[0])} ids/states agree across {len(paths)} clients; max drift {worst:.3f}m")
    return worst


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assert", dest="asserting", action="store_true")
    parser.add_argument("--max-drift", type=float, default=.5)
    parser.add_argument("logs", nargs="+")
    args = parser.parse_args()
    if args.asserting:
        check(args.logs, args.max_drift)
        return
    if len(args.logs) < 2:
        raise ValueError("provide at least two client logs")
    logs = [load(path) for path in args.logs]
    for clock in sorted(logs[0], reverse=True):
        for path, other in zip(args.logs[1:], logs[1:]):
            near = min(other, key=lambda value: abs(value - clock)) if other else None
            if near is None or abs(near - clock) > .6:
                continue
            missing, states, drift = compare(logs[0][clock], other[near])
            print(f"clock {clock:6.1f} {path}: ids {missing} states {states} max drift {drift:.3f}m")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError) as error:
        sys.exit(f"FAIL: {error}")
