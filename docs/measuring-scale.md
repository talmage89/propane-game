# Measuring scale

`make bench` measures the production in-process Tank Bank brains used in the Lab and lobby, rather than separate
Godot bot processes. It runs a deterministic dev scene, saves raw CSV/logs and JSON under `build/bench/`, and prints
one row per case. It is a measuring tool, outside `make test`; the gate runs one fast real-command/JSON smoke.
[The initial dev baseline](scale-baseline.md) includes the full Debug/Release grid and hotspot breakdown.

```sh
make bench                                     # Debug, both views, full grid, three repetitions
make bench CONFIG=Debug,Release                 # Alternate Debug/Release for each repetition of each case
make bench BOTS=8,16 TANKS=4,8 CONFIG=Release RENDERED=1 SECONDS=10
python3 -B tools/dev/bench_compare.py build/bench/a.json build/bench/b.json
```

`BOTS=0,2,4,8,16`, `TANKS=1,2,4,8`, and `RENDERED=0,1` accept comma-separated subsets. `TANKS` is a multiplier
of the baked single-player tank count (currently 40), not a new pile size. Defaults are `SECONDS=10` simulated
seconds, `WARMUP=2`, and `REPEATS=3`. The runner uses 120 Hz physics, unthrottled process frames and disabled
vsync. It also sets the benchmark process's idle sleep, max FPS and fixed frame delay to zero: Godot otherwise
sleeps 6900 microseconds whenever a headless window cannot draw, capping it near 144 FPS even with low processor
mode off ([engine pacing](https://github.com/godotengine/godot/blob/master/core/os/os.cpp)). JSON records and
checks those settings. It never uses `--fixed-fps`, which would hide how much wall time the workload costs.
A process frame may
contain up to twelve physics ticks; the final frame finishes, so JSON records both requested and actual ticks.
Brains and physics play during warm-up, which ends at a frame boundary. Short smoke timings have no statistical significance.

The seed is 23. Every case uses the same 254 m suburb dimensions from the existing 16-player map tuners, the
single-player formation weights/sizes/spacing, seventeen reserved starts and the same ammo supply. This map can
fit the full 320-tank case; the 190 m seed-23 map cannot. More tanks mean more complete piles placed by the normal
generator. Geometry and starts are identical across bot counts; JSON records the serialized plan's SHA-256.
There is one idle player plus the requested number of camera-free bots on the creator's main thread. The 16-bot
case is an offline stress fixture with seventeen actors, beyond the sixteen-actor network lobby ceiling.

The scene uses the real Lab Tank Bank runner, including shooting, banking, spills, ammo pickups, vent thrust,
chain fuses, ragdolls and gathering. No physics, effect or gameplay tuner is reduced. Halfway through measurement,
it punctures every fourth surviving tank through `TakeShot`, exercising tank/vent cost even with zero bots. JSON
has separate `intact` and `disturbed` stage summaries; `intact` means before this scripted disturbance, and bots
can already have punctured/exploded tanks. The fixed overview camera renders at 1280×720. This is a reproducible
scale view, not a prediction of frame rate from the player's shoulder camera. Headless still runs scripts and
physics. Rendering can change process/physics cadence, so compare each view separately.

## Reading the measurements

Each table entry is the median of that statistic across repetitions. Raw frames and individual run summaries
remain beside the JSON. CSV telemetry reuses its sample buffer and formats numbers into a stack buffer to
avoid per-frame managed garbage. Each run records UTC start, `uptime` before/after, engine/.NET versions, CPU, GPU, build
configuration, optimization flag, actual ticks, live tanks remaining, GC generation counts and memory.

| Column / JSON metric | Meaning |
| --- | --- |
| FPS | Frames divided by sampled wall time, including catch-up and waits. |
| wall p50/p95/p99/max | Nearest-rank percentiles and maximum interval between frame samples, in ms. |
| proc / `process_ms` | Mean time from `SceneTree.ProcessFrame` to the last-priority `FrameMetrics` callback; excludes subsequent rendering/engine work and telemetry serialization. |
| phys / `physics_ms` | Mean per tick of the physics callback interval plus the native tail below. |
| brain/tick | Inclusive `BankingBot.Think` time for all bots per physics tick; JSON also gives time per individual brain call. |
| nav% | `BankingBot.Navigate` / `BankingBot.Think`; inclusive of route searches, hazard collection and queries. |
| query | Mean per tick in `NavigationWorld.Clear` capsule sweep/intersection and `Support` ray wrappers; includes query setup/marshaling. JSON also records their call count. |
| Jolt≤ / `jolt_step_upper_ms` | Upper bound from the last physics callback until the next physics/process-frame signal. Includes native Jolt steps, engine queues/bookkeeping, and the next query flush/force integrators. **Not an exact solver timer.** |
| GPU | Godot's measured main-viewport GPU time, ms; zero with the dummy headless renderer. |
| tank | Inclusive `PropaneTank._PhysicsProcess` ms per physics tick. |
| vent | `VentJet._Process` ms per process frame. Other effect scopes are retained in each stage/run JSON. |
| GC / pause | Gen-0 collection count (includes higher-generation collections), and `GC.GetTotalPauseDuration()` delta, ms. JSON preserves all three generation counts; don't add them. |
| RSS | Final process resident memory, MiB; JSON also records peak RSS (including construction), managed heap and allocated bytes during measurement. |

`FrameMetrics` retains the engine's existing `TimeProcess` and `TimePhysicsProcess` monitor values in CSV and JSON
as `process_peak_monitor_ms` and `physics_peak_monitor_ms`. Godot refreshes these once per second with the
largest process frame / physics step seen in that interval, not mean costs. The callback timers provide the means
above. They use elapsed wall time and include descheduling under load. Per-run process CPU seconds help expose
that difference. A profiler-capable engine is needed to split the native tail into exact Jolt solver jobs.
See Godot's [frame loop](https://github.com/godotengine/godot/blob/master/main/main.cpp) and
[Jolt server](https://github.com/godotengine/godot/blob/master/modules/jolt_physics/jolt_physics_server_3d.cpp).

Scopes are inclusive: brain contains navigation, navigation contains queries, and blast contains its subscopes.
Do not add these together or subtract GPU time from wall time as if CPU and GPU ran sequentially. Opt-in
`EffectProfile` timers add measurement overhead; ordinary play does not read clocks/allocations for these scopes.
`Match.BotCost` retains its existing think/allocation counters and accumulates inclusive navigation seconds
when profiling is enabled. No `dotnet-counters` or `dotnet-trace` installation is required.

## Comparing optimization branches

Use identical commands and at least three repetitions. Do not compare different seeds, map hashes, resolutions,
measurement/warm-up durations or grids; `bench_compare.py` refuses these. Debug versus Release is allowed. A
positive FPS delta is better; a negative time/GC/memory delta is better. Keep individual run data: medians alone
can conceal load spikes and changes in how many tanks survive. There are no performance pass/fail thresholds.

On a shared machine, alternate branches back to back for **each case and repetition**, e.g. baseline A, candidate
B, candidate B, baseline A, baseline A, candidate B. Run one Godot measurement process at a time. Separate
worktrees avoid checkout/build churn and preserve each branch's ignored state. Save each run with
`python3 -B tools/dev/bench.py ... --repeats=1 --output=build/bench/<unique-name>.json`; aggregate medians across
matching repetitions, retaining all originals:

```sh
python3 -B tools/dev/bench_compare.py --merge build/bench/a-*.json --output build/bench/a.json
python3 -B tools/dev/bench_compare.py --merge build/bench/b-*.json --output build/bench/b.json
python3 -B tools/dev/bench_compare.py build/bench/a.json build/bench/b.json
```

The merge command also joins case subsets from the same revision and refuses mixed configurations or workloads.
Do not run both branches concurrently. Check their `uptime` and
CPU/GPU metadata, and remeasure on a quiet machine before deciding an optimization works.

For a visual check, use `make bench BENCH_ARGS=--capture` or add `--capture` to the Python command.
`BENCH_ARGS` also forwards `--output=build/bench/<name>.json` to choose a unique artifact name. The first rendered repetition of each case writes
`scale-final.png` after measurement, so screenshot readback/PNG encoding does not contaminate timings. Use
`--capture-dir=$PWD/build/...` when running the scene directly. Rendered runs use the normal `godot-mono` shim
and remain invisible on the desktop. The runner sets `DISABLE_GAMESCOPE_WSI=1` for its child processes: the
shim still runs gamescope's headless compositor, but its optional Vulkan presentation layer no longer forces
Godot's NVIDIA swapchain back to vsync. The runner rejects logs containing that fallback. This is a per-run
environment override; it edits no system/shim configuration. JSON records this presentation policy, and schema 3
rejects earlier prototype runs that could be capped. The layer's
[manifest](https://github.com/ValveSoftware/gamescope/blob/master/layer/VkLayer_FROG_gamescope_wsi.json.in)
defines the environment switch.

## Playing optimized C# locally

`make play` defaults to Release; `make play CONFIG=Debug` selects Debug. `make run` and F5 remain Debug.
`source_run.py` builds `Propane.csproj -c Release` (the solution only lists Godot's Debug/Export configurations),
then temporarily substitutes its output for `.godot/mono/temp/bin/Debug`, the editor engine's hard-coded source
assembly load path. It restores the original Debug directory on normal exit, errors and interrupts. The build
uses normal .NET Release optimization without `DEBUG`, while retaining the Debug Godot API required by the
editor engine. This needs no export/template install and leaves the source project/config unchanged.

This measures optimized C# on the same Godot editor engine, not the exported release engine/runtime. Both engine
and rendering settings stay identical between Debug and Release cases. The benchmark refuses a mislabeled or
unoptimized loaded assembly. Don't open/build the editor or run another build in the same worktree during the
substitution. A mkdir lock prevents two source-run tools from overlapping. If the process is killed without
cleanup, move `build/source-run/Debug.backup` back to `.godot/mono/temp/bin/Debug` and remove the stale
`build/source-run/lock` directory before rebuilding. These are compiled caches, not player settings.
