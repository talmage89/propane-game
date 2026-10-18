# Lab scenario ownership

`LabTest` owns scene attachment, capture, trace observation, frame waits, collection checkpoints and shutdown.
Plain objects in `src/Dev/LabScenarios/` own each async case's state, input sources, subscriptions and completion.
The host awaits the selected owner at the former dispatch site; owners retain the existing awaits and frame counts.
Returning successfully from the complete async body satisfies completion. A fault or unfinished body cannot pass.
Disposal runs subscription cleanup once in reverse order; guarded callbacks ignore delivery after disposal.

`LabScenarioRegistry` registers selections. The source inventory in `tools/dev/lab_scenario_inventory.py`, session
inventory in `check_lab.py`, and fixture/shard inventory in `check_lab_shards.py` are deliberately independent.
Unknown names now fail explicitly instead of silently running lifetime; valid selections retain their behavior.

Run `python3 -B tools/dev/lab_scenario_inventory.py` to record assertion, log and capture expressions, options and
command references. Run `LabScenarioTests` through the unit-test scene for fake-context lifecycle proof.

All selections now use the registry; the host has no case dispatch or mutable case state. `LabCases` is the C#
namespace to avoid colliding with the gameplay fixture registry `Propane.Lab.LabScenarios`.

| CLI selections | Owner |
| --- | --- |
| `lifetime` (default), `tracking` | `LifetimeScenario` |
| `dials` | `DialsScenario` |
| `frames` | `FramesScenario` |
| `duel`, `soak` | `BankScenario` |
| `rules`, `aimstall` | `BankRulesScenario`, `AimStallScenario` |
| `prototype` (Ring Out / King) | `PrototypeScenario` |
| `shortfuse`, `shortfuse-combo` | `ShortFuseScenario` |
| `effects`, `stackfire` | `EffectsScenario`, `StackFireScenario` |
| `measure`, `measure-play` | `MeasureScenario` |
| `runner-sampling` (opt-in whole-callback benchmark) | `RunnerSamplingScenario` |
| `bot-cost` (opt-in Banking/King brain and whole-callback benchmark) | `BotCostScenario` |
| `streaming-history` (opt-in controlled long stream) | `StreamingHistoryScenario` |
| `dissolve`, `reference-retirement` | `DissolveScenario`, `RetirementScenario` |

Subscriptions can detach at fixture/round boundaries and are removed from the owner immediately, so cleanup does
not hold old fixtures across collection checkpoints. Case disposal drains remaining registrations in reverse order.
Every case must return successfully from its complete async body before the host prints PASS. Existing early-capture
options still return through their original paths. Events are guarded after disposal; frame waits are the original
host Tasks, without an additional scheduler or cancellation system. The existing measurement/stream probe nodes
remain ordinary diagnostics at their original attachment sites; none is a scenario.

To add a case, add its owner and registry entry, update the independent inventory and `LabScenarioTests` selection
list, and extend the independent session/shard coverage when it belongs in the gate. Keep its waits and completion
checks with the case, and use a scoped owned registration for subscriptions that outlive a local call.

At parent `1019775`, the inventory contains 17 CLI cases, 52 options, 146 assertion calls, 46 log calls,
34 capture expressions and 45 command/reference sites. Reproduce the parent and compare the extraction with:

```sh
python3 -B tools/dev/lab_scenario_inventory.py --revision=1019775 --output=build/rfb7a-parent.json
python3 -B tools/dev/lab_scenario_inventory.py --compare=1019775 --output=build/rfb7a-current.json
```

The comparison checks executable expressions; caller context is retained for review because new documentation
naturally adds references. Numeric telemetry and native streaming completion are compared separately.

[Verification record](evidence/lab-scenarios-verification.md) includes migration proofs, failed attempts and native capture comparisons.

The opt-in `bot-cost` case builds seeded eight-brain Bank and King suburbs, settles 120 process frames, then
calls the existing runner callback synchronously for 2,000 warm-up and 6,000 measured 120 Hz ticks. It reports
allocations, ray/navigation workload counts and mean/p95/p99 brain and whole-callback microseconds. Banking uses
its existing inclusive `BotBrain` scopes; King's existing `ThinkSeconds` includes shared services and the rotating
brain loop. These columns have different scope and should be compared only within a mode. Both whole-callback
columns include shared services, activity sampling and rule work. During synchronous windows Jolt and the world
clock do not advance; runner-owned elapsed clocks and query budgets do. This is controlled callback cost, not
frame pacing. The regular native Lab/course/online runs cover actual motion. Run identical source on the parent
and candidate, alternating at least three pairs headlessly and rendered:

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/lab_test.tscn -- --scenario=bot-cost
DISABLE_GAMESCOPE_WSI=1 godot-mono --disable-vsync --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/lab_test.tscn -- --scenario=bot-cost --capture-dir="$PWD/build/bot-cost"
```

On the invisible Linux gamescope wrapper, the per-process `DISABLE_GAMESCOPE_WSI=1` override allows VSync to
stay disabled. Reject measurements whose logs report a VSync fallback, as the scale benchmark does.

`streaming-history --segments=512` manually calls the real arena physics callback in a paused world, builds the
next real seeded segment and retires the oldest through the arena. Controlled actor samples advance the real
rule window; worker readiness is awaited before timing. Begin/request, build, floor/dissolve, forget and retirement
all run inside the measured callback. The first 32 segments warm native/JIT paths. Every 64 retirements it collects and asserts
that segment, suburb, floor-shape and tank wrappers and native objects are released while the session survives.
It reports live containers/resources and whole callback allocation/time mean/p95/p99. `--raw` requests timing
histories; `--assert-bounded` additionally enforces the W1 ordinary-history bounds. It deliberately omits native
physics from callback timing; ordinary `dissolve --tracking-metrics` retains the whole-session/native-tail probe.
Rendered runs with `--capture-dir` save the reveal window at the initial and final live segments. This controlled
probe supplements ordinary bot/pacer runs; it does not simulate score, navigation or long-run physics.

`SegmentFeedTests --feed-transcript=<file>` records an independent parent-semantics request/forget transcript,
including negative/extreme indices, gaps, repeated forget, unfinished/completed and discarded late workers.

For B4b's immediate-parent checks, run `python3 -B tools/dev/check_lab_mechanics.py --baseline=PATH` after
building/importing both projects with identical harness sources. It compares the credit/spill/RNG and difficulty/
sampling observations, requires both unit suites' assertions to pass, and compares every detailed observation in
Ring Out and mixed-difficulty Bank/King runs. This supplements `check_equivalence.py --baseline=PATH --detail`;
it neither reads nor writes new release references. Live streaming remains statistical because worker/build-slice
completion is wall-time-sensitive; use the existing controlled retirement reference for exact Dissolve comparison.

`PrototypeViewTests` compares complete serialized HUD values with independent pre-B3b2 presenters in
`UnitTests/Fixtures/Legacy*Hud.cs`. Cases cover Ring Out warning/credit/departure, Short Fuse damage/read-time
regeneration/spectating, Dissolve open/closed chains/catch, unknown actors, and round end/replacement. The reflection
bridge in `PrototypePresentation` lets identical harness source exercise both the parent rule input and the new
plain value input; it is outside production and performance measurements. The legacy presenters are oracles:
change their expected behavior only with a separately approved rule or presentation change.
`python3 -B tools/dev/check_prototype_views.py --baseline=PATH` additionally requires 92 complete HUD observations
and compares their exact values with an independently built immediate parent. `runner-sampling` includes a driven
Dissolve bot, asserts every expected thought ran, and reports shared ray/navigation workload counts.

`PrototypeObservationTests` uses real runner `IBotWorld` adapters to assert zero managed allocation over warmed
reads, at several explicit mode-clock times and after life/front changes. It compares each scalar/struct with the
corresponding rule fact. It also retains a snapshot across accepted facts, collects the retired runner and rules
while retaining the snapshot, and collects released snapshots. King's fixture takes the first actor sample before
requiring its zone observation. `check_lab_mechanics.py` additionally compares a complete quick Short Fuse bot match,
including every detailed observation, beyond the scripted combo covered by the frozen references.
