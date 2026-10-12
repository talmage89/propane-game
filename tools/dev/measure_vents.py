#!/usr/bin/env python3
"""Alternate baseline/current Lab runs, preserving raw frames, engine logs and machine load."""
import argparse
import csv
import datetime
import os
from pathlib import Path
import re
import subprocess


def load(path):
    with path.open("w") as output:
        output.write(datetime.datetime.now(datetime.timezone.utc).isoformat() + "\n")
        output.flush()
        for command in (["nvidia-smi"], ["uptime"]):
            try:
                subprocess.run(command, stdout=output, stderr=output, check=False)
            except FileNotFoundError:
                output.write(f"{command[0]} unavailable\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", required=True, type=Path, help="scratch project with the baseline build and resources")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--counts", default="32,64,128,40")
    parser.add_argument("--repeats", type=int, default=2)
    parser.add_argument("--phases", default="idle,blast,chain")
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    environment = {**os.environ, "TMPDIR": str(output)}
    counts = list(dict.fromkeys(int(value) for value in args.counts.split(",")))
    phases = args.phases.split(",")
    if args.repeats < 1 or any(count < 24 or count > 1024 for count in counts) or any(phase not in ("idle", "blast", "chain") for phase in phases):
        parser.error("use positive repeats, counts 24..1024 and idle,blast,chain phases")
    with (output / "summary.csv").open("w", newline="") as summary:
        writer = csv.writer(summary)
        writer.writerow(["repeat", "tanks", "phase", "build", "system", "p50", "p95", "worst"])
        for repeat in range(1, args.repeats + 1):
            for count in counts:
                for phase in phases:
                    for label, checkout in (("before", args.baseline.resolve()), ("after", project)):
                        name = f"r{repeat}-{count}-{phase}-{label}"
                        load(output / f"{name}.load-before")
                        command = ["godot-mono", "--path", str(checkout), "--disable-vsync", "--resolution", "1920x1080",
                                   "res://scenes/dev/lab_test.tscn", "--", "--scenario=measure", f"--count={count}",
                                   f"--phase={phase}", "--windowed", f"--metrics={output / (name + '.csv')}"]
                        with (output / f"{name}.log").open("w") as log:
                            result = subprocess.run(command, stdout=log, stderr=log, env=environment, timeout=3600)
                        load(output / f"{name}.load-after")
                        text = (output / f"{name}.log").read_text()
                        if result.returncode or "[lab] PASS" not in text or "ERROR:" in text:
                            raise RuntimeError(f"{name} failed; inspect its log")
                        with (output / f"{name}.csv").open() as raw:
                            frames = list(csv.DictReader(raw))
                        if len(frames) != 600 or not any(float(row["gpu"]) > 0 for row in frames):
                            raise RuntimeError(f"{name} did not capture 600 rendered GPU samples")
                        for system, p50, p95, worst in re.findall(r"\[lab profile\] (\w+) p50=([\d.]+) p95=([\d.]+) worst=([\d.]+)", text):
                            writer.writerow([repeat, count, phase, label, system, p50, p95, worst])
                        summary.flush()
                        print(name, "PASS", flush=True)


if __name__ == "__main__":
    main()
