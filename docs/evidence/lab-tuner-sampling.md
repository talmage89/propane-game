# Lab tuner sampling verification

**Status:** Dated record (2026-10-17, refactor package P5). Not superseded. The current sampling contract is in
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

`make check` passed (490 unit tests, zero failures, warning-free build, all ordinary Lab/native/world suites and
gate self-tests). Final acceptance commands on the evidence commit are `make check` and `make -j4 test-full`, with
logs in `build/rfp5/check-head.log` and `build/rfp5/test-full-head.log`. The full gate includes all Lab dial endpoints,
all-at-once resets, full seeded prototype outcomes, deep generator sweeps and independent shard coverage.

This is a Lab-only optimization. No multiplayer native matrix was needed; no message, shared tuner value,
construction or tank behavior changes, and `Protocol.Version` stays unchanged. Exact Linux traces do not prove
cross-platform bit identity; the stationary callback probe does not model native physics or streaming scheduling.
