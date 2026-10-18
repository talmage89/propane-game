# Shared Lab mechanics verification

**Status:** Focused Linux preservation, lifecycle and controlled runner-cost proof passed. Current contract: [Lab](../lab.md) and
[architecture](../architecture.md). This does not close P2's inherited whole-session performance obligation.

2026-10-18, Linux x86_64, Godot 4.7.2 .NET, SDK 10.0.113, normal Debug; Intel i5-10400F (6 cores/12 threads),
NVIDIA GTX 1660. Parent production is `a2fcebd`; identical B4b harness instrumentation is `f86516e` on both builds.
The parent is an isolated `git archive` inside the worktree's ignored `build/`, imported and built with `make setup`.
No release reference was regenerated. All raw logs, detailed traces and PNGs are retained under `build/b4b-*`.

## Scope, retirement and dependencies

Each runner's `directShots` dictionary and `drops` set move to its own `LabTankFacts`. It stores mechanical facts,
not a scorer, chain ID, blast ID, clock or rule object. P2's unchanged `TankTracking` remains the registration owner:
its forget callback removes those facts on detonation/content retirement. The same session unsubscribe callback
still drains registrations in reverse order after world invalidation. The existing runner stopping callback clears
remaining facts after brain/service disposal. Facts do not carry across replacement Short Fuse sessions.

`LabBotDifficulty` replaces the two identical seven-line configuration methods; it reads live tuning at the same
attachment/think calls, with the attachment slot rather than the rotated turn index. `LabSpill.HitPlacement` replaces
the duplicate standing/down hit geometry. The existing 41-line offline spawn/safe-point block moves from LocalBank
to LabSpill without changing its body (verified as exact source text after its access modifier). The runners shrink
from 326/406 to 274/395 lines: 63 fewer runner lines, including the 41 moved lines. This is ownership cleanup, not a
claim that all deleted lines were duplicate or that total project LOC fell.

The direct `PrototypeRunner → LocalBank.SpawnDrops` coupling disappears. Both runners also stop naming BotProfile/
BotDifficulty directly. New helpers are explicitly mapped to Composition by type and file. The 62 reviewed dependency
exceptions and 86 source-site approvals are unchanged: zero new exceptions, zero removed/re-labeled baseline rows,
and zero rows assigned to B4b before or after the change.

Rule submission, score updates, grace admission, ticket counters and reservation → spawn → commit remain in the
runners. They still choose a bank scorer from ChainCause/last shooter and assign BlastRequest at the original site.
Short Fuse retains its frame-local blast cause and accepted-blast IDs; Dissolve retains its separate chain IDs and
its dissolved-tank check. The offline RNG object/draw order and add-child → invulnerability → Lab drop tracking →
runner tracking sequence are unchanged. Bank still samples only on a nonempty spill; prototypes still sample every
physics tick. No timers, callbacks, processing nodes/priorities, clocks or online spill algorithm changed.

B3b inherits the existing BotWorld, IBotWorld.Rules/View/Clock, Present/HUD and observation interfaces. These remain
live-rule reads and are intentionally not pre-empted. DissolveArena, LocalSegmentFeed, Match, Session and Bots sources
are untouched. Shared architecture/Lab docs and the module map are updated within this package's required scope.

## Assertions and exact differentials

Before production changes, `make setup`, `make build`, `make test`, the 12 new LabMechanicsTests and the repeat-self
supplementary differential all passed. The scenario inventory comparison against `a2fcebd` retained all 19 CLI cases,
52 options, 148 assertion expressions, 48 log expressions and 35 capture expressions. A deliberately changed detail
observation failed at its first tick. New harness and production changes are separate commits.

The candidate passes the following with identical observer source on the immediate parent:

```sh
python3 -B tools/dev/check_equivalence.py --baseline="$PWD/build/b4b-parent" --detail --output="$PWD/build/b4b-equivalence"
python3 -B tools/dev/check_lab_mechanics.py --baseline="$PWD/build/b4b-parent" --output="$PWD/build/b4b-mechanics"
```

All five immutable Lab summaries match v0.4.0 and the parent: Bank, King, spill-chain, Short Fuse round replacement,
and controlled Dissolve retirement. Each detailed parent/candidate file was also compared explicitly with the
checker's `diagnose` function: every observation agrees, not merely the window hashes. Release wire/schema/world/
publication assertions pass. The supplementary runner suites compare four exact credit/spill observations and seven
difficulty/sampling/RNG observations, plus every detailed Ring Out and mixed-difficulty Bank/King observation.
The new tests cover live attachment slots 0–3 and global Hard/points changes, sampling tick/spill/empty boundaries,
repeated shutdown, empty facts/tracking and weak-reference collection in all five runner modes. Existing P2 tests
retain content retirement, visual-dissolve boundaries, reverse drain, cleanup failure and callback-state collection.

## Rendered review

Parent, candidate and repeat parent use these arguments after `godot-mono --disable-vsync --fixed-fps 60
--resolution 1280x720 --path PROJECT res://scenes/dev/lab_test.tscn --`, with
`DISABLE_GAMESCOPE_WSI=1`, `--replay-random --tune.HitstopDuration=0` and distinct absolute `--capture-dir` paths:

- `--scenario=prototype --mode=ringout --quick --seeds=1 --first-seed=23 --overview --shots=4,12`
- `--scenario=prototype --mode=king --quick --seeds=1 --first-seed=23 --overview --shots=4,12`
- `--scenario=shortfuse-combo`
- `--scenario=dissolve --seconds=45 --first-seed=23 --shots=12,30 --camera=side`

All runs finish with clean engine logs and their original assertions. All 15 parent and 15 candidate PNGs were
reviewed, plus four repeat-parent diagnostics. Ring Out preserves the slab, warning and Bot 2 placement/result panel.
King preserves the contested zone, spilled tanks, move countdown, mint display and final tie. Short Fuse preserves
100 → 65 health, spectating Bot 2, the thrown victim, Bot 2's bullet credit/round win and a populated replacement round.
Dissolve preserves the visible front, stocked street/effects, score breakdown and paused result layout.

Five PNGs are byte-identical: both Ring Out views, King 12s, Short Fuse start and spectating. The other ten also vary
between two unmodified parent runs. King/Short Fuse differences are particle shapes; Dissolve live streaming outcomes
also vary (parent finals 187 m/34 tanks/612 and 177 m/34 tanks/602; candidate 159 m/27 tanks/482). This is visual review,
not a pixel-identity/equal-live-outcome claim. Controlled retirement is the exact Dissolve oracle; worker completion,
build slices and saved-best text are not deterministic capture inputs.

## Runner measurements

Six alternating pairs per probe and process type (headless/rendered), identical Debug instrumentation and
seed 23, with no trace observers or local compilation during measurement. Initial evidence is in
`build/b4b-measurements`, follow-ups in `build/b4b-followup` and `build/b4b-sampling-followup`, and the replacement
in `build/b4b-sampling-retake`. [All 152 sample windows](lab-mechanics-samples.csv) retain cohort, pair, allocation,
whole-callback mean/p95/p99, reported workload counts and before/after one-minute load; `accepted` identifies the
144 windows summarized below. Each cell is the median of six independent windows, not a pooled tick percentile.

The existing `bot-cost` probe warms 2,000 ticks and measures 6,000 per mode, with eight brains/48,000 thinks. Every
paired Bank run reports 48,753 rays/536,759 navigation queries, King 0/22,175. The existing `runner-sampling` probe
warms 2,000 and measures 10,000 ticks per mode, with 20,000 thinks for Ring Out/King/Short Fuse and zero for Dissolve.
It reports no query counters (blank CSV fields). Every reported paired workload count matches. All use 120 Hz
callback deltas. Commands, repeated in alternating order on parent and candidate projects:

```sh
godot-mono --headless --fixed-fps 60 --path PROJECT res://scenes/dev/lab_test.tscn -- --scenario=bot-cost
godot-mono --headless --fixed-fps 60 --path PROJECT res://scenes/dev/lab_test.tscn -- --scenario=runner-sampling
# Repeat both rendered, replacing --headless with:
# DISABLE_GAMESCOPE_WSI=1 godot-mono --disable-vsync --resolution 1280x720 ...
```

Probe source SHA256s are identical on both projects: BotCostScenario
`c64be0d57c30184f00f011e0aa545b01169806494b38f94320d35d11c06b0dbd`, RunnerSamplingScenario
`1f167281888e40ab34df2ce9f1a64da77a472184958b7a4749f1310ae83d74da`.
Machine-wide one-minute load spans 2.05–7.98; no quiet window was awaited and no VSync fallback was accepted.

| Process / probe / mode | Bytes per tick, parent → candidate | Whole callback mean / p95 / p99, µs, parent → candidate |
| --- | --- | --- |
| Headless / bot-cost / bank | 2816.00 → 2816.00 | 407.206 / 546.100 / 609.100 → 405.086 / 543.650 / 607.550 |
| Headless / bot-cost / king | 4025.05 → 4025.05 | 80.798 / 221.850 / 428.650 → 80.433 / 223.300 / 429.600 |
| Headless / runner-sampling / dissolve | 912.00 → 912.00 | 14.853 / 16.600 / 22.200 → 14.966 / 16.850 / 28.350 |
| Headless / runner-sampling / king | 3825.97 → 3826.02 | 32.354 / 32.650 / 48.400 → 32.210 / 32.700 / 46.100 |
| Headless / runner-sampling / ringout | 1112.00 → 1112.00 | 81.487 / 193.900 / 217.650 → 80.530 / 186.450 / 218.500 |
| Headless / runner-sampling / shortfuse | 1680.00 → 1680.00 | 23.467 / 24.800 / 37.600 → 23.425 / 24.700 / 34.300 |
| Rendered / bot-cost / bank | 2816.00 → 2816.00 | 412.888 / 554.400 / 619.100 → 410.371 / 551.150 / 618.500 |
| Rendered / bot-cost / king | 4025.05 → 4025.05 | 82.677 / 225.900 / 434.250 → 82.193 / 224.800 / 434.800 |
| Rendered / runner-sampling / dissolve | 912.00 → 912.00 | 15.469 / 17.350 / 22.600 → 15.367 / 17.300 / 23.550 |
| Rendered / runner-sampling / king | 3825.94 → 3825.83 | 33.182 / 36.700 / 46.000 → 32.560 / 33.450 / 48.550 |
| Rendered / runner-sampling / ringout | 1112.00 → 1112.00 | 77.697 / 117.400 / 201.250 → 76.200 / 115.550 / 195.250 |
| Rendered / runner-sampling / shortfuse | 1680.00 → 1680.00 | 23.691 / 25.250 / 37.350 → 23.968 / 25.900 / 35.450 |

No new steady allocation is visible. Bot-cost and the other sampler modes are exact; King sampler allocation
varies on both builds (parent 3,825.56–3,826.32, candidate 3,825.55–3,826.27 B/tick; medians across both process types
3,825.94 → 3,825.845). The tiny per-view differences in the table are retained, not rounded into an equality claim.
The facts owner adds one small managed object during assembly; it adds no per-tick allocation site.

The first three rendered Bank pairs were about 1% slower, so three further pairs were taken without changing code.
Their deltas reverse sign; all samples remain. Each whole-callback mean group has both positive and negative paired
deltas. Tails are noisy and are not universally lower; these data support no consistent whole-callback regression,
not a universal latency bound or an absolute optimization claim.

One complete headless sampler pair is retained but excluded: machine load rose 2.81 → 4.74 while the parent had
King mean/p99 77.907/1,685.1 µs and Short Fuse 73.113/1,086.7 µs, with changed incidental allocation. Compilation in
other agents is not observable from this sandbox, so its precise cause is unassigned. The entire pair was replaced,
including its candidate windows, rather than using the favorable candidate comparison. The replacement had normal
costs and allocations under load 7.42–7.98. No engine/scene assertion failed in any measured run.

These are actual whole runner callbacks, but synchronous stationary windows: Jolt, the world clock and other nodes
do not advance inside them. The rendered process initializes native rendering; this is not GPU/frame pacing proof.
Moving native scenarios/captures cover play. These measurements do not close P2's separate whole-session matrix.


## Gates, retained failures and limits

`make check` passes on the final production sources: Debug and ExportRelease warning-as-error builds have zero
warnings/errors; compiled architecture checks retain 62 exceptions and source checks 86 sites with zero failures;
579 unit tests, all 11 fast Lab sessions, real-device input, native physics/motion, bounded generator sweeps and
separate-process world/segment hashes pass. Gate self-tests reject 51 compiled and 16 source architecture negatives.
`make test-equivalence` passes its checker self-tests, release unit references and all five Lab traces. Logs are
`build/b4b-scratch/check.log` and `test-equivalence.log`. This focused record precedes the final-head
`make -j4 test-full`; its complete outcome is part of the branch handoff, not claimed here.

The first new collection fixture incorrectly retained a tank wrapper while expecting its world-owning runner to
collect. Both parent and candidate tank wrappers retain WorldContext.ViewRoot by design. The fixture was corrected
before its harness commit to release the complete fixture and check weak references to runner, bookkeeping and tank;
all 12 tests then passed. No production lifetime behavior was changed to satisfy that invalid assertion.

P2's uncompleted rendered whole-session/pacer matrix, ordinary native frame pacing, native Windows/macOS execution
and the architecture ledger's FU2/stock-soak/capacity/animation obligations remain inherited. Stationary callbacks
and this visual sample do not close them. The change is patch-safe: no shared messages, tuners, worlds, tank rules,
mode exposure or Protocol.Version change (still 3).
