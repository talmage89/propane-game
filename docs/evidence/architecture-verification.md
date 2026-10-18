# Architecture contract and gate verification — 2026-10-17

**Status:** Complete A0 contract/gate verification on Linux. Inherited acceptance remains open in the
[unfinished ledger](../architecture.md#unfinished-acceptance-ledger); this record does not close it.

A0, branch `wp/rf-a0-architecture-contract`, integrated `dev` through `f10e19d` (B1), including D1, M0 and D0.
Linux/CachyOS, GTX 1660, Godot 4.7.2 .NET/Jolt, .NET SDK 10.0.113. No A0 commit changes production C#,
shared behavior, test thresholds, historical evidence or release-reference data. Protocol remains 3.
Current contract: [architecture](../architecture.md); operation/limits: [checker](../../tools/architecture/README.md).

## Contract cross-check

Current claims were checked against the owners below; target paragraphs are explicitly proposals assigned to
packages, not assertions that those classes/capabilities already exist.

| Contract section | Verified code / detailed contract |
| --- | --- |
| Purpose and design authority | [VISION](../../design/VISION.md), [Game](../../src/Game.cs), [AGENTS](../../AGENTS.md). |
| System map | [Module map](../../tools/architecture/modules.json), [metadata reader](../../tools/architecture/Program.cs), [baseline](../../tools/architecture/baseline.tsv). |
| Composition roots | [Main](../../src/Main.cs), [Match assembly](../../src/Net/Match.Assembly.cs), [ServerRun](../../src/Net/ServerRun.cs), [LabSession](../../src/Lab/LabSession.cs). |
| Scopes and ownership | [SessionContext](../../src/Session/SessionContext.cs), [WorldContext](../../src/Core/WorldContext.cs), [LabMatch](../../src/Lab/LabMatch.cs), [LocalView](../../src/Player/LocalView.cs). |
| Assembly and retirement | [SessionFactory](../../src/Session/SessionFactory.cs), [SessionContext](../../src/Session/SessionContext.cs), [LobbyServer](../../src/Net/LobbyServer.cs). |
| Clock and callback domains | [WorldClock](../../src/Core/WorldClock.cs), [Match](../../src/Net/Match.cs), [ServerRun](../../src/Net/ServerRun.cs), [LogicalAim](../../src/Player/LogicalAim.cs), [project settings](../../project.godot). |
| Thread boundary | [LocalSegmentFeed](../../src/Session/SegmentWorldSource.cs), [FrameEncoder](../../src/ReleaseSmoke/CaptureDirector.cs), [UpdateCheck](../../src/Net/UpdateCheck.cs). |
| Identity and authority | [Participants](../../src/Net/Participants.cs), [BodySync](../../src/Net/BodySync.cs), [Protocol](../../src/Net/Protocol.cs), [IPacketTransport](../../src/Net/IPacketTransport.cs). |
| Replication transactions | [Match.OnAuthority](../../src/Net/Match.cs), [BodyPublisher](../../src/Net/BodyPublisher.cs), [SnapshotBuffer](../../src/Net/SnapshotBuffer.cs), [MatchInbox](../../src/Net/MatchInbox.cs). |
| Modes and capabilities | [ModeDefinition/registry](../../src/Modes/ModeDefinition.cs), [ModeWires](../../src/Net/ModeRunner.cs), [schemas](../../src/Net/ModeEnvelope.cs), [IBotWorld](../../src/Bots/IBotWorld.cs). |
| Simulation and presentation | [PlayerCharacter](../../src/Player/PlayerCharacter.cs), [LocalView](../../src/Player/LocalView.cs), [GameEvents](../../src/Core/GameEvents.cs), [Spawn](../../src/Core/Spawn.cs), [Rifle](../../src/Player/Rifle.cs). |
| Worlds, RNG and assets | [SuburbGenerator](../../src/World/Plan/SuburbGenerator.cs), [SuburbFurnishing](../../src/World/Plan/SuburbFurnishing.cs), [SegmentPlans](../../src/World/SegmentPlans.cs), [GameAssets](../../src/Core/GameAssets.cs). |
| Globals and budgets | [WorldTimeScale](../../src/Core/WorldTimeScale.cs), [TankSimulation](../../src/Tank/TankSimulation.cs), [DissolveArena](../../src/Lab/DissolveArena.cs), [source approvals](../../tools/architecture/source-sites.tsv). |
| Verification and evidence | [Makefile](../../Makefile), [infra probes](../../tools/dev/lint_tests.py), [release references](../release-reference.md), [evidence index](../README.md#evidence). |
| Build, export and release | [Project](../../Propane.csproj), [presets](../../export_presets.cfg), [release workflow](../../.github/workflows/release.yml), [deploy](../../tools/server/deploy.sh). |
| Extension recipes and limitations | [Session/source seams](../../src/Session/SessionFactory.cs), [mode catalog](../../src/Modes/ModeDefinition.cs), [transport seam](../../src/Net/IPacketTransport.cs), [world services](../world-services.md). |

## Initial policy inventory

The 13 map entries are Foundation, Plans, Rules, Simulation, Navigation, Bots, Wire, Server, Client, Presentation,
Composition, Smoke and Dev. Namespace defaults are narrowed by file/type overrides; nested/generated types retain
their top-level owner. The allowed direct directions are in the map and contract. Rules include pure native/clock
boundaries, simulation/client/HUD separation, bot/live-rule separation, server/physical separation, transport backend
independence, runtime/Dev exclusion, exact smoke entry, named global/timer owners and worker native access.

The initial reviewed baseline has 78 rule/source/target entries:

| Rule | Entries |
| --- | ---: |
| direction | 65 |
| bot-live-owner | 9 |
| pure-native | 1 |
| server-physical | 2 |
| transport-backend | 1 |

| Removing package | Entries |
| --- | ---: |
| A1 | 8 |
| B3a | 20 |
| B3b | 9 |
| C1 | 31 |
| C2 | 6 |
| N1 | 4 |

Each line retains its reason. Before B1 landed during A0 bootstrapping there were 74 entries: integration removed
four obsolete LobbyServer keys and added eight inherited references now owned/shared by ServerRun. This is the
initial baseline against already-landed dev, before A0's gate lands; it does not authorize future baseline growth.
The gate never writes it. The lead can generate a candidate under build/ for explicit review, with `REVIEW`
placeholders that cannot pass the checker.

There are 80 exact source-site approvals: 60 wall-clock, 8 timer, 3 worker entry, 3 direct quit, 3 catch-up, 1 scale,
1 shader-global and 1 tree-pause entries. Counts per token/file ratchet too. Global/timer owners are named in the map;
source approvals explain their clock domains. CoreTests' target-typed Stopwatch and B1's bounded OS poll wait are
explicit diagnostic domains. No new general permission for gameplay wall timers was introduced.

## Verification

All commands ran in this checkout with `TMPDIR=$PWD/build/a0-tmp`; logs/captures below are retained under ignored
`build/`, not shipped artifacts. No network behavior changed, so no separate net-bots matrix was required for A0.
B1's own native matrix is linked in the contract, not claimed as a new A0 run.

| Command | Result / retained evidence |
| --- | --- |
| `make setup` after each dev merge | Pass, import/build; final B1 build 0 warnings/errors (`build/a0-setup-b1.log`). |
| `make check` | Exit 0 (`build/a0-check-final.log`): warnings-as-errors Debug/ExportRelease builds 0 warnings; 514 unit tests, 763 movement assertions, 165 real-device input assertions; scripted native/behavior suites, forced formations, four separate-process formation hashes and five 12-segment hashes; checker self-tests. |
| Final `make lint` after the proof document and command corrections | Exit 0 (`build/a0-lint-committed-proof.log`): all 66 tracked Markdown files resolve, formatting passes, both builds have 0 warnings/errors, both compiled scans and source sites pass. |
| Compiled Debug / ExportRelease check (lint commands) | 2,106 / 1,008 Propane types; each reports 78 baseline entries, 0 failures. Source check: 80 approvals, 0 failures. |
| `make test-equivalence` | Exit 0 in 90.51 s (`build/a0-equivalence-final.log`, `build/rfp0-equivalence-9/`): wire/schema/world/publication and all five Lab traces (Bank, King, spill-chain, Short Fuse, Dissolve). References untouched. |
| `python3 -B tools/architecture/tests.py` (also through `make test-infra` in check) | Pass: 51 compiled / 16 source negatives, positive values/smoke, stale baselines/sites, increased counts, missing symbols and read-only candidate. Final check fixture time 14.95 s; isolated timing 11.68 s. Output below. |
| `python3 build/a0-rejection-probes.py` | Exit 0 means both deliberate failures were observed: qualified KingRules→NetClient fails actual `make lint`; fake stale baseline fails direct checker; restored build 0 warnings. Source/baseline restored byte-for-byte. Logs `build/a0-forbidden-lint.log`, `build/a0-stale-baseline.log`, `build/a0-rejection-probes.log`. |
| `python3 build/a0-time-gate.py` | All measured commands pass, full lint 52.83 s; details below (`build/a0-gate-timings.json`, `build/a0-timing-*.log`). |
| `make build` before each rendered scene | Pass, 0 warnings/errors (`build/a0-render-build.log`, `build/a0-render-retry-build.log`, `build/a0-lobby-build.log`). |
| Rendered input, initially without `--classic-lobby` | Exit 1, `TimeoutException: solo lobby` after 87.76 s (`build/a0-input.log`, 14 captures in `build/a0-input/`). The documented example missed the classic flag used by Make's legacy input fixture; default roaming enters a world instead of waiting on the classic lobby screen. Failed evidence retained; examples corrected, no harness/runtime edit. |
| Corrected rendered input command below | Exit 0: 165 assertions / 0 failures (`build/a0-input-classic.log`), all 19 captures reviewed. |
| Default roaming lobby command below | Exit 0: 60 assertions (`build/a0-lobby.log`), all ten captures reviewed. |

Exact rendered invocations (always the PATH `godot-mono` shim, GPU, no desktop windows):

```sh
godot-mono --disable-vsync --resolution 960x540 --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=input --classic-lobby --windowed --port=24682 --capture-dir=$PWD/build/a0-input-classic
godot-mono --disable-vsync --resolution 960x540 --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=lobby --windowed --port=24682 --capture-dir=$PWD/build/a0-lobby
```

`build/a0-input-classic/`: every `single_*`, `lab_*`, `single-after-lab_*` and `match_*` capture was inspected.
Before/after-turn pairs visibly rotate the scene, jump frames show airborne characters, burst frames show retained
upward aim, and all three remapped-pause captures show the correct readable controls. The return-from-Lab sequence
also responds. Visual samples support the assertions; they do not establish continuous animation or feel.
`build/a0-lobby/01_arrival.png` through `10_return.png` show the roaming world, settings panel, guest/bot roster,
staging/countdown, new match, tied results and returned lobby with bot continuity. Wrapper teardown prints its
Wayland broken-pipe line after successful exits; it is not an engine test failure.

Earlier checks on the pre-B1 head passed (492, then 495 units after M0/D0). The final 514-unit result above is the
integrated-head proof. A0 moves no runtime state: its production source is identical to immediate integrated dev,
so no additional parent/candidate gameplay differential or hot-path benchmark is applicable. The frozen suite was
still run. `make check` reports 34/46 animation visual checks passing, 12 pending redo, while enforcing data coverage;
that is now explicit acceptance debt, not a zero-visual-failure claim. No thresholds were changed.

## Negative proof

The independent console fixture assembly uses stub Godot types; no fixture code is executed. An independently
listed expected edge must be observed, and each expected violation must fail individually with other cases
reviewed only in that scratch baseline. The positive value-type/exact-smoke fixture passes. Missing PDB, stale
metadata baseline, stale/increased source counts and candidate non-mutation are also tested.

Output from final `make check` / infrastructure fixtures (each `NEW` line is an expected rejection):

```text
PASS architecture: Godot values and exact Main → ReleaseTest entry
STALE baseline (remove it): direction Propane.World.Plan.ValueOnly Propane.Bots.Brain
Architecture: 3 types, 0 reviewed exceptions, 1 failures
NEW src/Fixture.cs [Propane.Modes.AliasReference]: direction Propane.Modes.AliasReference Propane.Net.NetClient
NEW src/Fixture.cs [Propane.Modes.InheritsBot]: direction Propane.Modes.InheritsBot Propane.Bots.Brain
NEW src/Fixture.cs [Propane.Modes.IExtendsBot]: direction Propane.Modes.IExtendsBot Propane.Bots.IObservation
NEW src/Fixture.cs [Propane.Modes.IEvent]: direction Propane.Modes.IEvent Propane.Bots.Brain
NEW src/Fixture.cs [Propane.Modes.Locals]: direction Propane.Modes.Locals Propane.Bots.Brain
NEW src/Fixture.cs [Propane.Bots.Inherited]: bot-live-owner Propane.Bots.Inherited Propane.Modes.DerivedRules
NEW src/Fixture.cs [Propane.Bots.RuleInterface]: bot-live-owner Propane.Bots.RuleInterface Propane.Modes.IModeRules
NEW src/Fixture.cs [Propane.Modes.BadBot]: direction Propane.Modes.BadBot Propane.Bots.Brain
NEW src/Fixture.cs [Propane.Modes.VarargCall]: direction Propane.Modes.VarargCall Propane.Net.Varargs
NEW src/Fixture.cs [Propane.Modes.Qualified]: direction Propane.Modes.Qualified Propane.Net.NetClient
NEW src/Fixture.cs [Propane.Modes.EngineClock]: pure-native Propane.Modes.EngineClock Godot.Engine
NEW src/Fixture.cs [Propane.Modes.ElapsedClock]: rule-clock Propane.Modes.ElapsedClock System.Diagnostics.Stopwatch.get_ElapsedTicks
NEW src/Fixture.cs [Propane.World.Plan.Platform]: pure-native Propane.World.Plan.Platform Godot.OS
NEW src/Fixture.cs [Propane.Modes.Member]: rule-clock Propane.Modes.Member Godot.Time.GetTicksMsec
NEW src/Fixture.cs [Propane.Modes.NativeBase]: pure-native Propane.Modes.NativeBase Godot.Node3D
NEW src/Fixture.cs [Propane.Modes.ResourceField]: pure-native Propane.Modes.ResourceField Godot.Resource
NEW src/Fixture.cs [Propane.Modes.Nested+Inner]: direction Propane.Modes.Nested Propane.UI.Hud
NEW src/Fixture.cs [Propane.Modes.Generated+<>c]: direction Propane.Modes.Generated Propane.Net.Match
NEW src/Fixture.cs [Propane.Modes.GenericField]: direction Propane.Modes.GenericField Propane.Bots.Brain
NEW src/Fixture.cs [Propane.Modes.GenericMethod]: direction Propane.Modes.GenericMethod Propane.Bots.Brain
NEW src/Fixture.cs [Propane.Modes.Signature]: direction Propane.Modes.Signature Propane.Bots.Brain
NEW src/Fixture.cs [Propane.Modes.Signature]: direction Propane.Modes.Signature Propane.Net.Match
NEW src/Fixture.cs [Propane.Modes.Interface]: direction Propane.Modes.Interface Propane.Bots.Brain
NEW src/Fixture.cs [Propane.Modes.IBad]: direction Propane.Modes.IBad Propane.Bots.Brain
NEW src/Fixture.cs [Propane.Modes.Constraint`1]: direction Propane.Modes.Constraint`1 Propane.Bots.Brain
NEW src/Fixture.cs [Propane.Modes.BadDelay]: rule-clock Propane.Modes.BadDelay System.Threading.Tasks.Task.Delay
NEW src/Fixture.cs [Propane.World.Plan.Clock]: rule-clock Propane.World.Plan.Clock Godot.Time.GetTicksMsec
NEW src/Fixture.cs [Propane.World.Plan.Load]: plan-resource Propane.World.Plan.Load Godot.GD.Load
NEW src/Fixture.cs [Propane.World.Plan.Scene]: pure-native Propane.World.Plan.Scene Godot.Node3D
NEW src/Fixture.cs [Propane.Player.BlockingDelay]: timer-owner Propane.Player.BlockingDelay Godot.OS.DelayMsec
NEW src/Fixture.cs [Propane.Player.BadClock]: timer-owner Propane.Player.BadClock System.Diagnostics.Stopwatch..ctor
NEW src/Fixture.cs [Propane.Player.BadClient]: simulation-boundary Propane.Player.BadClient Propane.Net.NetClient
NEW src/Fixture.cs [Propane.Player.BadCodec]: simulation-boundary Propane.Player.BadCodec Propane.Net.MessageCodec
NEW src/Fixture.cs [Propane.Player.BadHud]: simulation-boundary Propane.Player.BadHud Propane.UI.Hud
NEW src/Fixture.cs [Propane.Bots.BadMatch]: bot-live-owner Propane.Bots.BadMatch Propane.Net.Match
NEW src/Fixture.cs [Propane.Bots.BadLiveRule]: bot-live-owner Propane.Bots.BadLiveRule Propane.Modes.LiveRules
NEW src/Fixture.cs [Propane.Net.BackendLeak]: transport-backend Propane.Net.BackendLeak Godot.ENetMultiplayerPeer
NEW src/Fixture.cs [Propane.Net.ServerRun]: server-physical Propane.Net.ServerRun Godot.Camera3D
NEW src/Fixture.cs [Propane.Session.BypassQuit]: global-owner Propane.Session.BypassQuit System.Environment.Exit
NEW src/Fixture.cs [Propane.Net.FaultPacketTransport]: transport-backend Propane.Net.FaultPacketTransport Propane.Net.NetTransport
NEW src/Fixture.cs [Propane.Net.LobbyServer]: server-physical Propane.Net.LobbyServer Propane.Player.ActorRuntime
NEW src/Fixture.cs [Propane.Net.WireGameplay]: direction Propane.Net.WireGameplay Propane.Player.ActorRuntime
NEW src/Fixture.cs [Propane.Session.GlobalWrite]: global-owner Propane.Session.GlobalWrite Godot.Engine.set_TimeScale
NEW src/Fixture.cs [Propane.Session.PauseInitializer]: global-owner Propane.Session.PauseInitializer Godot.SceneTree.set_Paused
NEW src/Fixture.cs [Propane.Session.BadDev]: runtime-dev Propane.Session.BadDev Propane.Dev.Harness
NEW src/Fixture.cs [Propane.ReleaseSmoke.BadDev]: runtime-dev Propane.ReleaseSmoke.BadDev Propane.Dev.Harness
NEW src/Fixture.cs [Propane.Session.BadSmoke]: smoke-entry Propane.Session.BadSmoke Propane.ReleaseSmoke.Other
NEW src/Fixture.cs [Propane.Session.StaticResource]: worker-native Propane.Session.StaticResource Godot.Node
NEW src/Fixture.cs [Propane.Session.AsyncWorker+<>c+<<Run>b__0_0>d]: worker-native Propane.Session.AsyncWorker Godot.Node
NEW src/Fixture.cs [Propane.Session.FactoryWorker+<>c]: worker-native Propane.Session.FactoryWorker Godot.Node
NEW src/Fixture.cs [Propane.Session.Worker]: worker-native Propane.Session.Worker Godot.Node
PASS architecture: exact baseline, read-only candidate and missing-symbol rejection
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: engine-scale TimeScale= count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: engine-catchup MaxPhysicsStepsPerFrame= count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: tree-pause .Paused= count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: shader-global GlobalShaderParameterSet( count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: direct-quit Quit( count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: timer Task.Delay count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: timer CreateTimer count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: timer OS.DelayMsec count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: timer Task.Delay count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: timer Thread.Sleep count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: timer newSystem.Threading.Timer count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: wall-clock Time.GetTicksMsec count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: wall-clock Time.GetTicksMsec count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: worker-entry newSystem.Threading.Thread( count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: runtime-dev-source Propane.Dev count=1, approved=0
NEW src/Fixture.cs:1 [Propane.Session.Fixture]: runtime-dev-source Propane.Dev count=1, approved=0
PASS architecture: 51 compiled negatives, 16 source negatives, stale/increased site counts (14.95s)
```

The deliberate production scratch method was private and never called:

```cs
private static object ArchitectureNegative() => new Propane.Net.NetClient();
```

It failed make lint at the compiled dependency stage. The source and baseline were restored byte-for-byte in
finally blocks. A separate stale entry after a restored Debug build failed independently:

```text
PASS: deliberate qualified production reference fails make lint
NEW src/Modes/KingRules.cs [Propane.Modes.KingRules]: direction Propane.Modes.KingRules Propane.Net.NetClient
NEW src/Modes/KingRules.cs [Propane.Modes.KingRules]: pure-native Propane.Modes.KingRules Propane.Net.NetClient
Architecture: 2106 types, 80 reviewed exceptions, 2 failures
make: *** [Makefile:53: lint] Error 1
PASS: deliberately stale checked-in baseline entry fails
STALE baseline (remove it): direction Propane.Modes.KingRules Propane.Net.NonexistentA0
Architecture: 2106 types, 78 reviewed exceptions, 1 failures

Probe time 87.90s; production source and baseline restored byte-for-byte
```

## Timing and CI

Timing was measured after integration under shared machine load, not in a reserved performance window. These are
build-tool costs, not gameplay performance claims. Three separate samples per scan/start/source component:

| Added lint operation | Seconds, samples | Median |
| --- | --- | ---: |
| Tool start, incremental tool build and Debug scan | 3.745 / 6.681 / 5.554 | 5.554 |
| ExportRelease scan | 1.092 / 1.137 / 3.012 | 1.137 |
| Source sites | 1.505 / 1.071 / 0.604 | 1.071 |

The three actual added lint commands sum to **7.76 s of component medians**; full lint was 52.83 s. A direct Debug
scan alone measured 3.700 / 4.673 / 2.414 s (median 3.700), and a forced nonincremental checker build was 1.43 s;
those are explanatory components, not additional lint commands. The architecture fixture suite adds **11.68 s**
to `make test-infra` locally (14.95 s inside the loaded final check). No new CI jobs/partitions are added: the existing
Ubuntu lint partition executes the same three positive commands and the infrastructure partition executes the fixture
suite. Actual new Ubuntu CI duration and first-run SDK/package cache cost are unmeasured; no push was authorized.
These local costs are the estimate, not a CI timing claim.

## Findings, retained limits and compatibility

The review predates D1: Main now enters ReleaseSmoke.ReleaseTest, and only ExportRelease excludes ordinary Dev.
The namespace graph's claimed World.Plan leaf missed furnishing's static Catalog metadata; SegmentSettings.From
also mixes plan data with native Tuning conversion. These are explicit C1 exceptions, not runtime changes here.
world-services now records both engine physics policies, production Game's CameraRig convenience and the single
independently advancing stream limitation. The legacy menu examples now include the required classic-lobby flag;
the real rendered input run exposed that additional command drift.

P4's historical shooter-ID defect was already fixed by 39eea7e/1019775; B1 also records native vent-contact proof.
B1's final-gates-pending evidence status predates its integration's recorded deep pass. Neither that pass nor this
fast gate closes the retained P5/FU2 residual. The ledger also records performance/capacity/platform obligations,
the older elapsed-time target, 12 pending animation visual checks and the unexplained loaded-unit native crash;
it distinguishes corrected/sharded historical fixture failures from current debt. No historical report was rewritten.

The checker is a dependency policy, not proof of semantic purity or all execution paths. Reflection/dynamic dispatch,
serialized attribute type names, erased constants/resource paths and unresolved generic/interface/delegate worker
calls still require review. Approved type edges do not approve every future call. Source token counts do not prove
unchanged semantic use. Linux source gates do not prove artifact contents or Windows/macOS behavior; R1 owns the
produced-artifact proof. Actual new Ubuntu CI wall time was not measured (no push).

Only requested build/tooling/docs files were edited; shared Makefile/project/lint_tests changes are gate wiring.
The integrated production source is byte-identical to dev f10e19d. No messages, plans, tanks, tuners, rules or
Protocol.Version changed in A0. No push, release, system change, recording or large baseline copy was made.
