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
    # Clean profile: 1.5s/2.5m; see docs/evidence/network-soak.md for all runs and margins.
    # Post-fix dev 21a2ac2 validation: ten faulted/four clean 4-bot/90s soaks.
    # Longest episodes 1.70/0.55s; moving p99 maxima 2.340/1.141m; final rest <=0.001m.
    # Nine faulted and four clean pass; one fresh final vent at 0.531m still fails the
    # unchanged 0.50m bound. See docs/evidence/network-corrections.md; do not hide it by relaxing limits.
    # Loaded-machine validation saw gaps up to 5.923s and a 7.30s late play start. Permit up to 10s
    # per gap, <=10% unobserved time, and <=min(10s, 10% of play) late startup. Positions
    # across gaps >0.35s are unobserved, never guessed. See docs/evidence/network-soak.md.
    settle: float = 2.0
    resting: float = .10
    moving: float = 3.0
    final_moving: float = .50
    gap: float = 10.0
    interpolation_gap: float = .35
    missing_fraction: float = .10
    startup: float = 10.0


PROFILES = {"faults": Limits(), "clean": Limits(settle=1.5, moving=2.5, final_moving=.35)}
STEP = .05


@dataclass
class Sample:
    time: float
    phase: str
    final: bool
    length: float
    tanks: dict
    kind: str = "check"


@dataclass
class Run:
    start: float
    peak: float
    kind: str
    detail: str
    observed: float = 0.


def finish_episode(key, run):
    return (*key, run.kind, run.start, run.peak, run.detail, run.observed)


def observed(times, time, limits):
    index = bisect.bisect_right(times, time) - 1
    return index == len(times) - 1 or time == times[index] or times[index + 1] - times[index] <= limits.interpolation_gap


def load(path):
    matches = {}
    for line in Path(path).read_text(errors="replace").splitlines():
        if not any(f"tank-{kind} match " in line for kind in ("check", "final", "completion")):
            continue
        match = re.search(r"tank-(check|final|completion) match (\d+) time (\S+) phase ([LPE]) final (True|False) length (\S+) n (\d+)(.*)$", line)
        if not match:
            raise ValueError(f"{path}: malformed sample")
        kind, id_, time, phase, final, length, count, tokens = match.groups()
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
        completion = kind == "completion" and bool(samples) and samples[-1].kind == "final"
        if kind != "check" and (phase != "E" or final != "True" or kind == "completion" and not completion):
            raise ValueError(f"{path}: invalid terminal sample")
        if samples and (time <= samples[-1].time or (samples[-1].final and not completion) or length != samples[-1].length
                        or "LPE".index(phase) < "LPE".index(samples[-1].phase)):
            raise ValueError(f"{path}: unordered samples or data after final")
        samples.append(Sample(time, phase, final == "True", length, tanks, kind))
    if not matches:
        raise ValueError(f"{path}: no tank-check samples (use net_bots.sh or --tank-check)")
    if any(samples[-1].kind == "final" for samples in matches.values()):
        raise ValueError(f"{path}: missing exact completion after live final")
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
    metrics = {"grids": 0, "body_pairs": 0, "unobserved_pairs": 0, "rest_peak": 0., "moving_peak": 0., "final_rest_peak": 0., "final_moving_peak": 0.}
    for match_id in sorted(logs[0]):
        complete = [log[match_id][-1] for log in logs]
        series = [log[match_id][:-1] if last.kind == "completion" else log[match_id] for log, last in zip(logs, complete)]
        length = series[0][0].length
        for peer, samples in enumerate(series, 1):
            playing = [s for s in samples if s.phase == "P"]
            ended = [s for s in samples if s.phase == "E"]
            if (not samples[-1].final or samples[-1].phase != "E" or not ended or not playing
                    or playing[-1].time - playing[0].time < length - min(limits.startup, length * limits.missing_fraction)
                    or ended[-1].time - ended[0].time < limits.settle
                    or any(s.length != length for s in samples)):
                raise ValueError(f"match {match_id} peer {peer}: missing play coverage or settled completion")
            # Loading is outside the comparison interval; first-draw shader work can stall there.
            if any(a.phase != "L" and b.time - a.time > limits.gap for a, b in zip(samples, samples[1:])):
                raise ValueError(f"match {match_id} peer {peer}: sampling gap > {limits.gap}s")
            missing = sum(b.time - a.time for a, b in zip(samples, samples[1:])
                          if a.phase != "L" and b.time - a.time > limits.interpolation_gap)
            if missing > (samples[-1].time - playing[0].time) * limits.missing_fraction:
                raise ValueError(f"match {match_id} peer {peer}: >{limits.missing_fraction:.0%} unobserved capture time")
        times = [[s.time for s in samples] for samples in series]
        start = max(next(s.time for s in samples if s.phase == "P") for samples in series)
        end = min(t[-1] for t in times)
        settled_end = max(next(s.time for s in samples if s.phase == "E") for samples in series) + limits.settle
        if end < settled_end:
            raise ValueError(f"match {match_id}: no common settled end")
        final_states = [{id_: tank[0] for id_, tank in last.tanks.items()} for last in complete]
        if any(states != final_states[0] for states in final_states[1:]):
            failures.append(f"match {match_id}: final identities/states disagree")
        for (pa, a), (pb, b) in combinations(enumerate(complete, 1), 2):
            for id_ in a.tanks.keys() & b.tanks.keys():
                left, right = a.tanks[id_], b.tanks[id_]
                if left[0] == right[0] == "I":
                    resting = left[1] == right[1] == "R"
                    limit = limits.resting if resting else limits.final_moving
                    distance = math.dist(left[2], right[2])
                    if distance > limit:
                        kind = "resting" if resting else "intact moving"
                        failures.append(f"final completion match {match_id} peers {pa}/{pb} body {id_}: {kind} drift {distance:.3f}m")
        active = {}
        final_bad = {}
        last_tick = math.floor(end / STEP)
        # A late frame stall cannot fabricate a final trajectory. Use the last common observed instant
        # after settlement, and independently check exact completion lifecycle/intact poses above.
        while last_tick * STEP >= settled_end and not all(observed(ts, last_tick * STEP, limits) for ts in times):
            last_tick -= 1
        if last_tick * STEP < settled_end:
            raise ValueError(f"match {match_id}: no common observed settled end")
        for tick in range(math.ceil(start / STEP), last_tick + 1):
            time = tick * STEP
            frames = [interpolate(samples, ts, time) for samples, ts in zip(series, times)]
            bad = {}
            skipped = set()
            metrics["grids"] += 1
            for (pa, (_, a)), (pb, (_, b)) in combinations(enumerate(frames, 1), 2):
                if not observed(times[pa - 1], time, limits) or not observed(times[pb - 1], time, limits):
                    skipped.add((pa, pb))
                    metrics["unobserved_pairs"] += 1
                    continue
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
                if key[1:3] not in skipped:
                    episodes.append(finish_episode(key, active[key]))
            # Missing frames pause the observed duration; they neither prove agreement nor prolong an error.
            updated = {key: run for key, run in active.items() if key[1:3] in skipped}
            for key, (peak, kind, detail) in bad.items():
                previous = active.get(key, Run(time, 0., kind, detail))
                # A body must actually agree to reset its clock, even if the type of disagreement changes.
                run_kind = kind if previous.kind == kind else "mixed"
                updated[key] = Run(previous.start, max(peak, previous.peak), run_kind, detail, previous.observed + STEP)
            active = updated
            final_bad = bad
        for key, value in active.items():
            episodes.append(finish_episode(key, value))
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
          f"{result['body_pairs']} body pairs, {result['unobserved_pairs']} unobserved pair grids; rest/moving peak {result['rest_peak']:.3f}/{result['moving_peak']:.3f}m; "
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
