#!/usr/bin/env python3
"""Compare same-machine tank trajectories interpolated to common wall timestamps.

Capture with net_bots.sh -- --tank-trajectories. Reports pairwise playing drift, excluding
gaps and lifecycle transitions, and checks final tank identities and states on every client.
Wall clocks must be shared: this tool is for clients running on the same machine.
"""
import argparse
import bisect
import math
import re
from itertools import combinations
from pathlib import Path


def load(path):
    samples = {}
    for line in Path(path).read_text(errors="replace").splitlines():
        match = re.search(r"tank-sample time (\S+) playing (True|False) clock (\S+) n (\d+)(.*)", line)
        if not match:
            continue
        time = float(match[1])
        tanks = {}
        for token in match[5].split():
            id_, state, position = token.split(":")
            point = tuple(map(float, position.split(",")))
            if state not in ("I", "V") or len(point) != 3 or not all(map(math.isfinite, point)) or id_ in tanks:
                raise ValueError(f"{path}: invalid tank sample")
            tanks[id_] = (state, point)
        if not math.isfinite(time) or len(tanks) != int(match[4]):
            raise ValueError(f"{path}: invalid timestamp or tank count")
        # Catch-up physics ticks can share a wall time; retain the last state.
        samples[time] = (match[2] == "True", tanks)
    if len(samples) < 2:
        raise ValueError(f"{path}: missing trajectory samples")
    return [(time, *sample) for time, sample in sorted(samples.items())]


def interpolate(samples, times, time, max_gap):
    index = bisect.bisect_left(times, time)
    if index < len(times) and times[index] == time:
        return samples[index][2] if samples[index][1] else {}
    if index == 0 or index == len(times):
        return {}
    start, playing_a, a = samples[index - 1]
    end, playing_b, b = samples[index]
    if not playing_a or not playing_b or end - start > max_gap:
        return {}
    blend = (time - start) / (end - start)
    return {id_: (a[id_][0], tuple(x + (y - x) * blend for x, y in zip(a[id_][1], b[id_][1])))
            for id_ in a.keys() & b.keys() if a[id_][0] == b[id_][0]}


def distribution(values):
    values = sorted(values)
    if not values:
        raise ValueError("no overlapping playing trajectory samples")
    return {"pairs": len(values), "median": values[len(values) // 2],
            "p95": values[int((len(values) - 1) * .95)], "max": values[-1]}


def compare(logs, step=.05, max_gap=.2):
    if len(logs) < 2 or step <= 0 or max_gap <= 0:
        raise ValueError("need at least two clients and positive sampling intervals")
    final = [{id_: state for id_, (state, _) in log[-1][2].items()} for log in logs]
    if any(log[-1][1] for log in logs) or any(last != final[0] for last in final[1:]):
        raise ValueError("missing final sample or final tank identities/states disagree")
    times = [[sample[0] for sample in log] for log in logs]
    start = max(t[0] for t in times)
    end = min(t[-1] for t in times)
    drift, vent_drift = [], []
    for tick in range(math.ceil((end - start) / step)):
        time = start + tick * step
        frames = [interpolate(log, t, time, max_gap) for log, t in zip(logs, times)]
        for a, b in combinations(frames, 2):
            for id_ in a.keys() & b.keys():
                if a[id_][0] != b[id_][0]:
                    continue
                distance = math.dist(a[id_][1], b[id_][1])
                drift.append(distance)
                if a[id_][0] == "V":
                    vent_drift.append(distance)
    return len(final[0]), distribution(drift), distribution(vent_drift)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+")
    args = parser.parse_args()
    try:
        final, all_body, vent = compare([load(path) for path in args.logs])
    except ValueError as error:
        parser.exit(1, f"FAIL: {error}\n")
    print(f"PASS final identities/states ({final} tanks)")
    for name, result in (("all", all_body), ("venting", vent)):
        print(f"playing {name}: {result['pairs']} pairs; median/p95/max "
              f"{result['median']:.3f}/{result['p95']:.3f}/{result['max']:.3f} m")


if __name__ == "__main__":
    main()
