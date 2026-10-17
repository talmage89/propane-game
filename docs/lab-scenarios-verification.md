# Lab scenario extraction verification

Parent: `1019775`. No production or design files, release fixtures, Make targets or checker thresholds change.
Raw logs, capture manifests and source inventories are retained under `build/rfb7a/`. The temporary runnable
parent snapshot is removed after the final comparisons.
The parent source inventory was recorded before editing the harness. The independent source scan records all
17 selections, 52 options, 146 assertions, 46 log expressions and 34 capture expressions. Parent native listings
contain 11 lifetime pairs and 514 dial fixtures (the older 530-fixture documentation predates the current tuner set).

## Migration proofs

- Lifetime/tracking: warning-free `make build`; `LabScenarioTests` (3 assertions); native
  `lab_test --scenario=lifetime --cycles=1`: all eleven pairs and both tracking permutations pass. Final native
  node/object/orphan counts and retired/reset wrapper counts match the parent. The source inventory comparison
  against `1019775` is unchanged. Frame waits and collection remain direct calls to the host's original Tasks.
  A first unit invocation raced an unfinished build and loaded the parent assembly (no matching suite); the
  post-build invocation discovers and passes the new suite. The failed log is retained.
- Dials: warning-free build; all 514 native fixtures pass in four shards (129/129/128/128), each built and reset.
  The complete `--list-dials` output is byte-for-byte identical to the parent, and the expression inventory is unchanged.
- Frames: warning-free build; rendered full review passes with exactly the parent's 49 capture names, 30 PNGs
  byte-identical. Reviewed main menu, player and controller controls: remaining differences are unseeded menu
  particles and active duel effects/HUD values. The first separate input probe collided with the ongoing gate's
  UDP port and failed at host-browser creation; its log remains. The isolated-port repeat is recorded separately.
  Isolated `menu_test --scenario=input --classic-lobby --port=24781`: 165 assertions, zero failures.

The first normal `make test` session reached 459 unit assertions and all eleven fast Lab sessions successfully,
then its execution session terminated during the later behavior probes. The raw log is retained as
`test-lifetime.log`; this is not claimed as a complete gate. Final gates use a detached supervisor with exact
revision, command, exit status and elapsed time in `final-status.json` to outlive the tool session.
- Banking family: warning-free build; native rules, blocked-aim, 15-second driven duel and seeded 40-second
  nine-actor suburb soak all pass. The seeded soak's idle and bank-result lines match the parent exactly;
  source assertions/options/logs/captures are unchanged. The three owners isolate the scripted player,
  blocked King world and bank-rule fixture without changing shared mechanics.
- Ring Out/King: warning-free build; quick King and two Ring Out seeds pass. An additional seeded Ring Out
  parent/candidate detailed trace agrees exactly for all 889 ticks (two windows, 24 tanks), supplementing P0's
  missing Ring Out case. Four lifecycle tests pass, including fixture-scoped detach before collection. The owner
  releases its per-seed observer before the original collection call and disposes it on early exit/failure too.
- Short Fuse: warning-free build; complete quick match and scripted combo pass. The seeded rendered combo
  retains all seven parent capture labels; reviewed spectating and round transitions agree (two PNGs are identical;
  particle/debris frames vary). Observer cleanup follows each replaced runner and the seed's existing collection
  boundary. Assertion/options/log/capture inventory is unchanged.
- Effects/stack: warning-free build; rendered eight-tank effects and maximum 512-tank stackfire pass with clean
  engine logs. Nine effect and eight stack capture names match the parent; vent, smoke and initial stack PNGs
  are identical. Reviewed chain/aftermath frames retain the fixture and effect presentation. Default entropy and
  wall-clock hitstop remain enabled: stack remainder is 5 on the parent and 4 on the candidate, with different
  final player headings and particle/debris positions. These runs are not exact or performance comparisons.
  A first build rejected the Godot signal delegate conversion; its stale-assembly scene attempt was stopped.
  The next native run caught a duplicate signal disconnect despite exit 0. Replacing the original unsubscribe
  statement with idempotent registration disposal fixed it at the same boundary. All failed logs are retained.
- Measurement: warning-free build; `measure` and `measure-play` pass with metrics output. Five additional
  runs cover every diagnostic exclusion, blast/chain, warm-up, physics cap 4 and frame cap 30; all return clean
  logs and CSVs. Native signal cleanup uses the owned registration exactly once at its original boundary.
  Source assertion/options/log/capture inventory is unchanged. Timings/allocations remain diagnostics, not
  paired performance claims from these short functional runs.
- Dissolve/controlled retirement: warning-free build; native 45-second bot run and retirement fixture pass.
  The retirement JSON is byte-identical to the parent. Pacer, early capture, shots, tracking and metric options
  also pass. Both original collection checkpoints remain in place; the command observer stays attached through
  the live-session collection proof and detaches after fixture teardown, before the final collection.
  The existing tick probes remain attached at the same positions/priorities; scenarios add no processing nodes.

The first final gate stopped at import-order lint in the moved Dissolve file before running tests. The corrected
head reruns the gates; that failed lint log/status remain retained separately.

## Final dispatch and lifecycle proof

All seventeen selections have factories. The fake-context suite has seven passing tests: pending completion,
continuation boundary, failure propagation, single reverse disposal, early fixture detach, captured-fixture
collection while the case remains alive, guarded late callbacks (typed and untyped), and an independent factory
inventory requiring ordinary objects and incomplete completion failure from every owner. Unknown selection and
forced `lifetime-shards=0` both exit 1 with `[lab] FAIL`; neither can print PASS. The default remains lifetime.
No scene attachment order, frame count, await configuration, assertion, capture expression or RNG call changed.
The independent shard/session checker remains unchanged.

Reproduce the per-family native commands from `build/rfb7a/owned/status.json`; the parent command matrix is in
`parent-rest/status.json` (plus `parent/status.json` and `parent-dials-status.json` for lifetime/dials). The five
parent release traces passed; the additional Ring Out detailed comparison and retirement JSON match exactly.
All command/capture names and source expressions match. The only caller-inventory additions are this package's
documentation; existing references move only by inserted documentation lines. Unseeded streaming, particles,
active duel and stack outcomes/timings are not exact or benchmark evidence. No expectations were widened.

Final gates run in order on the recorded unchanged commit:

```sh
make check
make test-equivalence
make -j4 test-full
```

`build/rfb7a/final-status.json` records revision, commands, statuses and elapsed times. Complete output is retained
in `final-check.log`, `final-equivalence.log` and `final-full.log`. These are completion evidence only after each
command exits successfully. The full gate includes its unchanged native bot/network audits; this package changes
no networking code. `Protocol.Version` remains 3, and players share no changed messages, world/tank data or tuners.

## Final sizes

Sizes below are source lines and bytes, excluding UID metadata. Every new script has its generated UID committed.

| File under `src/Dev/` unless noted | Lines | Bytes |
| --- | ---: | ---: |
| `LabTest.cs` | 103 | 4269 |
| `LabScenarios/AimStallScenario.cs` | 112 | 5886 |
| `LabScenarios/BankRulesScenario.cs` | 74 | 4349 |
| `LabScenarios/BankScenario.cs` | 96 | 5687 |
| `LabScenarios/DialsScenario.cs` | 95 | 5235 |
| `LabScenarios/DissolveScenario.cs` | 387 | 24167 |
| `LabScenarios/EffectsScenario.cs` | 71 | 3136 |
| `LabScenarios/FramesScenario.cs` | 84 | 3739 |
| `LabScenarios/LabScenario.cs` | 94 | 3311 |
| `LabScenarios/LabScenarioRegistry.cs` | 38 | 1809 |
| `LabScenarios/LabScenarioTools.cs` | 94 | 5336 |
| `LabScenarios/LifetimeScenario.cs` | 173 | 9674 |
| `LabScenarios/MeasureScenario.cs` | 217 | 12556 |
| `LabScenarios/PrototypeScenario.cs` | 153 | 9804 |
| `LabScenarios/RetirementScenario.cs` | 52 | 2908 |
| `LabScenarios/ShortFuseScenario.cs` | 270 | 17585 |
| `LabScenarios/StackFireScenario.cs` | 56 | 2999 |
| `UnitTests/LabScenarioTests.cs` | 148 | 7651 |
| `tools/dev/lab_scenario_inventory.py` | 76 | 4019 |
