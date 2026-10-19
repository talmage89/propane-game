**Status:** Linux verification complete at runtime head `8098956`; patch-safe. Native Windows/macOS and uncapped rendered performance are unverified. This record does not close the inherited architecture acceptance ledger.

# Application state and async lifetime

2026-10-18. Branch `wp/rf-a1-application-state`; production parent `08d20ae`, parent observation harness `b3fe482`, production `51371bc`, additional assertions `8098956`. Godot 4.7.2, .NET 10.0.13, Linux x86_64, Intel i5-10400F (12 logical CPUs), GTX 1660. The [architecture contract](../architecture.md) owns current rules.

## Ownership and preservation

- `Main` owns the lazy `PlayerSettings` store, or borrows an isolated store already bound by a test root. Path, ConfigFile and leaderboard cache moved from static storage into that instance. `IPlayerSettings`/`PlayerPreferences` expose only a borrowed Foundation contract/selection; persistence stays in Composition. Bindings restore in reverse order after child tree-exit callbacks; owners dispose their files. The old process-wide `UseFile` mutation is gone. Keys, values, appearance migration, lazy loading, cached HUD preference and immediate save timing remain unchanged.
- `LaunchArgs.Current` remains a lazy immutable process snapshot. Strictly nested main-thread overrides restore its exact predecessor, including exception unwinding. Parsing, overwrite order, culture handling and CLI precedence are unchanged. Tuning application moved to the Composition extension `LaunchTuningOverrides`.
- `Tuning.SavedTemplate` remains an application preference borrowed at composition/UI boundaries; worlds copy it, including local tuners. Asset caches remain shared, main-thread and immutable by convention. No per-world copies of shared meshes/materials were introduced. Callers own instantiated nodes, not their shared resources.
- `UpdateCheck` owns one request. Exit/predelete marks it retired, clears subscribers and cancels the request. Success/failure delivery is suppressed after retirement, including requests ignoring cancellation. The request disposes its cancellation source after completion. Godot-context continuation and the ten-real-second HTTP timeout remain unchanged.
- `ProcessPhysicsPolicies` owns hitstop's world count/owner/real-time deadline and catch-up's registered requests/prior engine budget. Hitstop retains its exclusive offline rule and restoration to one. Catch-up retains the overlap minimum, prior-budget restoration and live registered network-simulation veto, including paused network worlds. Tank eligibility/hold time stays in `TankSimulation`; first-tank registration and stopping/exit refresh points remain in place. No new processing node or clock was added.
- Application-created `ServerSettings.RunSettings` owns each native tuning resource behind `IServerRunSettings`. `ServerRun` owns/disposes that adapter. Lobby template/snapshot sampling, staging-resource lifetime, live debug reads, supply overrides and failed-construction retirement stay at their original synchronous points. Existing retirement fixtures still prove detached routing and exactly-once native disposal.

All ten A1 baseline entries were removed; none were added. LaunchArgs was remapped from Composition to Foundation after tuning application moved into the Composition extension; its remaining ambient reads became legal under that map. This was a module remap, not removal of those reads. Both compiled maps pass with 13 remaining reviewed exceptions; source approvals have 95 sites and no A1 retirement assignment. New runtime types/files are explicitly mapped. Release review paths include `ServerSettings` and `ProcessPhysicsPolicies` after code moved out of Net/Tank.

UI files changed only for preference access: `Menus`, `MenuProfile`, `MenuCommands`, `ConnectScreen`, `MatchMenu`, `MatchHud`. No Match/view-binding file changed. N1 overlap is limited to NetClient's lobby-default expression and LobbyServer/ServerRun configuration construction/access; receive/dispatch policy is unchanged. Existing fixture API migrations accompanied production so it builds; new assertions are a separate commit. The historical architecture record received only a moved-source link correction.

## Correctness and exact comparisons

Commands ran from the worktree unless noted; scratch paths below are under `build/`.

| Command/workload | Result |
| --- | --- |
| `make setup`; repeated `make build` | Strict import and final builds pass; touched code has zero warnings. |
| `make check` | PASS: Debug/ExportRelease warning-as-error builds, both architecture checks, 635 tests in 69 discovered suites, native behavior/input, bounded generator/world checks, and gate self-tests. |
| `make test-equivalence` | PASS: immutable release wire/schema/world/publication references plus Bank, King, spill-chain, Short Fuse and controlled Dissolve traces. No reference regenerated. |
| `make -j4 test-full` at `8098956` | PASS: all generator/tuner/Lab shards, 700/700 navigation attempts with zero stuck, deep piles/range, ten single-player and ten solo-match menu cycles with no orphans, and all three final multiplayer jobs. |
| `godot-mono --headless --path . res://scenes/dev/application_state_tests.tscn` | PASS: both offline-world retirement orders, overlap minimum, pause/resume, registration-timed network veto, paused-network veto, expiry/restoration, plus nine controlled update outcomes. Retired requests delivered zero callbacks, including inline cancellation, detached and never-attached owners. |
| `unit_tests.tscn -- --suite=ApplicationServerTests --application-transcript=ABSOLUTE_PATH` on parent/candidate | Four Bank/King × classic/roaming cases; exact delivery bytes and retirement order match. JSON is 661,855 bytes, SHA256 `06277bb78a8d88284a83d968ed7a15eadacdfe245fbecd34ac0115978762c38d`. Both physics transcript lines also match exactly. |
| Targeted `unit_tests.tscn -- --suite=...` | ApplicationSettings 4, LaunchArgs 9, Appearance 7, ServerRun 18, ServerMode 4 tests pass. ServerMode retains 192 supply cases and live allocation-free pile reads. Combined Application suite has 8 passing tests. |
| `menu_test.tscn -- --scenario=input --classic-lobby` | Headless and rendered PASS: 170 assertions. Keyboard/controller preference changes survive independent file reloads; HUD/menu borrow the same store. Existing 165 assertions remain. |
| `python3 -B tools/dev/release_tests.py` | 18 tests pass, including review of both newly moved paths. |
| Linux `--export-release Linux build/a1-artifact/Propane.x86_64` and `Architecture.dll inventory ...` | PASS: 1,091 shipping types, 532 packed resources, zero inventory failures; Dev absent, smoke closure present. |
| Packaged Linux `godot-mono --headless -- --release-test --classic-lobby` via `PROPANE_REAL_GODOT`, cwd artifact directory | PASS: main menu and single player, 40 tanks, no source-tree fallback. Source smoke also passes. |

The default settings file was absent before and after the deep gate. Persistence probes use isolated paths; migration retains the prior valid/custom/corrupt-record assertions.

The parent harness's `--a1-negative=minimum` exits 1 at the minimum-budget assertion. `--a1-server-negative` exits 1 in all four cases by violating the live results-duration expectation. These are deliberate failures, not weakened positive checks.

## Native network matrix

The deep run's four-client, 90-second faulted match passed 135,067 body-pair comparisons, zero disagreements, final moving drift 0.121 m, longest episode 0.65 s and one complete server audit. Its two-match faulted duel passed 39,202 pairs, final moving drift 0.046 m, longest episode 0.25 s and both audits. Loading readiness passed 16,772 pairs, zero disagreements and its complete audit.

Additional runs use a new output directory each time:

```sh
tools/dev/net_bots.sh -o "$PWD/build/a1-net-MODE-PROFILE" -p 24685 -n 2 -w 0 -l 20 -t PROFILE \
  -s '--match-seed=23' -- --matches=2 --lobby-roam=2
# MODE=bank or king; King adds --mode=king --mint-seconds=0.5.
# PROFILE=clean or faults; faults adds --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3.
```

| Mode | Profile | Body pairs | Final rest/moving (m) | Longest episode (s) | Audits/result |
| --- | --- | --- | --- | --- | --- |
| Bank | clean | 27196 | 0.000/0.072 | 0.05 | 2/2; zero disagreements/errors |
| Bank | faults | 25560 | 0.000/0.191 | 0.40 | 2/2; zero disagreements/errors |
| King | clean | 35182 | 0.001/0.000 | 0.05 | 2/2; zero disagreements/errors |
| King | faults | 36676 | 0.001/0.000 | 0.25 | 2/2; zero disagreements/errors |

All network logs are retained; complete audits and unchanged convergence checks are required. No engine/protocol error is accepted.

## Cost and rendered review

[Paired data](application-state/performance.json) records three alternating parent/candidate pairs in each display mode. Baseline/candidate assemblies were swapped only between exited processes, with restoration in `finally`; no other checkout was modified. No own compile or native workload overlapped the final measurements. One-minute machine load declined from 5.50 to 1.60; all six before/after `uptime` records are retained in the paired data.

```sh
# Two worlds/two fused tanks: 2,000 warm-up + 10,000 full tank render/physics callbacks and hitstop polls.
godot-mono [--headless] --path . --disable-vsync --resolution 1280x720 \
  res://scenes/dev/application_state_tests.tscn -- --a1-bench
# Whole server receive/run tick; 20,000 warm-up + 30,000 measured ticks per Bank/King/roaming case, 8 body updates.
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ServerRunBenchTests --server-bench --match-seed=23
# Native world: seed 23, two bots/40 tanks, 120-Hz physics, one-second warm-up/three-second sample.
godot-mono [--headless] --path . --disable-vsync --resolution 1280x720 res://scenes/dev/scale_bench.tscn -- \
  --config=Debug --bots=2 --tanks=1 --warmup=1 --seconds=3 --replay-random \
  --frame-metrics=ABSOLUTE_DIRECTORY --capture-dir=ABSOLUTE_DIRECTORY
```

Focused whole-callback allocation is zero in every final pair. Parent/candidate mean ranges are 1.727–1.797/1.710–1.792 µs; full p95/p99 data are linked. Server allocation is equal or lower to 0.02 bytes/tick: Bank 6,868.12, King approximately 7,070.44, roaming 6,813.65. Delivery counts/payload bytes match exactly in every pair. King means differ by +0.03–0.33 µs; p99 changes have mixed directions under shared load. This does not establish an absolute performance improvement or prove that all sub-microsecond overhead is absent.

Native samples retain one identical initial plan hash and 40 live tanks, 360–361 physics ticks, actual process-frame counts, total allocation and whole-physics-callback/native-tail means/p95/p99. Headless frame counts vary, so total allocation is not a per-tick zero-allocation claim; existing presentation allocation scales with process frames. Rendered samples have 178 frames. **VSync was forced on by gamescope despite `--disable-vsync`**; these rendered results do not prove uncapped throughput. Native tail is the existing engine/bookkeeping upper bound, not a Jolt-only timer. Builds were ordinary Debug, not optimized release.

Rendered commands used `--fixed-fps 60 --resolution 1280x720` and absolute capture paths. Reviewed the 78-frame menu-composition matrix (classic/docked, host/guest, 2/8/16 actors), 18 gamepad menu/profile/match/results images, the 19-frame input sequence, and the new preference-toggle frame in the 20-frame rerun. Contact sheets show intact layouts/overlays and color continuity; full-resolution review includes the match menu's shown/hidden preference and a native parent/candidate suburb pair. Mouse turns, jumps and remapped pause are visible. This is sampled visual evidence, not continuous-animation or subjective-feel acceptance.

## Retained failures and limits

- The first parent `make check` failed its self-test because the new native scene was missing from the explicit command inventory. The inventory was fixed in `b3fe482`; the corrected self-tests and final `make check` pass.
- An early ServerMode fixture still reflected retired private callback fields: three tests failed and their abandoned fixture servers produced three leaked-instance warnings. The fixture was adapted to the owned configuration boundary; the same assertion/supply inventory and final gate pass. This was not hidden as an inherited runtime leak.
- Initial measurement data are retained under `a1-pairs-initial`. The collector stopped on the rendered VSync fallback warning; a first catch-up forwarding layer was also removed before the final six pairs. The exact fallback remains recorded and is not treated as uncapped proof.
- The first expanded rendered-input attempt overlapped a Debug rebuild and could not instantiate C# scripts at startup. It was terminated (143), its log retained, and a serialized rerun passed all 170 assertions. No gameplay or checker threshold was changed to pass it.

No new framework, authority model, simulation scheduler, gameplay value, tuner/schema, message shape, world generation or RNG/traversal rule changed. `Protocol.Version` stays **3**. Shared asset immutability remains a borrowing convention. N1/C2 must reconcile their configuration/constructor and settings-access overlaps when integrating; this branch neither merges nor pushes. Windows/macOS runtime execution and broader inherited acceptance obligations remain outside this proof.
