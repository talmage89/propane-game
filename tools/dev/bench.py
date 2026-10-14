#!/usr/bin/env python3
"""Repeat the scale grid; retain raw samples and print medians of each run's statistics."""
import argparse
import csv
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import platform
import signal
import statistics
import subprocess

from source_run import ROOT, assembly, godot, interrupted

SCHEMA = 3
METRICS = ("fps", "wall_p50_ms", "wall_p95_ms", "wall_p99_ms", "wall_max_ms", "process_ms", "physics_ms",
           "process_peak_monitor_ms", "physics_peak_monitor_ms", "brain_tick_ms", "brain_bot_tick_ms", "navigation_tick_ms", "navigation_share_pct", "navigation_queries_tick_ms", "navigation_queries_per_tick",
           "jolt_step_upper_ms", "gpu_ms", "tank_tick_ms", "vent_frame_ms", "gc_count", "gc_pause_ms", "rss_mib")


def positive(value):
    parsed = float(value)
    if not math.isfinite(parsed) or parsed <= 0:
        raise argparse.ArgumentTypeError("must be a finite positive number")
    return parsed


def grid(value, allowed):
    result = [int(v) for v in value.split(",")]
    if not result or len(set(result)) != len(result) or any(v not in allowed for v in result):
        raise argparse.ArgumentTypeError(f"choose comma-separated values from {allowed}")
    return result


def percentile(values, fraction):
    values = sorted(values)
    return values[max(0, math.ceil(fraction * len(values)) - 1)]


def summarize(rows, bots):
    if len(rows) < 2:
        raise ValueError("too few measured frames")
    wall = [r["wall"] for r in rows]
    ticks = sum(r["physics_steps"] for r in rows)
    if ticks <= 0 or min(wall) <= 0:
        raise ValueError("missing physics ticks or invalid wall clock")
    mean = lambda key: statistics.mean(r[key] for r in rows)
    tick = lambda key: sum(r[key] for r in rows) / ticks
    thinks = sum(r["brain_calls"] for r in rows)
    brain, navigation = tick("BotBrain"), tick("BotNavigation")
    result = dict(fps=1000 / statistics.mean(wall), wall_p50_ms=percentile(wall, .50),
                  wall_p95_ms=percentile(wall, .95), wall_p99_ms=percentile(wall, .99), wall_max_ms=max(wall),
                  process_ms=mean("process_callbacks"), physics_ms=tick("physics_callbacks") + tick("physics_tail"), gpu_ms=mean("gpu"),
                  process_peak_monitor_ms=mean("process"), physics_peak_monitor_ms=mean("physics"),
                  brain_tick_ms=brain, brain_bot_tick_ms=sum(r["BotBrain"] for r in rows) / thinks if thinks else 0,
                  navigation_tick_ms=navigation, navigation_share_pct=100 * navigation / brain if brain else 0,
                  navigation_queries_tick_ms=tick("NavigationSweep") + tick("NavigationSupport"),
                  navigation_queries_per_tick=tick("navigation_sweep_calls") + tick("navigation_support_calls"),
                  jolt_step_upper_ms=tick("physics_tail"), tank_tick_ms=tick("TankPhysics"), vent_frame_ms=mean("VentProcess"))
    result["effects_ms_per_frame"] = {key: mean(key) for key in rows[0] if not key.startswith("Actor") and key not in
                                      ("frame", "wall", "gpu", "process", "physics", "time", "blast_count", "explosion_count",
                                       "physics_steps", "physics_tail", "stage", "process_callbacks", "physics_callbacks",
                                       "brain_calls", "navigation_sweep_calls", "navigation_support_calls")}
    actor_systems = [key for key in rows[0] if key.startswith("Actor") and not key.endswith(("Bytes", "Calls"))]
    result["actors"] = {key[5:]: dict(ms_per_frame=mean(key), ms_per_tick=tick(key),
                                    allocated_bytes=sum(r[key + "Bytes"] for r in rows),
                                    calls=sum(r[key + "Calls"] for r in rows)) for key in actor_systems}
    result["brain_calls"] = thinks
    result["frames"] = len(rows)
    result["sampled_physics_ticks"] = ticks
    return result


def load_case(directory, bots, config):
    summary = json.loads((directory / "summary.json").read_text())
    if summary["assembly_config"] != config or summary["assembly_optimized"] != (config == "Release"):
        raise ValueError("wrong loaded assembly configuration")
    if summary["headless_idle_usec"] or summary["max_fps"] or summary["frame_delay_ms"]:
        raise ValueError("engine frame pacing was not disabled")
    files = list(directory.glob("frames-*.csv"))
    if len(files) != 1:
        raise ValueError("expected one frame metrics stream")
    with files[0].open() as stream:
        rows = [{k: float(v) for k, v in row.items()} for row in csv.DictReader(stream)]
    if any(not math.isfinite(v) for row in rows for v in row.values()):
        raise ValueError("non-finite frame metrics")
    result = summarize(rows, bots)
    if result["sampled_physics_ticks"] != summary["physics_ticks"] or result["brain_calls"] != bots * summary["physics_ticks"]:
        raise ValueError("measured ticks/brain calls differ from the real input workload")
    if summary["bots"] != bots or not summary["requested_physics_ticks"] <= summary["physics_ticks"] < summary["requested_physics_ticks"] + 12:
        raise ValueError("wrong actor count or measurement interval")
    result.update(summary)
    result.update(gc_count=summary["gc_counts"][0], rss_mib=summary["rss_bytes"] / 2**20)
    result["stages"] = {name: summarize([r for r in rows if r["stage"] == stage], bots)
                        for stage, name in enumerate(("intact", "disturbed"))}
    return result


def median_runs(runs):
    return {key: statistics.median(r[key] for r in runs) for key in METRICS}


def validate_log(log):
    if "[bench] PASS" not in log or "ERROR:" in log or "FAIL" in log:
        raise ValueError("benchmark scene failed")
    if "Falling back to V-Sync mode Enabled" in log:
        raise ValueError("vsync could not be disabled; capped measurements are invalid")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bots", type=lambda s: grid(s, (0, 2, 4, 8, 16)), default=[0, 2, 4, 8, 16])
    parser.add_argument("--tanks", type=lambda s: grid(s, (1, 2, 4, 8)), default=[1, 2, 4, 8])
    parser.add_argument("--config", default="Debug", help="Debug, Release, or Debug,Release to alternate each repetition")
    parser.add_argument("--rendered", type=lambda s: grid(s, (0, 1)), default=[0, 1])
    parser.add_argument("--seconds", type=positive, default=10)
    parser.add_argument("--warmup", type=positive, default=2)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--capture", action="store_true")
    parser.add_argument("--omit", choices=("none", "actors", "shadows", "effects", "debris"), default="none")
    args = parser.parse_args()
    configs = args.config.split(",")
    if len(set(configs)) != len(configs) or any(c not in ("Debug", "Release") for c in configs):
        parser.error("config must be Debug, Release or Debug,Release")
    if args.repeats < 1 or args.seconds < .5 or args.seconds + args.warmup >= 180:
        parser.error("repeats must be positive; seconds must be at least 0.5 and seconds + warmup below 180")
    signal.signal(signal.SIGTERM, interrupted)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    output = (args.output or ROOT / f"build/bench/{stamp}-{args.config}.json").resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    raw = output.parent / (output.stem + "-raw")
    raw.mkdir()  # Refuse to overwrite raw evidence.
    # Keep the shim's invisible compositor, but bypass the Vulkan layer that exposes only FIFO on NVIDIA.
    env = dict(os.environ, TMPDIR=str(raw), DISABLE_GAMESCOPE_WSI="1")
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip())
    documents = {config: dict(schema=SCHEMA, revision=revision, dirty=dirty, config=config, seed=23,
                              seconds=args.seconds, warmup=args.warmup, repeats=args.repeats,
                              resolution="1280x720", presentation="no-idle-wait-vsync-disabled-gamescope-wsi-disabled", omitted_render_feature=args.omit, platform=platform.platform(), paired_configs=configs, cases=[])
                 for config in configs}
    outputs = {config: output if len(configs) == 1 else output.with_stem(output.stem + "-" + config) for config in configs}
    print("cfg     view bots tank×   FPS   wall p50/p95/p99/max ms      proc phys brain/tick nav% query Jolt≤ GPU tank vent GC pause RSS", flush=True)
    for config in configs:
        with assembly(config):
            pass  # Build once; subsequent swaps reuse these exact assemblies.
    for rendered in args.rendered:
        for bots in args.bots:
            for tanks in args.tanks:
                runs = {config: [] for config in configs}
                for repeat in range(args.repeats):
                    # Reverse who goes first on odd repetitions to reduce systematic order bias.
                    for config in configs if repeat % 2 == 0 else configs[::-1]:
                        directory = raw / f"{config}-r{rendered}-b{bots}-t{tanks}-{repeat}"
                        directory.mkdir()
                        load = subprocess.check_output(["uptime"], text=True).strip()
                        started = datetime.now(timezone.utc).isoformat()
                        command = [godot(), "--path", str(ROOT), "--disable-vsync", "--max-fps", "0", "--frame-delay", "0", "--resolution", "1280x720"]
                        if not rendered:
                            command.append("--headless")
                        command += ["res://scenes/dev/scale_bench.tscn", "--", f"--bots={bots}", f"--tanks={tanks}",
                                    f"--config={config}", f"--seconds={args.seconds}", f"--warmup={args.warmup}",
                                    f"--frame-metrics={directory}", f"--omit={args.omit}"]
                        if args.capture and rendered and repeat == 0:
                            command.append(f"--capture-dir={directory}")
                        with assembly(config, build=False), (directory / "run.log").open("w") as log:
                            subprocess.run(command, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
                                           timeout=max(120, (args.seconds + args.warmup) * 60), check=True)
                        log = (directory / "run.log").read_text()
                        try:
                            validate_log(log)
                        except ValueError as error:
                            raise RuntimeError(f"{error}; see {directory / 'run.log'}") from error
                        result = load_case(directory, bots, config)
                        result.update(uptime=load, uptime_end=subprocess.check_output(["uptime"], text=True).strip(),
                                      started_utc=started, evidence=str(directory.relative_to(output.parent)))
                        runs[config].append(result)
                for config in configs:
                    median = median_runs(runs[config])
                    documents[config]["cases"].append(dict(bots=bots, tank_multiplier=tanks, rendered=bool(rendered),
                                                          median=median, runs=runs[config]))
                    outputs[config].write_text(json.dumps(documents[config], indent=2) + "\n")
                    m = median
                    print(f"{config:7} {rendered:4} {bots:4} {tanks:5} {m['fps']:6.1f} "
                          + "/".join(f"{m[k]:.2f}" for k in METRICS[1:5]) + "  "
                          + " ".join(f"{m[k]:.2f}" for k in ("process_ms", "physics_ms", "brain_tick_ms", "navigation_share_pct",
                                                            "navigation_queries_tick_ms", "jolt_step_upper_ms", "gpu_ms", "tank_tick_ms",
                                                            "vent_frame_ms", "gc_count", "gc_pause_ms", "rss_mib")), flush=True)
    for path in outputs.values():
        print(f"JSON: {path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
