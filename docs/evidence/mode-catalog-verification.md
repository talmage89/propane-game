# Complete compiled mode catalog — B3a

**Status:** Targeted verification complete. The final deep sweep is reported separately in the branch report.

2026-10-18. Branch `wp/rf-b3a-mode-catalog`; production `039d0e1`, proof harness `76890ab`, immediate parent
`2ed9bb2` (inventory-only addition to `b90f360`). Linux, Godot 4.7.2 .NET, .NET SDK 10.0.113, Debug, GTX 1660.

## Ownership and boundaries

Composition owns `ModeBundle`, `ModeCatalog` and `ModeRegistry`. A bundle contains the existing definition
(rules/HUD/bot factories, metadata, world/supply and tuners), schema and server wire factory. There is no client
adapter factory yet; Bank/King/Lobby client policy and prototype live-rule views remain B3b. No capability flags,
processing nodes, timers, rule changes, actor registration changes or bot changes were introduced.

The process-lifetime shipped catalog, schemas' kind containers, tuner containers and eligibility lists are read-only.
The only new mutable owner is the main-thread test registration scope: it installs a validated overlay, replaces an
identity in place or appends it, then restores the exact prior catalog on idempotent disposal. Scopes must nest.
Rules and wires are still fresh per run; the catalog owns neither native resources nor live mode state.

Retired: the definition registration list, schema registration list, wire factory dictionary, all three unscoped
`Register` seams and `ModeWires.For`'s silent fallback. Sandbox explicitly selects `SilentModeWire`. Unknown runner
integration fails before either factory runs. Rule creation and initialization still precede wire creation.

The schema reader receives composition's resolver through MessageCodec; codecs do not look up a composition-owned
catalog. ServerRun obtains a runner through its existing ModeRegistry edge and uses the runner's selected schema.
No new dependency exception was added. The removed `ModeWires → ModeDefinition` exception was removed in the production
commit. Existing definition/Tuning/server boundary exceptions remain; this package does not claim plain server inputs
or complete the broader C1 boundary work. New runtime types/files are explicitly mapped in `modules.json`.

## Completeness, restoration and exact preservation

Commands (all generated output under `build/`):

```sh
make setup
make build
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ModeCatalogTests
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ModeEnvelopeTests
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ServerRunTests
```

Catalog: 17 assertions, zero failures. Each case asserts exact shipped catalog/list/entry/schema identity and order
before and in its finalizer. Covers each required bundle member, missing nested factories/codec members, schema
identity, duplicate identities, immutable shipped containers, explicit Sandbox silence, no unknown-mode fallback,
addition, replacement, nested scopes, exceptional unwinding, failed/idempotent/out-of-order disposal, per-run fresh
instances, factory order and a throwing/null-result wire factory. Migrated envelope and server-run suites pass
11 and 18 assertions; their causal and failure registrations now dispose through the same scope seam.

The identical `ModeInventoryTests` harness ran before production changes and after them:

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ModeInventoryTests --mode-inventory="$PWD/build/b3a-parent-inventory.json"
# Repeat on candidate with b3a-candidate-inventory.json.
cmp build/b3a-parent-inventory.json build/b3a-candidate-inventory.json
make test-equivalence
git diff --exit-code b90f360 -- src/Dev/Equivalence/Fixtures
```

The inventories are byte-identical (SHA256 `61efa2ebaf88c7b35e86a1bf12bd4b3ccb71de7e208dd36c76586785c362e48e`):
all seven definitions, registration/lobby/prototype order, IDs, exposure, factory types, tuner order, ammo/pickup
policy and 336 supply settings (six actor counts × eight multipliers × seven modes). Frozen wire/schema/world/
publication and all five Lab traces passed; no references were regenerated. Equivalence log: `build/b3a-equivalence.log`.

Immediate-parent packet proof used the existing isolated-runtime tool:

```sh
python3 -B tools/dev/server_equivalence.py --revision=2ed9bb2 --label=b3a-parent-observed --observe
python3 -B tools/dev/server_equivalence.py --revision=60feba1 --label=b3a-candidate-observed --observe
```

Run both projects with `godot-mono --headless --path "$PWD/build/b3a-REV-observed/project" res://scenes/dev/unit_tests.tscn -- --suite=SUITE --match-seed=23 --server-transcript=ABSOLUTE_PATH`.
`cmp` agrees for every packet byte, destination, traffic class and synchronous call order:

| Suite | Assertions per revision | Packet records |
| --- | ---: | ---: |
| ServerTests | 34 | 1500 |
| ParticipantServerTests | 17 | 728 |
| KingOnlineTests | 9 | 549 |
| LobbyWorldTests | 13 | 449 |
| LobbySupplyTests | 7 | 398 |

Total: 80 assertions per revision, 3624 matching packet records. World/supply/RNG construction inputs and phase
fallback are unchanged; the exact parent packets supplement the frozen references. No clocks or scheduling moved.

## Whole server ticks under shared load

Prepared uninstrumented projects with `server_equivalence.py --revision=2ed9bb2 --label=b3a-parent-bench` and
`--revision=60feba1 --label=b3a-candidate-bench`. The candidate's runtime is identical to production `039d0e1`;
subsequent proof changes only add assertions and formatting.

Each invocation: `godot-mono --headless --path PROJECT res://scenes/dev/unit_tests.tscn -- --suite=ServerRunBenchTests --server-bench --match-seed=23`.
Rendered invocations replace `--headless` with `--disable-vsync --resolution 960x540`. Six retained strictly alternating
parent/candidate pairs per display mode (pairs 7–12); 20000 warm-up and 30000 measured ticks per Bank, King and roaming
case, each tick receiving one actor pose and eight bodies before the complete server tick. Packet counts/payload bytes
match in every pair: Bank 60500/19024500, King 60518/19025886, roaming 60000/19020000.

[Raw paired data](mode-catalog-benchmark.csv) includes allocations, mean/p95/p99, workload counts, one-minute loads and
other active worktrees. First six pairs were superseded after compile overlap/noisy tails; all original logs remain
in `build/b3a-bench-*`. Retained pairs have no build processes in their before/after samples. They include other
worktrees' native runs and this checkout's gates/network clients; no quiet-machine claim is made.

Median of six samples, parent → candidate (microseconds for time):

| Display/mode | Bytes/tick | Mean | p95 | p99 |
| --- | --- | --- | --- | --- |
| Headless Bank | 6868.12 → 6868.12 | 17.233 → 16.956 | 23.20 → 23.20 | 24.85 → 25.10 |
| Headless King | 7070.44 → 7070.44 | 17.522 → 18.195 | 24.35 → 25.30 | 28.10 → 29.15 |
| Headless roaming | 6813.65 → 6813.65 | 17.621 → 16.559 | 23.15 → 22.90 | 28.75 → 25.85 |
| Rendered Bank | 6868.12 → 6868.12 | 17.796 → 17.351 | 23.65 → 23.45 | 25.55 → 24.95 |
| Rendered King | 7070.44 → 7070.43 | 18.218 → 18.237 | 26.05 → 25.80 | 29.15 → 29.25 |
| Rendered roaming | 6813.65 → 6813.65 | 17.547 → 16.712 | 23.65 → 23.00 | 28.65 → 25.85 |

No new steady-state allocation is observed. King's small headless median increase is inside the measured spread:
paired mean differences range from -5.726 to +0.950 microseconds; rendered differences also reverse sign. This is
no repeatable whole-tick regression under this shared load, not a precise bound on sub-microsecond cost. These are
synchronous server measurements, not frame-time measurements. The rendered launcher emitted a V-Sync-unavailable
warning and fell back to Enabled; no graphics settings were changed.

## Native network and rendered review

All runs used fresh output directories, two clients, server `--match-seed=23`, the ordinary unchanged tank/score
checkers and no skipped checks. Six commands were `tools/dev/net_bots.sh -n 2 -s '--match-seed=23'` plus:

| Case/output suffix below build/b3a- | Flags before `--` | Arguments after `--` |
| --- | --- | --- |
| bank-clean | `-l 45 -w 1 -t clean` | `--capture-dir=ABSOLUTE_PATH --shot-every=12` |
| bank-faults | `-l 45 -t faults` | `--net-lag=120 --net-jitter=30 --net-loss=3` |
| king-clean | `-l 60 -w 1 -t clean` | `--mode=king --scenario=king --capture-dir=ABSOLUTE_PATH` |
| king-faults | `-l 60 -t faults` | `--mode=king --scenario=king --net-lag=120 --net-jitter=30 --net-loss=3` |
| roam-clean | `-l 25 -w 1 -t clean` | `--lobby-roam=8 --matches=2 --capture-dir=ABSOLUTE_PATH --shot-every=12` |
| roam-faults | `-l 25 -t faults` | `--lobby-roam=8 --matches=2 --net-lag=120 --net-jitter=30 --net-loss=3` |

Each supplied `-o "$PWD/build/b3a-CASE"`; launch output is `build/b3a-CASE-launch.log`.

| Case | Compared grids/body pairs | Final moving difference | Score audits |
| --- | --- | --- | --- |
| Bank clean | 1099 / 22324 | 0.060 m | 1, banked 10 |
| Bank faulted | 1099 / 13756 | 0.093 m | 1, banked 23 |
| King clean | 1399 / 42160 | 0.000 m | 1, minted 26 |
| King faulted | 1399 / 42142 | 0.000 m | 1, minted 26 |
| Roaming clean | 1399 / 29535 | 0.053 m | 2, banked 10 and 11 |
| Roaming faulted | 1399 / 26643 | 0.186 m | 2, banked 15 and 13 |

Every run exits 0: complete audits, zero unobserved pair grids, zero disagreements, zero engine/protocol errors.
Both King peers prove hold/contest/mint/warning/move/fresh pile. Both roaming peers enter and retire three worlds,
shoot/move in the waiting worlds, and complete both matches. Roaming tank comparison covers the finite matches;
waiting-world stock/lifetime behavior is additionally covered by logs and the parent/unit comparisons, not by a new
continuous cross-peer waiting-world convergence assertion.

Also ran `godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/menu_test.tscn -- --scenario=lobby --port=24691 --capture-dir="$PWD/build/b3a-lobby-menu"`:
60 assertions pass through the real front end. Reviewed Bank `Bot1_024.0.png` and `Bot1_048.0.png`, all six
`Bot1_king_*.png`, and lobby `02_roaming`, `03_panel`, `05_bot_roaming`, `06_stage`, `08_match`, `09_results`,
`10_return`: intact Bank HUD/ammo, King's hold/contest/warning/zone/pile/results, correct Bank/King choice order,
roaming title/card, staged start, match and return. Screenshots remain local under the capture directories.

## Gate and limits

`make check` exits 0 on `76890ab`: Debug and ExportRelease builds have zero warnings/errors; both architecture
checks pass with 78 reviewed exceptions and zero failures; source sites pass; all 539 unit assertions, native
behavior suites, Lab, bounded generator/forced formations, separate-process world/segment hashes and gate self-tests
pass. The real-device `menu_test --scenario=input --classic-lobby --port=24681` gate passes 32 assertions. The rendered
lobby run is additional proof, not a replacement for device input. Log: `build/b3a-check.log`. An initial invocation
failed formatting in the newly added inventory harness; formatting was corrected, committed, and the entire gate
rerun successfully (`build/b3a-check-format-failed.log` preserves the failure).

The final `make -j4 test-full` invocation follows this evidence commit, on that final head. Its result belongs in
the final branch report; this record does not predeclare that result.

This targeted record does not claim new Windows/macOS execution, release-export/artifact smoke, universal server
plain-input boundaries, general online round rebuilding/streaming, or closure of the inherited FU2 acceptance debt.
B3b/C1/R1 retain their existing obligations. Protocol.Version remains 3; IDs, bytes, schemas, supply, tuners, available
modes, HUD policy, bots and server phase fallback are unchanged. Patch-safe.
