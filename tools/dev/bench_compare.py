#!/usr/bin/env python3
"""Compare median scale costs. Positive FPS is better; negative time/GC/memory is better."""
import argparse
import copy
import json
import math
from pathlib import Path

from bench import METRICS, SCHEMA, median_runs


def read(path):
    data = json.loads(Path(path).read_text())
    if data["schema"] != SCHEMA or not data["cases"]:
        raise ValueError("unsupported schema or empty benchmark")
    cases = {}
    for case in data["cases"]:
        key = (case["rendered"], case["bots"], case["tank_multiplier"])
        if key in cases:
            raise ValueError(f"duplicate case {key}")
        if not case["runs"] or any(not math.isfinite(case["median"][metric]) for metric in METRICS):
            raise ValueError(f"invalid metrics for {key}")
        cases[key] = case
    return data, cases


def compare(a, b):
    old, acases = read(a)
    new, bcases = read(b)
    for key in ("seed", "seconds", "warmup", "resolution", "presentation", "omitted_render_feature"):
        if old.get(key, "none") != new.get(key, "none"):
            raise ValueError(f"workload mismatch: {key}")
    if acases.keys() != bcases.keys():
        raise ValueError("case grids differ; select the same BOTS/TANKS/RENDERED")
    for key in acases:
        left, right = acases[key], bcases[key]
        for metric in ("world_hash", "tanks", "physics_hz", "godot"):
            values = {r[metric] for r in left["runs"] + right["runs"]}
            if len(values) != 1:
                raise ValueError(f"workload mismatch: {key} {metric}")
        yield key, left["median"], right["median"]


def merge(paths):
    combined = None
    cases = {}
    for path in paths:
        data, incoming = read(path)
        if combined is None:
            combined = copy.deepcopy(data)
        for key in ("revision", "config", "seed", "seconds", "warmup", "resolution", "presentation", "omitted_render_feature"):
            if combined.get(key, "none") != data.get(key, "none"):
                raise ValueError(f"cannot merge different {key}")
        for key, case in incoming.items():
            if key not in cases:
                cases[key] = copy.deepcopy(case)
            else:
                cases[key]["runs"].extend(case["runs"])
            for metric in ("world_hash", "tanks", "physics_hz", "godot"):
                if len({r[metric] for r in cases[key]["runs"]}) != 1:
                    raise ValueError(f"cannot merge different {metric}")
            cases[key]["median"] = median_runs(cases[key]["runs"])
    if combined is None:
        raise ValueError("no input runs")
    combined["cases"] = list(cases.values())
    combined["repeats"] = min(len(c["runs"]) for c in cases.values())
    combined["dirty"] = any(read(p)[0]["dirty"] for p in paths)
    return combined


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+")
    parser.add_argument("--merge", action="store_true", help="combine repetitions/subsets from one revision")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        if args.merge:
            if args.output is None or args.output.exists():
                parser.error("merge needs a new --output JSON path")
            args.output.write_text(json.dumps(merge(args.paths), indent=2) + "\n")
            return 0
        if len(args.paths) != 2 or args.output:
            parser.error("comparison needs exactly two JSON paths")
        cases = list(compare(*args.paths))
    except (ValueError, KeyError, TypeError) as error:
        parser.error(str(error))
    print("view bots tank× metric                         before       after     change")
    for (view, bots, tanks), old, new in cases:
        for metric in METRICS:
            before, after = old[metric], new[metric]
            change = f"{(after / before - 1) * 100:+.1f}%" if before else ("0%" if not after else "n/a")
            print(f"{int(view):4} {bots:4} {tanks:5} {metric:28} {before:11.3f} {after:11.3f} {change:>10}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
