# Body publication buffers (P3, October 2026)

**Acceptance incomplete:** behavior preservation, gates, native correctness and controlled allocation measurements
passed. The required three-pair native performance matrix still needs a reserved CPU/GPU window. Do not treat
this report as full performance acceptance.

Production baseline: `b3a21e9`; immediate parent and identical measurement instrumentation: `da1a88a`.
Candidate: `86ce487`. Linux, Godot 4.7.2 (`ed1daf0bf`), .NET SDK 10.0.112, ordinary Debug assemblies.
The baseline ran from an isolated source archive under `build/`, with its own imported state and user data.
The archived collector, Match, assembly wiring and measurement source were checked byte-for-byte against
`da1a88a`. No other worktree was modified.

`BodyPublisher` owns reusable entry lists and traffic groups; `BodySync.Collect` fills caller-owned lists.
The existing callback position, collection decisions, pose sampling time, traversal and traffic encounter order,
batch boundaries, checkpoints, sequences, pending creation suppression and synchronous retirement remain intact.
Packet writers and transport payload copies still allocate. There are no wire, tuner, world, gameplay or protocol
changes (`Protocol.Version` remains 3).

## Equivalence and gates

- `make setup` and `make build`: zero warnings/errors.
- Parent `make test`: the initial run failed the existing navigation zero-allocation assertion. The standalone
  `--suite=Navigation` repeat passed all 30 tests; the full parent repeat passed. The initial failed log remains
  `build/rfp3-parent-test.log`; no assertion or production navigation code changed.
- Candidate `make check`: passed formatting, warnings-as-errors build, all 426 unit tests, scripted mode rules,
  native behavior/menu/input probes, bounded generator sweeps, separate-process world/segment checks and gate
  self-tests. Real device delivery passed 165 assertions.
- `make test-equivalence`: passed all frozen wire/schema/world/publication references and Bank, King,
  spill/chain, Short Fuse and controlled Dissolve traces. Evidence: `build/rfp0-equivalence-3`.
- Focused `unit_tests.tscn -- --suite=AuthorityTests` and `--suite=TransportTests`: 58 and 25 tests passed.
- `--suite=PublicationReferenceTests`: the immutable 12-step, 48-packet release transcript passed before and
  after the change. None of the release fixtures or their manifest changed.
- `--suite=BodyPublicationTests --publication-transcript=PATH`: both revisions passed the added synchronous
  sampling/retirement and empty-collection encounter-order checks. `cmp` agreed; transcript SHA256 is
  `a859dbdadacf41d7cbbf6e76edd2da7f885d0f35731aaf11650bcf5431fba983` on both.

All Godot commands used `godot-mono` by name. Scratch, logs and captures were under this checkout's `build/`,
with `TMPDIR` directed there. Full gate logs are `build/rfp3-check.log`, `build/rfp3-parent-test-repeat.log` and
`build/rfp3-equivalence.log`; targeted logs use the same `rfp3-` prefix.

## Controlled measurements

Run the same command in each built/imported revision, alternating parent/candidate three times:

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=BodyPublicationTests --publication-bench
```

There are 256 fixed awake native bodies (128 classified tanks, 128 cars), default 15/6 Hz publication,
manual 120 Hz network time, 2,000 warm-up ticks and 10,000 measured ticks per window. The sink consumes packets
synchronously without recording or decoding. Measurements include the actual writers and payload copies.
The whole-callback window invokes `Match._PhysicsProcess`, including correction, actor sends and pickup checks;
it does not advance Jolt or other scene nodes. These are controlled callback costs, not native engine tick costs.

Every publication window sent 13,188 packets / 14,533,820 bytes. Every whole-callback window sent
15,688 packets / 14,781,320 bytes, including player poses. Packet counts and payload sizes matched in every pair.
Observed one-minute load throughout these runs was 2.85–3.08.

| Path / pair | Parent B/tick | Candidate B/tick | Parent mean/p95/p99 (µs) | Candidate mean/p95/p99 (µs) |
| --- | ---: | ---: | --- | --- |
| Publication 1 | 7,842.72 | 6,144.67 | 39.963 / 149.2 / 276.1 | 42.131 / 158.3 / 285.3 |
| Publication 2 | 7,842.69 | 6,144.79 | 40.766 / 160.1 / 289.6 | 39.908 / 145.4 / 274.0 |
| Publication 3 | 7,842.72 | 6,144.45 | 42.291 / 164.4 / 296.4 | 42.069 / 150.6 / 280.2 |
| Match physics 1 | 8,236.72 | 6,538.21 | 44.849 / 153.5 / 280.7 | 45.584 / 156.8 / 279.9 |
| Match physics 2 | 8,236.72 | 6,538.21 | 45.004 / 155.5 / 286.2 | 44.407 / 151.8 / 280.0 |
| Match physics 3 | 8,236.72 | 6,538.21 | 44.903 / 157.1 / 283.8 | 43.646 / 151.1 / 276.3 |

The collector/publisher saves about 1,698 B/tick (21.7% including unchanged packet allocation costs).
Mean publication time varies across pairs; whole-callback mean and tail costs show no repeatable regression.
Raw samples and load observations: `build/rfp3-micro-{1,2,3}-{parent,candidate}.{log,load.json}`.

## Native verification

The native matrix uses fresh `-o` directories and the unmodified tank and score checkers. Faulted runs use
`--net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3`. Rendered runs use the invisible GPU wrapper,
`--disable-vsync` and 960×540 captures. Scenario commands, results and captures are recorded below.

Each row is `tools/dev/net_bots.sh OPTIONS -o "$PWD/build/rfp3-NAME" -- ARGUMENTS`; append the fault arguments
above to faulted rows. Rendered rows also pass `--capture-dir="$PWD/build/rfp3-NAME/shots"`.

| Name | Options | Arguments | Result; final rest/moving error |
| --- | --- | --- | --- |
| body-stream | `-n 2 -l 20 -t clean -s '--match-seed=23'` | `--scenario=body-stream` | PASS; 0.000 / 0.000 m |
| readiness | `-n 2 -l 20 -t clean -s '--match-seed=23'` | `--scenario=body-stream --tank-check-ready-stall=Bot2` | PASS; 0.000 / 0.000 m |
| puncture | `-n 2 -l 20 -w 2 -s '--match-seed=23'` | `--scenario=puncture-race` | PASS; 0.000 / 0.112 m |
| refused-drop | `-n 2 -l 10 -w 2 -s '--start-score=8 --match-seed=23'` | `--scenario=drop-refusal` | PASS; 0.000 / 0.000 m |
| gather | `-n 2 -l 45 -w 2 -s '--start-score=8 --match-seed=23'` | `--scenario=gather --tune.DropScatter=.2 --tune.DropToss=1` | PASS; 0.000 / 0.000 m |
| departure-repeat | `-n 3 -l 35 -s '--match-seed=23'` | `--lobby-bots=1 --leave-at=15 --leave-who=Bot1 --melee-start` | PASS; 0.000 / 0.146 m |
| bank-clean | `-n 4 -l 90 -t clean -s '--match-seed=25'` | `--melee-start --tank-supply=1` | PASS; 0.000 / 0.210 m |
| bank-fault | `-n 4 -l 90 -s '--match-seed=25'` | `--melee-start --tank-supply=1` | PASS; 0.001 / 0.244 m |
| king-clean | `-n 4 -l 90 -t clean -s '--match-seed=302'` | `--mode=king --melee-start --tank-supply=1` | PASS; 0.000 / 0.222 m |
| king-fault | `-n 4 -l 90 -s '--match-seed=302'` | `--mode=king --melee-start --tank-supply=1` | PASS; 0.000 / 0.438 m |
| roaming | `-n 4 -l 25 -s '--match-seed=301'` | `--lobby-roam=10 --matches=3 --lobby-bots=1` | PASS, three matches; 0.001 / 0.256 m |

All completed rows had complete server/client bank audits, zero final disagreements and zero engine/protocol errors.
The eleven completed commands cover thirteen match audits; departure checks compare the surviving clients and
retain departed actors' banks in the standings. Roaming returned to a fresh lobby world between every match.
Puncture-race's own assertions collected 101 path samples per peer (maximum owner correction error 0.465 m).
`python3 -B tools/dev/compare_vents.py build/rfp3-puncture/bot1.log build/rfp3-puncture/bot2.log` also passed:
281 aligned trajectory pairs, median/p95/maximum drift 0.098 / 0.208 / 0.322 m, identical canonical vent.

The first departure run passed its tank and score checks, but the orchestration process received SIGTERM before
recording the launcher's return. Its logs remain in `build/rfp3-departure`; the fresh `departure-repeat` run proved
the full command's clean exit. No game or launcher change was made in response.

Reviewed 960×540 captures:

- `build/rfp3-puncture/shots/Bot1_race_300.png` and `Bot2_race_600.png`: the canonical vent is visible on both
  peers, with moving jet/fire presentation.
- `build/rfp3-refused-drop/shots/victim_drop_refused.png`: one accepted tank remains beside the victim, bank 7;
  the refused prediction has been removed.
- `build/rfp3-gather/shots/gather_host_shimmer.png`, `gather_host_settled.png` and `gather_join_settled.png`:
  two loose tanks progress into a stable upright gathered group. Both peers also asserted both glides and final rests.

The launcher summaries are `build/rfp3-NAME-launcher.log`; full peer/server logs and captures remain in each
named directory. The scenario batch inventory is in `build/rfp3-scenario-matrix.log` and `build/rfp3-remaining-matrix.log`.

## Native timing method

The alternating native runs use the same Debug assemblies and existing `FrameMetrics` instrumentation on both
revisions. Each command is:

```sh
tools/dev/net_bots.sh -n CLIENTS -l 35 -w RENDERED -t clean \
  -s '--match-seed=SEED --tune.MaxPlayers=16' -o "$PWD/build/rfp3-perf-CASE-PAIR-VARIANT" -- \
  --tank-supply=1 --lobby-bots=BOTS --melee-start --net-seed=2 \
  --frame-metrics="$PWD/build/rfp3-perf-CASE-PAIR-VARIANT/frames"
```

| Case | Clients | Lobby bots per client | Actors | Seed | Rendered clients |
| --- | ---: | ---: | ---: | ---: | ---: |
| 16h | 2 | 7 | 16 | 301 | 0 |
| 4h | 4 | 0 | 4 | 302 | 0 |
| 4r | 4 | 0 | 4 | 302 | 1 |

Supply is fixed at 1× (84 initial tanks for 16h, 36 for the four-actor cases). `perf` lines and full frame CSVs
are retained. Frame summaries use the common playing interval across all clients, excluding its first ten seconds;
percentiles select `floor((N - 1) * p)`. This covers roughly 25 seconds per client per run. Physics/process columns
are the existing engine monitor values sampled by `FrameMetrics`; wall columns measure actual process-frame
intervals. Match allocation totals sum every client's `match gc` line and include scene assembly, telemetry and
action-dependent effects. The controlled benchmark above isolates the collector/publisher allocation comparison.

Load is sampled once per second throughout each native command. Timing windows reaching load 4 are excluded.
The initial start check used load below 2.7; a stricter no-other-engine check then delayed runs behind light
headless checks. Subsequent runs require ten seconds below load 2.7 with no build or rendered engine job; the
acceptance threshold remains load below 4 throughout the measured window. Rendered measurements keep the GPU free.

All attempts are retained. The original 16h pair 2 candidate exceeded load 4 (maximum 5.12) and is excluded from
timing comparison, despite passing all game audits. An extra unpaired baseline (16h pair 3) also remains recorded.
The initial timing batch was explicitly stopped between native commands before resuming in fresh directories.

The final attempt also encountered a competing workload: 16h baseline 5 reached load 4.06, and 4h candidate 1
reached 5.61. Both passed their game audits but are excluded from paired timing conclusions. All attempted native
timing commands passed the unchanged game audits with zero engine/protocol errors. No reference, test assertion,
convergence threshold or load-4 acceptance limit was relaxed. The orchestration was stopped while idle; no P3
native processes remain running.

Two valid 16h pairs completed. Engine monitor times are milliseconds; allocation totals are decimal MB across
both clients for the whole match, rather than allocation per physics tick.

| Pair / revision | Frames | Window max load | Physics mean/p95/p99 | Process mean | Wall p99 | Allocated MB |
| --- | ---: | ---: | --- | ---: | ---: | ---: |
| 1 parent | 2,996 | 3.80 | 7.562 / 13.821 / 16.430 | 6.058 | 21.239 | 433.0 |
| 1 candidate | 2,992 | 2.99 | 7.019 / 13.097 / 15.960 | 6.113 | 20.916 | 449.3 |
| 4 parent | 2,994 | 1.92 | 6.702 / 11.481 / 16.975 | 4.830 | 20.372 | 451.1 |
| 4 candidate | 2,997 | 1.49 | 6.343 / 11.447 / 18.039 | 5.266 | 20.112 | 440.3 |

Native actions vary between runs; allocation totals include different numbers of spills and effects. These two
pairs do not establish completion of the requested matrix. Still required: one additional valid 16h pair and
three valid pairs each for 4h and 4r, alternating the same baseline/candidate and instrumentation below load 4.
No 4r timing run was attempted. The 4h baseline completed, but its candidate's contended timing is unqualified.

Raw logs, frame CSVs, command arguments and one-second load traces remain in `build/rfp3-perf-*`.
`build/rfp3-native-metrics.json` retains all parsed attempts, including rejected timings; `build/rfp3-analyze.py`
records the calculation. The isolated baseline source/import copy was removed after collecting these results;
recreate it from `da1a88a` and run `make setup` before continuing. Production source remains `86ce487`.
