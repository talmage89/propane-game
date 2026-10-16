# Test gate timing

## Test speed 2 (October 14, 2026)

| Alternating run | Wall seconds | Minutes | Initial load (1 / 5 / 15 min) | Result |
| --- | ---: | ---: | --- | --- |
| before-2 | 2030.07 | 33.83 | 11.70 / 13.64 / 13.88 | Pass |
| after-1 | 2054.52 | 34.24 | 7.31 / 8.98 / 12.45 | Pass |
| before-3 | 1989.95 | 33.17 | 18.21 / 21.61 / 26.89 | Pass |
| after-2 | 1660.48 | 27.67 | 2.71 / 7.21 / 16.43 | Pass |
| before-4 | 1732.72 | 28.88 | 8.24 / 12.68 / 20.24 | Pass |
| after-3 | 1231.58 | 20.53 | 7.20 / 10.21 / 13.03 | Pass |

The median fell from **1,989.95 to 1,660.48 seconds (33.17 to 27.67 minutes), 16.6% shorter**.
The mean fell from 1,917.58 to 1,648.86 seconds, 14.0%. Before range: 28.88–33.83 minutes;
after range: 20.53–34.24 minutes. **The under-15-minute target was not reached.** After 1 encountered
particularly heavy contention (observed load about 57); after 3 reached about 24.
These observations are not a continuously sampled peak-load record. Unseeded soaks also vary with play.
The paired improvements were −1.2%, 16.6% and 28.9%; the first candidate was slower, so one favorable
run would overstate the gain. No remote CI run was performed.

### Per-job wall time

One complete run of each variant follows. The fast partition figures span all its commands;
range and bots sum their consecutive launches. Separate probe/startup records remain in the raw logs.

| Job | Before 2 (s) | After 2 (s) |
| --- | ---: | ---: |
| test-core | 123.5 | 203.2 |
| test-behavior | 524.8 | 769.5 |
| test-generator | 105.6 | 157.3 |
| test-world | 17.7 | 47.2 |
| engine | — | 46.6 |
| lab-1 | 445.0 | — |
| lab-2 | 92.1 | — |
| lab-3 | 880.3 | — |
| lab-4 | 1059.9 | — |
| generator-1 | 384.9 | 452.4 |
| generator-2 | 391.5 | 449.4 |
| generator-3 | 367.5 | 333.2 |
| generator-4 | 384.3 | 366.5 |
| navigation | 294.5 | 133.1 |
| piles | 165.0 | 202.1 |
| range | 29.9 | 13.1 |
| menu | 29.0 | 17.7 |
| generator-coverage | 15.3 | 10.0 |
| lab-coverage | — | 10.0 |
| bots | 245.1 | 236.7 |
| lab-soak-void | — | 221.2 |
| lab-soak-suburb | — | 182.3 |
| lab-king | — | 530.8 |
| lab-fast | — | 105.9 |
| lab-king-suburb | — | 168.7 |
| lab-shortfuse-11 | — | 139.4 |
| lab-pace | — | 42.3 |
| lab-ringout-11 | — | 12.7 |
| lab-shortfuse-12 | — | 111.4 |
| lab-dissolve-11 | — | 117.3 |
| lab-dissolve-12 | — | 97.5 |
| lab-ringout-13 | — | 23.1 |
| lab-ringout-12 | — | 30.2 |
| lab-lifetime-1 | — | 31.0 |
| lab-lifetime-4 | — | 17.0 |
| lab-lifetime-2 | — | 31.8 |
| lab-lifetime-3 | — | 31.3 |
| lab-dials-1 | — | 68.5 |
| lab-dials-3 | — | 69.6 |
| lab-dials-2 | — | 81.8 |
| lab-dials-4 | — | 78.5 |
| lab-dials-5 | — | 58.7 |
| lab-dials-6 | — | 65.8 |
| lab-dials-7 | — | 57.7 |
| lab-dials-8 | — | 65.5 |


Before 2's late Lab 3 ran from 902.8 to 1,783.1 seconds; the bot tail ran from 1,784.6 to 2,030.0.
After 2's offline jobs finished at 1,422.2 seconds; its bot tail ran from 1,423.6 to 1,660.5.
After 1's slowest dial shard was 210.8 seconds; all candidate dial shards were well below their 600-second limit.
The original before 2 dial sweep took 550.8 seconds against its 600-second limit.


Baseline `d067ef6`; branch `wp/test-speed2`. Four other agents share the 12-thread machine.
The completed before/after runs alternate. Imported assets remain warm, every run records `uptime`, and
`time_gate.py` records wall-clock start/finish timestamps, total duration, return code and initial load.
Per-job timing uses the label wrapper; nested Python/Godot rows are not added to their enclosing label rows.
The three real-time bot matches still run consecutively after every offline prerequisite completes.

The original scheduler already allowed `test` and deep jobs to overlap. Its fast partitions were queued first,
then four large Lab jobs. In the first completed baseline, Lab 4 took 1,060 seconds and Lab 3 took 880 seconds;
the latter started late and ended with a 551-second unsharded dial sweep, only 49 seconds below its timeout.
Reordering alone cannot reduce the total work. The new scheduler queues long/variable sessions early and
splits independent work into 25 Lab jobs: four lifetime shards, eight dial shards, and individual full
Ring Out, Short Fuse and Dissolve seeds. King retains its three-seed aggregate mint assertion in one process.

`test-full-lab-coverage` uses the real C# harness's listings and the checker's actual session options.
It proves that all 564 cases (530 dial fixtures, 11 lifecycle pairs and the remaining session/seed cases)
occur exactly once. Each lifetime pair retains two warm-up cycles and all ten measured cycles. Every dial
still builds, advances 20 frames, resets, advances ten frames and asserts floor contact and tank supply.
Generator shards and their coverage check remain unchanged: 1,044 seeds across all sweeps.
No assertion, simulation duration, tank value, scoring rule, seed or client spacing was relaxed.

Deep probes compile optimized **Debug**, retaining `DEBUG`, `TOOLS`, the Debug Godot API and every assertion.
Their private runtime project under `build/test-full-engine/project` borrows the imported resources, has
separate output and intermediate directories, and copies this checkout's user-data override. A scene-entry
guard rejects an unoptimized or non-Debug assembly. Explicit `IntermediateOutputPath` is necessary:
Godot's SDK otherwise keeps the original intermediate directory even when `BaseIntermediateOutputPath`
is overridden, then copies the cached normal DLL into the alternate output. This mistake was detected
by the runtime guard and corrected before the measured after gate completed. The normal DLL checksum
remained unchanged after the corrected private build. `test`, `check`, CI, benchmark smoke and the final
bot matches continue using normal Debug.

A diagnostic generator sample (`--seeds=5 --large-seeds=2 --formation-seeds=1`) spent 44.0 seconds generating
plans and 4.0 seconds checking reachability before optimization; afterward, 33.4 and 1.8 seconds. All its
assertions passed. The run-based flood-fill prototype was discarded: plan generation dominated the sample,
so its small potential saving did not justify replacing the checker. No production generator code changed.

Two other proposals were measured and rejected. Godot's fixed-FPS loop [returns before the idle wait](https://github.com/godotengine/godot/blob/4.7.2-stable/main/main.cpp#L4789),
so clearing the headless idle delay cannot speed these probes. Reducing the Godot worker pool from eleven
workers to two slowed the quick King probe: single runs 8.8/10.5 seconds versus 11.3/10.6, and four-process
batches 18.6/19.0 versus 24.9/20.5. All passed; the worker setting is unchanged.

The bot launcher now waits for the server's actual `listening on UDP` log rather than sleeping 1.5 seconds;
it fails before launching clients if startup exits or does not become ready within 30 seconds. Its 0.4-second
client spacing and every match/soak argument remain unchanged. The timing wrapper forwards termination
signals to its engine, so launcher cleanup cannot leave a timed server alive. Lab timeouts kill the complete
process group, including a timing wrapper and its engine. The fast gate keeps its 600-second limit; full
sessions get 1,800 seconds, and dial sweeps `max(600, ceil(2400 / shards))` seconds. Eight dial shards therefore
have a 600-second floor, with ample headroom compared with measured shard times.

The first unmodified baseline attempt failed at the known ragdoll head snap (77.51 degrees in one frame),
after 961.36 seconds. It skipped the bot tail and is excluded from completed wall-time comparisons.
No player code or assertion bound was changed to hide it.

Raw evidence is in `build/test-speed2-proof/`: each run has `uptime.txt`, `run.log`, `steps.jsonl` and `total.json`;
later runs also record source hashes. Diagnostic builds, profiling and worker trials are in
`build/test-speed2-draft/`. Source snapshots used for the alternating runs are not committed.

The timing command for each source variant was:

```sh
mkdir -p build/test-speed2-tmp
TMPDIR="$PWD/build/test-speed2-tmp" python3 -B tools/dev/time_gate.py \
  --output="build/test-speed2-proof/<run>" -- make -j4 test-full \
  LABEL="python3 -B tools/dev/time_gate.py --probe label $PWD/tools/dev/label.sh"
```

Baseline runs restored the tracked bytes at `d067ef6`; candidate runs restored a frozen snapshot of the changes.
The profiler itself was held constant in a scratch copy for those swaps. The snapshots and later source hashes
prevented an alternating run from accidentally using the other variant. Compilation is included; no build or
other probe from this worktree ran beside a measured gate. Other worktrees continued running throughout.
The normal gate already overlapped the deep targets on the baseline, so adding that overlap was unnecessary.

For before 2, measured offline job spans total 5,310.8 seconds over a 1,784.6-second offline phase:
74.4% occupancy of four make slots. After 2 totals 5,651.2 job-seconds over 1,423.6 seconds: 99.2% occupancy.
These are occupied make slots, not CPU utilization. Holding those measured job durations constant gives a
1,649.5-second bound including the after bot tail, already above 15 minutes; durations themselves vary with
load and scheduling. The baseline's long last shard wastes slots, while the candidate keeps the queue busy.
This proves the scheduling waste was removed; it does not prove a load-independent runtime or the target.


### Verification and remaining limits

The alternating timings use the frozen candidate containing the scheduling, partitioning and optimized-build
changes. A final review then made coverage listings use the same process-group timeout cleanup as real sessions;
its sixth fixture proves a wrapped listing's child cannot survive timeout. A separate final gate verifies that
hardening and is excluded from the three paired timing samples. No session, seed, frame or assertion changed.

The final-source confirmation at `b285f73` passed in **1,405.16 seconds (23.42 minutes)**, initial load
21.27 / 22.75 / 21.64. All offline jobs passed, followed by all three bot matches with zero tank disagreements
and matching server score audits. It also emitted the nonfatal Jolt job-pool warning during the unchanged fast
probes; the final direct check was free of that warning. Compiler builds remained at zero warnings/errors.
This fourth candidate gate proves the final cleanup and is not included in the paired timing statistics.
The final `python3 -B tools/dev/lint_tests.py` also passed against the hardened sources.

A dedicated launcher verification also passed, writing `build/test-speed2-net`: two blocked-lane duel matches,
120 ms lag, 30 ms jitter and 3% loss; 1,197 comparison grids, 39,143 body pairs, zero tank disagreements and
both server score audits agreeing. Its command (with `TMPDIR` under this checkout's `build/`) was:

```sh
GODOT=godot-mono tools/dev/net_bots.sh -o "$PWD/build/test-speed2-net" -n 2 -w 0 -l 20 \
  -s '--start-score=8 --match-seed=23' -- --scenario=duel --matches=2 --duel-blocked \
  --tune.DropScatter=0 --tune.DropToss=0 --net-lag=120 --net-jitter=30 --net-loss=3
```

The final direct `TMPDIR="$PWD/build/test-speed2-tmp" make check` passed: formatter clean, warning-as-error
and incremental builds both at zero compiler warnings/errors, every fast scene and both fresh-process hash
comparisons passed. Its only engine warning was the malformed-Score fixture's expected diagnostic. An earlier
passing check also emitted a nonfatal Jolt job-pool exhaustion warning under load; it did not recur in the final
check. A profiled final-check attempt received SIGTERM during the core scene, with no assertion failure and no
completed timing record; it is retained as interrupted, not a pass. The signal's source was not established.
No physics setting or assertion threshold was changed to suppress either warning or the ragdoll failure.

`make build` and `make test-full-engine` compiled with zero warnings/errors. `python3 -B tools/dev/lint_tests.py`
passed every injected gate failure, plain/parallel deep wiring, bot-tail isolation, cache/install failures and
CI partition equivalence. The focused fixtures also passed: `check_lab_shards_tests.py` (six),
`full_engine_tests.py` (one), `time_gate_tests.py` (two) and `net_bots_tests.py` (six).
The normal assembly with `--full-engine-optimized` exited 1 before running a deep probe, proving the guard's
failure path. The actual coverage targets passed in all three candidate gates.

After `make build` and `make test-full-engine`, rendered verification used:

```sh
export TMPDIR="$PWD/build/test-speed2-tmp"
godot-mono --fixed-fps 60 --resolution 960x540 --disable-vsync --path . \
  res://scenes/dev/menu_test.tscn -- --scenario=input --port=24681 --windowed \
  --capture-dir="$PWD/build/test-speed2-input"
tools/dev/full_engine.py --fixed-fps 60 --resolution 960x540 --disable-vsync --path . \
  res://scenes/dev/menu_test.tscn -- --scenario=input --port=24681 --windowed \
  --capture-dir="$PWD/build/test-speed2-input-optimized"
tools/dev/full_engine.py --fixed-fps 60 --resolution 960x540 --disable-vsync --path . \
  res://scenes/dev/lab_test.tscn -- --scenario=frames --capture-dir="$PWD/build/test-speed2-lab"
```

Both input runs passed all 163 assertions, using real device events through `Input.ParseInputEvent`.
Reviewed all 19 captures from each: before/after camera turns, airborne jumps, firing/recoil views and remapped
pause panels in single-player, Lab, multiplayer and single-player after exiting the Lab. The optimized match
uses a different unseeded suburb, as expected. Reviewed all 49 Lab frames: main/scenario/options menus, each
available environment, keyboard/gamepad control panels and Free Tuning panels. The readable tuning panel and
player view are intact. These rendered runs prove resource loading and device delivery through the private
project; they are not frame-time measurements. Gamescope/Xwayland printed its wrapper startup/shutdown diagnostics.

`python3 -B build/test-speed2-proof/compare_builds.py` launched `unit_tests.tscn --write-world-plans=<build-dir>`,
then `--hash-world-plans=<build-dir>` through normal and optimized Debug, followed by
`generator_tests.tscn --hash-segments=5` through each. Loud assertions required all four world hashes and all
60 segment hashes to match exactly; both comparisons passed. `git diff --check` and the code commits' whitespace
checks passed. All scratch and `TMPDIR` remained under this worktree's `build/`.

No production generator, bot, navigation, tank, effect or player implementation changed. Further large savings
would need less work per probe, especially plan construction; the rejected flood-fill/worker experiments above
do not justify expanding this branch. The private project must be prepared after source changes and must never
be opened/imported in the editor: its resource symlinks borrow the normal checkout's assets. The guard catches
wrong build configuration; it cannot substitute for rebuilding changed sources. Bot startup still has a bounded
30-second deadline, and the soaks' unseeded variation and contention remain visible.

Messages, suburb construction, tanks and tuners are unchanged; `Protocol.Version` remains 3.

## Earlier timing work

Measured on the shared Linux development machine on October 11, 2026. Times are wall seconds, warm imports,
one sample per command; other agents also used the machine. Baseline: `7777296`. While this work ran, `dev`
advanced through `616b08b` and `13ae84f` to `c4a6f51`. Final verification includes two new movement probes,
motion assertions on five existing probes, gather appearance and mouse-menu probes, expanded HSV menus, two
melee probes, the deep melee-reload sweep and 21 added unit tests. Earlier measurements identify their baseline.

| Command | Before | After | Result |
| --- | ---: | ---: | --- |
| `make test` (matched original suites) | 350.03 | 166.76 | Pass; 52% shorter |
| `make check` (intermediate `616b08b` baseline) | 379.43 | 176.09 | Pass; 54% shorter, including new movement checks |
| `make test` (`13ae84f`, heavier load) | — | 308.27 | Pass; added coverage and substantially more contention |
| `make check` (`13ae84f`, heavier load) | — | 383.10 | Pass; added coverage and substantially more contention |
| `make test` (final `c4a6f51` baseline) | 350.03 | 178.96 | Pass; 49% shorter despite six added probes |
| `make check` (final `c4a6f51` baseline) | 379.43 | 212.64 | Pass; 44% shorter despite added coverage |

The first baseline `make check` failed in 56.43 seconds: `authority/new canonical vent reconciles an intact
observer (moving True)` reported “confirmed owner's stream did not reconcile the observer.” A retry passed
unchanged. This preexisting intermittent failure remains observable; no gameplay workaround or tolerance change
was made. The first `13ae84f` check also failed in 105.75 seconds at the unchanged real-game gather
assertion “knocked tanks did not gather.” The identical standalone command passed immediately without edits.
These failures are retained in the verification record rather than masked. The import-cache benchmark overlapped
part of the baseline retry's generator work, so the earlier standalone test is the cleaner matched comparison. Timing variation is expected on this shared machine.

## Every external step

Each cell is **total / startup / work-and-exit** in seconds. Startup ends at the first scene `_Ready` marker;
work includes shutdown. A dash means no scene marker, or the command was consolidated/added. Runtime launches
read already-imported assets and perform no editor import. Before columns used temporary entry markers and tool
wrappers; after columns use `tools/dev/time_gate.py`. Each world writer/hash launch is listed independently.
The after generator uses 20/3/2 seed depths versus 100/10/6 before; all assertion kinds remain.

| Step | Before test | Before check | Final test | Final check |
| --- | ---: | ---: | ---: | ---: |
| Incremental build | 1.20 / — / — | 1.22 / — / — | 1.18 / — / — | 1.23 / — / — |
| check_scores_tests.py | 0.06 / — / — | 0.05 / — / — | 0.05 / — / — | 0.05 / — / — |
| compare_vents_tests.py | 0.06 / — / — | 0.06 / — / — | 0.06 / — / — | 0.06 / — / — |
| compare_tank_paths_tests.py | 0.05 / — / — | 0.05 / — / — | 0.05 / — / — | 0.05 / — / — |
| unit_tests | 29.20 / 0.29 / 28.91 | 28.91 / 0.29 / 28.63 | — | — |
| blast_query_tests | 0.67 / 0.29 / 0.38 | 0.66 / 0.28 / 0.38 | 0.66 / 0.28 / 0.38 | 0.70 / 0.28 / 0.41 |
| gather_range | 1.60 / 0.29 / 1.31 | 1.59 / 0.28 / 1.31 | 1.59 / 0.28 / 1.31 | 1.50 / 0.28 / 1.22 |
| gather_range: grant-races | 0.95 / 0.30 / 0.65 | 0.94 / 0.29 / 0.66 | 0.94 / 0.29 / 0.65 | 0.98 / 0.28 / 0.70 |
| game_test: gather | 4.44 / 0.31 / 4.13 | 3.93 / 0.28 / 3.64 | 4.02 / 0.28 / 3.74 | 5.26 / 0.36 / 4.90 |
| movement_tests | 8.06 / 0.46 / 7.60 | 7.63 / 0.28 / 7.35 | — | — |
| bot_navigation_course | 8.10 / 0.29 / 7.81 | 7.54 / 0.29 / 7.25 | 8.10 / 0.28 / 7.82 | 8.10 / 0.28 / 7.82 |
| menu_test: gamepad | 9.41 / 0.28 / 9.13 | 7.27 / 0.27 / 6.99 | 9.22 / 0.28 / 8.94 | 8.75 / 0.28 / 8.48 |
| menu_test: devices | 3.82 / 0.63 / 3.19 | 3.18 / 0.28 / 2.90 | 2.16 / 0.30 / 1.86 | 2.06 / 0.28 / 1.78 |
| player_range: grip | 6.84 / 0.73 / 6.10 | 5.77 / 0.29 / 5.48 | 6.15 / 0.29 / 5.86 | 6.08 / 0.28 / 5.80 |
| player_range: ragdoll | 2.53 / 0.30 / 2.23 | 2.24 / 0.29 / 1.96 | 3.66 / 0.30 / 3.36 | 2.34 / 0.28 / 2.07 |
| player_range: feet | 3.83 / 0.29 / 3.54 | 3.55 / 0.29 / 3.26 | 4.14 / 0.29 / 3.84 | 4.01 / 0.28 / 3.73 |
| player_range: feet (corrections) | 3.83 / 0.30 / 3.53 | 3.54 / 0.28 / 3.26 | 4.69 / 0.28 / 4.41 | 4.76 / 0.29 / 4.47 |
| player_range: feet-events | 4.29 / 0.30 / 3.99 | 4.01 / 0.28 / 3.73 | 4.23 / 0.28 / 3.95 | 4.57 / 0.30 / 4.27 |
| generator_tests | 241.61 / 0.29 / 241.32 | 250.49 / 0.28 / 250.22 | 66.95 / 0.27 / 66.68 | 71.68 / 0.33 / 71.35 |
| platform_tests | 0.39 / 0.28 / 0.10 | 0.39 / 0.28 / 0.11 | — | — |
| World plan: Rows | 0.76 / 0.29 / 0.47 | 0.76 / 0.29 / 0.47 | — | — |
| World hash: Rows, process 1 | 1.90 / 0.29 / 1.62 | 1.80 / 0.29 / 1.52 | — | — |
| World hash: Rows, process 2 | 1.80 / 0.29 / 1.51 | 2.76 / 0.45 / 2.31 | — | — |
| World plan: Square | 0.76 / 0.28 / 0.47 | 0.86 / 0.29 / 0.56 | — | — |
| World hash: Square, process 1 | 1.80 / 0.28 / 1.51 | 2.93 / 0.82 / 2.10 | — | — |
| World hash: Square, process 2 | 1.79 / 0.29 / 1.50 | 2.57 / 0.50 / 2.07 | — | — |
| World plan: Triangular | 0.76 / 0.28 / 0.48 | 0.76 / 0.29 / 0.47 | — | — |
| World hash: Triangular, process 1 | 1.89 / 0.29 / 1.60 | 1.70 / 0.27 / 1.42 | — | — |
| World hash: Triangular, process 2 | 1.99 / 0.31 / 1.68 | 1.79 / 0.30 / 1.50 | — | — |
| World plan: Cluster | 0.86 / 0.31 / 0.55 | 0.85 / 0.29 / 0.56 | — | — |
| World hash: Cluster, process 1 | 1.89 / 0.30 / 1.60 | 1.89 / 0.30 / 1.59 | — | — |
| World hash: Cluster, process 2 | 1.90 / 0.31 / 1.59 | 1.79 / 0.31 / 1.49 | — | — |
| Formatter | — | 19.06 / — / — | — | 20.18 / — / — |
| Warning-as-error rebuild | — | 5.87 / — / — | — | 4.71 / — / — |
| time_gate_tests.py | — | — | 0.24 / — / — | 0.24 / — / — |
| core_tests | — | — | 32.53 / 0.28 / 32.25 | 32.92 / 0.28 / 32.63 |
| gather_range: appearance | — | — | 2.71 / 0.29 / 2.42 | 2.71 / 0.28 / 2.43 |
| melee_range | — | — | 1.78 / 0.28 / 1.50 | 1.69 / 0.28 / 1.41 |
| player_range: melee | — | — | 1.96 / 0.29 / 1.68 | 2.00 / 0.29 / 1.71 |
| menu_test: mouse | — | — | 6.52 / 0.28 / 6.24 | 6.47 / 0.28 / 6.19 |
| player_range: movement | — | — | 1.60 / 0.29 / 1.31 | 1.72 / 0.29 / 1.43 |
| player_range: motion | — | — | 4.04 / 0.28 / 3.76 | 6.18 / 0.43 / 5.76 |
| World plans: all formations | — | — | 1.41 / 0.28 / 1.13 | 1.50 / 0.29 / 1.22 |
| World hashes: all formations, process 1 | — | — | 3.56 / 0.28 / 3.28 | 4.09 / 0.28 / 3.81 |
| World hashes: all formations, process 2 | — | — | 3.55 / 0.28 / 3.28 | 4.65 / 0.80 / 3.85 |

The original gate launched Godot 28 times. Batching core suites and world probes reduces that to 17 for the
matched suite set; two movement probes, gather appearance, mouse menus and two melee probes make the final
gate 23. Most savings come from bounded generator sweeps and fixed-rate movement/menu execution.

The loaded `13ae84f` run illustrates contention: its formatter took 51.33 seconds versus 19.06 before, and unit
work took 53.82 seconds versus 28.91 before. Final core suite work was platform/movement/unit
**0.008/0.600/31.543 seconds** in `make test` and **0.009/0.629/31.882 seconds** in `make check`. All 189 unit
tests passed; the original matched run passed 168. Total startup was 9.15 seconds before, 5.59 seconds in the
matched original after-test run, and 6.53/7.30 seconds in the final test/check. Runtime launches did no editor import.
The final check's lint phase overlapped the separate wiring-fixture verification; the final test had no other
verification launched by this agent. Other agents still used the machine throughout.

## Import and CI estimate

Two fresh scratch checkouts used the same tracked source/assets. Cold import took **24.47 seconds**; restoring
only `.godot/imported` and rebuilding the resource index took **3.02 seconds**. Both exited zero. The cache was
about 53 MB. CI keys it on Godot version, all asset/import-setting bytes, icon/import settings and project settings;
a cache miss imports from source. The import job populates the cache before the matrix.

Local partition costs, excluding their shared build:

| CI partition | Final local check |
| --- | ---: |
| Lint | 24.89 s |
| Core, including Python checkers | 33.33 s |
| Behavior, including melee and HSV menus | 69.89 s |
| Generator | 71.68 s |
| World plans and two fresh builders | 10.25 s |

Parallel jobs replace the sum with the slowest partition. The final split is balanced between behavior and
generator. Applying the rough supplied CI/local baseline ratio of 480/379 gives about 91 seconds for the longest
partition. Allowing for checkout, SDK/editor setup, compilation, cache transfer, import/index rebuilding and
timing variation gives an estimated **2–4 minutes warm, 3–5 minutes cold**, excluding queue delays. This is an
estimate; no workflow was pushed or run remotely. Matrix, cache and final check wiring
were reviewed against GitHub's [matrix documentation](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/run-job-variations),
[cache contract](https://github.com/actions/cache/blob/v4/action.yml),
[restore contract](https://github.com/actions/cache/blob/v4/restore/action.yml) and the local wiring assertions.

## Coverage and failure proofs

The fast generator checks 100 layouts: 20 single-player, 20 two-player, 12 across the four large sizes, and 48
forced layouts (four formations × six sizes × two seeds). Every explicit formation count from 0 to 128 stays in
the gate. Deep sweeps retain 200 single-player and two-player seeds, 100 each at 8/16/24/32 players and six forced
seeds per formation/size: 944 layouts in addition to the fast gate. Former gate seeds 21–100, large seeds 4–10 and
forced seeds 3–6 moved to that sweep. Navigation stays five seeds in the gate and 100 in the full test. Both newly
landed melee gate probes and the melee-reload full sweep remain in their original tiers. The first 20 single-player seeds still exercised
all seven placement reasons and every group size 1–10 observed in the original 100-seed sample. Forced fixtures
cover each formation and size directly rather than relying on random selection. Fewer repetitions reduce the
chance of catching rare seed-specific regressions in the fast gate; the full sweep retains that depth.

Injected and reverted one assertion failure in each consolidated suite:

| Suite | Deliberate failure | `make test` exit |
| --- | --- | ---: |
| Platform | Expected an incorrect Windows archive suffix | 2 |
| Movement | Inverted the between-physics-frame assertion | 2 |
| Unit | Inverted repeated world-hash equality | 2 |

Each printed its suite's FAIL and stopped the gate. All three injections were repeated successfully on the
final `c4a6f51` baseline, restored, and followed by a passing complete test run. Restored sources rebuilt with
zero warnings/errors.
`python3 -B tools/dev/lint_tests.py` passed analyzer/format fixtures, parallel ordering, every scene/checker failure,
both hash-process failures, absent/partial/different hashes, core-scene/full-sweep wiring, failures of every deep
command, installer failures and CI/local partition equivalence. `time_gate_tests.py` checks parallel timing records and propagates a failing child command.

Intermediate rendered verification used fixed FPS 60 and requested 960×540 with `menu_test.tscn`,
scenarios `gamepad` and `devices`,
port 24684 and capture directories `/tmp/wptest-menu-gamepad` and `/tmp/wptest-menu-devices`. Both passed
(96 rendered gamepad assertions and 20 device assertions). Reviewed all 14 gamepad and four device captures:
focus outlines, name/address defaults, browser and lobby controls, guest/host states, match menu, results, return
navigation, keyboard/gamepad pause references and reload hints were correct. Screenshot-only assertions explain
the rendered gamepad count exceeding its headless count.

`13ae84f` rendered verification used fixed FPS 30 for gamepad and mouse, and real time for gather
appearance, requesting 960×540. All passed: 120 gamepad assertions, 15 mouse assertions and gather coexistence/lifetime
checks. Reviewed all 17 gamepad, eight mouse and two gather captures in `/tmp/wptest-appearance-gamepad30`,
`/tmp/wptest-appearance-mouse-retry` and `/tmp/wptest-gather-appearance`: focus and custom colors carry through
previews, lobbies, characters and real results; gather shimmer remains after shield expiry. A rendered gamepad
run at FPS 60 hit the existing 15-second results-cleanup deadline under load; FPS 30 passed unchanged. Headless
menus retain FPS 60. One mouse launch overlapped the warning-as-error rebuild and could not load its script; it
was stopped and repeated successfully after compilation. No assertions or deadlines changed.

Final `c4a6f51` rendered verification passed all three menu scenarios at fixed FPS 30: 135 gamepad assertions,
22 device assertions and 32 mouse assertions. Reviewed all 18 gamepad, four device and ten mouse captures in
`/tmp/wptest-hsv-gamepad`, `/tmp/wptest-hsv-devices` and `/tmp/wptest-hsv-mouse`, including full-size picker and
keyboard-help images. Presets/HSV controls, focus, color carry-through, real results and melee help are visible
and readable. Commands requested 960×540; the game applied its window settings, producing 1920×1042 captures.

Rendered `melee_range.tscn` and `player_range.tscn -- --scenario=melee --assert-motion --assert-grip` passed at
fixed FPS 60. Reviewed all seven collision captures in `/tmp/wptest-melee-range` and contact sheets of all 264
pose frames in `/tmp/wptest-melee-poses`, plus full-size windup/contact/magazine-drop samples. The collision
sequence shows blocked contact, the victim going down and an intact shoved tank; poses show strike and recovery
with hands tracking the rifle. Grip and motion assertions passed without tolerance changes.

A separate fresh-checkout `make lint` with no `.godot` and a nonexistent `GODOT` path passed in 26.55 seconds
with zero warnings/errors, proving the CI lint job needs no editor/import dependency.

All four batched world hashes exactly match the baseline hashes (205 fences and 36 tanks per fixture), as well
as each other across the two fresh builder processes. No messages, suburb construction, tanks, tuners or gameplay
rules changed relative to the updated `dev` baseline. `Protocol.Version` remains 2.

## Full sweep

All three full runs passed the gate, all 944 deep layouts and 700/700 navigation courses (success rate 1.000):

| Baseline and command | Wall seconds |
| --- | ---: |
| `616b08b`, `make test-full` | 1587.10 |
| `13ae84f`, `make -j4 test-full` | 1702.19 |
| `c4a6f51`, `make -j4 test-full` | 1286.39 (21:26) |

The final run also passed the retained melee-reload grip and motion sweep. Parallel gate execution used a single
shared build. Its gate took 142.83 seconds versus 178.96 seconds for the final sequential `make test`.
No assertion, tolerance or gameplay value was changed to pass these runs.

| Deep generator phase | `616b08b` | `13ae84f` | `c4a6f51` |
| --- | ---: | ---: | ---: |
| Single-player, 200 seeds | 31.84 s | 49.00 s | 38.25 s |
| Forced formations, six seeds at every size | 113.13 s | 136.93 s | 100.88 s |
| Two-player, 200 seeds | 99.16 s | 114.86 s | 82.29 s |
| Eight-player, 100 seeds | 72.85 s | 106.35 s | 71.07 s |
| Sixteen-player, 100 seeds | 127.81 s | 186.23 s | 120.88 s |
| Twenty-four-player, 100 seeds | 246.12 s | 285.73 s | 210.84 s |
| Thirty-two-player, 100 seeds | 438.44 s | 462.65 s | 334.19 s |

Final deep-launch timing (startup ends at the scene entry marker):

| Command | Total | Startup | Work and exit |
| --- | ---: | ---: | ---: |
| Melee reloads | 5.22 s | 0.29 s | 4.93 s |
| Generator deep sweep | 960.21 s | 0.29 s | 959.92 s |
| Navigation deep sweep | 178.12 s | 0.29 s | 177.83 s |


## Shutdown merge and suite discovery

Merged `dev`'s shutdown fix (`8f2cdcd`) in `f8d3392`. Consolidated completion now always reaches
`ShutdownGuard.Quit`; normal and paused window-close shutdown witnesses remain in the core partition.
The world wrapper retains failed logs and original child status while still batching four formations in
one writer and two fresh builders. Its four Python regression cases pass, including status 134 retention.

Six additional direct exit paths in melee, reload failure and capture errors now also use the guard; the wiring
check rejects direct quits in all dev harnesses except the final-teardown witness.

Unit suites now opt in with `[UnitSuite]` and are discovered in deterministic order. World construction runs
first; transport is a separate discovered suite. Both successful gate runs discover 13 suites and execute
189 assertions, matching the pre-discovery count. No test assertion was removed or moved by this merge.

`make setup` and the restored-code `make build` passed with zero warnings/errors. `dotnet format Propane.sln
--no-restore` completed. The final `python3 -B tools/dev/lint_tests.py` passed analyzer/format fixtures,
parallel gate and deep failure propagation, shutdown completion/discovery wiring, installer and CI equivalence.

A temporary inversion of the world rebuild equality assertion caused `make test` to exit 2 in 37.14 seconds,
with four world assertion failures and `[core] FAIL unit_tests`. The assertion was restored in a `finally`
block before rebuilding. A second proof added a temporary `[UnitSuite]` class without changing the runner.
Its named failing assertion was discovered automatically: `make test` ran 190 tests with one failure and exited
2 in 38.99 seconds. The temporary script and any UID were removed before rebuilding. No deliberate failure
is committed. An initial full run was intentionally interrupted while auditing remaining direct exit paths;
its deep generator received SIGTERM and the gate returned nonzero. The fresh full run uses final code.

The rendered melee command passed and exited normally through the guard:

```sh
godot-mono --fixed-fps 60 --path . res://scenes/dev/melee_range.tscn -- --capture-dir=/tmp/wptest-merge-melee
```

All seven PNG captures were reviewed: strike contact, victim knockdown/recovery and the intact tank are visible.
The numeric assertions proved wall blocking, exactly one victim hit/throw, 0.104 m intact tank movement,
0.593 m prop movement and the large duplicate-shape contact. Invisible collision-only fixtures are proven by
assertions rather than appearance. Screenshot readback/save error branches were audited but not fault-injected.

The profiler command was:

```sh
python3 -B tools/dev/time_gate.py --output=/tmp/wptest-merge-speed/check-final -- make check
```

It passed in **231.04 seconds**, with 25 Godot launches (two added shutdown witnesses).
Every external step below passed. Shutdown witnesses lack a scene-entry marker; their complete launch times
are recorded, and startup cannot be separated for those two launches. Other launch work includes orderly shutdown.

| Merged check step | Total | Startup | Work and exit |
| --- | ---: | ---: | ---: |
| Formatter | 26.082 s | — | — |
| Warning-as-error build | 5.000 s | — | — |
| Incremental build | 1.233 s | — | — |
| check_world_tests.py | 0.105 s | — | — |
| time_gate_tests.py | 0.409 s | — | — |
| check_scores_tests.py | 0.098 s | — | — |
| compare_vents_tests.py | 0.092 s | — | — |
| compare_tank_paths_tests.py | 0.078 s | — | — |
| core_tests | 39.584 s | 0.546 s | 39.038 s |
| shutdown_tests | 0.578 s | — | — |
| shutdown_tests: paused, window-close, repeat-quit | 0.607 s | — | — |
| blast_query_tests | 0.667 s | 0.304 s | 0.364 s |
| gather_range | 1.595 s | 0.309 s | 1.286 s |
| gather_range: scenario=grant-races | 0.943 s | 0.297 s | 0.646 s |
| gather_range: scenario=appearance | 2.801 s | 0.299 s | 2.501 s |
| game_test: scenario=gather | 4.107 s | 0.297 s | 3.811 s |
| melee_range | 1.802 s | 0.301 s | 1.501 s |
| player_range: scenario=melee | 1.990 s | 0.291 s | 1.699 s |
| bot_navigation_course: seeds=5 | 8.041 s | 0.295 s | 7.746 s |
| menu_test: scenario=gamepad | 8.871 s | 0.291 s | 8.580 s |
| menu_test: scenario=devices | 2.068 s | 0.299 s | 1.769 s |
| menu_test: scenario=mouse | 6.532 s | 0.294 s | 6.237 s |
| player_range: scenario=grip | 7.185 s | 0.399 s | 6.786 s |
| player_range: scenario=ragdoll | 2.902 s | 0.324 s | 2.578 s |
| player_range: scenario=feet | 5.161 s | 0.391 s | 4.770 s |
| player_range: scenario=feet, feet-corrections | 6.074 s | 0.357 s | 5.717 s |
| player_range: scenario=feet-events | 5.057 s | 0.366 s | 4.691 s |
| player_range: scenario=movement | 1.981 s | 0.349 s | 1.632 s |
| player_range: scenario=motion | 4.957 s | 0.348 s | 4.609 s |
| generator_tests: seeds=20, large-seeds=3, formation-seeds=2 | 73.596 s | 0.319 s | 73.277 s |
| World writer (four plans) | 1.502 s | 0.299 s | 1.203 s |
| World hash builder | 3.735 s | 0.289 s | 3.446 s |
| World hash builder | 3.995 s | 0.300 s | 3.695 s |

The fresh full command passed:

```sh
python3 -B tools/dev/time_gate.py --output=/tmp/wptest-merge-speed/full-final -- make -j4 test-full
```

Elapsed **1167.06 seconds** (19.45 minutes). All gate partitions passed under parallel make,
followed by melee reloads, 944 generator layouts (200 SP + 200 two-player + 400 large + 144 forced),
and 700/700 navigation goals across 100 seeds. No assertion or deep repetition was lost.

| Deep launch | Total | Startup | Work and exit |
| --- | ---: | ---: | ---: |
| Melee reloads | 5.510 s | 0.318 s | 5.192 s |
| Generator | 947.882 s | 0.343 s | 947.538 s |
| Navigation | 138.191 s | 0.292 s | 137.899 s |

| Deep generator phase | Elapsed |
| --- | ---: |
| Single-player | 33.344 s |
| Forced formations | 100.644 s |
| Two-player | 83.242 s |
| 8-player | 70.673 s |
| 16-player | 125.863 s |
| 24-player | 217.033 s |
| 32-player | 315.083 s |

The merged sequential check measured lint 31.08 s, core 41.55 s, behavior 72.73 s, generator 73.60 s,
world 9.23 s, plus one 1.23 s shared incremental build. CI retains the four parallel partitions and import
cache; the earlier estimate of 2–4 minutes warm / 3–5 minutes cold remains an estimate. CI was not pushed or run.
The existing malformed-packet fixture emits an expected network warning; builds reported zero warnings.
No messages, suburb construction, tank behavior or tuners changed relative to merged dev; protocol version remains 2.

## Parallel deep sweeps

*Historical.* This section and the timings below it measured the earlier scheduler, which ran `check_lab.py --full` as
three Lab jobs. Test speed 2 (top of this file) split it into 25 named jobs; `make -n test-full` lists the current ones.

Before this work, `make test-full` ran the gate and then every deep step one at a time. On `dev` at `a832c5d`, on the
lead's 12-thread machine at load about 2, it took 2,317 seconds: generator sweep 902, `check_lab.py --full` 612,
navigation 141, rifle soak 113, deep piles 77, duel lane 77, loading readiness 49, gate about 360. Each deep step is
now its own target ([code-quality.md](code-quality.md#fast-gate-and-deep-sweeps) lists them), so `make -j4 test-full`
runs them side by side with the gate's partitions. Nothing is dropped:

- The generator sweep runs as four seed-range shards. `test-full-generator-coverage` runs the sweep's own
  `--list-seeds` listing for the whole sweep and for each shard, and requires the shards to hold exactly the whole
  sweep's 944 seeds, each once: 200 single-player, 200 two-player, 100 at each of 8/16/24/32 players and six per
  formation at each of the six sizes. A forced gap and overlap (shard 4 starting at seeds 152 and 75) failed it, naming
  the six missing, four shared and two extra seeds.
- `check_lab.py --full` ran as three jobs of named sessions when this section was written (it is 25 jobs since
  [Test speed 2](#test-speed-2-october-14-2026)); `lint_tests.py` requires them to add up to every `--full`
  session. Navigation stays one process: it passes on its success rate over all 100 seeds.
- The bot matches run last and alone. They run in real time. On this VM, beside three CPU-bound jobs, the rifle
  soak's clients fell to 1–2 frames a second and it failed tank agreement with two disagreements. Running the offline
  jobs under `nice -n 19` restored client frame rates but did not protect the loading-readiness match (below).
- A temporary failing assertion at seed 102 failed `make -j4 test-full-generator-2 test-full-generator-3` with exit 2,
  `[generator-3] [tests] FAIL forced failure probe: 1`, `[generator-3] FAIL (exit 1): <command>` and make's
  `*** [Makefile:146: test-full-generator-3] Error 1`; generator 2 still finished and passed.

`-j` is not the default: worktree agents share one machine, so each run picks its job count.

### Timings on a four-CPU cloud VM

These timings are for the three-Lab-job scheduler described above, not the current 25 jobs.

Indicative only: 4 CPUs, no GPU, rendering on the CPU. Each run was wrapped in `tools/dev/time_gate.py`. Load averages
(1/5/15 minutes) were taken with `uptime` just before each run; a higher 5- or 15-minute figure is the previous run
fading out.

| Commit | Command | Load before | Wall seconds | Result |
| --- | --- | --- | ---: | --- |
| `8a0bf8b` | `make test-full` | 2.95 / 2.52 / 2.92 | 3,287.5 | Pass |
| `a5da896` | `make -j4 test-full`, bots beside other jobs | 4.60 / 2.34 / 1.56 | 1,013.2 | Pass |
| `8a0bf8b` | same, longest jobs first | 1.17 / 1.45 / 1.46 | 969.4 | Loading readiness failed |
| `8a0bf8b` | same | 1.19 / 1.55 / 2.29 | 975.5 | Rifle soak failed: starved clients, two disagreements |
| `273e118` | offline jobs under `nice -n 19` | 0.56 / 3.63 / 4.26 | 991.9 | Loading readiness failed; rifle soak passed |
| `441fa7d` | bots alone, after everything else | 0.57 / 2.10 / 3.58 | 1,288.9 | Loading readiness failed (alone); a 532-second void soak was the tail |
| `6f37471` | Lab soaks started first | 0.91 / 2.03 / 3.18 | 1,156.9 | Loading readiness failed (alone); everything else passed |
| `6f37471` | same | 1.16 / 3.11 / 3.76 | 1,194.7 | Loading readiness failed (alone); everything else passed |

In every run, every job that ran passed except as noted: 944 generator layouts, 700/700 navigation courses, all
seven Lab sessions, deep piles, the range probes, menu lifetime, the coverage proof, the rifle soak and the duel lane.
After the rifle soak failed, make started no new jobs, so that run skipped the coverage proof and the last two bot
matches. Seconds per job:

| Job | Serial (`8a0bf8b`) | `-j4` (`6f37471`) |
| --- | ---: | ---: |
| Generator shards 1–4 | 385.7, 393.0, 364.1, 353.9 | 416.3, 418.1, 393.9, 399.7 |
| Generator coverage proof | 11.4 | 12.7 |
| Navigation, 100 seeds | 262.8 | 279.6 |
| Lab void soak / suburb soak / other five sessions | 211.5 / 121.7 / 242.5 | 296.8 / 110.6 / 249.1 |
| Deep piles | 118.5 | 137.9 |
| Menu lifetime | 24.0 | 26.8 |
| Melee reloads, airborne recoil, ten-magazine bot recoil | 8.2, 3.7, 7.6 | 9.7, 3.9, 9.7 |
| Rifle soak / duel lane / loading readiness (host bot) | 112.5 / 76.7 / 49.3 | 113.1 / 77.1 / 49.0 |

The gate and build took about 530 seconds of the serial run. The Lab soaks are unseeded bot-versus-bot matches. Across
these runs, a soak's cost ranged from 84 to 532 seconds as play varied (banked 30–99, spilled 3–85), so they start
first. On the lead's 12-thread machine, `-j4` keeps four jobs running without contention. About 2,080 seconds of
offline work over four jobs, plus about 240 seconds of bots alone, suggests 13 to 15 minutes. That is an estimate
from the serial figures above, not a measurement.

| Rifle soak, four bots, 90 seconds | Client median frame rates | Longest above-tolerance episode | Result |
| --- | --- | ---: | --- |
| Alone | 58, 60, 59, 59 | 0.70 s | Pass |
| Beside three generator shards | 43, 40, 45, 36 | 0.65 s | Pass |
| Beside the same shards under `nice -n 10` | 50, 44, 59, 57 | 0.65 s | Pass |
| Beside the same shards under `nice -n 19` | 60, 57, 57, 47 | 0.55 s | Pass |
| Inside `make -j4 test-full` beside three jobs (`8a0bf8b`) | 52, 52, 46, 42; minimums 1–2 | 2.55 s | Fail, two disagreements |

### Intermittent loading-readiness failure

`net_bots.sh -n 2 -l 20 -- --scenario=body-stream --tank-check-ready-stall=Bot2` fails intermittently. The code it
runs is the same as on `dev`: the branch changes only `GeneratorTests.cs` under `src/`. Alone it failed 6 of 14 runs:
3 of 10 standalone runs (1 of the first 6, 2 of 4 two hours later), and 3 of the 4 bot jobs that ran alone inside
`make test-full` (the serial run's passed). Beside other jobs it failed 5 of 6: 3 of 4 runs beside three generator
shards under `nice -n 19`, and both `-j4` runs that reached it while other jobs still ran.

Every failure is the same. The server drops Bot2 with `TransportFailure` about 11 seconds after sending the plan,
and the checker reports `missing play coverage or settled completion`. ENet drops a peer that early only on the
established-player timeout (3-second minimum, 8-second maximum), not on the 20/30-second loading grace.

The cause, from timestamped traces on both ends (`claude/loading-grace-iqlm37`): the grace did not end before the
stall. Bot2 sent `Loaded` after it, and the server dropped Bot2 0.02 to 4.5 seconds after receiving it, in all 8
failures of 20 runs. Bot2's frames after the stall took about 17 ms (the bots are headless), so slow first frames
are not the cause. While Bot2 slept, the server's body stream overflowed its 208 KB UDP receive buffer
(`RcvbufErrors` in `/proc/net/snmp`: 158–326 drops in each failing run; 18–89 in 11 of the 12 passing runs, 201 in one). ENet doubles a
reliable command's retry interval on every unacknowledged send, without a cap (`enet_protocol_check_timeouts` in
Godot's bundled ENet 1.3.18). So commands lost in the overflow next retried seconds after Bot2 resumed. Once
`Loaded` had restored the short timeout, the next such retry found the command unacknowledged 3 seconds after its
last send, after six or more attempts, and declared the peer lost instead of resending. The run passed only when no
retry with an interval that long came due after `Loaded`. The fix keeps the grace after `Loaded` for as long as the
load took, plus 1 second (`LoadingGrace` in `NetTransport.cs`), and the match passes; see
[multiplayer.md](multiplayer.md#transport-contract).

| Loading-readiness match on the 4-CPU VM | Before | After |
| --- | ---: | ---: |
| Alone | 12/20 | 20/20 |
| Beside `make -j3 test-full-generator-1 test-full-generator-2 test-full-generator-3` | 4/10 | 10/10 |
