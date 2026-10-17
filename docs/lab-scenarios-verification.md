# Lab scenario extraction verification

Parent: `1019775`. No production or design files, release fixtures, Make targets or checker thresholds change.
Raw logs, capture manifests, source inventories and the runnable parent snapshot are retained under `build/rfb7a/`.
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
