# C1 contract placement evidence

**Status:** Focused C1 preservation and dependency evidence. Final branch gate results are recorded in the delivery report.

2026-10-18, Linux, Godot 4.7.2 .NET/Jolt. Parent production: `42ee691`; parent witness commit: `4f7eeb6`.
The same witness passed before production moved. `935e398` relocates shared contracts; `9138583` finishes plan
and admission directions. No new baseline exception was added. Three existing exceptions transfer to A1/N1 below.

## Ownership and moves

Paths below start at `src/`. Existing `.uid` files followed AppearanceRecord, AmmoPickup, EffectProfile and
SegmentPlans. Every new script file has its imported `.uid`. No assets, design files, `.import` metadata, tuner declarations,
reference data or protocol version changed.

| Types or members | From | To; reason |
| --- | --- | --- |
| AppearanceRecord, AppearanceCodec | Net/AppearanceRecord.cs; Propane.Net | Core/AppearanceRecord.cs; Propane.Core, Foundation. Settings and profiles share the same bounded serializer. |
| PlayerColors, ColorNames, PlayerColor | Net.Protocol | Core/AppearancePalette.cs; Foundation. Presets/fallback belong to appearance. |
| IdentitySequence, ActorKind, ControllerKind, ControllerConfig, ActorRecord | Net/Participants.cs; Propane.Net | Core/ActorIdentity.cs; Foundation. Plain actor identity/allocation values; server roster stays in Net. |
| BodyState, VentParameters | Net/MessageCodec.cs; Propane.Net | Core/BodyState.cs; Foundation. Shared poses and vent values have no codec/client owner. |
| TankLedger.State | Net/TankLedger.cs | Core.BodyState.State. Underlying int values and simple enum name State are unchanged, including frozen observation spelling. |
| GatherPhase | Tank/GatherGroups.cs | Core/BodyState.cs; Foundation. Same byte enum used by gathering and replication. |
| MapTankBase, DropTankBase, CarBase, bounds and ID formulas | Net.BodySync | Core/BodyIds.cs; Foundation. Exact arithmetic, argument names, failure points and counter handling. |
| MaxExtrapolation | Net.BodySync | Core.BodyState; same 0.3 unscaled network seconds. This erased const dependency was also removed from source. |
| PlayerNetState (including Mode and Lerp) | Player/PlayerNet.cs | Player/PlayerNetState.cs, same namespace; explicitly Foundation. Native ammo/shot integration stays Simulation. |
| LobbyPhase | Net.LobbyServer nested enum | Net/LobbyPhase.cs; Wire. Same byte values, independent of the concrete server. |
| LobbySummary, ModeSettings, PlayerProfile, LobbyInfo, MatchSetup, PeerCapabilities | Net/NetClient.cs | Net/LobbyContracts.cs; Wire, same namespace. These network-specific values leave Foundation. |
| Standing | Net/NetClient.cs; Propane.Net | Modes/Standing.cs; Rules. Plain results observation. |
| MaxStage | Net.LobbyServer | Net.Protocol; same 10-second StartsIn wire bound/tuner ceiling. Erased source dependency removed too. |
| AmmoPickup | Net/AmmoPickup.cs; Propane.Net | World/AmmoPickup.cs; Simulation. Same native node, fields, render clock and world retirement. |
| EffectProfile and Scope/System | Fx/EffectProfile.cs; Propane.Fx | Diagnostics/EffectProfile.cs; Foundation observation. Same opt-in process counters, diagnostic clock, drains and allocations. |
| SegmentSettings, SegmentPlan, SegmentPlans | World/SegmentPlans.cs; Propane.World | World/Plan/SegmentPlans.cs; Propane.World.Plan. Plain settings/plans/generation. |
| SegmentSettings.From | World/SegmentPlans.cs | SuburbPlans.Segments; Composition. Same live conversion at session construction, outside Plans. |
| PlanCatalogSnapshot | World/SegmentPlans.cs; Propane.World | World/Plan/PlanCatalogSnapshot.cs. Plans data owner; native capture moves to GameCatalog.Capture in Simulation. |
| CarScale, weighted CarModels, PickupModel/bed constants, TreeModels, BushModels, FlowerModels | World.Catalog | World/Plan/PlanModels.cs. Plain authored metadata; duplicate weights/order and dimensions unchanged. Native caches remain Catalog-owned. |
| GroundHeight, ToWorld | World.Suburb | World/Plan/PlanCoordinates.cs. Shared plain ground frame; same floating-point expression. |
| KingRules.SpotsFrom | Modes/KingRules.cs | Net/KingPlacement.cs; Server adapter shared by local composition. Same sorted groups, double means and empty-plan result; no plan dependency in Rules. |
| MeleeTiming (new value) | Five live Tuning reads in validator | Net/MeleeValidation.cs; Server. Caller samples five floats per start and contact cap per contact; active window/cooldown remain frozen at start. |
| Native generation binding | ServerRun calling SuburbPlans directly | Existing composition root ModeRegistry supplies a static generator callback through LobbyServer. Settings/seed/reroll callback and synchronous invocation point stay unchanged. |

Mutable state is moved, not duplicated: IdentitySequence.next remains per sequence under its existing roster/session/server or process-level view-ID owner;
profiling arrays/Enabled remain process-lived and opt-in; snapshot house/footprint containers remain snapshot-lived
managed data. Capture transfers fresh containers and retains no native source. Static model arrays retain their
existing process lifetime. New generator references borrow a static function for server/run lifetime and hold no
native resource. Actor, run, world, subscription and native disposal order are unchanged. ServerRun still owns and
releases its Tuning exactly once; moving that ownership is explicitly A1 work, not claimed as completed here.

No new processing node, scheduler, entity framework, authority policy or event bus was introduced. Existing car
prefix tests and numeric ID range tests remain separate, with their original comparison semantics. Offline shooter
attribution, online causality and Dissolve chain identity remain distinct.

## Dependency accounting

Candidates were generated under build/ against compiled Debug assemblies; checked-in policy was never regenerated.
Debug and ExportRelease checks agree: 53 prohibited type edges become 23, with zero new keys and zero stale keys.
Thirty C1 entries are removed; three existing entries are reassigned; none name C1. All 92 source-site approvals
remain, with only EffectProfile's two diagnostic clock source paths moved.

| Rule | Parent | Candidate |
| --- | ---: | ---: |
| direction | 49 | 22 |
| pure-native | 1 | 0 |
| server-physical | 2 | 0 |
| transport-backend | 1 | 1 |

Direction counts by source/target module (all prohibited direction edges, including other packages):

| Module pair | Parent | Candidate |
| --- | ---: | ---: |
| Client → Composition | 2 | 2 |
| Client → Server | 2 | 1 |
| Foundation → Server | 2 | 0 |
| Foundation → Wire | 2 | 0 |
| Plans → Composition | 1 | 0 |
| Plans → Simulation | 3 | 0 |
| Presentation → Composition | 2 | 2 |
| Rules → Plans | 2 | 0 |
| Server → Client | 1 | 0 |
| Server → Composition | 5 | 4 |
| Server → Simulation | 6 | 2 |
| Simulation → Client | 5 | 3 |
| Simulation → Presentation | 3 | 3 |
| Wire → Composition | 3 | 3 |
| Wire → Server | 4 | 0 |
| Wire → Simulation | 6 | 2 |

Every original C1 entry:

| Rule | Original edge (Propane prefix omitted) | Disposition |
| --- | --- | --- |
| direction | `Modes.KingRules` → `World.Plan.SuburbPlan` | Removed: Server KingPlacement maps plans into rule setup. |
| direction | `Modes.KingRules` → `World.Plan.TankPlacement` | Removed: unchanged grouping/means/order live in KingPlacement. |
| direction | `Net.AppearanceCodec` → `Net.NetReader` | Removed: Foundation codec checks its byte-sized count locally, with the same error. |
| direction | `Net.AppearanceRecord` → `Net.Protocol` | Removed: Foundation AppearancePalette owns presets and fallback. |
| direction | `Net.BodyAuthority` → `Net.TankLedger` | Removed: lifecycle enum is Core.BodyState.State. |
| direction | `Net.ClientAuthorityTransactions` → `Net.TankLedger` | Removed: lifecycle enum is Core.BodyState.State. |
| direction | `Net.GatherMessage` → `Tank.GatherPhase` | Removed: GatherPhase is a shared Foundation value. |
| direction | `Net.LobbyInfo` → `Net.LobbyServer` | Removed: standalone Wire LobbyPhase; lobby contract is Wire. |
| direction | `Net.LobbySummary` → `Net.LobbyServer` | Removed: standalone Wire LobbyPhase; lobby contract is Wire. |
| direction | `Net.MeleeValidation` → `Core.Tuning` | Removed: plain Server MeleeTiming and a live integer contact cap. |
| direction | `Net.MessageCodec` → `Core.Tuning` | N1: Single-tuner decoding still invokes the native Tuning schema; N1 must inject owner-scoped validation with the same bounds and rejection order. |
| direction | `Net.MessageCodec` → `Net.LobbyServer` | Removed: standalone LobbyPhase and Protocol.MaxStage. |
| direction | `Net.MessageCodec` → `Net.TankLedger` | Removed: lifecycle enum is Core.BodyState.State. |
| direction | `Net.MessageCodec` → `Player.PlayerNetState` | Removed: separate plain pose file mapped to Foundation. |
| direction | `Net.MessageCodec` → `Tank.GatherPhase` | Removed: shared Foundation GatherPhase. |
| direction | `Net.NetClient` → `Net.LobbyServer` | Removed: standalone Wire LobbyPhase. |
| direction | `Net.NetReader` → `Core.Tuning` | N1: Complete snapshot decoding still invokes the native Tuning schema; N1 must inject owner-scoped validation preserving complete-schema checks and rejection order. |
| direction | `Net.PlayerStateMessage` → `Player.PlayerNetState` | Removed: separate plain pose file mapped to Foundation. |
| direction | `Net.ServerRun` → `Core.Tuning` | A1: The run still owns, mutates and disposes native Tuning for live phase/stock/snapshot reads; A1 must transfer configuration ownership with template/snapshot borrowing, preserving live debug sampling and exactly-once retirement. Melee admission now receives plain values. |
| direction | `Net.ServerRun` → `Player.PlayerNetState` | Removed: separate plain pose file mapped to Foundation. |
| direction | `Net.ServerRun` → `World.SuburbPlans` | Removed: composition supplies the existing native generator at the same call point. |
| direction | `Net.TankLedger` → `Net.BodySync` | Removed: Foundation BodyIds owns the same allocation formulas. |
| direction | `Net.TankLedger` → `Tank.GatherPhase` | Removed: shared Foundation GatherPhase. |
| direction | `Net.TankLedger` → `World.Suburb` | Removed: Plans PlanCoordinates owns the unchanged ground conversion. |
| direction | `Player.ActorRuntime` → `Net.BodySync` | Removed: Foundation BodyIds owns the drop formula. |
| direction | `World.Plan.SuburbFurnishing` → `World.Catalog` | Removed: plain PlanModels owns weighted keys and authored dimensions. |
| direction | `World.PlanCatalogSnapshot` → `World.Catalog` | Removed: PlanModels supplies keys; GameCatalog.Capture performs native measurement outside Plans. |
| direction | `World.SegmentSettings` → `Core.Tuning` | Removed: composition SuburbPlans.Segments samples tuning; settings stay plain. |
| direction | `World.SegmentSettings` → `World.SuburbPlans` | Removed: conversion is outside the settings record. |
| direction | `World.Suburb` → `Net.BodySync` | Removed: Foundation BodyIds owns map-tank IDs. |
| pure-native | `World.SegmentSettings` → `Core.Tuning` | Removed: composition SuburbPlans.Segments samples tuning; settings stay plain. |
| server-physical | `Net.ServerRun` → `Player.PlayerNetState` | Removed: separate plain pose file mapped to Foundation. |
| server-physical | `Net.TankLedger` → `World.Suburb` | Removed: Plans PlanCoordinates owns the unchanged ground conversion. |

A1 owns application template/snapshot borrowing and the native run resource transfer. Splitting a second mutable
resource owner into C1 would widen this mechanical package and its exactly-once lifetime contract. The native
run field, disposal and live debug reads therefore stay intact. N1 already owns per-owner codec/schema resolution
and decode/admission ordering; tuner validation belongs at that boundary. No native schema was copied into Foundation.

## Preservation proof

The frozen release references and their manifests are unchanged. Parent and candidate both pass
`make test-equivalence`: exact wire/schema/world/publication and Bank, King, spill-chain, Short Fuse and controlled
Dissolve traces. The tuner inventory remains 557 scalar declarations, 431 UI entries and 404 ordered shared fields.

The six ContractPlacementTests cases run in make test and compare exact parent/candidate JSON via
`--contracts-transcript`. They cover ID boundaries and refusal parameter/messages, enum values, catalog
house/palette/key read order and detached values, segment clamps, ordered double pile means, original car prefix
membership, palette wrap/fallback/count diagnostics and live melee cap versus frozen window/cooldown. Three
independent deliberate scratch corruptions (ID arithmetic, capture order, pile order) each fail nonzero on the parent.

The server observer records all incoming/outgoing bytes, peers and traffic classes. Parent and candidate match
exactly for ServerTests (including ParticipantServerTests): 34 cases / 1,500 records; KingOnlineTests: nine cases /
549 records; LobbyWorldTests: 13 cases / 449 records. All 2,498 records match. A fixed seed is observation-only.
Original generation, traversal, sort/tie and RNG code remains intact; the same plan function runs synchronously
before ledger/rules construction and before wire construction. No clock, scene callback or send order moved.

Reproduction (set TMPDIR to an absolute worktree build directory first):

```sh
make setup
make build
make lint
make test
make test-equivalence
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ContractPlacementTests --contracts-transcript="$PWD/build/c1-candidate-contracts.json"
cmp build/c1-parent/contracts.json build/c1-candidate-contracts.json
python3 -B tools/dev/server_equivalence.py --revision=4f7eeb6 --label=c1-server-parent --observe
python3 -B tools/dev/server_equivalence.py --revision=working --label=c1-server-candidate --observe
# In each isolated project, use a fresh absolute transcript path and compare with cmp:
godot-mono --headless --path build/c1-server-candidate/project res://scenes/dev/unit_tests.tscn -- --suite=ServerTests --match-seed=23 --server-transcript="$PWD/build/c1-candidate-ServerTests.jsonl"
# Repeat for KingOnlineTests and LobbyWorldTests.
```

The preparers for authority, actor and mode client witnesses compile with zero warnings on both 4f7eeb6 and the
candidate. Old probes translate only moved body API names back to the parent's spelling. Actor's generated cycle
probe imports Core for the moved actor kind. Observer assertions and workloads are unchanged. Release-impact
fixtures cover the new compatibility-sensitive paths; vent diagnostic preparation tests pass.

One invalid standalone fixture launch overlapped lint's nonincremental assembly rebuild and reported missing
script classes. It was interrupted (exit 130), retained as c1-contract-witness-assembly-race.log and rerun after
build completion. The completed witness and reference runs pass with no engine errors. Initial compile and
formatting iterations were resolved before the green gates. An actor-preparer command initially used an unsupported
--observe flag (exit 2); corrected parent/candidate invocations both compile cleanly. These are orchestration failures,
not passing application samples.

## Native construction and review

```sh
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/game_test.tscn -- --scenario=clear --quit=32 --effects-seed=23 --tune.FixedSeed=23 --capture-dir="$PWD/build/c1-construction"
```

Parent and candidate pass the real clear/replace path: zero tanks in the cleared suburb, retired content, then a
40-tank replacement. Reviewed cleared.png and next_suburb.png in both build/c1-parent/construction and
build/c1-construction: intact house/car/ground presentation and normal aftermath/replacement. Blast/camera samples
are not pixel-identical and are not treated as deterministic physics traces. Exact plan and construction hashes,
not screenshots, establish the frozen construction equivalence. Both rendered scenes have zero Godot engine errors;
gamescope emits its ordinary compositor shutdown Broken pipe line after success.

Clean and faulted ordinary four-bot matches both pass unchanged conservation and continuous convergence gates:

```sh
tools/dev/net_bots.sh -n 4 -l 90 -t clean -o "$PWD/build/c1-net-clean4" -s "--match-seed=23" -- --melee-start --net-seed=1
tools/dev/net_bots.sh -n 4 -l 90 -o "$PWD/build/c1-net-fault4" -s "--match-seed=23" -- --melee-start --net-lag=120 --net-jitter=30 --net-loss=3 --net-seed=1
tools/dev/net_bots.sh -n 2 -l 65 -t clean -w 1 -o "$PWD/build/c1-net-king" -s "--match-seed=23" -- --mode=king --scenario=king --tune.PlayerSendRate=20 --net-seed=1 --capture-dir="$PWD/build/c1-king-shots"
```

| Native run | Grids / body pairs | Final rest / moving (m) | Audit |
| --- | --- | --- | --- |
| Clean four bots, 90 s | 2,000 / 254,729 | 0.001 / 0.076 | 33 map + 45 drops - 56 spills = 22 banked; all four clients agree. |
| Faulted four bots, 90 s | 1,999 / 185,163 | 0.001 / 0.482 | 36 map + 52 drops - 62 spills = 26 banked; all four clients agree. |
| Rendered King, two bots, 65 s | 1,500 / 45,588 | 0.000 / 0.000 | 28 minted = 28 banked; both clients agree. |

All three runs have zero unobserved pair grids, zero disagreements and zero engine/protocol errors, with complete
server audits. The faulted terminal moving result is near the existing 0.50 m limit; no threshold was changed.
The recorded FU2 residual remains an independent open obligation. Reviewed all six King captures in
build/c1-king-shots: hold, contest, warning, moved/materializing pile, settled pile and 28/0 results. The zone,
materialization, settled tanks, house/car/ground assets and HUD appear in their expected sampled states.

## Controlled receive/run and client callback cost

The same existing probes run unobserved on parent and candidate, after builds and the offline gate finish.
No compiler/render/network workload overlapped these measurements. Three parent/candidate pairs cover publication
and the actual whole Match physics callback. Three server pairs followed by three reversed candidate/parent pairs
resolve the first server tail's order concern; all samples are retained. Uptime was recorded per pair: one-minute
load 0.29, 0.57, 0.69, 1.21, 1.33, 1.43 (the raw uptime lines remain in build/c1-pair-N-uptime.txt).
These are synchronous controlled CPU probes, not native frame/capacity measurements.

```sh
# Alternate parent/candidate; record uptime per pair. Parent is an isolated unobserved 4f7eeb6 source build.
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ServerRunBenchTests --server-bench --match-seed=23
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=BodyPublicationTests --publication-bench --match-seed=23
```

Server: 20,000 warm-up + 30,000 measured ticks per mode, eight bodies and two received messages per tick;
OnReceived plus AdvanceMatches includes decode, admission, encoding, delivery and run ticking. Publication:
2,000 warm-up + 10,000 measured ticks per path, 128 tanks + 128 cars; the second window invokes the actual
Match._PhysicsProcess callback. Every pair has identical packets, payload bytes and reported allocations (0.01 B/tick
precision). Server packets/payload: Bank 60,500 / 19,024,500; King 60,518 / 19,025,886; roaming 60,000 / 19,020,000.
Publication: 13,188 / 14,533,820; whole Match physics: 15,688 / 14,781,320.

Medians across six server pairs or three publication pairs (parent → candidate; microseconds):

| Workload | B/tick, both | Mean | p95 | p99 |
| --- | ---: | --- | --- | --- |
| publication: match-physics | 6538.21 | 40.701 → 40.785 | 138.500 → 139.100 | 257.600 → 257.300 |
| publication: publication | 6144.21 | 37.390 → 37.526 | 143.100 → 135.500 | 260.200 → 255.400 |
| server: King of the Cul-de-sac | 7070.44 | 14.936 → 14.909 | 16.250 → 16.300 | 27.900 → 20.800 |
| server: Tank Bank | 6868.12 | 15.639 → 15.797 | 18.300 → 18.600 | 21.400 → 25.350 |
| server: roaming | 6813.65 | 13.808 → 14.003 | 14.200 → 14.400 | 18.650 → 18.600 |

No extra steady allocation was observed. Whole Match callback medians differ by +0.21% mean, +0.43% p95 and
-0.12% p99. Server whole receive/run means differ by -0.18% to +1.41%. Bank's combined p99 median is higher
(21.400 → 25.350 us), but its six individual paired p99 changes have three positive and three negative signs;
reversing order also reverses some mean/tail differences. No consistent per-pair timing regression was identified.
The short Debug tail windows do not justify a universal performance or capacity claim, nor do they close prior
native performance debts. No hot-loop algorithm or workload was changed.

Every timing pair, parent/candidate mean/p95/p99 (us); allocations/counts are the identical values above:

| Workload | Pair | Mean | p95 | p99 |
| --- | ---: | --- | --- | --- |
| publication: match-physics | 1 | 40.151 / 41.121 | 135.400 / 142.200 | 251.400 / 264.700 |
| publication: match-physics | 2 | 41.104 / 39.996 | 138.500 / 139.100 | 257.600 / 257.300 |
| publication: match-physics | 3 | 40.701 / 40.785 | 138.500 / 137.600 | 258.100 / 255.700 |
| publication: publication | 1 | 37.390 / 38.194 | 143.900 / 140.200 | 254.600 / 256.500 |
| publication: publication | 2 | 38.436 / 37.526 | 143.100 / 135.100 | 263.500 / 251.400 |
| publication: publication | 3 | 36.254 / 37.156 | 137.200 / 135.500 | 260.200 / 255.400 |
| server: King of the Cul-de-sac | 1 | 15.058 / 14.821 | 16.500 / 15.900 | 28.600 / 28.600 |
| server: King of the Cul-de-sac | 2 | 14.778 / 14.733 | 16.000 / 16.000 | 28.300 / 24.100 |
| server: King of the Cul-de-sac | 3 | 14.671 / 14.772 | 15.900 / 16.100 | 23.400 / 18.900 |
| server: King of the Cul-de-sac | 4 | 14.952 / 15.105 | 16.400 / 16.500 | 23.100 / 20.600 |
| server: King of the Cul-de-sac | 5 | 15.000 / 15.013 | 16.400 / 16.600 | 27.500 / 19.400 |
| server: King of the Cul-de-sac | 6 | 14.919 / 14.996 | 16.100 / 16.500 | 28.300 / 21.000 |
| server: Tank Bank | 1 | 15.316 / 15.877 | 18.400 / 18.600 | 27.600 / 29.200 |
| server: Tank Bank | 2 | 15.680 / 15.921 | 18.200 / 18.900 | 21.500 / 30.300 |
| server: Tank Bank | 3 | 15.828 / 15.892 | 18.300 / 18.600 | 21.300 / 21.200 |
| server: Tank Bank | 4 | 15.367 / 14.049 | 18.300 / 14.300 | 21.000 / 17.000 |
| server: Tank Bank | 5 | 15.603 / 15.602 | 18.300 / 18.900 | 27.500 / 21.500 |
| server: Tank Bank | 6 | 15.674 / 15.718 | 18.400 / 18.300 | 21.100 / 30.000 |
| server: roaming | 1 | 13.905 / 13.984 | 14.300 / 14.300 | 18.900 / 18.500 |
| server: roaming | 2 | 13.724 / 14.058 | 14.000 / 14.600 | 18.000 / 28.100 |
| server: roaming | 3 | 13.941 / 13.788 | 14.300 / 14.200 | 20.000 / 18.400 |
| server: roaming | 4 | 13.792 / 14.036 | 14.200 / 14.500 | 18.400 / 18.700 |
| server: roaming | 5 | 13.789 / 13.748 | 14.100 / 14.200 | 18.000 / 18.300 |
| server: roaming | 6 | 13.824 / 14.021 | 14.200 / 14.500 | 18.900 / 23.600 |


## Scope and compatibility

C1 touched shared contract consumers, the B3a composition factory and the existing differential preparers to keep
imports and parent witnesses valid. It changes no B2 authority transaction, B3 client policy, B4 tactics, L1 actor
retirement or A1 resource ownership behavior. Existing acceptance debts in architecture.md remain independent.
Linux is the verified runtime; native Windows/macOS execution and continuous subjective feel are not established.

Patch-safe: unchanged Protocol.Version = 3, message/appearance/settings bytes and migration, enum values, ID
arithmetic, car tests, asset keys, tuner schema/order/values, world hashes, construction/RNG/callback/clock order.
No references were refreshed and no branch was merged or pushed. The final delivery report records make check and
one make -j4 test-full on the final branch head; these focused observations do not replace those gates.
