#!/usr/bin/env python3
"""Check puncture-race canonical parameters and time-aligned trajectories on every client."""
import argparse
import bisect
import math
import re
from itertools import combinations
from pathlib import Path


def load(path):
    text = Path(path).read_text(errors="replace")
    if "ERROR: race:" in text or "ERROR: scenario ended" in text:
        raise ValueError(f"{path}: scenario assertion failed")
    params = re.findall(r"race params owner (\d+) epoch (\d+) hole (\S+) normal (\S+) lean (\S+) swirl (\S+)", text)
    if len(params) != 1 or "puncture-race: assertions passed" not in text:
        raise ValueError(f"{path}: missing canonical parameters or scenario completion")
    contacts = re.findall(r"race contact tick (\d+) hole (\S+) swirl (\S+)", text)
    samples = [(float(t), tuple(map(float, p.split(','))))
               for t, p in re.findall(r"race path time (\S+) pos (\S+)", text)]
    if any(len(position) != 3 or not all(math.isfinite(value) for value in (time, *position))
           for time, position in samples):
        raise ValueError(f"{path}: invalid trajectory sample")
    # Several physics ticks can run within a render/poll frame. Keep the last sample at each clock reading.
    samples = sorted(dict(samples).items())
    if len(samples) < 100:
        raise ValueError(f"{path}: only {len(samples)} trajectory samples")
    return params[0], contacts, samples


def compare(paths, max_drift):
    logs = [load(path) for path in paths]
    if len(logs) < 2:
        raise ValueError("at least two clients are required")
    if any(params != logs[0][0] for params, _, _ in logs[1:]):
        raise ValueError("clients disagree on canonical owner, epoch, hole, normal, lean or swirl")
    contacts = [contact for _, contacts, _ in logs for contact in contacts]
    if len(contacts) != 2 or any(tick != "180" for tick, _, _ in contacts) or contacts[0][1] == contacts[1][1]:
        raise ValueError("expected two different predicted holes on simulation tick 180")
    drift = []
    for (_, _, first), (_, _, other) in combinations(logs, 2):
        times = [time for time, _ in other]
        pairs = []
        for time, position in first:
            i = bisect.bisect_left(times, time)
            if i == 0 or i == len(other):
                continue
            start, a = other[i - 1]
            end, b = other[i]
            if end - start > 0.3:
                continue
            blend = (time - start) / (end - start)
            target = tuple(x + (y - x) * blend for x, y in zip(a, b))
            pairs.append(math.dist(position, target))
        if len(pairs) < 100:
            raise ValueError("insufficient overlapping trajectory samples")
        drift.extend(pairs)
    drift.sort()
    if max(drift) > max_drift:
        raise ValueError(f"trajectory drift {max(drift):.3f} m exceeds {max_drift:.3f} m")
    return len(drift), drift[len(drift) // 2], drift[int((len(drift) - 1) * .95)], max(drift)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+")
    parser.add_argument("--max-drift", type=float, default=3, help="test tolerance in meters (default: the game's default snap distance)")
    args = parser.parse_args()
    try:
        count, median, p95, worst = compare(args.logs, args.max_drift)
    except ValueError as error:
        parser.exit(1, f"FAIL: {error}\n")
    print(f"PASS canonical vent; {count} trajectory pairs, median/p95/max {median:.3f}/{p95:.3f}/{worst:.3f} m")


if __name__ == "__main__":
    main()
