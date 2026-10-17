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
