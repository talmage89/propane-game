# Ledger live count verification

**Status:** Dated record (2026-10-17, B1a). All required checks passed. See [the accounting contract](../multiplayer.md)
and [test procedure](../code-quality.md#ledger-accounting-checks).

Parent: `02f6c00f31ae31026711804e0e6ca33bd40723d8`. Production candidate: `a9d8cb1da49f2c6bac29e4469e557ceaadc95c23`
(branch `wp/rf-b1a-ledger-count`); harness and evidence are in the following commit.
Linux, Godot 4.7.2 .NET/Jolt, SDK 10.0.112, normal Debug. No release references changed.
Raw logs and the alternating build driver are under `build/rfb1a-scratch/`.

## Differential accounting

`TankLedgerTests` checks the exact parent scan over the private canonical dictionary after every operation,
independently of the maintained count and `Live()`. It also compares the sorted live snapshot and emits digests
of canonical encounter order, authority, lifecycle, stream sequence and audit counts. The scripted path makes
explicit assertions about refusal reasons, count changes, retained dependencies and map/drop detonation totals.

All 8,480 operations pass: 287 scripted operations, one empty-plan refill, and 16 seeds × 512 randomized operations.
Cases include map tanks plus a car and a skipped non-car prop, invalid/refused atomic drops, reused tombstone IDs,
invalid/oversized/empty refills, claims, punctures, wrong phase, stale epoch, unknown bodies/dependencies, duplicate
requests, wrong causal actors, dependent detonations, transfers (including self/repeated transfer), updates,
gathers, 64 refill/transfer/detonation cycles, depletion to zero and refill after zero.

The same instrumentation runs against parent source and candidate source in the same checkout, alternating
parent then candidate three times. Each build runs both headless and rendered. All 12 processes exit 0 with three
tests and zero failures; all 18 sequence digests agree exactly across every process. No release fixture is changed.
The driver restores and rebuilds candidate source in a `finally` block. The source copies, build logs, process logs
and driver remain in `build/rfb1a-scratch/` (`parent.cs`, `candidate.cs`, `paired.py`, `bench-*.log`).

## Paired costs

Commands for each source revision (see the identical checked-in opt-in instrumentation and the retained driver):

```sh
make build
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=TankLedgerTests --ledger-bench
godot-mono --disable-vsync --resolution 960x540 --path . res://scenes/dev/unit_tests.tscn -- --suite=TankLedgerTests --ledger-bench
```

Each case retains 60 live tanks and the stated number of tombstones. It warms 128 calls, then samples 1,024 times.
Getter samples batch 16 reads; server samples advance the real server's complete `AdvanceMatches` stock tick by
0.5 s with an inert send sink. Both sources allocate **0 bytes/read** and **1,120 bytes/server tick** in every case.
The candidate adds no steady allocation. Rendered means the enclosing engine process uses the renderer; this
synchronous loop does not measure frame rendering, Jolt or socket polling. Native soaks below cover actual delivery.

All cells below are mean/p95/p99 microseconds. Raw tick differences (rather than rounded `TimeSpan` values) retain
sub-microsecond resolution. Getter candidate measurements are near timing overhead, so they establish constant
cost rather than a precise nanosecond estimate. No repeatable whole-tick regression appears at any history size.

| Pair/mode | Tombstones | Parent read | Candidate read | Parent server tick | Candidate server tick |
| --- | ---: | --- | --- | --- | --- |
| 1/headless | 0 | 0.4890/0.4947/0.4984 | 0.0047/0.0048/0.0054 | 12.5377/7.8850/8.5290 | 7.0514/7.5300/9.1480 |
| 1/headless | 4096 | 24.7247/25.1721/27.0394 | 0.0048/0.0049/0.0056 | 32.3894/32.9460/36.9930 | 10.5493/7.5130/9.2290 |
| 1/headless | 16384 | 98.2315/102.5887/105.7530 | 0.0049/0.0050/0.0056 | 102.0077/104.3690/105.3220 | 6.9589/7.3640/9.6600 |
| 1/rendered | 0 | 0.5117/0.5174/0.8236 | 0.0048/0.0049/0.0056 | 7.8132/8.3610/8.8130 | 7.3829/7.8990/9.3040 |
| 1/rendered | 4096 | 27.6277/28.8546/29.1798 | 0.0049/0.0051/0.0057 | 34.1524/34.8910/37.0270 | 10.7486/7.5870/21.7000 |
| 1/rendered | 16384 | 86.1655/89.5727/96.4557 | 0.0050/0.0051/0.0056 | 86.5471/89.6290/104.3550 | 6.8701/7.2820/7.5340 |
| 2/headless | 0 | 0.4865/0.4929/0.4959 | 0.0048/0.0049/0.0054 | 7.5109/7.9330/8.2970 | 7.1529/7.7260/8.2530 |
| 2/headless | 4096 | 25.7235/26.5201/27.9837 | 0.0048/0.0049/0.0054 | 34.5381/35.1710/48.9890 | 7.0347/7.4420/7.6300 |
| 2/headless | 16384 | 100.4669/104.4731/107.7924 | 0.0048/0.0049/0.0054 | 100.7570/115.5050/124.3060 | 7.1555/7.3290/21.5850 |
| 2/rendered | 0 | 0.4960/0.4918/1.3641 | 0.0050/0.0061/0.0069 | 7.8283/8.0970/23.7890 | 7.0409/7.5700/10.1240 |
| 2/rendered | 4096 | 26.9831/28.6735/30.8158 | 0.0048/0.0050/0.0056 | 34.8982/38.2670/53.1440 | 6.9617/7.2970/9.9170 |
| 2/rendered | 16384 | 86.5942/92.1241/97.8215 | 0.0052/0.0061/0.0070 | 91.5705/103.4580/127.7480 | 7.1595/7.7100/10.8530 |
| 3/headless | 0 | 0.5032/0.5084/0.5184 | 0.0048/0.0049/0.0057 | 7.7206/8.1230/21.7100 | 7.3499/7.9200/8.4760 |
| 3/headless | 4096 | 24.6421/24.7170/26.8578 | 0.0048/0.0049/0.0056 | 29.9128/30.4320/32.0170 | 10.9113/7.6780/7.9740 |
| 3/headless | 16384 | 94.1045/103.9213/115.4986 | 0.0049/0.0050/0.0056 | 96.5668/126.5860/127.9040 | 6.7749/7.2180/8.0040 |
| 3/rendered | 0 | 0.5045/0.5091/0.5127 | 0.0049/0.0051/0.0057 | 13.0689/8.5250/11.0560 | 6.9052/7.3340/7.7210 |
| 3/rendered | 4096 | 23.6509/25.1802/26.1786 | 0.0049/0.0051/0.0056 | 31.1656/31.7830/33.8780 | 6.9220/7.0710/23.1890 |
| 3/rendered | 16384 | 94.0628/98.0166/100.4839 | 0.0049/0.0049/0.0056 | 108.4876/119.3960/129.5090 | 6.7628/7.0750/21.2060 |

Pilot logs are retained too. The first pilot used `Stopwatch.GetElapsedTime`, whose `TimeSpan` rounding hid the
candidate's sub-100 ns batch cost; only the corrected raw-timestamp measurements above are acceptance evidence.
The initial harness build failed on four API-name errors; these were corrected before any parent/candidate series.
Rendered wrapper logs include gamescope's shutdown Wayland broken-pipe message; no Godot `ERROR:` or unit failures
occurred in any of the 12 runs.

Both deliberately broken variants were rejected: removing only the increment, then only the decrement, made both
accounting tests fail and each process exit 1 (`negative.py`, `negative-{increment,decrement}.log`). Candidate source
was restored and rebuilt afterward. The first `make check` stopped at two IDE0011 missing-brace warnings in the
new scripted test loops; `dotnet format Propane.sln --include src/Dev/UnitTests/TankLedgerTests.cs` added only braces
and a blank line. The paired measurements above precede that formatting-only correction.

## Release equivalence

`make test-equivalence` passed, including its six checker self-tests, seven reference unit tests (including platform references) and all
five exact Lab traces (Bank, King, spill/chain, Short Fuse, controlled Dissolve retirement). Evidence is retained in
`build/rfp0-equivalence-9/`; the command log is `build/rfb1a-scratch/equivalence.log`. Reference fixtures and their
manifest remain unchanged. The immediate-parent comparison is the differential operation transcript above, since
the release traces do not specifically stress ledger tombstone history.

## Native soaks and captures

Both commands use a new output directory, normal Debug, four independently connected smart bots, seed 23 and one
rendered client. All other clients and the server are headless. Each match lasts 90 seconds plus results settlement.
No checker is skipped and no tolerance is changed.

```sh
tools/dev/net_bots.sh -n 4 -l 90 -w 1 -t clean -s '--match-seed=23' -o "$PWD/build/rfb1a-clean" -- --capture-dir="$PWD/build/rfb1a-clean-shots" --shot-every=30
tools/dev/net_bots.sh -n 4 -l 90 -w 1 -t faults -s '--match-seed=23' -o "$PWD/build/rfb1a-faults" -- --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/rfb1a-faults-shots" --shot-every=30
```

The clean run exits 0: 1,999 grids, 132,477 body pairs, zero unobserved pair grids and zero disagreements.
Longest above-tolerance episode is 0.75 s; final resting/moving drift is 0.001/0.082 m. Its complete audit is
`0 initial + 36 map + 51 drops + 0 minted - 67 spills = 20 banked`; all four bots agree with the server.
There are zero engine/protocol errors. Logs are in `build/rfb1a-clean/`, checker output in
`build/rfb1a-scratch/net-clean.log`.

Reviewed `build/rfb1a-clean-shots/Bot1_030.0.png`, `Bot1_060.0.png` and `Bot1_090.0.png`: normal suburb play,
remote actors, blast effects, bank totals and the running/end countdown. The `000.0` image is a uniform gray
capture before the first rendered frame; it is retained but is not visual acceptance evidence.

The faulted run also exits 0: 2,000 grids, 109,853 body pairs, zero unobserved pair grids and zero disagreements.
Longest above-tolerance episode is 0.85 s; final resting/moving drift is 0.009/0.129 m. Its complete audit is
`0 initial + 35 map + 62 drops + 0 minted - 68 spills = 29 banked`; all four bots agree with the server.
There are zero engine/protocol errors. Logs are in `build/rfb1a-faults/`, checker output in
`build/rfb1a-scratch/net-faults.log`. All five logs from each soak were checked again after launcher cleanup;
none contains an engine/protocol error.

Reviewed the faulted run's `Bot1_030.0.png`, `Bot1_060.0.png` and `Bot1_090.0.png` in
`build/rfb1a-faults-shots/`: suburb, player/remote actors, venting drops, effects, score and timer remain visible.
These are visual smoke checks, not proof of count equivalence; the operation tests and audits provide that proof.

## Final gate

`make setup` imported the new script and generated its committed `.uid`; `make build` passed with zero warnings.
After the formatting-only correction, `make check` passed in full with zero compiler/analyzer warnings: formatting and a full rebuild,
all `make test` partitions, and the gate self-tests (`make test-infra`). The discovered unit suite ran **492 tests,
zero failures**, including ServerTests, ParticipantServerTests, ParticipantTests, KingOnlineTests, LobbySupplyTests,
LobbyWorldTests and TankLedgerTests. All eleven Lab checks, physics/gather checks, menu/device/input checks,
player probes, generator sweeps, four separate-process formation hashes and five runs of twelve segment hashes
passed. No release fixture or design file changed.

The gate deliberately emits two Godot `ERROR:` lines in `CaptureTests` (failed directory/save probes) and one
`WARNING:` in `LifecycleTests` (an intentionally truncated Score message). These existing negative-test diagnostics
are not build warnings or native soak failures. A supplementary blanket log scan initially rejected them; inspection
of the named tests confirmed all three are intentional. No checker or negative test was changed.

The final formatted harness reproduced all 18 parent sequence digests in the full gate, checked separately against
`bench-1-parent-headless.log`. Full gate output is `build/rfb1a-scratch/check.log`; the earlier failed lint is retained
as `check-initial.log`. Transcript comparisons are in `compare.log` and `final-transcripts.log`. `git diff --check`
passed. No required verification remains outstanding for this package.

## Limits and compatibility

This is server accounting only. Canonical tombstones and explosion dependencies remain retained; `Live()` sorting
and transfer traversal are untouched. Future lifecycle/registration paths must maintain the count too; the scan
oracle should be extended with their operations. This package makes no claim about tombstone memory reduction.

No rule, tuner, message, wire byte, generated world or RNG behavior changes. `Protocol.Version` stays 3.
Verification is Linux only; Windows and macOS were not run. The opt-in benchmark measures controlled server
processing rather than full live frame time; native soaks establish convergence and conservation under delivery.
