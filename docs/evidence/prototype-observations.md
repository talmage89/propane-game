# Prototype observations verification

**Status:** Focused preservation, allocation and lifetime proof complete. Final gate completion is recorded in the
branch's submission report; this record does not claim a deep-gate result.

2026-10-18, Linux x86_64, Godot 4.7.2, .NET 10.0.113, Debug; Intel i5-10400F (12 logical CPUs),
NVIDIA GTX 1660; branch `wp/rf-b3b2-prototype-views`. Production extraction:
`1dd3ccc`; parent production: `dfec072`. `7bb00f9` first proves the independent presenter harness against that parent;
`db022c5` adds candidate observation lifetime/allocation assertions; `e122b77` corrects only the opt-in whole-match
Short Fuse replay fixture. Parent builds use the same probes, including that replay correction where required.
Scratch projects, raw logs, full traces and captures are under `build/b3b2-*`, excluded from commits.

## Boundary and compatibility

No mutable gameplay state moves. Rules still own life, health, clocks, placements, scores, chain bookkeeping and
round decisions. Runners still submit accepted facts and synchronously apply commands in the same order; arenas
still own mutation, pile placement/retries, spectating and round replacement. Bot traversal, tie order, random draws,
retargeting and intent assembly remain in place.

HUDs and the Lab overlay now read copied Ring Out, Short Fuse and Dissolve snapshots. Producers take an explicit
mode clock; health regeneration/shields and elapsed time are sampled at the read. Dissolve's front remains the
already integrated rule fact. Snapshots own copied actor/event arrays, borrow Ring Out's immutable plan, and retain
neither rules nor delegates nor native objects. No cached mutable observation owner or processing node is added.
Consumers release snapshots normally; runner retirement remains unchanged.

Bots receive structs/scalars through `IBotWorld` at their existing read points. No presentation snapshot is built
for a thought. `IBotWorld.Rules` and `ModeHudInput.Rules` are removed. King's existing value/projection type moves
to the Rules module, retaining its namespace for existing client call sites. Its static local-rule producer keeps
no reference; network conversion still reads received values. Eight live-rule edges disappear; the King producer's
existing edge is now internal to its proper Rules module. All nine B3b exceptions retire (62 → 53), with zero new
exceptions. The parent has no B3b baseline entries for the Match/client-adapter half. That integration remains
B3b1; no `src/Net/`, catalog or bundle file changes here.

No match-shared messages, generated suburbs, tank behavior, tuner values/schema/order, eligibility or wire bytes
change. Protocol remains 3; this is patch-safe. The optional Dev replay correction below is separate from production.

## Presenter and observation proof

`PrototypeViewTests` freezes independent pre-extraction presenters in `UnitTests/Fixtures/Legacy*Hud.cs`. The same
reflection bridge runs existing presenter assertions on both input shapes; it is excluded from timing. The suite
compares 92 complete serialized HUD values: every counter/caption, column, timer, objective, visibility, banner,
out-state, feed and bar. States include pre-round, warning, credited fall, departure, damage, read-time regeneration,
spectating, open/closed chain, catch, round end, replacement and unknown actors. King's existing navigation/presenter
assertions cover absent zone, mint/contest, warning and move clocks. A deliberate `STANDING` → `PERTURBED` change in
a scratch parent oracle failed its Ring Out comparison; restoring it passed.

`PrototypeObservationTests`: 12 passing tests across all four prototypes. Real runner adapters match live fact
oracles at four explicit clock reads and after life/front changes. Warmed loops of 1,000 reads allocate zero managed
bytes. Retained snapshots remain unchanged across accepted facts; retired runners and rules collect even while their
snapshots survive; released snapshots collect too. King's fixture must take its first actor sample to establish a
zone. The initial test mistakenly assumed one existed before that sample and failed; the fixture now asserts a
non-null snapshot before testing either its contents or collection.

Commands (after `make setup` / warning-free `make build`, `TMPDIR="$PWD/build/tmp"`):

```sh
python3 -B tools/dev/check_prototype_views.py --baseline="$PWD/build/b3b2-parent" --output="$PWD/build/b3b2-candidate-values"
python3 -B tools/dev/check_equivalence.py --baseline="$PWD/build/b3b2-parent" --detail --output="$PWD/build/b3b2-equivalence"
python3 -B tools/dev/check_lab_mechanics.py --baseline="$PWD/build/b3b2-parent" --output="$PWD/build/b3b2-mechanics-seeded"
python3 -B tools/dev/check_bot_tactics.py --baseline="$PWD/build/b3b2-parent" --output="$PWD/build/b3b2-tactics"
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/unit_tests.tscn -- --suite=PrototypeObservationTests
python3 -B tools/dev/lab_scenario_inventory.py --compare=dfec072 --output=build/b3b2-harness-inventory.json
```

Results: 92 byte-identical HUD observations; all five immutable Lab summaries and parent summaries agree; wire,
schema, tuner, plan/world and publication references pass. Explicitly comparing every decompressed detailed trace
also passes: Bank 192,473 observations, King 370,214, spill-chain 49,858, scripted Short Fuse 28,525, controlled
Dissolve retirement 90 (641,160 total). Frozen references are unchanged. Lab mechanics credit/spill/RNG (4 lines),
difficulty/sampling (7 lines), Ring Out, mixed Bank/King and an ordinary quick Short Fuse match all agree, including
every detailed observation. Tactical comparison: 23,040 byte-identical intent/decision observations in 24 cases.
Scenario inventory retains all selections/captures and existing assertions, adding only the benchmark workload
assertion and ray/query telemetry. Parent `make check` passes with 599 unit tests before production extraction.

### The ordinary Short Fuse replay correction

The added whole-match differential initially failed at tick 2231 on actor 2's pose. Repeating the unchanged parent
and unchanged candidate independently also diverged at the same tick. Unlike its scripted combo, this fixture set
`TestRandomSeed` but did not set `TestSeedPhysicsRandom` when `--replay-random` was supplied. `e122b77` adds the same
opt-in policy already used by the combo. Identical corrected harness source on both builds passes the full-match
comparison; additional parent and candidate repeats match every detailed observation too. Ordinary unflagged runs
and immutable combo traces retain their old policy. The failed unseeded runs remain in `build/b3b2-mechanics-expanded`
and `build/b3b2-shortfuse-repeat`; they are not relabeled as passes. No gameplay or native-physics correction is made.

## Controlled whole-runner cost

Six alternating parent/candidate pairs each, headless and rendered, run identical `RunnerSamplingScenario` source.
The first widely separated headless pair is retained but discarded: candidate compilation intervened between its
samples. Its replacement is consecutive, and all other pairs remain. One-minute load averages are recorded for
every sample (accepted one-minute loads 2.29–5.39). [Raw paired measurements](prototype-observations-cost.csv) include allocations, means, p95/p99,
workload counts, loads and the discarded pair. No trace observer or recording runs inside timing.

Each mode warms 2,000 ticks and measures 10,000 complete runner callbacks at 120 Hz. Ring Out, King and Short Fuse
run two brains (20,000 thoughts); Dissolve runs one (10,000). Per sample, rays are respectively 8,105/0/0/612;
navigation queries are zero in this stationary fixture. Shared services, bot rotation, activity sampling, retunes,
actor samples and command application are included. Jolt, world clock and other scene nodes do not advance during
the synchronous windows. These are callback measurements, not native frame-pacing or capacity claims.

```sh
uptime
godot-mono --headless --fixed-fps 60 --path "$CHECKOUT" res://scenes/dev/lab_test.tscn -- --scenario=runner-sampling
DISABLE_GAMESCOPE_WSI=1 godot-mono --disable-vsync --fixed-fps 60 --resolution 1280x720 --path "$CHECKOUT" res://scenes/dev/lab_test.tscn -- --scenario=runner-sampling
```

Medians of the six samples per side, microseconds (parent → candidate):

| Process | Mode | Mean | p95 | p99 |
| --- | --- | --- | --- | --- |
| Headless | Ring Out | 82.987 → 82.660 | 192.700 → 164.400 | 218.600 → 219.200 |
| Headless | King | 34.401 → 34.797 | 44.150 → 50.250 | 53.950 → 57.100 |
| Headless | Short Fuse | 24.410 → 24.496 | 32.000 → 30.800 | 38.850 → 37.550 |
| Headless | Dissolve | 21.159 → 20.575 | 29.450 → 25.600 | 72.800 → 71.450 |
| Rendered | Ring Out | 79.850 → 77.773 | 133.850 → 117.300 | 212.700 → 200.050 |
| Rendered | King | 34.636 → 33.214 | 44.600 → 39.350 | 54.900 → 50.550 |
| Rendered | Short Fuse | 24.721 → 23.975 | 32.500 → 26.500 | 38.750 → 38.050 |
| Rendered | Dissolve | 21.486 → 20.675 | 31.350 → 26.350 | 77.000 → 68.650 |

Median **paired ratios** are different from ratios of independent medians. Mean ratios range 0.975–1.019; p95
0.849–1.050 and p99 0.938–1.029. The adverse headless King tails remain visible; the rendered pairs do not reproduce
them. This establishes no consistent whole-callback regression under the sampled shared load, not a quiet-machine
limit or an improvement claim.

Ring Out stays at 1,112 bytes/tick and Dissolve at 912 on both builds. Short Fuse's candidate is exactly 1,680
(parent 1,680.01–1,680.64). King's parent is 3,825.55; candidate 3,825.55–3,826.23. The tiny King total difference
in short-warmup samples warranted a separate investigation rather than a zero-allocation claim for the runner.
[Diagnostic-only instrumentation](prototype-observations-allocation.patch) applied identically to two scratch
projects restricts the fixture to King, warms 20,000 ticks, then records allocation around each of 10,000 callbacks:
**all 10,000 per-tick amounts match exactly**, totaling 38,264,544 bytes on each build. Both have 9,996 ticks at
3,808 bytes and four at 88,664 / 4,248 / 102,600 / 4,264 bytes, at identical indices. This reproduces the same warmed
allocation behavior; it does not establish the cause of the earlier small total variation. The actual observation
getter assertions independently prove zero allocation without extending their warm-up. Runner allocations are
inherited and remain present.

To reproduce that diagnostic, apply the patch only in two scratch projects, rebuild both, and pass a separate
absolute allocation output for each side:

```sh
godot-mono --headless --fixed-fps 60 --path "$CHECKOUT" res://scenes/dev/lab_test.tscn -- --scenario=runner-sampling --allocation-path="$PWD/build/b3b2-alloc-LABEL.csv"
```

## Rendered review and device delivery

Before and after: `godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/lab_test.tscn --` plus
`--replay-random --tune.HitstopDuration=0 --capture-dir="$PWD/build/b3b2-{before,after}-NAME"` and:

| NAME | Additional options | Review |
| --- | --- | --- |
| ringout | `--scenario=prototype --mode=ringout --quick --seeds=1 --first-seed=23 --overview --shots=1,5,10` | All four captures byte-identical. Standing count/countdown, rim warning, removed lots and final placement panel agree. |
| king | `--scenario=prototype --mode=king --quick --seeds=1 --first-seed=23 --overview --shots=1,11,15` | Holding/mint bar, move warning, contest and final tie panel agree. First two captures byte-identical; later blast/smoke pixels vary. |
| shortfuse | `--scenario=shortfuse-combo` | All seven reviewed: 100 → 65 health, spectating Bot 2/dead bar, thrown opponent, kill feed, round banner and rebuilt round 2 at 58 health agree. Initial and spectating images byte-identical; remaining differences are sampled effect/motion pixels. |
| dissolve | `--scenario=dissolve --seeds=1 --first-seed=23 --shots=1,5 --until=6` | Both reviewed: 1 m/1 tank/11 score/26 m gap, then 12 m/3 tanks/42 score/33 m gap. Front bar, captions and timer agree; smoke/fire pixels differ. This early capture is not a completion/streaming-capacity proof. |

All four scenes complete their applicable assertions with no Godot error/warning. These 17 paired captures were
reviewed individually; sampled frames do not prove continuous animation or subjective feel.

```sh
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/menu_test.tscn -- --scenario=input --classic-lobby --port=24681 --windowed --capture-dir="$PWD/build/b3b2-input"
```

Rendered real-device delivery: **165 assertions, zero failures**. Reviewed Lab before-turn, after-turn and jump
captures show camera rotation and airborne movement. The test also exercises reload, melee, menus, reset, tuning,
remaps and return to single-player/match. No new device path or scene-tree/viewport layout is introduced here.

## Remaining verification boundary

Final submission runs `make check`, `make test-equivalence` and `make -j4 test-full` on the completed branch; their
outcomes belong to the final report. Existing architecture acceptance debts, including FU2 native vent variability,
animation checks and ordinary capacity limits, are not resolved by this package. No native Windows/macOS, cross-CPU
physics or quiet-machine throughput claim is made. Match/client adapters remain B3b1's scope.

The first scratch parent setup used a nonexistent TMPDIR and failed; retrying with the existing worktree `build/tmp`
passed. One premature suite invocation before compilation found no suite; the completed build passed. Initial
formatting drift failed lint and was corrected before the passing parent check. Those failures, the deliberate
oracle negative, the initial King fixture failure and the unseeded Short Fuse differentials are retained in raw logs.
