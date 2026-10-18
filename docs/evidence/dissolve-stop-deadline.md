**Status:** H2 complete: controlled before/after native proof, five shard passes, full check and frozen equivalence pass.

# Dissolve stopped-runner deadline — 2026-10-18

Branch `wp/rf-h2-dissolve-catch`, immediate parent `dfec072`, Linux, Godot 4.7.2 .NET/Jolt.
Only `DissolveScenario` and its documentation change. No production state moves, retirement changes,
removed dependencies, new types, architecture exceptions or gameplay changes.

## Diagnosis

`DissolveRunRules.Sample` advances the front and catches when the runner's sampled X is strictly less than
`Front`, emitting `RunCaught` and recording `CaughtAt`. The native runner samples the pelvis when ragdolled,
otherwise the character position. Catch does not depend on floor strips or a supporting surface.
The rules end the round only after `DissolveFallSeconds` (2.5 by default) from `CaughtAt`.
`PrototypeRunner.IsPlaying` remains true during this fall. Position stops updating after catch, while the front
continues advancing, so a negative reported gap can also describe a runner already caught.

The harness guarded **round completion**, despite its former “never caught” wording. Its old budget was
`gap / max(0.1, start speed) + fall seconds + 2`. At the reported −7.8 m and default 1.5 m/s this is −0.7 s:
it cannot even permit the first stopped-frame check. The corrected budget clamps only travel distance at zero.
Positive-gap budgets, the two-second margin, simulation clock and all final assertions remain unchanged.

The supplied `b3b1-dissolve12-candidate.log` currently records a pass (gap 60 m), not the reported failure.
Its neighboring parent log fails optimized-Debug setup; the pair logs contain passes. Those files were read only.
The original −7.8 m event could not be inspected or replayed from these files; the controlled case below
establishes the deadline defect independently, without attributing an unseen original physics event.

## Controlled native regression

After `make setup` and `make build`, run:

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/lab_test.tscn -- \
  --scenario=dissolve --driver=pace --seconds=20 --first-seed=12 --stop-behind-front=8
```

The opt-in fixture moves the upright runner behind the front, clears velocity, waits one process frame,
and requires the real native runner to sample a negative gap and emit `RunCaught`. It then retains the ordinary
completion guard and final fall, score, chain, stream and result-menu assertions. This is a scripted diagnostic,
not device-input coverage; input routing and scene layout do not change.

With only the diagnostic fixture added to the parent's guard, `build/h2/control-before.log` exits 1:
sampled X 51.59, front X 59.62, gap −8.0 m, `caught_at=20.02`, `over=False`. `RunCaught` already occurred;
the old guard fails immediately. With the clamp, identical arguments exit 0 in `control-after.log`:
catch at 20.02 s, finish at 22.5 s, 119 m, zero tanks, score 119, four segments built and one retired.
The effective −8.03 m budget changes from approximately −0.85 s to 4.5 s. No catch or fall rule changes.
This before/after uses the same diagnostic source and immediate-parent production code, with only the guard changed.

Rendered command: remove `--headless`, add `--resolution 1280x720 --camera=side --shots=19,20.1,21,22`
and `--capture-dir="$PWD/build/h2/captures"`. It passes identically (`control-rendered.log`). Reviewed all five
captures: `dissolve_12_019.0s.png` shows the runner ahead of the front; `020.1s` shows the caught runner over
the void; `021.0s` and `022.0s` show the fall; `dissolve_12_end.png` shows the paused result menu.
These are sampled correctness captures, not performance measurements.

## Gates and repeated shard

Prepared optimized Debug with `make test-full-engine`, then ran five sequential invocations:

```sh
GODOT="$PWD/tools/dev/full_engine.py" python3 -B tools/dev/check_lab.py \
  --full --only=dissolve-full --first-seed=12 --seeds=1
```

Every invocation exited 0 with clean engine logs and all tracking/final assertions passing. The wrapper's complete
output, including its engine log, is retained in `build/h2/shard-1.log` through `shard-5.log`.

| Run | Start uptime | Load averages (1/5/15 min) | Stop gap (m) | Catch / finish (s) | Meters / tanks | Built / retired |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 13:59 | 5.04, 6.28, 6.84 | 60.0 | 313.8 / 316.4 | 1280 / 185 | 24 / 21 |
| 2 | 13:59 | 12.09, 7.81, 7.32 | 57.0 | 313.3 / 315.8 | 1267 / 190 | 23 / 21 |
| 3 | 14:00 | 10.15, 7.84, 7.35 | 59.4 | 313.8 / 316.3 | 1248 / 185 | 23 / 21 |
| 4 | 14:01 | 14.68, 9.27, 7.85 | 59.7 | 313.7 / 316.2 | 1258 / 193 | 23 / 21 |
| 5 | 14:01 | 14.40, 9.86, 8.11 | 60.0 | 313.9 / 316.4 | 1337 / 188 | 25 / 22 |

No stochastic negative-gap stop occurred in these five runs; the controlled fixture supplies that proof.
Controlled runs also record `uptime`: before 13:57 (load 4.06/6.67/7.02); corrected headless and rendered
both 13:58 (load 5.09/6.56/6.96).

`make build`: warning-free before and after the fix. `make test-equivalence`: exit 0; frozen wire/schema/world/
publication and Bank, King, spill/chain, Short Fuse and controlled Dissolve traces all pass; output in
`build/h2/equivalence.log`, full evidence in `build/rfp0-equivalence-2/`. No reference was regenerated.
`make check`: exit 0 (`build/h2/check.log`), including 596 unit tests with zero failures, device/menu delivery,
Lab/integration/animation checks, bounded generator sweeps, separate-process world/segment hashes and gate
self-tests (51 compiled architecture negatives, 16 source negatives and stale/increased site checks).
Both lint builds have zero compiler warnings. The existing `CaptureTests` deliberately log two save-failure
engine errors, and `LifecycleTests` deliberately log one malformed-packet warning; these are expected negative
tests, not clean-log claims for the entire unit output. All five Dissolve shards have clean engine logs.
`python3 -B tools/dev/check_links.py`: all 78 tracked Markdown documents resolve.
`git diff --cached --check`: clean.

The parent/candidate Lab inventories were recorded with
`python3 -B tools/dev/lab_scenario_inventory.py --revision=dfec072 --output=build/h2/parent-inventory.json`
and the same command without `--revision`, writing `build/h2/scenario-inventory.json`.
The checked Counter diff in `build/h2/inventory-diff.log` confirms unchanged 20 cases and 37 capture expressions;
assertions change from 161 to 163 (replace this guard, add two controlled-fixture assertions), with one stop log
updated and only `stop-behind-front` added to the option set. No other assertion or log expression changes.

## Preservation and limits

No rules, tuners, frozen references, other Lab cases, RNG draws, native callback order or clock domains change.
Only the explicit regression option repositions an actor and adds a frame wait. Ordinary runs retain their original
stop/input path and gain diagnostic fields on the stop log. No new mutable owner or runtime allocation path.
No players' shared state or wire shape changes; `Protocol.Version` remains 3. Harness-only patch classification.
Live streaming remains wall-time sensitive, and repeated passing shards cannot prove every possible physical
support/fall situation. This investigation proves the negative-travel deadline defect and the tested native catch.
