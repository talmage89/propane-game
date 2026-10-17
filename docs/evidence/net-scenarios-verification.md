# Network scenario ownership verification

October 16, 2026, Linux, Godot 4.7.2 .NET/Jolt. Parent `128a664`; final code `a4c17b5`.

Parent: `128a664`, Godot 4.7.2 .NET/Jolt, Linux Debug. Evidence lives under the worktree's `build/rfp4*` directories.
All rendered invocations use `godot-mono` through the headless gamescope wrapper. Scratch uses `build/rfp4/tmp`.
The parent matrix runs before source changes and retains command arrays, exit codes, assertion/PASS/FAIL lines and
capture manifests. Failed runs are retained alongside successful ones.

The parent `vent-contact` fixture deterministically fails: its `TakeShot` call omits `current.Player.ActorId`.
`Match.OnTankPunctured` forwards the resulting zero actor/life IDs, and the server rejects all eight punctures with
`integer out of range: 0`; both clients then fail the existing canonical-vent assertion. No checker limit or
production code is changed to conceal this failure.

### Duel / refusal / gather migration

`make setup`, `make build` and `make test` passed (451 unit assertions, zero failures). Focused
`unit_tests.tscn -- --suite=NetScenarioTests` passed all five lifecycle checks after the completion-table update.
The candidate repeated the parent matrix's exact duel/refusal/gather arguments; all three passed, with zero engine
or protocol errors, complete tank coverage and agreeing server audits. Reviewed the candidate gather shimmer
and settled-pile captures against the parent. Native output: `build/rfp4-candidate/{duel,refusal,gather}`.
The scaffold's generic completion bridge is the sole temporary source-inventory addition.

### Pickup migration

`make setup`, warning-free `make build`, the five fake-context lifecycle tests and the source-inventory comparison
passed (only the temporary completion bridge differs). The single rendered client repeated the parent's 25 s
pickup command and passed take, exact ammo, absence and return assertions with an agreeing server audit.
Evidence: `build/rfp4/pickup-*.log`, `build/rfp4-candidate/pickup`.

### Watch migration

Warning-free setup/build, inventory comparison and six lifecycle assertions passed. An added fake-context assertion
requires a watch joiner to return its tick to ordinary bot dispatch. The rendered 15 s watch host and playing
joiner repeated the parent command and passed complete tank/score audits with clean engine logs.
Evidence: `build/rfp4/watch-*.log`, `build/rfp4-candidate/watch`.

### Melee migration

Setup/build (zero warnings), six lifecycle tests and the unchanged source inventory passed. The same rendered
30 s, two-peer 120/30/3-fault fixture with server `--start-score=5 --match-seed=23` passed grip/motion probes,
both throws/get-ups, five/zero-bank spills, complete tank coverage and agreeing audits. Reviewed
`melee_host_1_06.png`: the victim is thrown among five shielded drops, matching the parent presentation.
Evidence: `build/rfp4/melee-*.log`, `build/rfp4-candidate/melee`.

### Movement migration

Warning-free builds, seven lifecycle tests and source-inventory preservation passed. The new fake-context check
requires incomplete observer completion to fail on the host and imposes no observer obligation on the joiner.
The rendered 35 s parent/candidate fixture passed remote gait/support, reload/recoil, grip, feet and motion
checks, plus tank/score audits. Reviewed `remote_02_air.png` and `remote_06_landed_creep.png` against the parent:
airborne and shouldered landing poses retain the same presentation. Evidence: `build/rfp4/movement-*.log`,
`build/rfp4-candidate/movement`.

### Puncture-race migration

Warning-free setup/build, seven lifecycle tests and the unchanged expression inventory passed. The owner's
canonical-vent subscription is installed after host authority telemetry and before assembly, retaining delivery
order. The same rendered 20 s faulted race passed native assertions, complete tank/score audits and
`compare_vents.py`: 280 paired trajectory samples, median/p95/max drift 0.112/0.226/0.358 m.
Evidence: `build/rfp4/race-*.log`, `build/rfp4-candidate/race`.

### Body-stream migration

Warning-free setup/build, seven lifecycle tests and the unchanged expression inventory passed. The same 20 s
native fixture with server `--net-loss=100 --net-body-stall=2000 --match-seed=23` passed owner-motion assertions
on both clients, continuous tank coverage and score conservation with zero engine/protocol errors. Its existing
connection-scoped tank reference is preserved and documented rather than silently reset. Evidence:
`build/rfp4/stream-*.log`, `build/rfp4-candidate/stream`.

### Vent-contact migration — preserved baseline failure

Setup/build (zero warnings), seven lifecycle tests and the unchanged expression inventory passed. The candidate
repeated the parent's exact 30 s mixed-axis/faulted command and failed identically at tick 1200: eight server
actor-zero puncture rejections and one canonical-vent error on each client (exit 1). The omitted shooter ID
remains unchanged. This is preservation evidence, **not a passing native acceptance proof**; P4 cannot satisfy
the zero-error requirement while this preexisting fixture defect remains. The scope question about a separate
fixture correction is pending. Evidence: `build/rfp4/{parent-matrix,vent-native}.log`,
`build/rfp4-{parent,candidate}/vent`.

### Capacity migration

The owner keeps the special last-builder delay, pre-inbox staging/subscriptions, tick schedule and post-navigation
completion phase. Setup/build remained warning-free; seven lifecycle tests and the unchanged expression inventory
passed. The first native extraction trial failed during retirement: its unsubscribe closure evaluated
`Match.Player` after session disposal cleared actors. The corrected closure retains the subscribed player itself;
teardown order is unchanged. The failed trial, including its otherwise agreeing tank/score audit, is retained at
`build/rfp4-candidate/capacity-retirement-failure` and `build/rfp4/capacity-initial-*.log`.

The corrected full 16-client repeat passed with zero engine/protocol errors: 2,489 grids, 41,729,416 body pairs,
no disagreements or overflows, identical 49-bank standings and explicit creator departure/authority transfer.
The delayed client's inbox peaked at 2,879 packets / 338,821 bytes. `check_capacity.py` passed every obligation.
Evidence: `build/rfp4/capacity-{build,lifecycle,native}.log`, `build/rfp4-candidate/capacity`.

### King / late-builder migration

Setup/build (zero warnings), seven lifecycle tests and unchanged expression inventories passed. The 55 s rendered
King fixture passed all hold/contest/mint/warning/move/fresh-pile obligations and audits. Reviewed contest,
settled-pile and 23/0 results frames against the parent. The identical 110 s late-builder command passed: Bot3
replayed zone 1 and 90 minted tanks before readiness, then all peers finished with 217 minted/banked for the host.
The undelayed peers passed 2,399 continuous grids; all three completion inventories/poses agree at 0 m peak drift.
`check_scores.py` and the separate `build/rfp4/late.py` audit passed with no engine/protocol errors. Evidence:
`build/rfp4/king-*.log`, `build/rfp4-candidate/{king,king-late}`, `king-late-audit.txt`.

### Appearance migration

The owner initializes menus and handles lobby/setup checks at the same host callback positions. Lobby screenshots
use connection lifetime, while results screenshots use match lifetime; a new fake-context assertion ensures
lobby callbacks survive a match return and stop at connection disposal. Warning-free builds and all eight
lifecycle tests passed. Both rendered three-peer repeats with duplicate cyan/near-white colors passed exact
lobby/setup/body/drop/results checks and complete audits, with no engine/protocol errors. Reviewed tags/glow and
results captures against the parent. Evidence: `build/rfp4/appearance-*.log`,
`build/rfp4-candidate/{appearance-initial,appearance}`.

### Host completion

The legacy completion bridge, case fields and selection chain are removed. The connection callback is named
`OnMatchStarting`; `_Ready` keeps connection wiring and is 99 lines. `NetTest.cs` is 612 lines (from 1,458),
with the unchanged 95-line roaming-lobby service beside it. All named factories construct owners in fake contexts;
only the default bot path has no scenario. The shared shield-reader regression scene now calls the extracted
utility directly and passes its native impulse/contact/readback checks.

Warning-free build, eight lifecycle tests and `git diff --check` passed. The final source inventory exactly matches
`128a664`: 51 options, 58 complete assertion expressions, 20 direct capture expressions and 82 log expressions.
Against the immediate parent, the only inventory difference is removal of the temporary duplicate completion
bridge. The new guide/tool add two reference sites; all original 122 sites remain available in the parent inventory.
An actual unknown-scenario launch with `--host-server` exits 1 before binding/connecting, the intentional tooling
correction. Evidence: `build/rfp4/{host-*,final-source-inventory,unknown-scenario}.log`.

The broader native and timing results follow. Final gate commands are recorded at the end. The preserved
vent-contact fixture failure remains an explicit blocker to the requested zero-error acceptance.

## Final file ownership

`NetTest.cs` is 612 lines (1,458 before), with a 99-line `_Ready`; its existing 95-line lobby partial remains a host
service. No new scenario derives from a Godot node. Each C# script has its committed `.uid`.

Reviewed movement air/landing, melee throw, King contest/pile/results, appearance tags/glow/results and gather
shimmer/settled captures against the parent. The settled gather view is partly arm-occluded in both versions.

| File under `src/Dev/NetScenarios/` | Lines | Bytes | Owns |
| --- | ---: | ---: | --- |
| `AppearanceScenario.cs` | 138 | 7094 | Connection-owned menus/lobby captures and match-owned setup/body/drop/results proof. |
| `BodyStreamScenario.cs` | 48 | 1884 | Original connection-scoped flight/rest state and native delivery completion. |
| `CapacityScenario.cs` | 192 | 10516 | Last-builder delay, pre-inbox staging, contested bodies/throws/departure and post-navigation final checks. |
| `DuelScenario.cs` | 306 | 15136 | Shared duel/refusal/gather input, clock, spill/blast/gather assertions, captures and subscriptions. |
| `KingLateScenario.cs` | 35 | 1453 | Quiet holder/observer placement and moved-zone/holder/mint completion. |
| `KingScenario.cs` | 104 | 4759 | Snapshot-driven hold/contest/mint/warning/move/pile state, camera, captures and completion. |
| `MeleeScenario.cs` | 129 | 6067 | Two swings, throw/get-up state, probes, input and captures. |
| `MovementScenario.cs` | 216 | 9591 | Remote gait/support/reload/recoil observations, input, probes, captures and host completion. |
| `NetScenario.cs` | 138 | 4855 | Host service contract, per-match phases, completion flags, subscription retirement and connection/match callback guards. |
| `NetScenarioRegistry.cs` | 38 | 2207 | One CLI factory/completion table, default fallback and explicit unknown-name refusal. |
| `NetScenarioTools.cs` | 38 | 1772 | Unchanged aim geometry, drop IDs, shield readback, appearance parsing and monotonic observation time. |
| `PickupScenario.cs` | 105 | 4225 | Pickup approach, exact ammo/take/absence/respawn proof and captures. |
| `PunctureRaceScenario.cs` | 106 | 5466 | Canonical vent observer, tick/path samples, camera and race completion. |
| `VentContactScenario.cs` | 65 | 2930 | Eight isolated vents, tick/axis/contact telemetry and canonical-vent completion; parent defect retained. |
| `WatchScenario.cs` | 51 | 1771 | Host follow camera; returns joiner ticks to ordinary bot dispatch. |

`UnitTests/NetScenarioTests.cs`: 171 lines / 9591 bytes, eight fake-context lifecycle/dispatch/retirement/allocation/coverage checks.
`tools/dev/net_scenario_inventory.py`: 111 lines / 5036 bytes, reproducible documented-command and complete expression inventories.

## Native comparisons after host cleanup

The frozen parent assembly saved before source edits and the final candidate both passed four additional rendered
variants: movement with `--view=player` (35 s), movement with `--view=rifleright --grip-metrics` (45 s), melee with
`--view=player` and both clients rendered (30 s, 120/30/3 faults), and dark/saturated/white appearance colors
`000000,0000FF,FFFFFF` (10 s, all three rendered). Existing grip/motion/feet checks, exact colors and native
audit gates stayed enabled. Reviewed the reload close-up and victim get-up handoff. Full command arrays and
results: `build/rfp4-additional/results.json`; launcher log: `build/rfp4/additional-run.log`.

The clean four-bot 90 s seed-25/`--net-seed=2 --melee-start` soak passed 2,000 grids / 220,474 body pairs,
zero disagreements and 0.124 m final moving drift. Bank and King (`--mint-seconds=0.5`) each passed
`-n 2 -l 20 --lobby-roam=10 --matches=2 --lobby-bots=1`: two audits and three world visits per client.
Bank finished with 29/24 total banked; King with 12/16, including 14/18 minted. The faulted departure run
`-n 4 -l 25 --lobby-roam=10 --lobby-bots=1 --leave-at=12 --leave-who=Bot2 --net-seed=2` with 120/30/3 faults
passed 700 grids / 55,880 pairs, 33 banked and 0.240 m final moving drift across the three survivors.
Every positive run had zero engine/protocol errors.

The actual incomplete `duel` fixture (`-n 2 -l 10`, server `--start-score=8 --match-seed=23`) exited 1, with
only the required-completion assertion on each client. Unknown names also exited 1 before startup. These expected
negative failures are kept separate from normal-run error checks.

Source inventories match exactly. Primary runtime capture-name inventories also match for every selection except
one native boundary frame: the parent `watch` run captured `watch_015.0.png`; the candidate stopped at 13.5 s.
Both use the identical 1.5 s condition while `IsPlaying`: delivery of End can precede or follow the final physics
step at the 15 s boundary. No capture label, timer or condition changed. Other native numeric differences include
random capacity seeds, admission/spawn order and measured correction drift; within-run rosters, identities,
conservation and final poses remain independently checked. Captures and raw outputs are retained in
`build/rfp4/{parent,candidate}-output-inventory.json` and `capture-comparison.json`.

## Paired measurements

Source: `128a664` parent harness versus the final candidate, identical Debug build, Godot 4.7.2 .NET/Jolt.
Temporary probes used preallocated arrays around the actual host callback and the existing `FrameMetrics` physics
boundaries (last physics priority), including native-step tail. No processing node was added. The whole-tick
window is first observed play +10..+20 seconds (about 1,200 actual physics ticks per peer).

Instrumentation SHA256 on both builds:
`90904cb329ee2bad352a23420c7905f191a91df0aca3d5f40dbf7153732ad410`.
Both strict instrumented builds had zero warnings. Source snapshots, probe insertion, binaries, process/load
snapshots, per-tick allocation/cost CSVs and exact command arrays are retained under `build/rfp4` and
`build/rfp4-paired`. Three alternating parent/candidate pairs ran headlessly, then three with one rendered client:
`net_bots.sh -n 2 -l 30 -w 0|1 -t clean -s '--match-seed=23' -- --scenario=body-stream --human=idle --frame-metrics=DIR`.
All twelve passed complete continuous tank and score audits with zero engine/protocol errors.

Medians of the three runs per role; values are mean / p95 / p99 microseconds for the whole physics stage:

| Mode / peer | Parent | Candidate |
| --- | --- | --- |
| headless-host | 330.39 / 458.74 / 548.87 | 339.94 / 474.65 / 586.63 |
| headless-observer | 669.46 / 865.63 / 981.00 | 680.96 / 899.86 / 1001.54 |
| rendered-host | 389.46 / 494.68 / 572.78 | 381.90 / 505.60 / 570.84 |
| rendered-observer | 713.56 / 909.10 / 989.54 | 711.47 / 930.35 / 1036.71 |

Observer-heavy whole-tick timings vary in both directions across pairs; the modest headless increase and some
tail increases are retained rather than hidden. To isolate host dispatch from opt-in tank-observer formatting/I/O,
three more alternating headless pairs used the same compiled probes and native body-stream workload without
`--tank-check` or frame observers. All six passed the scenario's native owner-motion assertions and separate
score/conservation/engine-error checks; continuous tank sampling is intentionally absent only in this focused
measurement. This is additional timing evidence, not a replacement for the twelve fully audited pairs.

Focused host callback medians, mean / p95 / p99 microseconds:

| Peer | Parent | Candidate |
| --- | --- | --- |
| focused-host | 6.848 / 11.059 / 18.309 | 6.537 / 10.418 / 17.999 |
| focused-observer | 4.081 / 5.428 / 6.349 | 3.854 / 5.540 / 6.189 |

All focused runs allocated on exactly six existing digest ticks per peer; every other warmed host tick allocated
zero bytes (roughly 2,900 measured ticks each). The focused host mean is lower in all three pairs; the observer
mean is lower in two and 1.2% higher in the third. This supports no added steady allocation or repeatable host
dispatch regression; broad whole-stage results remain subject to native scheduling and observer noise. The eight
fake-context tests also require zero allocation across 10,000 warmed dispatches.

Every temporary probe was removed. A forced non-incremental warning-as-error build restored the clean assembly
and verified that its probe literal is absent before repeated capacity/final gates. Final source remains free of
benchmark logging or cadence changes.

## Repeated capacity acceptance

All six repetitions completed their native and dedicated checks: three clean and three 120/30/3-faulted 120 s
matches, each with 16 distinct clients and one rendered creator. Every repetition passed frozen
actor/peer/spawn/appearance identity, scaled starts/supplies, contested claims/punctures, victim-owned throws,
delayed inbox use, creator departure, authority succession, full coverage, final per-actor standings, resource
reporting and server conservation. No engine/protocol errors or inbox overflows occurred.

The first `make test-capacity` session ended with status 143 during the final dedicated checker, after all native
matches and their tank/score audits had passed. That checker was completed separately from the saved logs.
The interrupted command and its separately completed checker are retained in `build/rfp4/capacity-acceptance.log`,
`capacity-interrupted.json` and `capacity-last-check.log`. Complete per-run results are in
`build/rfp4-capacity-interrupted/<row>/acceptance.txt`.

The whole unchanged target is rerun with a durable launcher; its actual command/status/duration are recorded in
`build/rfp4/capacity-acceptance.json`, output in `capacity-acceptance-complete.log`, and all native logs in
`build/a6-capacity/`. The final repository gates wait for that command to exit successfully.

| Repetition in the first batch | Tank comparison | Result |
| --- | --- | --- |
| clean-1 | 2485 grids, 41657124 body pairs | PASS |
| clean-2 | 2487 grids, 41684344 body pairs | PASS |
| clean-3 | 2486 grids, 41673389 body pairs | PASS |
| faulted-1 | 2476 grids, 41425526 body pairs | PASS |
| faulted-2 | 2486 grids, 41591873 body pairs | PASS |
| faulted-3 | 2485 grids, 41575839 body pairs | PASS |

## Final gate commands

The final documentation commit is followed by these exact commands, in order, on that unchanged head:

```sh
make check
make test-equivalence
make -j4 test-full
```

`build/rfp4/run_final.py` records the actual head, command, elapsed time and exit code in
`build/rfp4/final-status.json`; full outputs are `final-check.log`, `final-equivalence.log` and `final-full.log`.
The full gate includes the separate faulted four-bot 90 s soak, blocked-lane duel across two matches and native
body-stream readiness stall; their ordinary convergence and conservation gates remain unchanged.
No release references were regenerated, no Make target or checker threshold changed, and all production, design,
wire/schema/world/tank/tuner code is unchanged (`Protocol.Version` remains 3).

## Remaining limitations

The preserved vent-contact actor-zero fixture defect prevents a clean native acceptance result for that selection;
its separate correction question remains pending. Body-stream retains its original connection-scoped tank reference
and therefore its existing repeated-match limitation. Whole-stage timings include native scheduling/observer noise;
raw increases and failed trials remain available. Linux validation does not establish cross-platform timing identity.

## Commits

- `a527ac6` Give duel, refusal and gather an owned network scenario
- `3841088` Move the ammo pickup fixture into its own network scenario
- `a362746` Give the network watch camera its own scenario
- `e23181b` Own the network melee fixture's input and recovery proof
- `173b7f5` Own the remote movement scenario and its host completion check
- `d2c40a8` Move puncture-race reconciliation into its network scenario
- `622dfc7` Move the body-stream lane fixture into an owned scenario
- `89adde4` Give the native vent-contact fixture an independent owner
- `c19a741` Own capacity staging, delayed loading and departure completion
- `92942c4` Give King and its late-builder proof independent scenario state
- `21105ed` Own appearance checks across lobby, match and results
- `a4c17b5` Finish the network host lifecycle and remove legacy scenario dispatch
