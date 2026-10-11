# Test gate timing

Measured on the shared Linux development machine on October 11, 2026. Times are wall seconds, warm imports,
one sample per command; other agents also used the machine. Baseline: `7777296`. While this work ran, `dev`
advanced through `616b08b` and `13ae84f` to `c4a6f51`. Final verification includes two new movement probes,
motion assertions on five existing probes, gather appearance and mouse-menu probes, expanded HSV menus, two
melee probes, the deep melee-reload sweep and 21 added unit tests. Earlier measurements identify their baseline.

| Command | Before | After | Result |
| --- | ---: | ---: | --- |
| `make test` (matched original suites) | 350.03 | 166.76 | Pass; 52% shorter |
| `make check` (intermediate `616b08b` baseline) | 379.43 | 176.09 | Pass; 54% shorter, including new movement checks |
| `make test` (`13ae84f`, heavier load) | — | 308.27 | Pass; added coverage and substantially more contention |
| `make check` (`13ae84f`, heavier load) | — | 383.10 | Pass; added coverage and substantially more contention |
| `make test` (final `c4a6f51` baseline) | 350.03 | 178.96 | Pass; 49% shorter despite six added probes |
| `make check` (final `c4a6f51` baseline) | 379.43 | 212.64 | Pass; 44% shorter despite added coverage |

The first baseline `make check` failed in 56.43 seconds: `authority/new canonical vent reconciles an intact
observer (moving True)` reported “confirmed owner's stream did not reconcile the observer.” A retry passed
unchanged. This preexisting intermittent failure remains observable; no gameplay workaround or tolerance change
was made. The first `13ae84f` check also failed in 105.75 seconds at the unchanged real-game gather
assertion “knocked tanks did not gather.” The identical standalone command passed immediately without edits.
These failures are retained in the verification record rather than masked. The import-cache benchmark overlapped
part of the baseline retry's generator work, so the earlier standalone test is the cleaner matched comparison. Timing variation is expected on this shared machine.

## Every external step

Each cell is **total / startup / work-and-exit** in seconds. Startup ends at the first scene `_Ready` marker;
work includes shutdown. A dash means no scene marker, or the command was consolidated/added. Runtime launches
read already-imported assets and perform no editor import. Before columns used temporary entry markers and tool
wrappers; after columns use `tools/dev/time_gate.py`. Each world writer/hash launch is listed independently.
The after generator uses 20/3/2 seed depths versus 100/10/6 before; all assertion kinds remain.

| Step | Before test | Before check | Final test | Final check |
| --- | ---: | ---: | ---: | ---: |
| Incremental build | 1.20 / — / — | 1.22 / — / — | 1.18 / — / — | 1.23 / — / — |
| check_scores_tests.py | 0.06 / — / — | 0.05 / — / — | 0.05 / — / — | 0.05 / — / — |
| compare_vents_tests.py | 0.06 / — / — | 0.06 / — / — | 0.06 / — / — | 0.06 / — / — |
| compare_tank_paths_tests.py | 0.05 / — / — | 0.05 / — / — | 0.05 / — / — | 0.05 / — / — |
| unit_tests | 29.20 / 0.29 / 28.91 | 28.91 / 0.29 / 28.63 | — | — |
| blast_query_tests | 0.67 / 0.29 / 0.38 | 0.66 / 0.28 / 0.38 | 0.66 / 0.28 / 0.38 | 0.70 / 0.28 / 0.41 |
| gather_range | 1.60 / 0.29 / 1.31 | 1.59 / 0.28 / 1.31 | 1.59 / 0.28 / 1.31 | 1.50 / 0.28 / 1.22 |
| gather_range: grant-races | 0.95 / 0.30 / 0.65 | 0.94 / 0.29 / 0.66 | 0.94 / 0.29 / 0.65 | 0.98 / 0.28 / 0.70 |
| game_test: gather | 4.44 / 0.31 / 4.13 | 3.93 / 0.28 / 3.64 | 4.02 / 0.28 / 3.74 | 5.26 / 0.36 / 4.90 |
| movement_tests | 8.06 / 0.46 / 7.60 | 7.63 / 0.28 / 7.35 | — | — |
| bot_navigation_course | 8.10 / 0.29 / 7.81 | 7.54 / 0.29 / 7.25 | 8.10 / 0.28 / 7.82 | 8.10 / 0.28 / 7.82 |
| menu_test: gamepad | 9.41 / 0.28 / 9.13 | 7.27 / 0.27 / 6.99 | 9.22 / 0.28 / 8.94 | 8.75 / 0.28 / 8.48 |
| menu_test: devices | 3.82 / 0.63 / 3.19 | 3.18 / 0.28 / 2.90 | 2.16 / 0.30 / 1.86 | 2.06 / 0.28 / 1.78 |
| player_range: grip | 6.84 / 0.73 / 6.10 | 5.77 / 0.29 / 5.48 | 6.15 / 0.29 / 5.86 | 6.08 / 0.28 / 5.80 |
| player_range: ragdoll | 2.53 / 0.30 / 2.23 | 2.24 / 0.29 / 1.96 | 3.66 / 0.30 / 3.36 | 2.34 / 0.28 / 2.07 |
| player_range: feet | 3.83 / 0.29 / 3.54 | 3.55 / 0.29 / 3.26 | 4.14 / 0.29 / 3.84 | 4.01 / 0.28 / 3.73 |
| player_range: feet (corrections) | 3.83 / 0.30 / 3.53 | 3.54 / 0.28 / 3.26 | 4.69 / 0.28 / 4.41 | 4.76 / 0.29 / 4.47 |
| player_range: feet-events | 4.29 / 0.30 / 3.99 | 4.01 / 0.28 / 3.73 | 4.23 / 0.28 / 3.95 | 4.57 / 0.30 / 4.27 |
| generator_tests | 241.61 / 0.29 / 241.32 | 250.49 / 0.28 / 250.22 | 66.95 / 0.27 / 66.68 | 71.68 / 0.33 / 71.35 |
| platform_tests | 0.39 / 0.28 / 0.10 | 0.39 / 0.28 / 0.11 | — | — |
| World plan: Rows | 0.76 / 0.29 / 0.47 | 0.76 / 0.29 / 0.47 | — | — |
| World hash: Rows, process 1 | 1.90 / 0.29 / 1.62 | 1.80 / 0.29 / 1.52 | — | — |
| World hash: Rows, process 2 | 1.80 / 0.29 / 1.51 | 2.76 / 0.45 / 2.31 | — | — |
| World plan: Square | 0.76 / 0.28 / 0.47 | 0.86 / 0.29 / 0.56 | — | — |
| World hash: Square, process 1 | 1.80 / 0.28 / 1.51 | 2.93 / 0.82 / 2.10 | — | — |
| World hash: Square, process 2 | 1.79 / 0.29 / 1.50 | 2.57 / 0.50 / 2.07 | — | — |
| World plan: Triangular | 0.76 / 0.28 / 0.48 | 0.76 / 0.29 / 0.47 | — | — |
| World hash: Triangular, process 1 | 1.89 / 0.29 / 1.60 | 1.70 / 0.27 / 1.42 | — | — |
| World hash: Triangular, process 2 | 1.99 / 0.31 / 1.68 | 1.79 / 0.30 / 1.50 | — | — |
| World plan: Cluster | 0.86 / 0.31 / 0.55 | 0.85 / 0.29 / 0.56 | — | — |
| World hash: Cluster, process 1 | 1.89 / 0.30 / 1.60 | 1.89 / 0.30 / 1.59 | — | — |
| World hash: Cluster, process 2 | 1.90 / 0.31 / 1.59 | 1.79 / 0.31 / 1.49 | — | — |
| Formatter | — | 19.06 / — / — | — | 20.18 / — / — |
| Warning-as-error rebuild | — | 5.87 / — / — | — | 4.71 / — / — |
| time_gate_tests.py | — | — | 0.24 / — / — | 0.24 / — / — |
| core_tests | — | — | 32.53 / 0.28 / 32.25 | 32.92 / 0.28 / 32.63 |
| gather_range: appearance | — | — | 2.71 / 0.29 / 2.42 | 2.71 / 0.28 / 2.43 |
| melee_range | — | — | 1.78 / 0.28 / 1.50 | 1.69 / 0.28 / 1.41 |
| player_range: melee | — | — | 1.96 / 0.29 / 1.68 | 2.00 / 0.29 / 1.71 |
| menu_test: mouse | — | — | 6.52 / 0.28 / 6.24 | 6.47 / 0.28 / 6.19 |
| player_range: movement | — | — | 1.60 / 0.29 / 1.31 | 1.72 / 0.29 / 1.43 |
| player_range: motion | — | — | 4.04 / 0.28 / 3.76 | 6.18 / 0.43 / 5.76 |
| World plans: all formations | — | — | 1.41 / 0.28 / 1.13 | 1.50 / 0.29 / 1.22 |
| World hashes: all formations, process 1 | — | — | 3.56 / 0.28 / 3.28 | 4.09 / 0.28 / 3.81 |
| World hashes: all formations, process 2 | — | — | 3.55 / 0.28 / 3.28 | 4.65 / 0.80 / 3.85 |

The original gate launched Godot 28 times. Batching core suites and world probes reduces that to 17 for the
matched suite set; two movement probes, gather appearance, mouse menus and two melee probes make the final
gate 23. Most savings come from bounded generator sweeps and fixed-rate movement/menu execution.

The loaded `13ae84f` run illustrates contention: its formatter took 51.33 seconds versus 19.06 before, and unit
work took 53.82 seconds versus 28.91 before. Final core suite work was platform/movement/unit
**0.008/0.600/31.543 seconds** in `make test` and **0.009/0.629/31.882 seconds** in `make check`. All 189 unit
tests passed; the original matched run passed 168. Total startup was 9.15 seconds before, 5.59 seconds in the
matched original after-test run, and 6.53/7.30 seconds in the final test/check. Runtime launches did no editor import.
The final check's lint phase overlapped the separate wiring-fixture verification; the final test had no other
verification launched by this agent. Other agents still used the machine throughout.

## Import and CI estimate

Two fresh scratch checkouts used the same tracked source/assets. Cold import took **24.47 seconds**; restoring
only `.godot/imported` and rebuilding the resource index took **3.02 seconds**. Both exited zero. The cache was
about 53 MB. CI keys it on Godot version, all asset/import-setting bytes, icon/import settings and project settings;
a cache miss imports from source. The import job populates the cache before the matrix.

Local partition costs, excluding their shared build:

| CI partition | Final local check |
| --- | ---: |
| Lint | 24.89 s |
| Core, including Python checkers | 33.33 s |
| Behavior, including melee and HSV menus | 69.89 s |
| Generator | 71.68 s |
| World plans and two fresh builders | 10.25 s |

Parallel jobs replace the sum with the slowest partition. The final split is balanced between behavior and
generator. Applying the rough supplied CI/local baseline ratio of 480/379 gives about 91 seconds for the longest
partition. Allowing for checkout, SDK/editor setup, compilation, cache transfer, import/index rebuilding and
timing variation gives an estimated **2–4 minutes warm, 3–5 minutes cold**, excluding queue delays. This is an
estimate; no workflow was pushed or run remotely. Matrix, cache and final check wiring
were reviewed against GitHub's [matrix documentation](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/run-job-variations),
[cache contract](https://github.com/actions/cache/blob/v4/action.yml),
[restore contract](https://github.com/actions/cache/blob/v4/restore/action.yml) and the local wiring assertions.

## Coverage and failure proofs

The fast generator checks 100 layouts: 20 single-player, 20 two-player, 12 across the four large sizes, and 48
forced layouts (four formations × six sizes × two seeds). Every explicit formation count from 0 to 128 stays in
the gate. Deep sweeps retain 200 single-player and two-player seeds, 100 each at 8/16/24/32 players and six forced
seeds per formation/size: 944 layouts in addition to the fast gate. Former gate seeds 21–100, large seeds 4–10 and
forced seeds 3–6 moved to that sweep. Navigation stays five seeds in the gate and 100 in the full test. Both newly
landed melee gate probes and the melee-reload full sweep remain in their original tiers. The first 20 single-player seeds still exercised
all seven placement reasons and every group size 1–10 observed in the original 100-seed sample. Forced fixtures
cover each formation and size directly rather than relying on random selection. Fewer repetitions reduce the
chance of catching rare seed-specific regressions in the fast gate; the full sweep retains that depth.

Injected and reverted one assertion failure in each consolidated suite:

| Suite | Deliberate failure | `make test` exit |
| --- | --- | ---: |
| Platform | Expected an incorrect Windows archive suffix | 2 |
| Movement | Inverted the between-physics-frame assertion | 2 |
| Unit | Inverted repeated world-hash equality | 2 |

Each printed its suite's FAIL and stopped the gate. All three injections were repeated successfully on the
final `c4a6f51` baseline, restored, and followed by a passing complete test run. Restored sources rebuilt with
zero warnings/errors.
`python3 -B tools/dev/lint_tests.py` passed analyzer/format fixtures, parallel ordering, every scene/checker failure,
both hash-process failures, absent/partial/different hashes, core-scene/full-sweep wiring, failures of every deep
command, installer failures and CI/local partition equivalence. `time_gate_tests.py` checks parallel timing records and propagates a failing child command.

Intermediate rendered verification used fixed FPS 60 and requested 960×540 with `menu_test.tscn`,
scenarios `gamepad` and `devices`,
port 24684 and capture directories `/tmp/wptest-menu-gamepad` and `/tmp/wptest-menu-devices`. Both passed
(96 rendered gamepad assertions and 20 device assertions). Reviewed all 14 gamepad and four device captures:
focus outlines, name/address defaults, browser and lobby controls, guest/host states, match menu, results, return
navigation, keyboard/gamepad pause references and reload hints were correct. Screenshot-only assertions explain
the rendered gamepad count exceeding its headless count.

`13ae84f` rendered verification used fixed FPS 30 for gamepad and mouse, and real time for gather
appearance, requesting 960×540. All passed: 120 gamepad assertions, 15 mouse assertions and gather coexistence/lifetime
checks. Reviewed all 17 gamepad, eight mouse and two gather captures in `/tmp/wptest-appearance-gamepad30`,
`/tmp/wptest-appearance-mouse-retry` and `/tmp/wptest-gather-appearance`: focus and custom colors carry through
previews, lobbies, characters and real results; gather shimmer remains after shield expiry. A rendered gamepad
run at FPS 60 hit the existing 15-second results-cleanup deadline under load; FPS 30 passed unchanged. Headless
menus retain FPS 60. One mouse launch overlapped the warning-as-error rebuild and could not load its script; it
was stopped and repeated successfully after compilation. No assertions or deadlines changed.

Final `c4a6f51` rendered verification passed all three menu scenarios at fixed FPS 30: 135 gamepad assertions,
22 device assertions and 32 mouse assertions. Reviewed all 18 gamepad, four device and ten mouse captures in
`/tmp/wptest-hsv-gamepad`, `/tmp/wptest-hsv-devices` and `/tmp/wptest-hsv-mouse`, including full-size picker and
keyboard-help images. Presets/HSV controls, focus, color carry-through, real results and melee help are visible
and readable. Commands requested 960×540; the game applied its window settings, producing 1920×1042 captures.

Rendered `melee_range.tscn` and `player_range.tscn -- --scenario=melee --assert-motion --assert-grip` passed at
fixed FPS 60. Reviewed all seven collision captures in `/tmp/wptest-melee-range` and contact sheets of all 264
pose frames in `/tmp/wptest-melee-poses`, plus full-size windup/contact/magazine-drop samples. The collision
sequence shows blocked contact, the victim going down and an intact shoved tank; poses show strike and recovery
with hands tracking the rifle. Grip and motion assertions passed without tolerance changes.

A separate fresh-checkout `make lint` with no `.godot` and a nonexistent `GODOT` path passed in 26.55 seconds
with zero warnings/errors, proving the CI lint job needs no editor/import dependency.

All four batched world hashes exactly match the baseline hashes (205 fences and 36 tanks per fixture), as well
as each other across the two fresh builder processes. No messages, suburb construction, tanks, tuners or gameplay
rules changed relative to the updated `dev` baseline. `Protocol.Version` remains 2.

## Full sweep

All three full runs passed the gate, all 944 deep layouts and 700/700 navigation courses (success rate 1.000):

| Baseline and command | Wall seconds |
| --- | ---: |
| `616b08b`, `make test-full` | 1587.10 |
| `13ae84f`, `make -j4 test-full` | 1702.19 |
| `c4a6f51`, `make -j4 test-full` | 1286.39 (21:26) |

The final run also passed the retained melee-reload grip and motion sweep. Parallel gate execution used a single
shared build. Its gate took 142.83 seconds versus 178.96 seconds for the final sequential `make test`.
No assertion, tolerance or gameplay value was changed to pass these runs.

| Deep generator phase | `616b08b` | `13ae84f` | `c4a6f51` |
| --- | ---: | ---: | ---: |
| Single-player, 200 seeds | 31.84 s | 49.00 s | 38.25 s |
| Forced formations, six seeds at every size | 113.13 s | 136.93 s | 100.88 s |
| Two-player, 200 seeds | 99.16 s | 114.86 s | 82.29 s |
| Eight-player, 100 seeds | 72.85 s | 106.35 s | 71.07 s |
| Sixteen-player, 100 seeds | 127.81 s | 186.23 s | 120.88 s |
| Twenty-four-player, 100 seeds | 246.12 s | 285.73 s | 210.84 s |
| Thirty-two-player, 100 seeds | 438.44 s | 462.65 s | 334.19 s |

Final deep-launch timing (startup ends at the scene entry marker):

| Command | Total | Startup | Work and exit |
| --- | ---: | ---: | ---: |
| Melee reloads | 5.22 s | 0.29 s | 4.93 s |
| Generator deep sweep | 960.21 s | 0.29 s | 959.92 s |
| Navigation deep sweep | 178.12 s | 0.29 s | 177.83 s |
