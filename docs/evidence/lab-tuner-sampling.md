# Lab tuner sampling verification

**Status:** Dated record (2026-10-17, refactor package P5). Not superseded. P5 equivalence/allocation checks pass;
the aggregate deep gate failed its final native moving-vent convergence check. The current sampling contract is in
[lab.md](../lab.md#extension-and-ownership).

Production parent: `c26eaf7`. Candidate implementation: `8f5da5d`. Linux, Godot 4.7.2 .NET (`ed1daf0bf`),
.NET SDK 10.0.112, normal Debug. No release reference, gameplay rule, tuner declaration, schema, order, default or
protocol version changed. Raw logs, comparison drivers and compressed traces are in `build/rfp5/`; reviewed PNGs
are in `build/rfp5-{ringout,king,shortfuse,dissolve}/`. Scratch source copies are disposable, not extra worktrees.

## Scalar and behavior proof

`ModeTunerSamplerTests` passes all 20 tests. Every Bank/prototype sampled scalar is compared bit for bit with
`CopyTuners` through initialization, individual direct edits, validated shared snapshot application, restoring
shared and local defaults, resource replacement and a fresh round sampler. Prior snapshots remain immutable;
unchanged samples retain identity. A separate float/integer/toggle case covers conversion, signed zero, NaN and
infinities. Six warmed sampler checks (including the empty definition) assert zero allocation with
`TestRunner.AssertNoAllocation`. Two rounds per definition also compare ordered serialized rule commands and
rule snapshots with the reflection path while editing and restoring tuners.

The final focused suite took 2.712 wall seconds including engine startup. An earlier redundant sweep of unrelated
exported tuners took 17.313 seconds; it was replaced with the bounded type case so the ordinary gate stays fast.

```sh
make setup
make build
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ModeTunerSamplerTests
make test-equivalence
python3 -B tools/dev/check_equivalence.py --baseline="$PWD/build/rfp5/parent" \
  --output="$PWD/build/rfp5/parent-equivalence"
```

The frozen wire/schema/world/publication checks and all five Lab references pass. The immediate parent comparison
also agrees for Bank, King, spill/chain, Short Fuse replacement and controlled Dissolve retirement. Independently
decompressing and comparing every corresponding `.detail.jsonl.gz` produces identical bytes, not just equal digests.

The parent was archived inside `build/rfp5/parent` with `git archive c26eaf7`, imported and built independently.
Only the new benchmark owner, registry entry, sampler tests/helper and registry test inventory were copied into
that scratch source tree. Its production runners still use the parent's `CopyTuners` path. No other checkout was
modified. P0's committed fixtures were never refreshed.

P0 has no Ring Out fixture. `build/rfp5/ring-pairs.py` supplements it with three parent/candidate pairs, seeds 1–3,
using the same existing trace observer. For each checkout/seed it runs:

```sh
godot-mono --headless --fixed-fps 60 --path "$CHECKOUT" res://scenes/dev/lab_test.tscn -- \
  --scenario=prototype --mode=ringout --quick --seeds=1 --first-seed="$SEED" \
  --replay-random --tune.HitstopDuration=0 \
  --equivalence-trace="$OUTPUT.json" --equivalence-detail="$OUTPUT.detail.jsonl.gz"
```

All three JSON summaries/windows and all decompressed per-tick observations are byte-identical. The independent
Lab expression inventory retains every prior assertion, log and capture expression, adding only the opt-in probe's
assertion/log; its registry/unit inventory now includes `runner-sampling` (18 selections).

## Whole-runner measurements

Three alternating parent/candidate pairs per rendering context, 12 processes and 48 mode windows. No other native
test or build ran during the series. Both use identical `RunnerSamplingScenario` source and instrumentation:
2,000 warm-up ticks then 10,000 measured ticks at 120 Hz, seed 23, two NPC brains in Ring Out/King/Short Fuse and
the ordinary solo Dissolve fixture. Every measured window retained 10,000 live ticks and the same think count
(20,000 or zero). CPU affinity was 11 for both processes and their engine threads.

```sh
# Omit --headless for the rendered counterpart. Repeat parent then candidate three times for each context.
taskset -c 11 godot-mono --headless --disable-vsync --resolution 1280x720 --path "$CHECKOUT" \
  res://scenes/dev/lab_test.tscn -- --scenario=runner-sampling
```

The probe invokes the actual whole `_PhysicsProcess` callback, including brains, activity, `TunersChanged`, King's
internal retuning, actor sample arrays and rule-command application. It is synchronous: native bodies stay still,
and Jolt/arena/scene callbacks do not advance. The rendered context initializes Vulkan through invisible gamescope;
these callback timings do not measure drawing or establish a full-game FPS gain. Gamescope reports the existing
disabled-vsync fallback and shutdown broken-pipe message. No Godot errors occurred in the accepted probe runs.

Medians of three runs, parent → candidate; times are microseconds. [All per-run data](lab-tuner-sampling.csv).

| Context | Mode | Bytes/tick | Mean µs | p95 µs | p99 µs |
| --- | --- | --- | --- | --- | --- |
| headless | ringout | 2816.00 → 1112.00 | 118.741 → 113.835 | 172.4 → 169.3 | 278.1 → 270.5 |
| headless | king | 5553.55 → 3825.55 | 66.640 → 60.736 | 71.7 → 66.3 | 83.6 → 82.4 |
| headless | shortfuse | 3624.00 → 1680.00 | 45.748 → 39.131 | 50.4 → 42.7 | 63.3 → 54.5 |
| headless | dissolve | 2640.42 → 912.00 | 39.421 → 34.240 | 39.6 → 35.7 | 43.8 → 41.4 |
| rendered | ringout | 2816.00 → 1112.00 | 123.087 → 118.801 | 190.1 → 184.6 | 298.3 → 297.6 |
| rendered | king | 5553.55 → 3825.55 | 68.404 → 62.792 | 82.6 → 76.7 | 104.8 → 100.3 |
| rendered | shortfuse | 3624.00 → 1680.00 | 47.222 → 40.769 | 59.4 → 52.1 | 80.1 → 61.7 |
| rendered | dissolve | 2640.33 → 912.00 | 40.689 → 35.176 | 47.1 → 41.7 | 66.1 → 52.2 |

Every whole-callback median improves: mean 3.5–14.5%, with 1,704–1,944 fewer managed bytes per tick. One rendered
Ring Out p99 sample rose from 298.3 to 302.0 µs (1.2%); the other two pairs and its median improved. There is no
repeatable cost regression. Whole runners still allocate for their other existing work, including King's internal
retuning and actor samples; only unchanged scalar sampling is asserted allocation-free.

## Rendered review and gates

All four rendered invocations passed at 1280×720. Common command:

```sh
godot-mono --disable-vsync --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/lab_test.tscn -- \
  --replay-random --tune.HitstopDuration=0 --capture-dir="$PWD/build/rfp5-NAME" OPTIONS
```

| NAME | OPTIONS | Reviewed captures |
| --- | --- | --- |
| ringout | `--scenario=prototype --mode=ringout --quick --seeds=1` | `ringout_void_1_end.png`: shared-win standings and result controls. |
| king | `--scenario=prototype --mode=king --quick --seeds=1 --overview --shots=10` | `king_void_1_010s.png`, `king_void_1_end.png`: zone/move warning, mint count and timed result. |
| shortfuse | `--scenario=shortfuse-combo` | All seven `shortfuse_*.png`: full health, chip damage, spectating, thrown victim, blast/bullet death, round winner and replacement suburb. |
| dissolve | `--scenario=dissolve --seconds=45 --shots=4,12.5` | All three `dissolve_23_*.png`: running, chain scoring and caught/result screen (553 points, 175 m, 32 tanks). |

Preliminary scaffolding failures were corrected without changing production behavior: the first benchmark used
out-of-range Lab dials; the expanded test initially used a float endpoint below its double schema minimum; formatting
initially required two brace fixes. The failed benchmark and scalar logs remain in `build/rfp5/`. Accepted rendered
runs have the same gamescope vsync warning noted above and no Godot errors.

`make check` passed on `21d1500` (490 unit tests, zero failures, warning-free build, all ordinary Lab/native/world
suites and gate self-tests). `make -j4 test-full` ran once on that same head and failed its final native bot soak.
Logs are `build/rfp5/check-head.log` and `build/rfp5/test-full-head.log`. All preceding ordinary/deep jobs passed,
including the full seeded prototype outcomes, 700/700 navigation courses, 514 dial fixtures built and reset,
550 independently enumerated Lab cases and 1,044 generator cases without duplicate shard coverage. Later changes
extend this record only; production and tests remain at `8f5da5d`.

## Deep-gate network failure and isolation

The final four-client, 90-second faulted soak failed the unchanged 0.50 m final-moving bound. Body `2020000e`
was venting/moving on all peers, but peer 1 differed from peers 2/3/4 by 0.710/0.639/0.672 m at the final shared
grid. It compared 2,000 grids and 263,746 pairs, with no unobserved grids. Final resting drift was 0.001 m;
the longest above-tolerance episode was 0.80 s. All four clients agreed with the server's conservation audit:
`0 initial + 35 map + 42 drops - 56 spills = 21 banked`. All five process logs contain zero engine errors/warnings.
The failed process logs were copied intact to `build/rfp5/full-failed-bots/` before any follow-up.

The full target stopped before its subsequent duel-lane and loading-readiness commands. Diagnostic native runs
use fresh output directories, not `-f`; the original failed run remains a failed gate. A new parent source archive
was independently imported/built for the first comparison, without the sampler or new instrumentation:

```sh
# Run once against the parent, then once against the candidate, with distinct OUTPUT directories.
"$CHECKOUT/tools/dev/net_bots.sh" -o "$OUTPUT" -p 24681 -n 4 -w 0 -l 90 -s '--match-seed=25' -- \
  --melee-start --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3
```

| Run | Result | Grids / body pairs | Final rest / moving m | Longest episode s | Bank audit |
| --- | --- | --- | --- | --- | --- |
| Full gate, candidate | FAIL, 3 final disagreements | 2000 / 263746 | 0.001 / 0.710 | 0.80 | 21, all clients agree |
| `net-parent-1`, `c26eaf7` | PASS | 1999 / 195722 | 0.000 / 0.000 | 1.00 | 34, all clients agree |
| `net-candidate-1`, `21d1500` | PASS | 1998 / 168936 | 0.001 / 0.000 | 0.55 | 19, all clients agree |

Both isolated runs have zero unobserved grids and zero engine errors/warnings. They demonstrate variability,
not a causal explanation of the original vent trajectory. Network code, mode rules, the soak launcher and checker
have empty diffs from the parent; the changed Lab runners are absent from `src/Net` and the native NetTest path.
The failed vent/moving state is consistent with the previously documented
[residual final moving-vent convergence failure](network-corrections.md). That classification is an inference,
not a proof of its contact dynamics. No tolerance, prediction, gameplay or network code was changed for P5.

The two skipped native commands were then run separately against the candidate, with fresh directories:

```sh
tools/dev/net_bots.sh -n 2 -w 0 -p 24681 -l 20 -s '--start-score=8 --match-seed=23' \
  -o "$PWD/build/rfp5/net-duel" -- --scenario=duel --matches=2 --duel-blocked \
  --tune.DropScatter=0 --tune.DropToss=0 --net-lag=120 --net-jitter=30 --net-loss=3
tools/dev/net_bots.sh -n 2 -p 24681 -l 20 -o "$PWD/build/rfp5/net-readiness" -- \
  --scenario=body-stream --tank-check-ready-stall=Bot2
```

Both passed with zero unobserved grids and clean engine logs. Duel: 1,199 grids / 39,194 body pairs,
final rest/moving drift 0.000/0.227 m, longest episode 0.25 s, both clients agree with both bank audits (11 and 10).
Readiness: 600 grids / 16,800 pairs, final drift 0.000/0.000 m, no episodes, both clients agree with the zero-bank
audit. These passes complete the otherwise skipped component checks; they do not turn the failed full invocation
into a pass. No second full sweep was run to seek a green result.

The package changes only Lab runners. No message, shared tuner value, construction or tank behavior changes, and
`Protocol.Version` stays 3. The deep gate remains blocked by the retained network failure. Exact Linux traces do
not prove cross-platform bit identity; the stationary callback probe does not model native physics or streaming
scheduling.
