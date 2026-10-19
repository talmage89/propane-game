# Release performance: v0.4.2 versus the v0.5 candidate (P6)

**Status:** paired comparison complete. Frame time, tick time and allocation are equal or better on four of the
seven workloads. Three findings remain open: more frequent wall-frame hitches in the 16-actor headless workload, a
small consistent four-client rendered cost increase, and +10% Lab bot-session allocation, bisected to a
per-read prototype view copy. None changes a frame-time mean or median.

2026-10-19, Linux x86-64 (12 cores), GTX 1660, Godot 4.7.2 .NET/Jolt, Debug builds from `make setup`. Baseline:
`v0.4.2` (`d54d3df`) in a separate detached worktree. Candidate: `5b03f1a` (FU2b landed) in another. No other
agent ran during measurement. Lab rendered runs used the invisible gamescope wrapper with
`DISABLE_GAMESCOPE_WSI=1`. Raw data: [all attempts](release-performance/runs.json),
[16-actor wall-frame hitches](release-performance/hitches-16h.csv) and
[allocation bisection](release-performance/allocation-bisect.csv). Full logs and frame CSVs stay in the lead's
local `build/p6/`.

## Workloads and harness identity

- **P3 native (net_bots)**, as in the [P3 method](body-publication-verification.md#native-timing-method):
  `tools/dev/net_bots.sh -n CLIENTS -l 35 -w RENDERED -t clean -s '--match-seed=SEED --tune.MaxPlayers=16' -o DIR --
  --tank-supply=1 --lobby-bots=BOTS --melee-start --net-seed=2 --frame-metrics=DIR/frames`, cases 16h (2 clients,
  7 lobby bots each, seed 301), 4h and 4r (4 clients, seed 302; 4r renders one client). The window is the clients'
  common playing interval minus its first ten seconds (about 25 s per client).
- **P2 tracking (Lab Dissolve)**, as in the [P2 method](lab-tracking-verification.md#whole-session-measurements):
  `godot-mono --fixed-fps 60 --disable-vsync --resolution 960x540 [--headless] --path PROJECT
  res://scenes/dev/lab_test.tscn -- --scenario=dissolve --seconds=300 --first-seed=11 --census-every=2
  --tracking-metrics --replay-random --tune.HitstopDuration=0 --metrics=FILE [--driver=pace]`, headless and
  rendered, bot and pacer.

The measuring code is identical in both builds: `FrameMetrics` only moved namespace (`src/Dev` to
`src/ReleaseSmoke`) and reads its flag through `LaunchArgs`; `net_bots.sh` and the `[lab whole-session]` and
`[lab dissolve]` output lines are unchanged. Two harness differences apply to the candidate only: `NetTest`
composes the client and server through the post-refactor constructors, and the Dissolve scenario enables
`DissolveArena.CollectRawTimings` by reflection (a stopwatch per segment build/retire step, about 50 per run).

Pairs alternate which build runs first (pair 1 baseline first, pair 2 candidate first, pair 3 baseline first). Each
run waited for 20 s with one-minute load below 2.7 and no foreign engine process. A run is accepted when it passes
(net_bots exit 0 with complete audits; Lab `[lab] PASS`), saw no foreign engine process, and stayed below load 4:
over the measured window for P3 (P3's rule) and over the whole run for P2. Load was sampled every second; uptime
at each start is in `runs.json`.

**Attempts:** 46 completed runs, all passing their game assertions. Two pairs (16h pair 1, headless-bot pair 1) were
retained but rerun because the sampler saw an engine process whose working directory it could not read; it was
probably one of this script's own exiting children. An earlier launch of the matrix failed before any game started
(the script put its own log inside net_bots' output directory, which net_bots correctly refused); those 20
zero-second attempts are kept apart as invalid launches and are not samples. Accepted: 3 pairs for every workload.
Every accepted P3 window stayed at or below load 2.77.

## Engine monitor columns are per-second maxima

`FrameMetrics`' `process` and `physics` columns sample Godot's `TIME_PROCESS` and `TIME_PHYSICS_PROCESS` monitors.
Godot refreshes these once per second with the largest frame's value, so a CSV shows constant one-second plateaus.
They are worst-frame-per-second values, not per-frame times. Their "mean" is a mean of per-second maxima, and their
p95/p99 are set by one or two seconds out of about 25 per client. The `wall` column is the real per-frame interval.
The tables below report the monitors for continuity with P3; verdicts rest on `wall`, allocation and the Lab tick
timers. Earlier P3 tables used the same columns and inherit this interpretation.

## P3 native results

Medians over three accepted pairs, baseline → candidate. Monitor columns are means of per-second maxima (ms); wall
is per frame (ms); allocation is the sum of every client's `match gc` total (MB). "All pairs" marks a difference
with the same sign in every pair.

| Case | Monitor physics / process | Wall mean / p95 / p99 | Allocated MB | Frames | Blasts | Max window load |
| --- | --- | --- | --- | --- | --- | --- |
| 16h | 6.22 / 4.89 → 5.92 / 5.70 | 16.67 / 18.01 / 20.01 → 16.67 / 18.04 / 20.34 | 447.2 → 408.5 (all pairs lower) | 2,989 → 2,991 | 140 → 126 | 1.96 → 1.30 |
| 4h | 2.96 / 1.93 → 2.92 / 1.87 | 16.67 / 17.20 / 19.21 → 16.67 / 17.08 / 18.75 (p99 all pairs lower) | 456.4 → 427.8 (all pairs lower) | 5,987 → 5,980 | 100 → 80 | 1.10 → 0.91 |
| 4r | 3.16 / 3.19 → 3.43 / 3.39 (physics all pairs higher) | 16.67 / 17.14 / 19.31 → 16.67 / 17.31 / 19.33 (p95, p99 all pairs higher) | 463.4 → 446.0 | 5,984 → 5,977 | 132 → 96 | 2.41 → 2.22 |

Workloads are not identical: bots and blasts differ by run (16h blasts 84–172 per run), so total allocation is
compared with the blast counts beside it. Every run passed its tank and score audits with zero engine errors.

**Verdicts.**

- 16h: mean and p95 frame time equal. Allocation is lower in every pair, also against similar blast counts.
  Frame-time tails are worse; see the next section.
- 4h: equal or better. Allocation and wall p99 are lower in every pair, although the candidate pairs also had fewer
  blasts.
- 4r: slightly worse, beyond pair noise but small. Wall p95 is higher by 0.06–0.27 ms and the physics monitor by
  0.09–0.71 ms in every pair, despite fewer blasts. The rendered client's GPU time is equal (1.45 → 1.44 ms).

## 16-actor wall-frame hitches

The 16h candidate showed bursts of slow frames. To settle it, six more alternating pairs, four fresh baseline
controls and two diagnostic cohorts were run with the same command:

| Build | Runs | Runs with a frame > 33 ms | Median frames > 25 ms per run | Worst frame |
| --- | ---: | ---: | ---: | ---: |
| v0.4.2 | 14 | 2 | 4 | 40.1 ms |
| candidate `5b03f1a` | 18 | 9 | 7 | 66.6 ms |
| R2 `d519708` (before FU2b) | 4 | 2 | 8 | 43.1 ms |
| L1 `75e76b2` | 4 | 0 | 9 | 32.8 ms |

In the six strictly alternating pairs, the candidate had more frames over 25 ms in five pairs. Over all nine
accepted or extra 16h pairs it had more in six. The baseline control batch also produced one hitching run (17
frames over 25 ms, worst 40.1 ms), so the effect exists before the refactor and becomes more frequent after it.

What the data shows about the cause:

- Hitches strike both client processes at the same frame indices. In a hitch frame every instrumented system (bot
  brain, navigation, actor physics, movement, presentation) slows together by about 1.5–2×, rather than one system
  spiking. Explosion and blast counts are unchanged.
- That pattern fits a machine-wide slowdown in a three-process, one-machine test (CPU or thread-pool contention
  between the server and two Jolt clients), not a single code path. It is not proven.
- The candidate does not run more physics: physics steps per frame are equal. It allocates less in this workload.
- FU2b's reliable checkpoint interval is not the cause. Client-side `--tune.BodyCheckpointInterval=0.25` (verified
  with `--expect-checkpoint`) still hitched in three of four runs, against one of four at the 0.05 s default.
  Server-side overrides of this tuner are ignored, as FU2b recorded.
- The hitches predate FU2b (R2 shows them). L1 already has elevated counts of frames over 25 ms. A bisection would
  need about eight runs per commit to separate the rates, so it was not completed.

Players run one client per machine, so a same-machine contention artifact would not reach them. That remains to be
shown.

## P2 tracking results

Medians over three accepted pairs, baseline → candidate. Whole-tick times are the Lab tick timer (ms). Frame wall
percentiles are the Dissolve report's (ms). Allocation is after assembly.

| Workload | Whole tick mean / p95 / p99 | Frame wall p95 / p99 | Callback bytes/tick | Allocated MiB | Ticks, tanks, meters, built/retired |
| --- | --- | --- | --- | --- | --- |
| Headless bot | 0.378 / 0.60 / 1.58 → 0.377 / 0.61 / 1.60 | 2.2 / 6.2 → 2.3 / 6.2 | 2,270.7 → 2,247.2 (all pairs lower) | 167.2 → 184.0 (all pairs higher) | 37,955, 181, 1,351, 25/23 → 37,973, 180, 1,351, 25/23 |
| Headless pacer | 0.301 / 0.37 / 1.00 → 0.299 / 0.37 / 0.96 (mean all pairs lower) | 1.2 / 5.3 → 1.2 / 5.1 | 2,674.2 → 2,644.5 (all pairs lower) | 173.4 → 171.4 (all pairs lower) | 37,963, 0, 1,799, 32/30 (both) |
| Rendered bot | 0.434 / 0.71 / 2.01 → 0.438 / 0.74 / 2.07 (p95 all pairs higher) | 5.8 / 9.3 → 6.0 / 9.5 | 2,259.9 → 2,230.2 | 166.3 → 184.7 (all pairs higher) | 37,967, 176, 1,355, 25/23 → 37,973, 179, 1,399, 26/23 |
| Rendered pacer | 0.339 / 0.46 / 1.40 → 0.340 / 0.46 / 1.75 (mean, p99 all pairs higher) | 3.8 / 7.6 → 3.8 / 7.6 | 2,675.3 → 2,645.9 (all pairs lower) | 173.4 → 171.3 (all pairs lower) | 37,963, 0, 1,799, 32/30 (both) |

The pacer's workload is identical in every run; the bot's varies (tanks 172–191). One rendered-bot baseline (pair 2)
was legally caught early: 15,705 ticks, 97 tanks, 68 MiB. It is retained in the medians and not censored.

**Verdicts.**

- Pacers: equal frame time and better allocation. Rendered pacer whole-tick p99 rises 1.40 → 1.75 ms in every pair,
  but wall p95/p99 are identical; tails of this size are within P2's earlier variance.
- Bots: tick time equal; rendered whole-tick p95 is 0.03 ms higher in every pair, and wall p95 0.2 ms higher. Total
  session allocation is **10% higher in every pair** (+17 MiB over 300 s, about 57 KB/s), while callback
  bytes/tick fall.

## Lab bot allocation bisection

Each first-parent landing was built in a scratch worktree and run twice with the headless bot command
([data](release-performance/allocation-bisect.csv)):

| Commit | Allocated MiB (two runs) |
| --- | --- |
| v0.4.2 (P6 baseline) | 166.5–168.0 |
| L1 `75e76b2` | 166.9, 170.0 |
| B4b `dfec072` | 166.5, 164.5 |
| B3b2 `5190c07` | 180.2, 179.3 |
| C1 `08d20ae` | 186.3, 183.7 |
| candidate (P6) | 183.1–186.6 |

B3b2 adds about 13 MiB. `PrototypeRunner.View` now builds a fresh mode-state snapshot on every read. For Dissolve,
`DissolveRunRules.State` copies the growing closed-chain list (`closed.ToArray()`), and the prototype HUD reads
`View` every frame. A further roughly 5 MiB arrives between B3b2 and C1 (B3b1, H2 or C1), not yet isolated. Pacer
sessions build no chains, which is consistent with their lower allocation. This cost is per frame in Lab prototype
modes only and shows no frame-time effect in these measurements.

## Limits

Debug builds on one Linux machine; rendered runs used gamescope's headless backend on the GTX 1660. Release builds,
Windows and macOS were not measured. P3's 16h and 4h cases run every process on one machine. The baseline and
candidate trees each hold the post-refactor or pre-refactor harness, so "identical" means the same scenario,
arguments and instrumentation, not the same Dev source.
