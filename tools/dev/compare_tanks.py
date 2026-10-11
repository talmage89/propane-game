#!/usr/bin/env python3
"""Assert same-machine tank agreement on a shared monotonic 50 ms grid.

net_bots.sh enables --tank-check. No nearest-neighbor countdown pairing: positions
are interpolated only across an unchanged identity/state; lifecycle is held from
the preceding sample. Missing/gapped/incomplete logs fail closed.
"""
import argparse
import bisect
from dataclasses import dataclass
from itertools import combinations
import math
from pathlib import Path
import re


@dataclass(frozen=True)
class Limits:
    # Calibration on Linux dev b907f7f, 2026-10-11: six 4-bot/90s --melee-start captures with
    # 120/30/3 faults. Lifecycle transients <=1.60s -> 2.0s (+25%); moving p99
    # <=2.05m -> 3m (+46%); resting p95 <=0.013m -> 0.10m (>7x margin).
    # Persistent resting outliers are failures, not included in the noise budget.
    # Final moving drift <=0.281m -> 0.50m (+78%); clean <=0.197m -> 0.35m (+78%).
    # Clean profile: 1.5s/2.5m; see docs/network-soak.md for all runs and margins.
    # Observed capture cadence gaps <=0.28s -> 0.35s (+25%); multi-second stalls fail.
    settle: float = 2.0
    resting: float = .10
    moving: float = 3.0
    final_moving: float = .50
    gap: float = .35


PROFILES = {"faults": Limits(), "clean": Limits(settle=1.5, moving=2.5, final_moving=.35)}
STEP = .05


@dataclass
class Sample:
    time: float
    phase: str
    final: bool
    length: float
    tanks: dict


@dataclass
class Run:
    start: float
    peak: float
    kind: str
    detail: str


def finish_episode(key, run, end):
    return (*key, run.kind, run.start, run.peak, run.detail, end - run.start)


def load(path):
    matches = {}
    for line in Path(path).read_text(errors="replace").splitlines():
        if "tank-check match " not in line:
            continue
        match = re.search(r"tank-check match (\d+) time (\S+) phase ([LPE]) final (True|False) length (\S+) n (\d+)(.*)$", line)
        if not match:
            raise ValueError(f"{path}: malformed sample")
        id_, time, phase, final, length, count, tokens = match.groups()
        time, length = float(time), float(length)
        tanks = {}
        for token in tokens.split():
            id_body, state, rest, pos = token.split(":")
            point = tuple(map(float, pos.split(",")))
            if (not re.fullmatch(r"[0-9a-f]+", id_body) or id_body in tanks or state not in ("I", "V")
                    or rest not in ("R", "M") or len(point) != 3 or not all(map(math.isfinite, point))):
                raise ValueError(f"{path}: invalid tank {token}")
            tanks[id_body] = (state, rest, point)
        if not math.isfinite(time) or not math.isfinite(length) or length <= 0 or len(tanks) != int(count):
            raise ValueError(f"{path}: invalid time, length or count")
        samples = matches.setdefault(int(id_), [])
        if samples and (time <= samples[-1].time or samples[-1].final or length != samples[-1].length
                        or "LPE".index(phase) < "LPE".index(samples[-1].phase)):
            raise ValueError(f"{path}: unordered samples or data after final")
        samples.append(Sample(time, phase, final == "True", length, tanks))
    if not matches:
        raise ValueError(f"{path}: no tank-check samples (use net_bots.sh or --tank-check)")
    return matches


def interpolate(samples, times, time):
    index = bisect.bisect_right(times, time) - 1
    a = samples[index]
    if index == len(samples) - 1 or time == a.time:
        return a.phase, a.tanks
    b = samples[index + 1]
    blend = (time - a.time) / (b.time - a.time)
    tanks = {}
    for id_, (state, rest, pos) in a.tanks.items():
        target = b.tanks.get(id_)
        if target is not None and target[0] == state:
            pos = tuple(x + (y - x) * blend for x, y in zip(pos, target[2]))
            # Both endpoints must be resting to classify an interpolated frame as resting.
            rest = "R" if rest == target[1] == "R" else "M"
        tanks[id_] = (state, rest, pos)
    return a.phase, tanks


def compare(logs, limits=PROFILES["faults"]):
    if len(logs) < 2:
        raise ValueError("need at least two peers")
    if any(log.keys() != logs[0].keys() for log in logs):
        raise ValueError("match IDs disagree")
    failures, episodes = [], []
    metrics = {"grids": 0, "body_pairs": 0, "rest_peak": 0., "moving_peak": 0., "final_rest_peak": 0., "final_moving_peak": 0.}
    for match_id in sorted(logs[0]):
        series = [log[match_id] for log in logs]
        length = series[0][0].length
        for peer, samples in enumerate(series, 1):
            playing = [s for s in samples if s.phase == "P"]
            ended = [s for s in samples if s.phase == "E"]
            if (not samples[-1].final or samples[-1].phase != "E" or not ended or not playing
                    or playing[-1].time - playing[0].time < length * .95
                    or ended[-1].time - ended[0].time < limits.settle
                    or any(s.length != length for s in samples)):
                raise ValueError(f"match {match_id} peer {peer}: missing play coverage or settled completion")
            # Loading is outside the comparison interval; first-draw shader work can stall there.
            if any(a.phase != "L" and b.time - a.time > limits.gap for a, b in zip(samples, samples[1:])):
                raise ValueError(f"match {match_id} peer {peer}: sampling gap > {limits.gap}s")
        times = [[s.time for s in samples] for samples in series]
        start = max(next(s.time for s in samples if s.phase == "P") for samples in series)
        end = min(t[-1] for t in times)
        settled_end = max(next(s.time for s in samples if s.phase == "E") for samples in series) + limits.settle
        if end < settled_end:
            raise ValueError(f"match {match_id}: no common settled end")
        final_states = [{id_: tank[0] for id_, tank in samples[-1].tanks.items()} for samples in series]
        if any(states != final_states[0] for states in final_states[1:]):
            failures.append(f"match {match_id}: final identities/states disagree")
        active = {}
        final_bad = {}
        last_tick = math.floor(end / STEP)
        for tick in range(math.ceil(start / STEP), last_tick + 1):
            time = tick * STEP
            frames = [interpolate(samples, ts, time) for samples, ts in zip(series, times)]
            bad = {}
            metrics["grids"] += 1
            for (pa, (_, a)), (pb, (_, b)) in combinations(enumerate(frames, 1), 2):
                for id_ in sorted(a.keys() | b.keys()):
                    left, right = a.get(id_), b.get(id_)
                    key = (match_id, pa, pb, id_)
                    if left is None or right is None or left[0] != right[0]:
                        bad[key] = (0., "lifecycle", f"{left and left[:2]}/{right and right[:2]}")
                        continue
                    distance = math.dist(left[2], right[2])
                    resting = left[:2] == right[:2] == ("I", "R")
                    kind = "rest" if resting else "moving"
                    metrics["body_pairs"] += 1
                    metrics[kind + "_peak"] = max(metrics[kind + "_peak"], distance)
                    if tick == last_tick:
                        metrics["final_" + kind + "_peak"] = max(metrics["final_" + kind + "_peak"], distance)
                    moving_limit = limits.final_moving if tick == last_tick else limits.moving
                    if distance > (limits.resting if resting else moving_limit):
                        bad[key] = (distance, "position", f"{kind} {left[:2]}/{right[:2]}")
            for key in sorted(active.keys() - bad.keys()):
                episodes.append(finish_episode(key, active[key], time))
            updated = {}
            for key, (peak, kind, detail) in bad.items():
                previous = active.get(key, Run(time, 0., kind, detail))
                # A body must actually agree to reset its clock, even if the type of disagreement changes.
                run_kind = kind if previous.kind == kind else "mixed"
                updated[key] = Run(previous.start, max(peak, previous.peak), run_kind, detail)
            active = updated
            final_bad = bad
        for key, value in active.items():
            episodes.append(finish_episode(key, value, time + STEP))
        if time < settled_end:
            raise ValueError(f"match {match_id}: last grid precedes settled end")
        for (match_id, pa, pb, body), (peak, kind, detail) in final_bad.items():
            failures.append(f"final match {match_id} peers {pa}/{pb} body {body} {kind}: {detail}, drift {peak:.3f}m")
    for match_id, pa, pb, body, kind, start, peak, detail, duration in episodes:
        if duration > limits.settle + 1e-6:
            failures.append(f"match {match_id} peers {pa}/{pb} body {body} {kind} {detail}: "
                            f"{duration:.2f}s from {start:.2f}, peak {peak:.3f}m")
    metrics["episodes"] = episodes
    metrics["failures"] = sorted(failures)
    return metrics


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+")
    parser.add_argument("--profile", choices=PROFILES, default="faults")
    parser.add_argument("--details", action="store_true", help="print every above-tolerance episode for calibration")
    args = parser.parse_args()
    try:
        result = compare([load(p) for p in args.logs], PROFILES[args.profile])
    except (ValueError, OSError) as error:
        parser.exit(1, f"FAIL: {error}\n")
    longest = max((e[-1] for e in result["episodes"]), default=0)
    print(f"{'FAIL' if result['failures'] else 'PASS'} tanks ({args.profile}): {result['grids']} grids, "
          f"{result['body_pairs']} body pairs; rest/moving peak {result['rest_peak']:.3f}/{result['moving_peak']:.3f}m; "
          f"final rest/moving {result['final_rest_peak']:.3f}/{result['final_moving_peak']:.3f}m; "
          f"{len(result['episodes'])} above-tolerance episodes, longest {longest:.2f}s; {len(result['failures'])} disagreements")
    if args.details:
        for episode in result["episodes"]:
            print(episode)
    for failure in result["failures"][:20]:
        print(f"  {failure}")
    if result["failures"]:
        parser.exit(1)


if __name__ == "__main__":
    main()
