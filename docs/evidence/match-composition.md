# Match composition and actor/view separation

**Status:** C2 implementation and Linux preservation/lifetime/gate proof complete; loaded timing and allocation
transients retained below. The current ownership contract is [architecture](../architecture.md), not this dated
record. Existing P2/P3 performance, FU2
convergence, capacity and animation acceptance obligations remain open.

2026-10-18, Linux, Godot 4.7.2 .NET/Jolt. Runtime parent `32e3c9f`, production extraction `82724a9`, final
runtime `8539a3e` / harness head `b268eb4` on
`wp/rf-c2-match-composition`. The intervening harness commits are `1f1a07b`, `63238bb`, `54853e4` and `32e3c9f`.
Snapshots at `54853e4` have the same non-Dev runtime as `32e3c9f` (verified with `git diff --exit-code` restricted
to runtime source). All scratch projects, native logs, transcripts and captures are under this checkout's ignored
`build/`; commands below use checkout-relative destinations. No frozen release reference was regenerated.

## Ownership and retirement

| Owner | Lines | Moved mutable state and lifetime |
| --- | ---: | --- |
| `ClientWorldReplica` | 335 | Tank/body identity maps, loose props, pickups, explosion predictions, `BodySync`, `BodyPublisher`; one physical session. Replication stop unsubscribes live tanks and clears lookup containers before native content free. Borrows authority transactions. |
| `ClientActorSet` | 324 | Attachment index, pending-drop publication scratch and synchronous actor subscriptions/relays; one physical session. L1 registrations synchronously remove each entry and subscription, including individual actor departure. |
| `ClientActorReplication` | 71 | Remote intent/snapshots, pose/action sequence and send timer, received action watermarks, melee, drop allocation/predictions, pickup notice state; one actor registration. Unsubscribe at L1's existing slot, clear client references during actor retirement before native free. |
| `OwnedBotSet` | 254 | Brains, shared per-world services, peer lists, rotating think order, activity/cost counters; one physical session. Individual departure clears actor caches; the original stop callback disposes brains/services before replication. Modes without a bot factory release their empty binding at replication stop without adding a callback. |
| `MatchViewBinding` | 369 | M0 selection cache, primary view, HUD/menu/results, mode presentation, reveal/notice/objective clocks and roster presentation; one physical session. Selection changes at the original roster notifications. Session owns native view free; binding releases selection and mode state at replication stop. |

Match is 629 lines including partials, down from 1,614. Breakdown: `Match.cs` 293 (was 931), assembly 167 (257),
bots forwarding 17 (181), lobby coordination 97 (139), mode/authority forwarding 55 (76); King partial removed
(30). `MatchInbox` is a separate, unchanged 75-line type, not a Match partial. Match retains callback/phase and
envelope coordination, profile/roster assembly, connection attachment and session lifetime boundaries.

Physical `ActorRuntime` no longer owns `DropPredictions`, `SnapshotBuffer` or `ReplicatedIntent`.
`PlayerCharacter` no longer references `LocalView`, `CameraRig` or `DeviceInput`. The physical actor emits its existing
presentation recoil fact synchronously after logical/rig recoil; the primary view subscribes there. A plain
`HasLocalView` flag preserves the old native pose sampling decision. Reset goes through the input-source contract;
`DeviceInput.Read` is unchanged. Game and Lab retain their view bindings in composition. Closing a primary view does
not promote a secondary view. There is no processing node, event bus, deferred replication command or new clock.

All six baseline entries naming C2 were removed; none was relabeled or added. The new owners have explicit logical
module mappings (Client for the replica/actor types, Composition for bot/view binding; the reset input contract is
Foundation). The source-site approvals move the two existing bot diagnostic clock sites without increasing counts.
No N1 transport/dispatch or A1 application-service file changed. Game/Lab view callers and L1/B3b1/FU2a diagnostic
hooks necessarily changed to follow the extracted owners; RifleRig and recovery decomposition remain outside scope.

## Parent/candidate synchronous proof

Preparation tools create isolated, compiled scratch projects and inject opt-in observers only there. They fail if
an expected hook is absent. New assertions/hook compatibility landed separately before production. Representative
commands (use a fresh label per preparation):

```sh
python3 -B tools/dev/match_composition_equivalence.py --revision=54853e4 --label=c2-composition-parent --observe
python3 -B tools/dev/match_composition_equivalence.py --revision=working --label=c2-composition-candidate --observe
godot-mono --headless --path "$PWD/build/c2-composition-parent/project" res://scenes/dev/unit_tests.tscn -- \
  --suite=MatchCompositionTests --match-seed=23 --composition-transcript="$PWD/build/c2-boundary-parent.jsonl"
```

Run the same invocation for candidate and for `ActorLifetimeTests` / `MatchPlayerTests`, then compare each with
`cmp`. Final files and SHA-256 (both sides identical):

| Workload | Rows | SHA-256 |
| --- | ---: | --- |
| Match composition, including current-input-source overrides | 655 | `7cd644492a9ed92e6249f0a33ab25295090d294e0d3f1e00a7e517bee37b06b3` |
| Actor lifetime | 739 | `66d86c8782358c281acbc874435b5754af7fb81f93b9b2294a4d5ac9e07569ab` |
| Player selection | 307 | `73bb016810f6fb2fad1f828f05b620a2975d1dd060718e6da21919979d4628e2` |

All 1,701 rows match: intent reads/aim advancement, callback order, messages/packet bytes, correction/publication
boundaries and actor/view/session retirement. Bank/King no-camera assertions exercise owned, remote and bot actors,
animation, exactly one intent read at the physical callback, melee/ragdoll/get-up and results. Three repeated
match retirements collect weak references to Match, session, world, all four session owners and actor attachments.
L1 actor departure tests additionally keep the match alive while proving individual attachment retirement and stale
callback isolation. Native-signal assertions are surfaced through the runner rather than merely logged by Godot.
Observed scratch builds pin otherwise randomized world/drop/blast sources to seed 23 on both sides; production
random source creation, consumption and traversal are untouched.

Review after the initial extraction found a missed boundary: the parent routes and applies remote snapshots
through the current `IntentSource`, whereas the initial attachment retained its original source. Four new cases
(delivery/application in Bank and King) pass on the parent and fail on the first extraction with exit 1. The final
attachment creates the source at the original post-teleport boundary and consults the current source; no detached
buffer is retained. All seven composition cases pass, including absent/temporary/replacement input sources and
zero extra input reads. The expanded 655-row transcript matches exactly (`c2-boundary-{parent,candidate}.jsonl`);
the two lifetime/selection transcripts were rechecked with the final attachment. Red/green logs are
`build/c2-input-boundary-{red,green,parent,candidate}.log`. The first deep-gate attempt was deliberately stopped
before editing source; `c2-test-full-interrupted.log` is incomplete, not a passing full invocation. The new fixture's
first compile used an incorrect generic helper call; that compile failed, then explicit casts restored a clean build.

Authority proof used `authority_equivalence.py --revision=32e3c9f --label=c2-authority-parent-proof --observe` and
the working candidate. `AuthorityTests` passed 58 tests with 391 identical observations; `GatherTests` passed 13
with 21 identical observations (`build/c2-proof-{parent,candidate}-*.jsonl`). Their hashes are
`255572c83d41968da8b4dc4212e4003a9f76d05c2af431fa830f7c38a6bde25e` and
`22cee421d565e9073447d588e33c367c8eb481a8b465ce7f7d30121943dd71ba` respectively. Physical authority resolution
still notifies through Match at the original synchronous position.

Mode/HUD proof used `mode_client_equivalence.py --revision=54853e4 --label=c2-mode-parent --observe` and working
candidate, `--suite=ModeClient --match-seed=23 --mode-client-transcript=...`, then its `--compare` command:
eight tests on each side, 177 identical observations with all operation/mode coverage present; hash
`dec87d4c13118b6307496ae47e2c5a4b317d7eb5e83a2c4c2f4c8408c54c24c1`. Lifetime/rollback cases include Sandbox,
Bank, King and roaming lobby, including the no-bot-factory case.

`BodyPublicationTests --publication-transcript=...` passed two tests on both sides; exact file comparison, hash
`a859dbdadacf41d7cbbf6e76edd2da7f885d0f35731aaf11650bcf5431fba983`. Publication capture timing, packet batching
and sequence advancement across synchronous send retirement are unchanged. The existing `actor_equivalence.py`
also prepared the working candidate; `ActorLifetimeCycleProbe --actor-order` passed all three cases, six repeated
removal cycles each for Bank, King and lobby. FU2a observer infrastructure passed all ten hook/strip tests, including
token-identical removal of scratch instrumentation.

The P0 publication driver's four drop-state member accesses now go through the parent/candidate compatibility
accessor; its stimuli, assertions, expected bytes and immutable reference files are unchanged.

No RNG/tuner/world construction or native skeleton/modifier algorithm changed. Match's physical callback still
applies snapshots, corrects bodies, ticks mode policy, thinks bots, publishes actors, publishes bodies, then checks
pickups. Physical actors still read intent and advance aim in their original callbacks; observers do not read input.

## Whole callback costs

Three alternating pairs per workload, P/C then C/P then P/C, identical compiled instrumentation and seed 23.
No own compile/native scene ran concurrently. Machine-wide `uptime` before/after every pair and every raw metric are
in [the retained CSV](match-composition-costs.csv); final raw logs/results are `build/c2-cost-final2/`. The prepared projects
are `c2-cost-parent-final` (`54853e4`) and `c2-cost-candidate-final` (final runtime attachment). Earlier complete pairs
are retained in `build/c2-cost-final/`; final pairs were repeated after the source-boundary correction.

| Whole workload | Parent median mean/p95/p99, µs | Candidate median mean/p95/p99, µs | Allocations, parent → candidate |
| --- | --- | --- | --- |
| Publication helper, 256 bodies | 38.170 / 143.400 / 272.100 | 37.813 / 140.200 / 264.500 | 6,144.21 → 6,144.21 B/tick |
| Loading Match physics, two actors, same 128 tanks + 128 cars | 43.944 / 151.100 / 280.300 | 43.588 / 147.300 / 271.500 | 6,538.21 → 6,538.21 B/tick |
| Match physics, two actors/58 bodies, no authority requests | 12.514 / 39.100 / 65.400 | 12.677 / 41.400 / 65.100 | 1,862.75 → 1,862.75 B/tick |
| Same, one authority request/reply each tick | 29.038 / 53.400 / 81.100 | 29.029 / 53.600 / 80.700 | 8,988.23 → 8,988.23 B/tick |
| Loading client + physical actor tick, two actors/one owned/58 bodies | 25.715 / 32.700 / 41.500 | 25.669 / 31.500 / 40.200 | 63,483,168 → 63,482,880 B / 60,000 ticks |
| Rendered whole Match process, ten actors/one view | 37.109 / 49.200 / 59.400 | 41.009 / 50.600 / 62.200 | 248 → 248 B/frame |
| Playing Match with two owned brains, four actors/76 bodies, no requests | 90.300 / 147.900 / 179.900 | 88.281 / 143.700 / 178.000 | 2,677.32 → 2,677.32 B/tick |
| Same, one authority request/reply each tick | 123.291 / 178.200 / 219.800 | 124.712 / 180.400 / 221.600 | Median 9,683.48 → 9,683.53 B/tick; distribution probe below |

Commands per project: `unit_tests.tscn -- --suite=BodyPublicationTests --publication-bench`,
`--suite=AuthorityBenchTests --authority-bench`, `--suite=ActorTickBenchTests --actor-tick-bench
--actor-tick-warmup=100000`; rendered `match_player_benchmark.tscn` with `--resolution 1280x720 --disable-vsync`.
All use `--match-seed=23`. Publication measures 10,000 ticks after 2,000 warm-up; helper emits 13,188 packets /
14,533,820 payload bytes; whole Match emits 15,688 / 14,781,320. Authority measures 100,000 ticks after 20,000 warm-up;
request case emits 100,000 packets / 7,900,000 bytes / 100,000 notifications. Actor path measures 60,000 ticks after
100,000 warm-up, including real `DeviceInput.Read`. Rendered Match measures 2,000 frames after five real seconds and
180 frames of warm-up. This frame workload isolates the full facade callback rather than promising whole-engine
frame cost. Actors are fixed and their native processing is disabled in this controlled presentation fixture;
the GPU still renders the scene.

The bot variant prepares each revision with `--owned-bots-bench`, then runs the unchanged
`AuthorityBenchTests --authority-bench --match-seed=23` entry. Only scratch fixture construction/instrumentation
changes: four actors, two real owned brains, 76 bodies, no view, 20,000 warm-up / 100,000 measured ticks. Native
actors/bodies remain stationary; the complete playing Match callback thinks both brains and publishes their
actor/body state. All six processes pass assertions: each measured case has 200,000 thinks, zero navigation
queries, 200,000 sight rays; the request case additionally has 100,000 packets / 7,900,000 bytes / 100,000
notifications. The preparer's positive transformation and six missing-hook negatives passed before paired use.
Logs/data: `build/c2-bot-cost-pairs/`; endpoint loads 8.25–10.11. Normal allocations are exactly 2,677.32 B/tick
in every sample. Request ranges are parent 9,683.48–9,683.56 and candidate 9,683.51–9,683.54 B/tick; the +0.05
median difference is retained for per-tick distribution diagnosis, not rounded away or called a steady-state pass.

The follow-up [allocation record](match-composition-allocations.csv) adds preallocated per-tick whole-callback and
`Match.BotCost.Bytes` arrays in both scratch fixtures, with an assertion that the whole-callback array's sum equals
the complete thread allocation counter. At the original 20,000 warm-up, normal totals match exactly (267,732,384 B); brain
allocations are zero in every tick. The request probe has one candidate brain allocation of 5,640 B at measured
tick 76,483; all other brain ticks allocate zero. Remaining per-tick differences are ±288 B at displaced tick
positions, rather than an added allocation on each call.

Repeat the identical instrumented workload after 100,000 warm-up ticks: normal totals are exactly 267,732,384 B
on both sides; request totals are exactly 968,349,840 B on both sides, over 100,000 measured ticks each. Every
measured brain tick allocates zero in both cases/versions. The remaining ±288 B displacements cancel exactly
(43 each direction in normal, 154 each direction with requests). Both allocation-accounting assertions and all
workload assertions pass. This establishes equal warmed allocation without discarding the earlier burst; its exact
native/runtime cause was not identified, and no claim about that mechanism is made.

Probe recipe: start with `--owned-bots-bench` projects; allocate two `long[ticks]` arrays before the measurement
counter; record thread-counter and `Match.BotCost.Bytes` deltas around each existing `Tick`, then assert the sum
and write the arrays after measurement. Run once with the fixture's existing `warmup = 20000`, then change only
that scratch constant to 100000 and repeat on both revisions. Drivers are `build/c2-bot-allocation.py` and
`build/c2-bot-allocation-warm.py`; source backups remain beside the scratch fixture as `.before-allocation.txt`.
Logs and two-column per-tick TSVs use `build/c2-bot-allocation[-warm]-{parent,candidate}*`. These diagnostic runs
are allocation evidence, not additional paired timing claims.

Final main-pair endpoint one-minute loads ranged from 2.90 to 9.06. Keep all pairs: no known compile overlap,
no request to wait for a quiet machine. Final rendered candidate/parent mean ratios are +13.5%, +4.9%, −12.3%;
the last pair also favors candidate p95/p99 (50.7/56.9 vs 54.8/74.7 µs). The independent rendered medians above
are higher for the candidate; they do not establish a stable timing effect across paired runs. The earlier complete
rendered set favored the candidate in two of three pairs, with unchanged view callback code. Whole-actor final
means are 25.715→25.423, 26.713→25.669 and 25.190→25.829 µs. No candidate slowdown repeats across all pairs in a workload; no
measured steady-state allocation increases. These loaded samples are not a quiet-capacity or speedup claim.
The earlier retained set includes a slow parent publication pair (70.766 µs whole callback) and a slow candidate
actor pair (26.408 vs 25.209 µs); none was erased by repetition.

Early pairs exposed a real +40 B/playing-tick regression: enumerating pickups through `IReadOnlyList` boxed the
original concrete `List` enumerator. The final code returns that concrete enumerator, retaining synchronous list
mutation checks. Earlier whole-actor samples with 20,000 warm-up ticks also differed by about 2.83 MB. Scratch-only
stage counters isolated that difference to unchanged `DeviceInput.Read` held-action native interop. At 100,000
warm-up both sides allocate exactly 23,040,000 B in input and 35,520,000 B in physical actor work over 60,000 ticks.
The optional warm-up flag exposes this transient; default behavior, measured ticks, assertions and input code are
unchanged. Earlier pairs/stage logs remain in `build/c2-cost-pairs/` and `build/c2-warm-{parent,candidate}.log`.

## Rendered and device delivery proof

All commands used the named `godot-mono` wrapper, `--resolution 1280x720 --fixed-fps 60` and absolute
`--capture-dir="$PWD/build/c2-..."`. All final scene logs contain zero Godot errors.

| Scene and arguments after `--` | Result and reviewed captures |
| --- | --- |
| `session_actors_tests.tscn` | Passed two views/devices sharing one world, firing/melee, surviving view, camera-free bot navigation/fire/melee/ragdoll/get-up. Reviewed `c2-actors-final/two_views.png` and `survivor.png`. |
| `player_range.tscn --scenario=grip --grip-sweep --assert-grip --assert-motion --view=rifleleft` | Passed 2,347 frames; reviewed all 29 samples on contact sheet plus full-size `fire.png`. Weapon/hand poses, fire/reload/sprint retained. |
| `player_range.tscn --scenario=ragdoll --assert-grip --assert-motion --view=side` | Passed 595 frames; reviewed all 20 captures: inverted launch, settling, get-up and stance. |
| `player_range.tscn --scenario=feet --feet-corrections --assert-feet --assert-motion --view=side` | Passed 24 corrected remote gait episodes; max pose 35.822°. Reviewed midframes for all 24 and full-size sprint. 600 captures generated; kept frames 00/12/24 per episode (72), deleted 528 redundant intermediates. |
| `menu_test.tscn --scenario=input --classic-lobby --port=24683` | 165 assertions, zero failures; 19 captures. Reviewed `match_02_after_turn.png` and `lab_04_remapped_pause.png`: HUD/camera turn and remapped pause/focus. Uses real `Input.ParseInputEvent` for mouse/gamepad/jump/reload/melee and menus. |

The contact sheets are `build/c2-{grip,recovery,remote}-review.png`. Captures show sampled poses/presentation,
not owner acceptance of continuous animation or responsiveness.

## Native network and final gates

The six-case matrix used `tools/dev/net_bots.sh -n 2 -p 24683 -s '--match-seed=23'`, fresh
`-o "$PWD/build/c2-net-<mode>-<profile>"`, and `-- --lobby-bots=1 --melee-start --net-seed=2` (four actors).
Bank/King: `-l 25`, King adds `--mode=king`; roaming: `-l 15 --lobby-roam=6 --matches=2`.
Each clean case uses `-t clean`; each faulted case uses `-t faults --net-lag=120 --net-jitter=30 --net-loss=3`.
King/roaming clean also use `-w 1` (960×540); other clients are headless. Every launcher exited zero, every
client/server log has zero engine errors, every pair grid was observed, and every case has zero tank disagreements.
Score, map/drop/mint/spill conservation and all server audits passed (two audits per roaming run).

| Case | Grids / body pairs | Final rest / moving disagreement, m | Longest above-tolerance episode, s |
| --- | --- | --- | --- |
| Bank clean | 700 / 17,791 | 0.000 / 0.072 | 0.05 |
| Bank faulted | 699 / 19,082 | 0.000 / 0.224 | 0.40 |
| King clean | 699 / 22,259 | 0.001 / 0.054 | 0.05 |
| King faulted | 700 / 21,727 | 0.000 / 0.155 | 0.40 |
| Roaming clean, two matches | 1,000 / 24,579 | 0.001 / 0.071 | 0.05 |
| Roaming faulted, two matches | 999 / 28,098 | 0.000 / 0.173 | 0.45 |

The same complete matrix was repeated on final runtime `8539a3e` / harness head `b268eb4`, with fresh
`build/c2-net-final-<mode>-<profile>` directories. All six launchers again exited zero, every server audit and
conservation check passed (two per roaming case), all grids were observed, and every client/server log has zero
engine errors. Final results:

| Case | Grids / body pairs | Final rest / moving disagreement, m | Longest above-tolerance episode, s |
| --- | --- | --- | --- |
| Bank clean | 700 / 15,333 | 0.001 / 0.116 | 0.05 |
| Bank faulted | 701 / 7,811 | 0.000 / 0.000 | 0.40 |
| King clean | 699 / 19,928 | 0.001 / 0.000 | 0.05 |
| King faulted | 700 / 23,475 | 0.000 / 0.207 | 0.40 |
| Roaming clean, two matches | 998 / 26,895 | 0.000 / 0.090 | 0.05 |
| Roaming faulted, two matches | 1,000 / 30,963 | 0.001 / 0.251 | 0.40 |

The initial rendered network commands set a capture directory without a screenshot interval/scenario; those runs
rendered successfully but saved no images. Dedicated King/lobby capture results are recorded below, rather than
claiming visual review of those matrix runs.

`TMPDIR="$PWD/build/c2-tmp" make check` passed on the final runtime (`build/c2-check-final-head.log`): Debug and
ExportRelease zero warnings and errors, 634 unit tests/zero failures, behavior/device input, bounded generation and forced formations,
separate-process world/segment hashes, and all gate self-tests. Compiled architecture: 2,353 Debug / 1,081 shipping
types, 17 approved exceptions, zero failures. Source sites: 92 approvals, zero failures. Gate infrastructure:
51 compiled negatives, 16 source negatives, plus stale/increased-site rejection (33.50 s). The earlier
`c2-check-complete.log` pass had 630 tests, before the four source-override cases were added. Unit capture failure
injection intentionally logs its rejected directory/PNG errors; clean production native runs are audited separately.

`TMPDIR="$PWD/build/c2-tmp" make test-equivalence` passed (`build/c2-equivalence.log`,
`build/rfp0-equivalence-13`): wire/schema/world/publication 8.07 s; Bank 23.19 s, King 41.14 s, spill-chain 8.50 s,
shortfuse 5.97 s, dissolve 2.27 s, exact frozen references. The final cold disposal-order cleanup is additionally
covered by the committed-head build/native/deep runs below. `check_links.py` passes with the new report staged;
its earlier invocation correctly rejected the then-untracked document link.

Repeated after the current-input-source correction: `build/c2-equivalence-final.log` / `build/rfp0-equivalence-14`,
all pass in 68.38 s total: wire/schema/world/publication 7.12 s; Bank 18.52 s, King 27.43 s, spill-chain 5.27 s,
shortfuse 6.27 s and dissolve 2.36 s. The immutable references are unchanged.

On committed runtime `82724a9`, `make build` passed with zero warnings/errors. The additional rendered native King
run used `net_bots.sh -n 2 -w 1 -l 60 -p 24683 -t clean -s '--match-seed=23' -o "$PWD/build/c2-king-scenario" --
--mode=king --scenario=king --net-seed=2 --capture-dir="$PWD/build/c2-king-scenario/shots"` (no owned extra bots,
as required by the scenario's single-actor peer selection). Both peers passed holder/contest/mint/warning/move/fresh
pile assertions. Complete audits passed: 1,400 grids, 42,194 body pairs, 26 minted/banked, zero tank disagreements,
zero engine errors. Reviewed all six captures on `build/c2-king-review.png`: zone hold/contest, warning, relocated and
settled fresh pile, final standings. The images show the received zone/HUD state and correct mint/results count.

`godot-mono --resolution 1280x720 --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=lobby
--port=24683 --capture-dir="$PWD/build/c2-lobby-input"` passed 60 assertions, zero engine errors. Reviewed all ten
captures on `build/c2-lobby-review.png`: lobby title/floor/card, docked settings, guest/bot arrival, staging/countdown,
Match GO/HUD, results and a new returned lobby world with its bot. Logs are `build/c2-final-native-{build,king,lobby}.log`.

`TMPDIR="$PWD/build/c2-tmp" make -j4 test-full` passed on `b268eb4` (runtime source identical to `8539a3e`),
978.51 s / 16 min 18.51 s, log `build/c2-test-full.log`. This is the complete final-head invocation, not the earlier
interrupted run. Zero build warnings/errors; 634 unit tests; all behavior and deep native assertions. Coverage
checks prove exactly 1,044 generator seed cases across 31 sweeps / four shards and 550 Lab cases, including 514
dial fixtures, across 25 shards. All lifetime shards passed repeated entry/reset/exit cycles. Navigation, King,
ringout, shortfuse, dissolve/recovery, long recoil/remote animation and menu lifetime passed; the latter reports
ten single-player and ten solo-match cycles without orphans. Upright stand/topple/gather passed 53 piles / 738
tanks, 120 simulated seconds / 60 player passes, max drift 0.001669 m / tilt 0.151°.

The serial native jobs also passed their complete audits, all with zero engine errors and zero disagreements:

| Deep native job | Grids / body pairs | Final rest / moving disagreement, m | Audit |
| --- | --- | --- | --- |
| Four clients, faulted 90 s rifle soak, seed 25 / net seed 2 | 1,999 / 172,940 | 0.001 / 0.221 | 36 map + 53 drops − 65 spills = 24 banked; four clients agree. |
| Faulted two-match duel, seed 23 | 1,198 / 40,183 | 0.001 / 0.187 | Both scenario completions and both server conservation audits agree. |
| Loading readiness/body stream with delayed ready client | 600 / 16,800 | 0.000 / 0.000 | Both clients complete; final score/conservation audit agrees. |

The rifle soak's peak moving disagreement is 4.888 m with a longest above-tolerance episode of 0.50 s, within the
unchanged fault profile; the final is 0.221 m. This passing sample does not erase earlier stock/FU2 failures or
establish a correction fix. No convergence threshold or classifier changed.

After the complete gate, the rendered real-device command was repeated on the final runtime, with fresh
`--capture-dir="$PWD/build/c2-input-final"`: 165 assertions, zero failures, zero engine errors, 19 captures.
Reviewed final `match_02_after_turn.png` and `lab_04_remapped_pause.png`: delivered turn, Match HUD/ammo/GO,
and correctly focused remapped pause. Log `build/c2-render-input-final.log`. All final Runtime/Dev C# source is
unchanged after the `b268eb4` full invocation; the subsequent commit only records/indexes this evidence.

## Failures retained and limits

An early new fixture mistook a legitimate null event field for a missing field and raised native callback errors;
the helper now distinguishes existence, and tests propagate native callback failures into assertions. Final
transcripts and suite runs are clean. An initial rendered actor launch overlapped this checkout's lint clean
rebuild and had no assembly to load; it was stopped and rerun after compilation (`c2-render-actors-final.log`).
The failed launcher log remains. Formatting/source-site sorting and stale FU2a observer anchors were corrected;
the infrastructure now proves the new anchors instead of dropping that diagnostic coverage. No threshold,
reference, gameplay behavior or protocol was changed to obtain a pass.

Linux proof only. Native Windows/macOS, broad ordinary capacity, continuous animation/feel and inherited acceptance
ledger items were not established. Recoil feedback remains synchronous; it is a narrow actor fact, not an event bus.
The cached view flag preserves primary-view presence semantics, not a shipped multi-user routing feature. Network
bytes, `Protocol.Version` (3), tuner schema/order/values, world construction and tank rules are unchanged: patch-safe.

## Landed application/dispatch integration

**Status:** Fresh parent comparisons, paired costs, normal/frozen gates, native matrix and rendered review pass.
The final deep gate runs after this evidence commit; its result is reported in the handoff.

2026-10-18. Merged A1 in `101d439`, then landed N1 (`dev` at `97773dc`) in `cdb2749`.
The integration production change is `f171fa6`, harness update `de837a2`. The fresh comparison parent is `97773dc`.
No other checkout, frozen reference, design file or shared gameplay rule changed.

Merge resolutions (conflict and relevant automatic combinations):

- `docs/README.md`: retain the C2, A1 and N1 evidence entries.
- `docs/architecture.md`: retain the C2 ownership/lifetimes and N1 transport/dispatch contract; document connection-codec borrowing.
- `docs/multiplayer.md`: retain scoped Match owners and N1 failure containment; clarify the facade's Dev-only callers.
- `tools/architecture/baseline.tsv`: combine both packages' removals; the file is empty, with no relabel or added exception.
- `tools/architecture/modules.json`: retain all C2 owner mappings and N1 Wire/Composition mappings.
- `tools/architecture/source-sites.tsv`: retain the moved C2 bot clocks and both N1 diagnostic clocks.
- `src/Dev/Equivalence/PublicationReferenceTests.cs`: retain the C2 state accessor and explicit N1 connection inputs; stimuli/references unchanged.
- `src/Dev/UnitTests/BotTests.cs`: retain C2 actor access and explicit connection construction.
- `src/Dev/UnitTests/Fixtures/MatchClient.cs`: retain `createLocalView`, inject transport/codec/clock; allow a borrowed codec for the focused assertion.
- `src/Dev/UnitTests/KingOnlineTests.cs`: retain C2 state access and explicit connection construction.
- `src/Dev/UnitTests/ParticipantServerTests.cs`: retain C2 actor-state access and explicit connection construction.

`NetClient.Codec` exposes its readonly captured codec. Match authority/mode sends and the extracted world gather
and actor pose/action sends use that same instance, selected by Main as before. The two client owners borrow it;
it owns no native resource and adds no retirement operation. `MessageCodec` stays in Composition, with only its
own declaration outside Dev and 38 calling files under `src/Dev/` (unit/fixture/scenario and frozen-reference probes).
Its comment and the contracts now say so. No encoding layout, publication capture point, callback or sequence
advancement moved. `awk -F'\t' '$4=="C2"' tools/architecture/baseline.tsv` prints nothing.

Match remains 629 lines across 293/167/17/97/55-line partials. World replica is 337 lines, actor set 326,
actor attachment 71, bot set 254 and view binding 370. Moved state, scopes and retirement remain as documented
above; codec borrowing is immutable connection input, not another mutable owner.

### Fresh parent preservation and cost

All fresh artifacts are `build/c2-n1-*`, using absolute scratch/capture paths and worktree-local TMPDIR.
`match_composition_equivalence.py --revision=97773dc|working --observe` prepares identical current composition,
lifetime and selection workloads. Both pass 7/10/3 tests; all 655/739/307 rows compare byte for byte.
`authority_equivalence.py` on the same revisions passes 58 authority, 13 gather and 2 publication tests on each.
Authority/gather have 391/21 identical rows; the publication packet/sequence JSON is identical.
`mode_client_equivalence.py --observe` passes 8 tests each and its comparator validates all 177 rows and required
operation/Bank/King/lobby coverage. All seven transcript hashes remain exactly those recorded earlier in this file.
The mode observer encodes through the actual selected codec; it does not substitute the static encoder.

`MatchCodecTests` passes the real Match mode send using an instrumented connection resolver. A private scratch build
restoring the static encoder fails that assertion with exit 1 (`c2-n1-codec-negative.log`); the normal source passes.
Seven mode comparator self-tests pass. The initial focused probe used an invalid empty spill and failed validation;
it now supplies a valid one-body spill. The first parent mode scratch compile lacked the C2 accessor; after copying
it, comparison exposed the parent's smaller pre-C2 Sandbox inventory. The helper now copies the exact same current
lifetime cases to both sides; no record is removed or normalized. Failed preparation/comparison artifacts remain.

[Paired integration data](match-composition-n1-costs.csv) records three alternating parent/candidate pairs, with
uptime before/between/after. No own compile/native workload overlaps measurement. Prepare unobserved projects
with `match_composition_equivalence.py --owned-bots-bench`; increase only the private benchmark's warm-up to 100,000
on both, then run `AuthorityBenchTests --authority-bench --match-seed=23`. Each case measures 100,000 complete Match
callbacks, four actors, 76 bodies, two real brains, 200,000 thinks and 200,000 rays. The claim case also asserts
100,000 requests/notifications and 7,900,000 payload bytes.

| Whole callback | Bytes/tick, both | Median mean / p95 / p99 microseconds, parent → candidate |
| --- | ---: | --- |
| Ordinary playing | 2677.32 | 76.105/110.900/145.300 → 75.701/110.500/145.300 |
| Controlled claims | 9683.50 | 103.798/138.700/173.600 → 104.952/140.100/174.500 |

Allocations/counts agree in all six runs; pairwise timing changes have both signs. This establishes no added
steady-state allocation or consistent slowdown in these samples, not a portable speedup or GPU/frame budget.

### Integrated gates and native review

`make setup` and `make check` pass (`build/c2-n1-setup.log`, `build/c2-n1-check.log`): zero Debug/ExportRelease
warning-as-error warnings, 666 unit tests, 170 real-device input assertions, all bounded/native behavioral checks
and gate self-tests. Architecture reports 2,439 Debug / 1,096 shipping types, zero exceptions; source checks approve
97 sites. The animation data audit retains its existing 12 visual redo items, not a new failure.

`make test-equivalence` passes (`build/c2-n1-equivalence.log`, `build/rfp0-equivalence-15`): frozen
wire/schema/world/publication and controlled Bank, King, spill-chain, Short Fuse and Dissolve traces. No reference
was regenerated.

Six fresh native runs pass, each with two clients and one owned bot per client, with one rendered client:
`tools/dev/net_bots.sh -o "$PWD/build/c2-n1-net-CASE-PROFILE" -p 24683 -n 2 -w 1 -l LENGTH -t PROFILE
-s '--match-seed=23' -- --lobby-bots=1 --shot-every=10 --capture-dir=ABSOLUTE_PATH`.
Bank/King use LENGTH=25; King adds `--mode=king --mint-seconds=0.5`. Roaming uses LENGTH=15 and
`--lobby-roam=2 --matches=2`. Faults add `--net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3`.
All 18 server/client logs have zero engine/internal-owner errors; all runs have zero disagreements/unobserved grids
and complete conservation audits. Peaks/episodes retain the ordinary checker thresholds; this is no convergence fix.

| Case/profile | Body pairs | Final rest/moving (m) | Longest episode (s) | Complete audits |
| --- | ---: | ---: | ---: | ---: |
| bank/clean | 9670 | 0.001/0.067 | 0.10 | 1 |
| bank/faults | 10425 | 0.006/0.163 | 0.55 | 1 |
| king/clean | 26081 | 0.001/0.000 | 0.05 | 1 |
| king/faults | 24075 | 0.000/0.287 | 0.45 | 1 |
| roam/clean | 13415 | 0.000/0.000 | 0.10 | 2 |
| roam/faults | 12517 | 0.000/0.124 | 0.40 | 2 |

Reviewed native `bank-clean/shots/Bot1_020.0.png` and `king-clean/shots/Bot1_020.0.png` under the case roots above:
Bank jump/weapon/ammo/standings and King zone/holding/mint/score presentation are present.

Rendered runs use `godot-mono --path . --resolution 1280x720 --fixed-fps 60`, the scene paths below and absolute
`--capture-dir="$PWD/build/c2-n1-NAME"`. All five exit 0, with zero engine/test errors:

| Scene / arguments | Captures / assertion result |
| --- | --- |
| `menu_test.tscn --scenario=input --classic-lobby --port=24683` | input: 20, 170 assertions pass |
| `session_actors_tests.tscn` | actors: 2; independent views/devices, survivor and camera-free bot combat/get-up pass |
| `player_range.tscn --scenario=grip --grip-sweep --assert-grip --assert-motion --view=side` | grip: 29; both probes pass |
| `player_range.tscn --scenario=ragdoll --assert-grip --assert-motion --view=side` | recovery: 20; both probes pass |
| `player_range.tscn --scenario=feet --assert-feet --assert-motion --view=side` | remote: 600; all 24 episodes/probes pass |

Reviewed the input, actor, grip and recovery contact sheets plus remote frame 12 from each of the 24 episodes.
Full-resolution review includes `input/match_05_leaderboard_toggled.png` (focused hidden preference) and
`grip/aim_up_fire.png` (both hand contacts). Delivered turns/jumps/pause, camera survivor, local/remote gait and
weapon holds, reload/firing extremes, ragdoll launch and get-up are visible. This is sampled visual review, not
continuous animation/feel acceptance. Gamescope's expected shutdown diagnostics remain in the raw logs.

The final `make -j4 test-full` runs once after this evidence-only commit; the handoff records its exit and tail.
Runtime/Dev source is unchanged after `de837a2`. Windows/macOS runtime, continuous feel and the inherited acceptance
ledger remain outside this Linux integration proof. Wire bytes, Protocol.Version (3), tuner schema/order/values,
world generation/RNG, tank/scoring rules and synchronous retirement remain unchanged; patch-safe.
