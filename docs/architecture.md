# Architecture contract

This is the map and change contract for Propane's single Godot .NET project. **Current** describes the checked-in
owners; **target** describes the approved direction, not an implemented capability. Detailed contracts linked below
remain authoritative for their subjects. [The developer guide](development.md) supplies commands and harnesses.

## Purpose and design authority

The owner controls rules, feel and direction through [design/](../design/), especially [VISION.md](../design/VISION.md).
Engineering refactors preserve behavior: wire bytes, tuner values/classification/order, worlds, random draws,
traversal/tie order, synchronous callbacks, clock domains and native processing order. A semantic change is separate
work with its own release classification and owner decision. Every gameplay/effect value remains a `Tuning` tuner;
this contract does not authorize converting existing literals or changing their classification.

Keep one Godot project, ordinary owned classes and Godot's processing model. No ECS, universal entity framework,
dependency-injection framework, generic event bus, replacement scheduler or parallel server physics simulation is
required. Small foundations and cohesive native simulation are intentional; acyclicity alone does not justify an
interface around every actor/effect collaboration.

## System map

The target arrows below are **direct references**, not transitive permission. Godot/.NET libraries are subject to
additional purity, timing and worker rules. Godot value types (`Vector3`, `Transform3D`, `Color`) are allowed in plain
contracts/plans; purity excludes native objects/resources, loading and implicit time.

| Logical module | Responsibility; allowed direct Propane dependencies |
| --- | --- |
| Foundation | Small identity, appearance, configuration, time and observation contracts; no other module. |
| Plans | Ordered generation and plan/catalog data; Foundation. |
| Rules | Accepted facts, state machines, health, commands and plain mode observations; Foundation. |
| Simulation | World lifetime, native builder, physical actors/tanks, assets and physical effects; Foundation, Plans. |
| Navigation | Search, caches, budgets and native query adapter; Foundation, Simulation. |
| Bots | Tactical owners producing intent from observations; Foundation, Simulation, Navigation, Rules. No live rule objects. |
| Wire | Transport contracts/adapters, lanes, bounded codecs; Foundation, Plans, Rules. Helpers must not depend on ENet implementation. |
| Server | Connection/lobby admission and run authority, ledger and rule driving; Foundation, Plans, Rules, Wire. No physical actors/cameras/HUD. |
| Client | Replication, prediction, authority transactions and mode wire adaptation; Foundation, Plans, Rules, Simulation, Wire. No concrete HUD or front end. |
| Presentation | Cameras, HUD, mode visuals, device presentation and view binding; Foundation, Plans, Rules, Simulation. |
| Composition | Main/Game/Match/Lab, front end and compiled factories; all preceding modules. Connect owners without absorbing their mutable state. |
| Smoke | Explicit exported smoke closure; runtime modules. Only `Main → ReleaseSmoke.ReleaseTest` is an approved runtime entry. |
| Dev | Tests, probes, scenarios and independent oracles; runtime and Smoke. Runtime and Smoke cannot reach Dev. |

The executable map is [modules.json](../tools/architecture/modules.json): namespace defaults, explicit type overrides
for mixed namespaces, and allowed directions. Nested/compiler-generated types inherit their top-level owner's
module. Dependencies remain visible in their actual compiled nested type in diagnostics; ratchet keys use the
stable top-level owner so compiler-generated numbering does not cause churn.

| Current source location | Logical mapping and qualifications |
| --- | --- |
| `src/Main.cs`, `Game*`, `Session/`, `Lab/` | Composition (physical session assembly, local adapters and runners). |
| `Core/` | Foundation by default; world services, spawn, assets and `Tuning` are Simulation; launch/settings are Composition; device setup is Presentation. |
| `World/Plan/` | Plans; `SegmentPlans.cs` also contains plan types. `SuburbPlans` converts live tuning in Composition; native catalog/builder/materials remain Simulation. |
| `Modes/` | Rules by default; `ModeDefinition`/`ModeBundle`/`ModeCatalog`/registry/supply are Composition; HUD input/presenters are Presentation; HUD values/`IModeView` remain plain rules observations. |
| `Player/`, `Tank/`, `Fx/` | Simulation by default; input values and resolved state values are Foundation; cameras/views/device input and mode geometry are Presentation; `ReplicatedIntent` is Client. Profiling remains colocated pending C1. |
| `Navigation/`, `Bots/` | Navigation and Bots. |
| `Net/` | Wire by default; `LobbyServer`/`ServerRun`, ledger, roster, contact admission, ServerMode/ServerModes inputs, plain ModeWireTuners and mode wires/runner are Server; body/actor snapshot helpers, prediction, authority transactions and inbox are Client; Match/backend factory/update/diagnostics are Composition; NetClient is Client. Plain identity/appearance and selected decoded values are Foundation; `AmmoPickup` is Simulation. |
| `UI/` | Presentation by default; front-end screens, their transactions and lobby/stage composition are Composition. |
| `ReleaseSmoke/`, `Dev/` | Smoke and Dev respectively; no game dependency on the checker project under `tools/architecture/`. |

Current cycles remain. [baseline.tsv](../tools/architecture/baseline.tsv) records **each existing prohibited type edge**
with a reason and removing package; an absent entry or a stale entry fails lint. This is not permission for another
source type to acquire that dependency. Do not broaden the map or silently regenerate the baseline to pass a gate.

| Temporary boundary | Owner package and exit condition |
| --- | --- |
| Match owns transactions, world/actor replication, bots, mode policy and views | B2 extracts transactions; B3b extracts mode policy; C2 finishes independent replication/bot/view owners. Preserve synchronous ordering. |
| Physical actor holds replication attachments and view/device conveniences | C1 moves identity/body contracts; C2 moves client state and narrow view binding beside actor lifetime. |
| Wire/server values depend on concrete server enums, client identity helpers, physical snapshot/gather types; plans reach mixed catalog/settings factories | C1 separates plain contracts, formulas and catalog snapshot construction without changing bytes, RNG or IDs. |
| Lobby/run native tuning and codec catalog resolution | B3a supplies presentation-free ServerMode/ServerModes inputs. A1 removes LobbyServer template/snapshot sampling; C1 removes ServerRun native tuning/melee coupling; N1 injects owner-scoped codec schema/eligibility resolution. No baseline entry remains assigned to B3a. |
| Fault transport calls ENet lane helper; server diagnostics/factory cross composition | N1 supplies transport-independent lanes and injected delivery/diagnostic boundaries. |
| Settings and launch access are implicit | A1 provides explicit application ownership and scoped test stores/overrides. |
| Client actor attachment retirement (L1) | Session-owned `ActorRegistration` detaches subscriptions and actor caches before native free; remaining attachments keep the original unsubscribe/stop/view/actor order. |
| Lab bookkeeping and prototype live-rule reads | B4b shares local difficulty, tank facts and offline placement; semantic attribution and rule submission stay runner-owned. B3b2 supplies plain observations with explicit read-time clock and allocation-free bot reads. |
| Streaming incidental history (W1 implemented) | Ordinary arena timing lists stay empty; a feed watermark compacts the retired prefix, retaining only out-of-order forgotten indices. Navigation eviction and ledger-history pruning need separate retention contracts. |
| Global stream reveal; implicit character/weapon binding assumptions | W2 before independent streams; V1 before a second asset family. Existing single-stream/binding behavior stays intact. |
| Cross-platform artifact runtime acceptance remains partly manual | R1 gates setup imports, inventories all packaged exports and runs the Linux artifact before publishing; Windows/macOS retain the [runtime checklist](runbooks/release-artifacts.md). |

These package labels name engineering work, not authority to change design. The exact baseline reasons and source
approvals are reviewable alongside the contract; [checker operation](../tools/architecture/README.md) explains its
coverage and limits.

## Composition roots

[Main](../src/Main.cs) selects `--release-test`, `--server`, or ordinary application startup. Ordinary startup owns
`NetClient`, menus, update checking, a backdrop world and the current `Game`, `Match` or `LabSession`. A hosted
`LobbyServer` uses the same ENet loopback path as a dedicated server; hosting does not bypass authority/transport.
Main's generation/connection/run checks guard asynchronous match construction before attachment.

**Current B1:** [LobbyServer](../src/Net/LobbyServer.cs) retains connections, lobbies, staging, transport, diagnostics
and routing. A plain [ServerRun](../src/Net/ServerRun.cs) owns each match or roaming run's actors/admission, ledger,
mode, clocks, readiness, pickups and tuning. Narrow synchronous callbacks provide delivery/loading/lobby transitions;
it owns no transport, lobby dictionary or processing node. Finite-match membership/profiles and creator debug
capability freeze at start; roaming membership follows roster observations. The facade detaches routing before
idempotent disposal releases run-owned Tuning, including construction failure, empty lobby, results, staging
replacement, identity exhaustion and server shutdown ([server contract](multiplayer.md),
[B1 evidence](evidence/server-run-verification.md)).

[Game](../src/Game.cs) assembles offline play and replaces suburb content. [Match](../src/Net/Match.Assembly.cs)
assembles both roaming lobby worlds and network matches. [LabSession](../src/Lab/LabSession.cs) selects local sources
and adapters; [LabMatch](../src/Lab/LabMatch.cs) can retain logical rules across replacement sessions. Most scene-tree
composition is C#, not `.tscn` structure. `ShutdownGuard` is the autoload exit owner ([shutdown](shutdown.md)).

**Target Match:** retain its Godot callbacks, run identity and lifetime coordination. Ordinary components separately
own authority queues/reservations/epochs/causes/gather bookkeeping (B2), physical world replica, per-actor replication,
mode client policy, owned bots and view binding (B3b/C2). Each extraction owns its own mutable state, inputs,
synchronous operations and retirement; a partial file or a new container for all Match fields is not an extraction.
Action sequences and drop predictions retire with a client actor attachment, rather than becoming session-global.
`ReplicatedIntent` belongs on the client side of the physical actor's input-source seam.

**Current M0:** Match refreshes its selected owned character synchronously on `SessionContext.ActorsChanged` and
lobby profile Kind changes. It prefers the first owned Human, otherwise the first owned actor, in session order;
retirement clears the cache before freeing actors. Empty ownership skips local presentation; public `Match.Player`
still requires an actor. This cache neither retargets an existing local view nor implements multi-view selection
([participant contract](multiplayer.md#participants-a6-protocol-2), [M0 evidence](evidence/match-player-cache.md)).

[ModeRegistry](../src/Modes/ModeRegistry.cs) compiles the network integrations into one validated
[ModeCatalog](../src/Modes/ModeCatalog.cs). Bundles pair existing definitions with schemas and explicit server wire
factories; codecs remain in Wire. MessageCodec passes composition's schema resolver into the envelope reader, and
composition constructs LobbyServer through `ModeRegistry.CreateServer`, selecting immutable ServerModes/ServerMode
inputs (rules/schema/wire factories, metadata, tuner names and ordered eligibility) with no HUD/bot factories.
ServerRun constructs its runner from those inputs. Composition supplies supply/tuner samplers at the original
synchronous read points; rule initialization still precedes wire construction.

## Scopes and ownership

| Lifetime | Current owner; retirement boundary |
| --- | --- |
| Application | Main, application settings/template, launch snapshot, caches, optional hosted server; process exit. A1 makes mutable services explicit. |
| Connection/lobby | NetClient and transport/inbox; disconnect/reconnect. Server connections/lobbies can span successive runs. |
| Logical run/match | ServerRun and LabMatch rules; end/replacement, not necessarily a world lifetime. LobbyServer retains connections/lobbies across runs. |
| Physical session | SessionContext: world attachment, actor registry, views, cancellation and registrations; synchronous session disposal. |
| World | WorldContext: World3D/offscreen viewport, roots, tuning copy, RNG, clock, effects, registries and presentation selection; invalidation then disposal. |
| Content | Suburb/segment root plus originating effects/registrations; content retirement inside a surviving world. |
| Actor | ActorRuntime, character and client `ActorRegistration`; roster removal or session retirement. Subscriptions and actor caches detach before native free. |
| View | LocalView camera/HUD/device attachment; detach/reassign independently of actor survival. |

There is no universal application → session → match → world nesting: Game keeps its world while replacing suburbs;
Dissolve Run keeps a world while retiring segments; Short Fuse keeps LabMatch rules while replacing sessions/worlds.
A view can leave before its actor, an actor before its world, and content before either. Registration ownership must
match the shorter lifetime. See [world-services](world-services.md) and [Lab](lab.md).

## Assembly and retirement

[SessionFactory](../src/Session/SessionFactory.cs) is one synchronous assembly transaction: preload/configure inputs,
construct detached session/world, bind and configure mode owners, attach world, build source, create actors, create
views, register replication, attach matching inbox, acknowledge ready. Loading presentation precedes inbox replay;
ready must not overwrite replayed transitions. Initialization readiness is checked explicitly because managed
`_Ready` exceptions do not propagate through Godot `AddChild` as ordinary constructor exceptions.

Partial construction rolls back through [SessionContext.Dispose](../src/Session/SessionContext.cs), retaining both
construction and retirement failures. Retirement invalidates world work first, cancels the session, detaches inbox,
unsubscribes/stops adapters, retires views and actors, then frees world content/resources. Cleanup continues through
callback failures and reports them afterwards. Call disposal before freeing the controller. World-only owners use
[WorldContext.Dispose](../src/Core/WorldContext.cs); content uses originating spawn/effect ownership and
`RetireContent`, not a process-wide current-world lookup.

On the server, `LobbyServer.Retire(ref routing)` clears the route before `ServerRun.Dispose` releases Tuning.
Terminal MatchEnd delivery precedes a later Rejected message if returning to the lobby exhausts participant identity.

Callbacks and sends can synchronously retire the run. Do not defer transactions into a batch or continue using
retired state. Presentation bindings restore the newest surviving world in either disposal order. Node free alone
is not disposal of managed subscriptions, timers, cancellation or owned native resources.

Each Match actor has one idempotent `ActorRegistration`, indexed by its session. Early removal removes its
unsubscription entry (including the closure), detaches owned action handlers, retires its owned bot, removes
BotServices collider entries, surviving bot peer/target/sight references and actor action/melee caches, then frees
the character. M0 selection still refreshes at the original roster notification, and views close before character
free. In-flight delegate snapshots suppress detached action handlers. King rival/watch references retire here too; physical callback continuations stop when the emitter retired.
King rival/watch references retire here too; physical callback continuations stop when the emitter retired.
Bot invalid-target flags retain the old
retarget/selection decisions without keeping a freed character alive. No decision clocks or random draws move.
Whole-session unsubscription retains each actor's original position among other subscriptions, bot stopping stays
before replication stopping, and views/actors still free in reverse order. Removal during stop and repeated removal
are safe; attachment failures do not skip native retirement. Lobby profiles/scores retire too; finite-match score/profile/standing history remains for results and late drops.
Authority
requests keyed by numeric identities and nonrecycling identity tombstones remain run-owned.

## Clock and callback domains

The project configures Jolt at 120 physics ticks/s and an ordinary 12 catch-up steps/frame
([project.godot](../project.godot)). These are existing settings, not a gate threshold to retune here.

| Domain | Owner and constraints |
| --- | --- |
| Simulation seconds | WorldClock advanced by WorldDriver from the SceneTree physics signal before node physics; freezes with that world. Fuses/shields use it. |
| Physics delta | Actor movement, reload, gathering, bot thinking and correction; preserve callback and processing-priority order. Local rule runners use priority `-10`. |
| Render delta | Presentation and some logical aim/recoil settling; LogicalAim affects shot transforms even without a camera. |
| Unscaled monotonic network time | IMonotonicClock/MonotonicClock, authority deadlines, snapshots, publication and transport faults. Never compare to simulation seconds. |
| Server phase time | Injected server clock and phase machine, advanced in `_Process`; no Jolt world on the dedicated server. |
| Real-time process leases | WorldTimeScale hitstop and TankSimulation dense-chain catch-up. Expire independently of slowed simulation. |
| Wall calendar/diagnostics | Daily seed chosen at run creation, save-status display, profiling; never substitute for gameplay timers. |

[WorldClock](world-clock.md) snapshots eligible timers at entry in insertion order, keeps cancellation observable,
removes/cancels immediately before invoking each callback, defers newly scheduled zero-delay timers to another
advance, permits nested advances without duplicate delivery, and preserves unvisited timers on exception. Disposal
suppresses callbacks. A heap or batch-removal rewrite must prove those semantics, not merely firing times.

[NetClient](../src/Net/NetClient.cs) polls in render and physics callbacks. Current
[Match._PhysicsProcess](../src/Net/Match.cs) applies remote actors → body correction → lobby rifle refill → bot
thinking → owned actor publication → body publication → pickups. World/tank/actor native callbacks produce immediate
local facts and authority requests; Match's render callback handles reveal/HUD/notices/deferred transitions.
[LobbyServer](../src/Net/LobbyServer.cs) polls/decode/admission/dispatch first, then delayed disconnects/timeouts and
ServerRun phases/stock/pickups through `Tick`. Preserve these domains and order through extraction.

Native skeleton/modifier order, ragdoll launch/recovery, logical muzzle and render-domain aim remain cohesive physical
behavior ([motion](motion.md), [locomotion](locomotion.md), [recoil](recoil.md)). A camera-free bot still needs native
character/animation machinery.

## Thread boundary

Scene-tree operations, resource loading, physics queries and native construction remain on Godot's main thread.
[LocalSegmentFeed](../src/Session/SegmentWorldSource.cs) snapshots the catalog before `Task.Run`; workers receive
plain settings/catalog/seed and return plans. Worker completion order must not determine generated content.
[FrameEncoder](../src/ReleaseSmoke/CaptureDirector.cs) consumes managed bytes through a bounded queue; main-thread GPU
readbacks drain before native teardown. These are approved worker boundaries, not permission to send scene objects
to another worker.

[UpdateCheck](../src/Net/UpdateCheck.cs) explicitly resumes on the Godot context after awaiting HTTP. **Current
exception, A1:** it lacks cancellation/liveness suppression; completion after retirement must be canceled or ignored.
Main already guards asynchronous world transitions by generation/run identity. New async owners state cancellation
and late-completion behavior. The checker flags new worker entry sites and native references in the direct-call
closure of known worker delegates; dynamic/interface/reflection dispatch still requires review. The one intentional
Dev thread-guard probe is named in the policy and site inventory, not a general worker exemption.

## Identity and authority

The model is **server-authoritative lifecycle, ownership and rules; peer-authoritative physical simulation with local
prediction and replicated correction**. It is neither lockstep/rollback nor server resimulation. Shooters report local
contacts; actor owners simulate movement/throws/spills; current body owners simulate their bodies. The server checks
peer/actor/life/action/run/world/phase identity, deadlines, canonical existence, authority epochs and atomic claims,
then distributes canonical outcomes. See [multiplayer](multiplayer.md) for exact admission and message contracts.

Actor IDs differ from peer IDs: one peer can own human and bot actors. Local view IDs stay process-local. Allocation
slots/retired actor IDs are not recycled; exhaustion fails before reuse. [Participants](../src/Net/Participants.cs)
and [BodySync](../src/Net/BodySync.cs) currently house these contracts/formulas; C1 relocates them without changing
ranges. Map tanks start at `0x10000000`, drops at `0x20000000`, cars at `0x30000000`; preserve allocation-slot and
counter bit arithmetic, epochs, actor lives and world revisions. An ENet backend ID is not a protocol peer ID
([ProtocolPeerMap](../src/Net/ProtocolPeerMap.cs)).

`IPacketTransport` distinguishes borrowed receive memory, accepted sends from delivery, per-lane ordering, handle
invalidation and deferred disconnect notification. Six traffic classes include three independently ordered rotating
reliable body checkpoint lanes. Keep those semantics when adding a backend ([transport contract](../src/Net/IPacketTransport.cs)).

## Replication transactions

Current authority ownership remains primarily [Match](../src/Net/Match.cs). The resolution order is binding:
finish pending request/release reservations → update dependent epochs → reconcile/reconstruct/adopt canonical bodies
→ flush newly eligible requests → notify `AuthorityResolved` → remove gather bookkeeping. Preserve causal
root-before-child sending, proposal capture time, explicit dependent refusals and winning-vent adoption. B2 extracts
this transaction with explicit synchronous physical collaborators; it does not reorder it.

Gather leases do not immediately predict ownership: grants revalidate geometry/disturbance before pushing. Bank
reservation/score confirmation and dropped-body creation confirmation are different boundaries. Actor-owned action
sequences/drop predictions remain actor-lived. [MatchInbox](../src/Net/MatchInbox.cs) buffers bounded ordered traffic
until actor/replication assembly is ready; run and receiver identity protect replacement. This is loading support,
not reconnect/resume or arbitrary mid-match join.

[SnapshotBuffer](../src/Net/SnapshotBuffer.cs) owns bounded actor history and offset/interpolation/extrapolation;
[BodySync](../src/Net/BodySync.cs) separates sample age from receipt freshness, preserves rest anchors, suppresses
fresh remote vent thrust/spin and applies geometry/contact-aware correction.
[BodyPublisher](../src/Net/BodyPublisher.cs) preserves grouping, encounter/batch order, sequence advancement and
rotating checkpoints. Later batches sample after earlier synchronous sends; pre-capturing all batches changes
behavior. B2/C2 extraction must retain the publication and retirement boundaries pinned by
[release references](release-reference.md).

## Modes and capabilities

[Rules](../src/Modes/ModeRules.cs) consume accepted facts and explicit clocks and return commands. Integrators supply
physical/authority consequences. Current eligibility is explicit: Tank Bank and King are lobby choices; Ring Out,
Short Fuse and Dissolve Run remain Lab prototypes. Sandbox and roaming Lobby are separate noncompetitive paths.
A type or registration does not graduate a mode ([modes](modes.md), [Lab](lab.md)).

**Current B3a:** a composition-owned `ModeBundle` pairs the existing `ModeDefinition` (metadata, rules/HUD/bot
factories, supply, world selection and tuners) with its schema and server wire factory. Catalog construction validates
required members and matching schema identity before publication. Sandbox explicitly selects `SilentModeWire`; an
unknown integration or missing factory fails, with no silent fallback. `ModeSchemas` supplies explicit codec schemas,
not a registration table; `ModeWires` and all three unscoped registration seams are retired. No client adapter factory
exists yet: Match's existing Bank/King/Lobby policy remains B3b, rather than a placeholder capability in the bundle.

The process-lifetime shipped catalog and its collection containers are immutable. Main-thread synthetic tests use
`ModeRegistry.RegisterScoped`: validated overlays replace an entry in place or append a new identity; strictly nested,
idempotent disposal restores the exact previous catalog, including identities/order, during exception unwinding.
The scope owns only its overlay, with no rules, wires, native resources or timers. `ModeRegistry.CreateServer`
captures each server's default/roaming modes and ordered lobby choices into immutable `ServerMode`/`ServerModes`
inputs owned by Server. These contain only metadata, tuner names, schema and rules/wire factories. Supply callbacks
retain supply factories, never a definition/HUD/bot factory, and yield plain SuburbSettings; rule tuner callbacks
yield ModeTuners at the same read points. These bindings borrow each run's tuning only until run retirement; the
existing ServerRun still owns/disposes that resource. IModeHost supplies plain live ModeWireTuners values, so KingWire
and LobbyWire do not borrow a Tuning resource. Network registration remains
Sandbox, Bank, King, Lobby; lobby choices remain Bank, King; prototype order remains Ring Out, King, Short Fuse,
Dissolve Run. Test overlays never change lobby/prototype eligibility. No IDs, schemas, bytes or exposure change.
[Original B3a evidence](evidence/mode-catalog-verification.md) records catalog completeness/restoration.
The server-input follow-up completes that boundary; its parent inventory/packet comparison and
remaining obligations are recorded in the [server-input evidence](evidence/mode-catalog-server-inputs.md).
MessageCodec's schema/eligibility lookup remains assigned to N1: inject each owner's resolver while separating decode/admission failure from dispatch, preserving
malformed-packet rejection order. LobbyServer's saved-template and snapshot conversion is A1 application-state work;
ServerRun's native tuning/snapshot/melee inputs are C1 contract work. No edge is deferred to B3a.

**Current B4b:** `LocalBank` and `PrototypeRunner` each own a `LabTankFacts` instance for last direct contacts
and drop membership. Existing `TankTracking` still owns subscriptions and reverse-order retirement; its forgotten
callback removes the tank's facts. Runner stop clears remaining facts after subscriptions drain. This owner has no
callbacks, clocks or rules and retires with that runner, including every replacement Short Fuse session.
`LabBotDifficulty` and `LabSpill` are stateless Composition helpers: live attachment-slot difficulty, identical hit
geometry and the already-shared offline spill algorithm. Online placement is separate. Rule submission, scores,
tickets, grace, Short Fuse's frame-local blast cause and Dissolve's chain IDs remain in their runners, as do Bank's
spill-time and prototypes' per-tick tuner sampling. B4b changes no bot/HUD/view/observation contract; the observation boundary below is supplied by B3b2.
No dependency baseline entry is assigned to B4b.
[B4b evidence](evidence/lab-mechanics-verification.md) records exact traces, retirement, captures and runner costs.

**Current B3b2:** Prototype HUDs and the Lab overlay consume copied `RingOutState`, `ShortFuseState` and
`DissolveRunState` observations; `ModeHudInput` and `IBotWorld` expose no live rules. Snapshot producers receive an
explicit `ModeClock`, including health regeneration/shields and elapsed round time, and retain no rules or callbacks.
Snapshots own copied actor/event arrays and may outlive their runner; Ring Out borrows its immutable slab plan.
No cached mutable observation owner or processing node is added. Hot bot observations return scalar/struct values
synchronously at the original reads, without constructing presentation snapshots. `RoundAlive` returns a life fact;
`ShortFuseHealth` takes the explicit mode clock. King retains its existing value API, now mapped to Rules, with local
conversion supplied by the runner and received-state conversion on network peers. Arena mutation, accepted-fact
submission, command application and runner retirement keep their original ownership and order.

**Target B3b1:** Bank policy is composed into King and Lobby has its own client adapter. ServerRun's `NextRound { RebuildWorld: true }` fallback ends the match: general round-world replacement,
health/lives and online segment streaming still require end-to-end work.

## Simulation and presentation

Keep movement, logical aim, weapon pose, animation, ragdoll and recovery together where native order requires it.
[LocalView](../src/Player/LocalView.cs) attaches camera/HUD/device input independently of actor lifetime. A physical
actor must work without a camera/HUD. Current character view conveniences and actor-held client state are temporary
C2 exceptions; production Game also uses its `CameraRig` accessor. D0 removed the unused instance
`Rifle.AlignBarrel` wrapper; the static transform helper remains. Do not move render-domain LogicalAim into a
camera-only owner or split RifleRig/recovery merely because of size.

The offscreen simulation viewport does not receive application device events. View-owned device endpoints stay in
the application viewport; explicit assignments observe handled releases, while Primary receives unhandled input.
View close/reassignment releases endpoint and intent state. Changes to viewports/focus/menus/input must exercise
`menu_test --scenario=input --classic-lobby` through `Input.ParseInputEvent`, not only scripted intent/action presses.

[GameEvents](../src/Core/GameEvents.cs) is typed world-local feedback, not a generic bus. Physical blast receiver and
causal authority callbacks stay synchronous. Effects retain their originating content via
[Spawn](../src/Core/Spawn.cs)/WorldContext, so retirement cannot leave callbacks or effects targeting reused geometry.
Presentation consumes deduplicated predicted/confirmed facts and mode projections; richer effects/audio must preserve
confirmation boundaries ([world-services](world-services.md)).

## Worlds, RNG and assets

[Generation](suburb-generation.md) separates tuning-to-settings conversion, seeded ordered plans and main-thread
[Suburb](../src/World/Suburb.cs) native building. The server sends complete plans/shared tuning; clients do not
regenerate a supposedly identical suburb from a seed. Preserve generator stage order, master/per-lot random streams,
retry order, arithmetic and traversal. Segment seeds derive from run seed/index independently of worker completion.
World gameplay RNG is separate from layout RNG and cosmetic debris randomness. Online spill causality, offline
shooter attribution and Dissolve chain identity are not interchangeable integers.

**Current C1 exceptions:** plan furnishing reads `Catalog`'s static model metadata; `PlanCatalogSnapshot` also captures
native catalog data at construction, and `SegmentSettings.From` converts live Tuning. Pure worker generation must
not start loading resources through these mixed types. C1 separates plain metadata and main-thread conversion;
world-plan and construction hashes must remain exact.

`GameAssets`, `FxLibrary`, `Catalog` and `WorldMaterials` cache borrowed meshes/models/materials on the main
thread. `MeshKit` owns temporary per-build geometry scratch. Shared resources remain immutable by convention; per-instance mutable effects/material state stays owned.
Do not duplicate immutable assets per world to eliminate statics. Character bones/sockets/clips and rifle calibration
are implicit asset bindings today; V1 validates them once before another asset family is introduced
([animation sources](animation-sources.md), [motion](motion.md)).

**Current W1:** `DissolveArena.CollectRawTimings` is false for ordinary play. Diagnostic callers opt in before
streaming and own the recording duration; `BuildSlices` and `RetireMilliseconds` retain every measured sample in
order until arena collection. Existing Dissolve harness reports explicitly request this evidence. Ordinary play
retains only the last whole-step duration, with empty timing lists and zero backing capacity.

`LocalSegmentFeed` owns a nonnegative exclusive forgotten-prefix watermark plus a set of forgotten indices beyond
gaps for its lifetime. Ordered retirement uses only the watermark, including the `int.MaxValue` boundary. Sparse
storage follows the peak number of out-of-order forgets not yet absorbed into the prefix; arbitrary callers with
unclosed gaps have no constant bound. Negative indices remain unrequestable. Forgotten plans are removed at once;
in-flight workers finish without publishing into the feed. Dispose clears plan handles; tombstones preserve the
existing request contract until feed collection. Active plans follow outstanding requested/unforgotten indices.
The arena requests one plan ahead; the source owns one staged build and the live/retiring content windows. Their
bounds follow progression and build/retirement throughput, not elapsed run length; no eviction policy changed.
See [world services](world-services.md#streamed-content-history), [the Lab](lab.md) and
[W1 verification](evidence/streaming-history.md).

## Globals and budgets

| Process state | Current owner; target/constraint |
| --- | --- |
| Launch arguments | LaunchArgs.Current lazy snapshot, internal test replacement; A1 preserves immutable application input and scoped test override. |
| Player preferences | PlayerSettings static mutable persistence; A1 application store and isolated test stores, same format/save timing. |
| Saved tuning | Tuning.SavedTemplate application preference; each WorldContext copies it. UI edits explicit world target. A1 formalizes borrowing. |
| Asset/catalog caches | Main-thread process caches above; borrowed immutable resources, no per-world mutation. |
| Instance materials/effects | Instance/world/content owners; retire at their real lifetime. |
| Engine.TimeScale | WorldTimeScale exclusive hitstop lease: one live unpaused offline world; a second world, pause or retirement releases it. |
| Engine.MaxPhysicsStepsPerFrame | TankSimulation dense-chain lease: smallest overlapping offline request, prior value restored, any registered live network tank simulation vetoes. Registration timing matters. A1 consolidates discoverability of these two distinct policies. |
| SceneTree pause / quit | No production tree-wide pause; WorldContext pauses its own root/physics space. ShutdownGuard owns orderly direct quit; isolated legacy/witness Dev exceptions are inventoried. |
| World shader state | WorldReveal instance parameters occupy slots 12–15; actor parameters use lower slots. DissolveArena still writes global `reveal_window`: one independently advancing streamed world per process until W2. Multiple cameras of that same stream are supported by the model. |
| Mode catalog | Composition-owned immutable shipped ModeCatalog; validated ModeRegistry overlays restore exact prior entries/order on scoped disposal (B3a). |
| Device/glyph preference | Application-level InputSetup/UiStyle today; per-view routing before multiple local users become a shipped feature. |
| Diagnostics | Explicit launch/profiling switches (EffectProfile, ActorProfile, capacity/capture instrumentation); never hidden gameplay state. C1 relocates misplaced profiling utilities. |

[Protocol](../src/Net/Protocol.cs) distinguishes the tested 16-actor ceiling from wire safety bounds of 64 actors/peers;
wire bounds are not a capacity promise. Packet size is bounded to 1 MiB and body batches to 20. Inbox, snapshot,
causal-wait and worker queues have their own bounds; preserve overflow/refusal behavior rather than dropping checks.
Do not prune ledger tombstones/causal history or navigation caches without an explicit retention/correctness contract.
W1 bounds ordinary incidental stream diagnostics/forgotten prefixes, not those histories. Exact bounds and conservation rules
remain in [multiplayer](multiplayer.md), [bot-navigation](bot-navigation.md) and the owning types.

[Source approvals](../tools/architecture/source-sites.tsv) name today's global writes, timers, wall-clock reads and
worker entries with their owners/domains. New sites, increased counts and stale approvals fail lint. An approved
diagnostic clock is not an approved gameplay timer. Do not use Task.Delay, SceneTree timers or wall time for new
simulation timers outside a reviewed clock domain.

## Verification and evidence

[Code quality](code-quality.md) owns the gate. `make lint` checks links/formatting/UID pairs, rebuilds Debug and
ExportRelease with warnings as errors, then checks compiled dependencies and source sites. `make test` covers core,
behavior (including real device delivery), bounded generator sweeps and separate-process world/segment hashes.
`make test-infra` tests the checkers, including compiled architecture negatives; `make check` runs these in order.
Deep sweeps stay in `make test-full`; real-time bot jobs do not share its heavy offline jobs.

Frozen [release references](release-reference.md) protect bytes, tuner schema/order, plans/world hashes, publication
and selected Lab traces. Never regenerate them to pass a refactor. Immediate-parent differential tests cover moved
responsibilities beyond the frozen inventory. Native scenes prove physics/animation/lifetime/input; reviewed captures
prove sampled visual behavior; complete clean/faulted network audits prove the measured workload's conservation and
convergence. None alone proves feel, all platform behavior or unlimited capacity.

Hot-path acceptance requires at least three alternating parent/candidate pairs, identical instrumentation/workload,
allocations and the whole relevant callback/tick with p95/p99 and workload counts. A faster helper is insufficient.
Native performance needs a reserved CPU/GPU window. Preserve failed samples and distinguish complete, incomplete and
inherited evidence; a later passing rerun does not erase a failed gate.

### Unfinished acceptance ledger

These are open acceptance obligations, not claims that an inherited refactor caused the failure. Historical reports
remain unchanged. Close an entry with new linked evidence showing its condition, never by relabeling old samples.

| Acceptance | Missing/failed proof; evidence | Owner/closure condition |
| --- | --- | --- |
| P2 tracking performance | [Tracking](evidence/lab-tracking-verification.md): three uncontended rendered bot pairs and three rendered pacer pairs; full allocation/whole-tick/frame mean/p95/p99 conclusion still owed. Headless tails alone are inconclusive. | P2 acceptance follow-up, reserved CPU/GPU window and complete paired analysis. |
| P3 native performance | [Publication](evidence/body-publication-verification.md): one further valid 16-client headless pair and three pairs each at four clients headless/rendered remain (load below 4); controlled callback allocation/bytes and native correctness passed. | P3 acceptance follow-up: matched whole-tick native allocation/timing matrix, not another microbenchmark. |
| P5 deep gate | [Tuner sampling](evidence/lab-tuner-sampling.md): four-client faulted final moving vent drift 0.710/0.639/0.672 m versus 0.50 m. Later component passes do not complete that failed invocation. | FU2a diagnosis, FU2b separately classified correction, then complete unchanged deep gate. |
| FU2 moving-vent residual | [Corrections](evidence/network-corrections.md): fault 7 final 0.531 m versus 0.50 m with fresh traffic, high spin and floor contact. Contact/spin is an inference; original fault-9 alignment/mechanism was not retrospectively established. [FU2a](evidence/moving-vent-residual.md) reproduces a parent final at 0.557 m, isolates repeatable acceleration lag, and records contact/age/timing controls; the ordinary failure’s full mechanism remains unknown. [Participant integration](evidence/participant-integration.md) and [King crowd](evidence/king-crowd.md) also retain moving-vent failures. | FU2a separates sample age/receipt freshness/ownership/contact/spin/measurement alignment; FU2b proves clean/faulted stationary/moving/vent/contact matrix without threshold changes. |
| Stock-soak resting variability | [Lobby supply](evidence/lobby-tanks.md): seed-25/net-seed-2 failed resting pair up to 3.90 s at 0.152 m; unchanged retry passed. | Native convergence investigation covering the retained resting case, unchanged limits and complete deep runs; FU2 moving-vent diagnosis alone does not close it. |
| Ordinary capacity and supply | [Tank scaling](evidence/tank-scaling.md), [network soak](evidence/network-soak.md), [network integration](evidence/network-integration.md): ordinary sixteen-process populated gameplay, startup delivery and reliable 60 FPS/100 ms bounds remain unproved; eightfold native capacity also absent in [lobby supply](evidence/lobby-tanks.md). Controlled capacity fixtures do not establish those workloads. | Owner-defined representative capacity workload plus quiet native client/server fanout/construction/physics runs; no ceiling or admission change in refactors. |
| Bot absolute cost/allocation | [Bot scale](evidence/bot-scale.md): residual 120-byte ticks and loaded maxima above 4 ms; universal zero-allocation/4 ms claim unproved. | Bot/navigation performance follow-up isolates residual allocation and repeats representative quiet whole-tick tails; B4 tactical extraction alone is not closure. |
| Attribution and visual limits | [Vent effects](evidence/vent-performance.md), [actor scale](evidence/scale-actors.md), [tank scaling](evidence/tank-scaling.md): exact Jolt/contact attribution and quiet-machine limits missing; sampled captures do not prove continuous animation/subjective responsiveness. | Dedicated profiling/native review under matching workload, plus human feel/animation review where the claim requires it. |
| Animation visual acceptance | [Animation review](runbooks/animation-review.md#metrics-and-calibration) retains 12 of 46 pending checks (stock/head/shoulder clearance, limb separation, pelvis span and sliding); A0’s unchanged gate reproduces 34/46 passing. Data-coverage success is not visual acceptance. [Actor evidence](evidence/scale-actors.md) also does not establish continuous motion. | Owner-approved animation redo with rendered multi-view/action review and calibrated visual checks; enable strict enforcement only after acceptance. No threshold change in a structural refactor. |
| Loaded-unit native crash | [Unit time regression](evidence/unit-time-regression.md): one baseline signal-11/caller-thread crash was not diagnosed by the manual-clock fix. Later loaded passes do not identify its cause; [shutdown](shutdown.md) establishes a separate teardown mechanism. | Native lifetime/thread follow-up: reproduce and isolate the recorded mechanism, then retain repeated loaded proof; do not attribute it to the known shutdown race without evidence. |
| Gate elapsed-time target | [Test speed](evidence/test-speed.md): historical under-15-minute deep target not met; later source partition/CI estimates are not an observed stock-runner timing. | Gate-performance follow-up measures full target and CI partitions without cutting assertions/depth. |
| Platform/artifact proof | [R1 evidence](evidence/artifact-verification.md) verifies three packaged inventories, Linux ordinary/server/classic/smoke/network paths and Windows single-player smoke under Proton. Native Windows, macOS execution/signing and cross-machine alignment remain unverified. | Retain the [platform runtime checklist](runbooks/release-artifacts.md); automate native execution before claiming broader acceptance. |

The [P4 record](evidence/net-scenarios-verification.md) also says vent-contact's shooter-ID defect is pending.
That historical obligation is **closed**: `39eea7e` supplies the owning actor ID in
[VentContactScenario](../src/Dev/NetScenarios/VentContactScenario.cs), and integration commit `1019775` records the
native pass. The old failure remains in its dated report; it is not a current actor-zero fixture defect. B1's
[23-case matrix](evidence/server-run-verification.md) also records a passing native vent-contact run.
Its `f10e19d` integration records a later full deep-gate pass; that statistical pass does not erase P5's failed
invocation or establish a fix for the FU2 residual. The B1 record's final-gates-pending status predates that integration.

The older [unsharded dials timeout](evidence/test-flakes.md) remains a failed historical invocation. Later
[sharded gate work](evidence/test-speed.md) and P5's complete offline sweep provide the subsequent workload proof;
they do not relabel that old invocation. The elapsed-time target above remains a separate obligation.

Resolved historical failures (for example corrected idle replay and generator shard issues) remain in evidence, but
are not newly open acceptance items. [Evidence index](README.md#evidence) preserves the provenance and limitations.

## Build, export and release

[Propane.csproj](../Propane.csproj) is the production Godot.NET.Sdk 4.7.2/net10.0 project. Explicit Tuning-part order
is observable in reflection, native properties, UI and snapshot bytes. The checker is a separate plain SDK console
project, excluded from game compilation by `tools/**` and from Godot imports/exports by its `.gdignore`; its fixtures stay outside shipping.

**D1 current:** only `ExportRelease` removes `src/Dev/**/*.cs`; Debug, ExportDebug, source Release and optimized Debug
probes retain Dev. Export presets exclude Dev source/scenes and references. The exported smoke closure lives in
`src/ReleaseSmoke/` (ReleaseTest, Bot, CaptureDirector/FrameEncoder, FrameMetrics and their generated/nested types).
Main's exact smoke entry is retained. Do not exclude tests based on `Optimize=true` or source `Release`, and do not
split native tests into another game assembly just to remove them from shipping. See
[development](development.md#versions-and-releases) and [release references](release-reference.md#limits).

**R1 current:** setup rejects import errors/nonzero exits, except the exact missing-assembly ShutdownGuard bootstrap
diagnostic before compilation; its post-build import must be clean. The architecture checker's `inventory` mode reads
the unpacked shipping assembly without PDBs and the Godot 4.7.2 PCK v4 directory. Its reviewed `artifact.json` requires
the smoke closure, normal/server/smoke methods, main scene/remap and autoload settings/resources, and rejects Dev
types/resources. `tools/release/check_artifacts.py` checks all three archives and executes the packaged Linux ordinary,
classic, server and single-player smoke paths before release publication. CI uses dummy headless display/audio;
rendered Forward+ and multiplayer remain separate local proof. Source Release and optimized Debug retain Dev and
are checked independently of the shipping configuration. No new runtime owner, timer or cross-module edge is added.

Deployment refuses a dirty checkout, a version without its matching release tag at HEAD, or a tag outside local
`main`'s history. It inventories the exported game before contacting the host and records tag/commit in
`game/release-id.txt`. This is a selected local release checkout, not a promise of the latest remote release.
Three exports do not prove Windows/macOS runtime behavior: retain the
[platform checklist and CI limits](runbooks/release-artifacts.md) until automated execution replaces them.

All patches within a minor interoperate. Anything players share—messages, suburb construction, tanks, tuners—needs
a minor release for a behavior change; bump `Protocol.Version` for message-shape changes, retaining trailing-only
Hello/Welcome/Rejected extensions. Current protocol is 3. Structural moves must update release-impact path checks
when compatibility-sensitive files move, and retain immutable reference manifests/provenance. Release from `dev`
only through the release tasks with push approval; `main` publishes releases ([developer guide](development.md)).

## Extension recipes and limitations

| Growth | Recipe and boundary |
| --- | --- |
| Mode | Owner chooses exposure/rules; add complete compiled bundle (B3a), plain rules/views and client/server adapters (B3b). Validate eligibility, ordering and missing integrations. Registration alone does not implement life/round/world transitions. |
| World | Add plain plan/catalog/source and main-thread builder; preserve explicit RNG and construction order. Local streaming is not online streaming: versioned segment/world-revision authority is separate protocol work. |
| Players/bots/views | Use actor registry/actor-lived attachments, shared BotServices and independent view registrations. Measure actual populated workload before increasing limits. M0's primary-actor cache is not multi-view support; per-view device/glyph routing precedes shipped split-screen. |
| Physical prop | Specify identity range, lifecycle, classification and construction; retain tank/car formulas and traversal. Another real prop family may justify a descriptor, not a universal entity system now. |
| Character/weapon | V1 validates bones/sockets/clips/calibration once; use a second real asset family to justify abstraction. Retain native rig/recovery math/order. Cosmetic IDs stay opaque codec values with safe fallback, not arbitrary resource paths. |
| Transport/hosting | Implement IPacketTransport semantics and endpoint/discovery composition; preserve authority and lane contracts. Dedicated server remains rules/accounting, not a duplicated physical world. |
| Effects/audio | Add world/view-scoped predicted/confirmed presentation facts with deduplication and originating content lifetime. Keep physical/authority consequences synchronous. |
| Platforms | Validate produced exports and real input/render behavior; Linux exact physics/reference results are not cross-platform proof. |

Independently advancing streamed worlds require W2; multiple cameras of one stream do not. Navigation eviction,
ledger/protocol-history compaction and bulk tuner conversion are separate correctness/design work. Interest
management, reconnect/resume, stronger anti-cheat, unlimited server lifetimes and authoritative server physics are
future product/networking projects, not hidden refactor requirements.
