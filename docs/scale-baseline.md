# Scale baseline

Baseline for dev `91c97bb`, before bot/tank optimization, with measurement-only commits through `c167cd6`.
The two portable reports retain all 40 cases, three repetitions per case, stage summaries and load readings:
[Debug](scale-baseline/Debug.json) and [Release](scale-baseline/Release.json).

Measured 2026-10-14T04:42:41.967366+00:00 through 2026-10-14T06:26:14.385297+00:00 (last run start), on Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz (12 logical CPUs),
NVIDIA GeForce GTX 1660, Godot 4.7.2-stable (arch_linux), .NET 10.0.12, Linux.
One-minute load at run start ranged 1.21–32.96, median 5.24.
Other agents were active. Treat these as a baseline to remeasure quietly, not hardware capacity promises.

Command: `make bench CONFIG=Debug,Release SECONDS=10 WARMUP=2 REPEATS=3 BENCH_ARGS="--capture --output=build/bench/baseline.json"`.
This is 240 separate processes. Debug/Release alternate within each case/repetition, reversing first build on
odd repetitions. Seed 23, fixed 254 m map, 40/80/160/320 tanks, 120 Hz physics, 1280×720 overview.
The scene plays during warm-up, then punctures every fourth surviving tank halfway through measurement.
All runs passed assembly/optimization, uncapped pacing, exact observed tick/brain counts and JSON validation.
Stopped and preliminary capped runs are excluded. [Measurement definitions and reproduction](measuring-scale.md).

## Where the time goes

Values below are medians, written **Debug/Release**. Physics, brain, query wrappers, native bound and tank
scripts are ms per physics tick. Navigation is a share of brain time; scopes are inclusive. GC is pause time
as a percentage of elapsed wall time. Do not add GPU time or GC pauses to inclusive CPU scopes.

| Headless bots | Tanks | Physics | Brain | Nav % | Queries | Jolt ≤ | Tank script | GC % |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 160 | 1.17/1.09 | 0.00/0.00 | 0.0/0.0 | 0.00/0.00 | 0.21/0.20 | 0.17/0.16 | 0.5/0.8 |
| 0 | 320 | 2.06/1.91 | 0.00/0.00 | 0.0/0.0 | 0.00/0.00 | 0.26/0.26 | 0.32/0.30 | 0.4/0.6 |
| 8 | 40 | 18.02/16.55 | 16.17/14.73 | 97.2/97.1 | 11.96/11.88 | 0.98/0.99 | 0.04/0.04 | 13.9/14.3 |
| 16 | 40 | 29.97/24.66 | 26.48/21.22 | 94.3/95.6 | 19.46/17.06 | 1.85/1.83 | 0.04/0.03 | 13.5/14.4 |
| 8 | 160 | 13.83/12.71 | 8.92/9.40 | 86.7/87.7 | 3.80/5.01 | 2.35/2.34 | 0.19/0.18 | 6.9/8.8 |
| 8 | 320 | 33.94/25.14 | 27.64/19.19 | 89.5/87.1 | 8.48/7.56 | 3.30/3.15 | 0.42/0.39 | 5.5/7.0 |
| 16 | 160 | 21.45/19.75 | 16.07/14.78 | 90.7/89.1 | 7.40/7.70 | 3.07/3.02 | 0.13/0.13 | 7.4/8.7 |
| 16 | 320 | 43.99/34.07 | 36.53/27.69 | 87.9/87.4 | 10.18/10.23 | 4.48/3.40 | 0.31/0.27 | 5.1/6.7 |

Rendered runs include the same simulation plus the overview. GPU and vent values are ms per process
frame; CPU and GPU overlap. FPS includes catch-up, pauses and presentation. RSS is final resident MiB.

| Rendered bots | Tanks | FPS | Wall p95 ms | GPU ms | Vent script ms | RSS MiB |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 160 | 203.9/150.8 | 6.7/14.7 | 4.55/4.57 | 0.050/0.051 | 1149/1139 |
| 0 | 320 | 151.8/157.2 | 12.8/11.9 | 4.57/4.58 | 0.046/0.046 | 1208/1152 |
| 8 | 40 | 4.7/4.9 | 342.1/314.1 | 30.63/28.84 | 0.008/0.008 | 1228/1227 |
| 16 | 40 | 2.5/2.8 | 623.7/594.3 | 13.41/9.39 | 0.015/0.021 | 1208/1236 |
| 8 | 160 | 7.3/10.0 | 272.7/268.5 | 24.72/16.48 | 0.052/0.042 | 1260/1241 |
| 8 | 320 | 2.8/3.2 | 596.0/502.0 | 9.40/9.02 | 0.081/0.099 | 1291/1269 |
| 16 | 160 | 3.3/4.3 | 457.1/394.9 | 42.94/34.98 | 0.047/0.044 | 1306/1298 |
| 16 | 320 | 2.0/2.4 | 840.1/664.0 | 47.43/47.50 | 0.085/0.077 | 1288/1275 |

## Hot spots and limits

- `src/Bots/BankingBot.cs`, `Think` → `Navigate`: the dominant measured physics cost with many bots.
  `Choose` is also timed in the raw effect dictionary. Navigation includes hazard collection, route searches
  and collision query wrappers; its inner loops call `src/Navigation/BotNavigator.cs`, `Navigate`, and
  its nested `RouteSearch.Advance`. Those inner methods are not individually timed.
- `src/Navigation/NavigationWorld.cs`, `Clear` and `Support`: timed capsule intersection/cast and support-ray
  wrappers. Their inclusive time includes setup, native calls and result marshaling; query counts are in JSON.
  Query reuse/caching and route validation frequency are the most direct bot follow-up.
- `src/Tank/PropaneTank.cs`, `_PhysicsProcess`: tank script time grows with supply, but is much smaller than
  brain cost in the heavy bot cases. `src/Tank/VentJet.cs`, `_Process`, and
  `src/Fx/VentLightBudget.cs`, `_Process`, cover vent presentation. Other explosion/blast/debris scopes remain
  available per stage in the reports.
- Native Jolt/scene work and rendering matter after brains: the native tail is an upper bound, not an exact
  solver timer. The physics callback interval also includes actors, gather scans and engine node updates;
  its residual is not attributed to a particular method. No installed dotnet trace/counter tools were found.
- GC pauses are measured directly. Allocation totals cover all managed work during measurement; these timers
  do not prove which method allocated each byte. Zero-bot runs expose the instrumentation/actor floor.

The grid is not monotonic: more piles change available targets/routes and the generator chooses starts/ammo
against that supply. Bot counts share each tank case’s identical plan, starts and ammo. Native physics and
frame-boundary warm-up/catch-up introduce variation even with a fixed seed; actual ticks and remaining tanks
are retained. Compare all repetitions and workload hashes before judging a branch.

Release means optimized C# on the same editor engine, not an exported release engine. Exact internal Jolt
solver attribution and a quiet-machine capacity claim remain unverified. Production rules, tuners and
`Protocol.Version` (2) are unchanged.

Raw CSV/logs and screenshots remain in `build/bench/baseline-raw/` in the measuring worktree; `evidence` paths
in the portable reports are relative to the original `build/bench/` report directory. They are not release
assets or versioned build output.
