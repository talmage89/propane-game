# Profiling crowded effects

How to measure the cost of vents, smoke, blasts and chains in a crowd of tanks. The results the method produced, and
the optimizations they led to, are dated records in the [crowded effect profiling report](../evidence/vent-performance.md).
For the bot and tank grid use [measuring scale](measuring-scale.md) instead.

## One Lab measurement

The Lab `measure` scenario builds a stack of 24 to 1,024 tanks in the void (the shipped menus stop at 512), waits 180
warm-up frames, then samples 600 real frames. It loads the game's baked tuning at seed 23 and prints wall frame time,
GPU render time, render CPU, process and physics time, object counts, pipeline-compilation monitors, managed
allocations and collection counts. Opt-in `EffectProfile` timers are inclusive (`Blast` includes the vents created by
chain hits) and change no simulation decision. The trigger is part of the first wall sample.

```sh
make build
godot-mono --disable-vsync --resolution 1920x1080 --path . res://scenes/dev/lab_test.tscn -- \
  --scenario=measure --count=128 --phase=blast --windowed --metrics="$PWD/build/pv/blast-128.csv"
```

- `--phase=idle|blast|chain`: nothing, two shots into the intact stack, or first puncturing every tank for a
  deliberately harsh primed chain.
- `--metrics=<absolute path>` writes the raw per-frame CSV.
- `--profile-hide=smoke|particles|lights|shadows|decals` removes one kind of effect for a controlled rendering
  comparison. Only effect nodes created after the measurement starts are excluded, and the sun is kept. These runs are
  GPU evidence only.
- `--scenario=effects --count=1|8|128 --capture-dir=<absolute path>` captures a single vent or a primed chain. Add
  `--fixed-fps 60` for visual comparisons and leave it out for throughput.

Run measurements with no other rendered scenes or heavy CPU tests. The headless gamescope backend can fall back to 60 Hz
VSync despite `--disable-vsync`; GPU timings stay useful when wall frames are capped. GPU readings are delayed relative
to script timers, and Performance phase monitors can repeat across samples, so use direct timers for an individual
CPU spike. Jolt's active-body and contact monitors can return zero: that is missing telemetry, not zero contacts. Rendered
harnesses also accept `--frame-metrics=<directory>` for per-process CSV telemetry after 180 startup frames, with
monotonic timestamps and explosion counts, which locates the largest rendered burst.

## Before and after

Compare a candidate with a baseline built from a common revision, alternating the two back to back:

```sh
python3 -B tools/dev/prepare_vent_baseline.py --ref=<common revision> --output="$PWD/build/pv-before"
python3 -B tools/dev/measure_vents.py --baseline=build/pv-before --output="$PWD/build/pv-measurements"
```

`prepare_vent_baseline.py` archives the revision into a new directory under this checkout's `build/`, applies
`docs/evidence/perf-vents/baseline-instrumentation.patch` (timers on the original effect paths, written against dev
`729c8c8`, so another revision may need it regenerated) and copies the current dev harness, with no optimized effect,
tuning or warm-up code, then runs `make setup` there. `measure_vents.py` alternates the baseline and the current build
twice (`--repeats`) at 32, 64, 128 and 40 tanks (`--counts`, 24 to 1,024) in the idle, blast and chain phases
(`--phases`). It keeps the raw samples of every run, the engine logs, the machine load before and after, and a summary
CSV, and it aborts on a missing GPU sample, an engine error or a failed scene. Alternate runs, record `uptime`, and treat
wall results under shared load, especially chain spikes, as indicative only.

For a repeatable CPU comparison of bot runs, `--replay-random` seeds the physical-event generators and turns wall-clock
hitstop off (see [the Lab](../lab.md#verification)).
