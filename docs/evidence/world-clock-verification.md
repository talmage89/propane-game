# World clock allocation verification

**Status:** Dated record (2026-10-16, refactor package B6). Not superseded. The clock it verifies is described in
[world-clock.md](../world-clock.md).

B6, October 16, 2026. Production parent: `1019775`; transcript/benchmark scaffolding was committed first as
`261f2f9`, while production still used the parent clock. Godot 4.7.2, .NET SDK 10.0.112, Linux, normal Debug and
optimized Release. No release fixtures were changed. Raw evidence and temporary measurement drivers remain in
`build/rfb6/` (not packaged or committed).

## Semantics and allocation

The five callback transcripts passed on the parent before production changes. They cover time and zero-delay
eligibility, insertion order, pending visibility, cancellation by an earlier callback, three active dispatch depths,
disposal, original exception propagation, and recovery with unvisited work still pending. A 1,024-timer dense test
adds alternating cancellation and next-advance work. All 24 final native benchmark runs reproduced the same
transcripts exactly and passed all six dispatch tests on both implementations.

The warmed allocation assertion uses three separately populated 1,024-timer windows after warm-up. Each includes a
nested dispatch, pending scans before/after dispatch, canceled future work and a scan with no due work. A deliberate
`GC.KeepAlive(new byte[1])` per nonempty dispatch failed with **96 bytes** in the measured window and process exit 1.
It was removed before final verification. Scheduling remains outside this assertion's measured window.

## Burst measurements

Each native sample warms 2,000 ticks, then measures 100,000 ticks with 256 zero-delay callbacks per tick. Runs
alternate parent then candidate, three pairs per configuration and rendering mode, with identical instrumentation.
The final series pins its process and engine threads to CPU 11. Native commands, after building each source:

```sh
make build
# Normal Debug; omit --headless for the rendered counterpart.
taskset -c 11 godot-mono --headless --disable-vsync --resolution 1280x720 --path . \
  res://scenes/dev/unit_tests.tscn -- --suite=ClockDispatchTests --clock-bench
```

Release uses the repository's `source_run.assembly("Release")` context to build/swap the editor assembly and
restore Debug afterward. `build/rfb6/final-pairs.py` retains the exact driver; `final-pair-*` logs retain every run.
Dispatch includes `Advance` plus `PendingCount`. Whole-clock tick also includes scheduling; neither includes Jolt,
other nodes or an entire scene frame. Every parent dispatch allocated 4,360 bytes; every candidate dispatch allocated
0 bytes. Whole-clock tick allocations were 12,552 versus 8,192 bytes, the latter being the retained timer allocations.

Times below are **mean/p95/p99 microseconds**. Headless and rendered refer to the enclosing engine process; the
controlled clock loop itself does not advance rendering or physics.

| Build/mode/pair | Parent dispatch | Candidate dispatch | Parent whole tick | Candidate whole tick |
| --- | --- | --- | --- | --- |
| Debug/headless/1 | 18.818/27.948/32.912 | 17.347/18.309/29.502 | 24.988/36.307/42.096 | 23.116/24.216/38.644 |
| Debug/headless/2 | 17.677/19.179/32.917 | 17.202/18.122/27.969 | 23.468/25.034/41.573 | 22.940/23.869/38.509 |
| Debug/headless/3 | 21.687/31.599/34.047 | 17.322/18.287/23.777 | 28.271/40.223/43.426 | 23.032/24.043/36.438 |
| Debug/rendered/1 | 19.846/31.481/38.892 | 17.978/25.544/33.076 | 26.302/40.125/52.325 | 24.052/33.739/43.539 |
| Debug/rendered/2 | 18.241/26.274/35.310 | 18.621/29.252/33.699 | 24.234/34.543/46.383 | 24.924/37.690/45.384 |
| Debug/rendered/3 | 19.521/31.329/38.296 | 18.771/30.032/39.263 | 25.821/39.973/51.849 | 25.135/39.147/53.781 |
| Release/headless/1 | 14.236/21.776/22.685 | 12.315/18.037/19.470 | 17.109/25.761/26.826 | 14.941/21.668/23.403 |
| Release/headless/2 | 13.945/20.340/22.377 | 11.807/17.523/20.183 | 16.627/24.044/26.422 | 14.405/21.122/24.272 |
| Release/headless/3 | 13.754/19.897/22.743 | 11.074/11.256/13.334 | 16.436/23.660/26.886 | 13.379/13.577/19.296 |
| Release/rendered/1 | 15.367/22.024/30.892 | 11.435/11.387/23.027 | 18.413/26.090/37.860 | 14.040/14.830/28.984 |
| Release/rendered/2 | 13.239/13.606/27.726 | 12.194/19.011/22.454 | 15.861/17.126/32.282 | 14.886/23.105/26.391 |
| Release/rendered/3 | 13.654/18.010/26.950 | 11.494/11.503/21.170 | 16.245/21.618/31.448 | 14.109/14.328/25.274 |

Concurrent compiler/game workloads made cross-process timing noisy, including on the pinned core's sibling.
An additional plain .NET driver therefore compiles copies of both clocks into one executable, changes only their
namespaces and adds the same `IClock` interface to each. It alternates **individual ticks** (also reversing which
version runs first on each tick) for three 100,000-tick pairs, again after 2,000 warm-up ticks. This controls drift
between adjacent parent/candidate samples. It uses CPU 11 and `DOTNET_TieredCompilation=0`, with identical Debug
or Release compilation settings. Its scheduling, dispatch and timing windows match the native benchmark; it
asserts callback totals and no retained pending work. Source and driver: `build/rfb6/paired-clock/`; results:
`paired-final-{debug,release}.log`. This supplements the native runs; it does not measure engine tick performance.

| Build/pair | Parent dispatch | Candidate dispatch | Parent whole tick | Candidate whole tick |
| --- | --- | --- | --- | --- |
| Debug/1 | 23.498/33.805/35.659 | 22.561/32.554/34.173 | 31.279/44.265/46.660 | 30.678/43.977/45.948 |
| Debug/2 | 19.645/29.509/33.253 | 19.145/28.608/31.913 | 26.410/38.856/43.648 | 25.993/38.401/42.976 |
| Debug/3 | 19.075/23.260/32.714 | 18.824/22.545/31.515 | 25.731/31.364/42.985 | 25.508/30.703/42.421 |
| Release/1 | 15.540/17.909/25.542 | 13.847/15.897/23.473 | 18.843/21.955/31.877 | 17.112/19.897/29.227 |
| Release/2 | 16.051/23.571/26.263 | 14.425/21.821/24.162 | 19.481/29.013/32.577 | 17.830/27.052/29.891 |
| Release/3 | 25.117/27.794/33.927 | 22.769/25.640/31.114 | 30.621/34.031/42.712 | 28.456/31.567/39.384 |

The controlled final comparison improves mean, p95 and p99 for both dispatch and whole-clock tick in all three
pairs in both builds. Whole-clock mean improves about 1–2% in Debug and 7–10% in Release. No new steady allocation
or repeatable controlled cost regression remains.

Earlier attempts are retained: `pair-*` is the initial 10,000-tick series; `long-pair-*` is the 100,000-tick series
using list enumerators and CPU 10. Cross-process costs varied substantially. The per-tick console comparison of
that version (`paired-*-foreach.log`) revealed a repeatable roughly 2% Debug whole-tick increase, despite Release
improving. Indexed loops with cached snapshot lengths removed it. The intermediate indexed version without cached
lengths is retained in `paired-indexed-debug.log`. These attempts are evidence, not final-code passes.

## Whole-scene supplement

The existing Lab `measure` scenario additionally ran three alternating parent/candidate pairs in both headless
and rendered Debug processes (12 runs), without fixed FPS. Each starts 160 tanks, warms 180 frames plus three
simulation seconds, then measures a primed chain for four simulation seconds. Physics entropy is seeded and
hitstop is zero in both versions. Rendered runs use `DISABLE_GAMESCOPE_WSI=1`, disabled VSync and 1280×720. Driver,
raw frame CSV and logs are retained as `build/rfb6/frame-pairs.py` and `frame-*`.

```sh
godot-mono --headless --disable-vsync --resolution 1280x720 --path . res://scenes/dev/lab_test.tscn -- \
  --scenario=measure --count=160 --phase=chain --measure-warmup=3 --measure-seconds=4 \
  --replay-random --tune.HitstopDuration=0 --metrics="$PWD/build/rfb6/frame.csv" \
  --capture-dir="$PWD/build/rfb6-frame"
# Rendered counterpart: omit --headless; set DISABLE_GAMESCOPE_WSI=1.
```

All runs passed, all finished with zero live tanks, and no Godot engine errors were logged. Reviewed the third
parent/candidate rendered `stack_160.png` captures: both show the cleared arena and zero-tank HUD. After source
swaps, the exact final clock source was restored (SHA256
`5e27c0d2207e0a6c8c0b52868dd08cacfe8c525b89943ff16333d79836c1fd34`), rebuilt warning-free and all 11 clock tests
passed again. The earlier full `make check` tested this same source.

Frame percentiles below use nearest rank. Allocation is total managed allocation across the measured window,
including the opt-in metric collector and all effects. Physics is Godot's sampled physics **peak monitor** averaged
across frames, not an isolated solver or per-tick CPU measurement. These are whole-scene context measurements;
only the controlled clock windows above attribute dispatch/tick cost to this refactor.

| Mode/pair/version | Frames | Frame mean/p95/p99 ms | Allocated MiB | Physics monitor mean ms |
| --- | --- | --- | --- | --- |
| headless/1/parent | 794 | 5.062/7.909/15.924 | 18.054 | 8.767 |
| headless/1/candidate | 808 | 4.973/6.921/17.189 | 17.921 | 7.876 |
| headless/2/parent | 526 | 7.743/24.555/88.915 | 16.690 | 12.385 |
| headless/2/candidate | 806 | 4.996/8.278/13.525 | 18.224 | 9.096 |
| headless/3/parent | 770 | 5.220/7.387/16.642 | 15.019 | 8.860 |
| headless/3/candidate | 752 | 5.345/7.500/18.494 | 17.621 | 11.498 |
| rendered/1/parent | 279 | 15.633/19.679/91.601 | 11.318 | 12.018 |
| rendered/1/candidate | 275 | 15.810/19.082/99.867 | 11.264 | 11.953 |
| rendered/2/parent | 145 | 33.357/65.444/166.891 | 10.448 | 19.104 |
| rendered/2/candidate | 263 | 16.737/19.897/105.841 | 11.242 | 13.805 |
| rendered/3/parent | 256 | 16.925/23.807/98.963 | 13.884 | 12.306 |
| rendered/3/candidate | 270 | 16.264/21.734/94.751 | 11.294 | 12.967 |

Whole-scene results vary with concurrent machine load, frame count, Jolt/effect work and collection timing; the
second parent pair is visibly slower in both modes. Allocation totals also vary in either direction. These runs
do not establish an attributable scene-frame improvement or a repeatable scene-frame regression. All raw samples,
including slower runs, are retained. The isolated allocation guarantee applies to warmed clock dispatch/scans,
not to callbacks, the collector or the rest of the game.

## Final verification

Commands used during verification (all scratch/log paths are relative to this checkout):

```sh
make setup
make build
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=Clock
# Run once per chain=fresh and chain=primed:
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/tank_range.tscn -- \
  --formation=Square --count=64 --chain=primed --capture-dir="$PWD/build/rfb6-dense-primed"
make test-equivalence
TMPDIR="$PWD/build/rfb6/tmp" make check
```

- Final build: zero warnings/errors. Focused clock suites: 11 tests, zero failures. Initial transcript scaffolding
  had two compile failures from nonexistent `DevArgs` helpers; those were corrected to `DevArgs.Get` before the
  parent transcript pass. Final negative allocation probe: expected exit 1 and 96 bytes; reverted and rebuilt.
- Both rendered formation runs: PASS, 63 neighbors each. Primed neighbors wait for their fuses and then detonate;
  fresh neighbors remain venting. Inspected `rfb6-dense-primed/{00_placed,02_chain,03_chain}.png` and
  `rfb6-dense-fresh/{02_chain,03_chain}.png` under `build/`. Placement is intact; the primed dense blast fills the
  camera with flash/smoke, and the fresh blast leaves flying venting tanks. Assertions establish exact fuse
  completion where flash and smoke obscure the image.
- Parent `check_equivalence.py --output="$PWD/build/rfb6/parent-equivalence"`: PASS. Final `make test-equivalence`:
  six checker self-tests pass; release wire/schema/world/publication and all five Lab cases pass. The five parent
  JSON files and final `build/rfp0-equivalence-2/run-1/` files are byte-for-byte equal: Bank 4,801 ticks, King 5,441,
  spill/chain 4,153, Short Fuse 1,779 and Dissolve eight controlled retirement boundaries.
- `make check`: PASS, including formatting, the warnings-as-errors rebuild (zero warnings/errors), all 463 unit
  tests, native behavior/input scenes, bounded generator and separate-process world/segment checks, and all nine
  infrastructure gate self-tests. `make test` runs inside this gate.
- `git diff --check`: PASS. No deliberate probe or fixture refresh remains.

## Limits

Buffer capacity retains the largest observed burst at each active dispatch depth; cleared buffers retain no timer
references. A new high-water mark can allocate. Scheduling still allocates, and list removal remains quadratic.
Controlled clock measurements do not establish cross-platform timing or an attributable whole-scene frame benefit.
Simulation rules, tuners, generated worlds, wire formats and `Protocol.Version` are unchanged.
