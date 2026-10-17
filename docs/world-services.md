# World services

`Game` and `Match` are facades around `SessionContext` and `WorldContext`. A context owns its `World3D`, root, content/effects roots,
spawn service, tuning copy, random stream, events, blast receiver registry, simulation clock, deferred work, views, and mutable
magazine/decal/light accounting. Consumers receive a context or tuning explicitly before entering the tree.
Immutable asset, shader, material, mesh, collision and catalog caches remain shared.

`TankSimulation` lives under each world's effects root and batches tank physics callbacks. Tanks register on
entry and unregister on exit. Intact tanks still observe rest/motion every physics tick, including scripted
sleep/wake and relocation between gather scans; vents retain the same thrust and correction path.
The batch respects each tank's effective process mode, so disabled staged content leaves simulation immediately.
`GatherGroups` searches neighboring spatial cells, retaining exact radius connectivity, ordered membership and
revision-keyed delay clocks. Neither service changes the puncture, fuse or gathering rules.

`DebrisQueue` also lives under the effects root. A burst captures its selected fragment poses, velocities and
cosmetic randomness, then the queue registers at most `DebrisSpawnPerFrame` bodies per presentation frame.
It checks each fragment's originating content before creating it; disabling or retiring content discards pending fragments,
and world invalidation clears the queue. Spawned bodies stay in their originating content. Deferred fragments
start at the captured burst pose, so very large queues delay shell breakup and subsequent fragment collisions.
Debris uses an independent cosmetic random generator, keeping queue drain timing out of `World.Random`.
Full-explosion counts are scoped to the world and released on effect retirement. Over the budget, a blast keeps
its immediate core flash/fireball while omitting the secondary layers. All physical blast and chain work remains
synchronous, including receiver callbacks and causal network requests.

Each simulation lives in a viewport with rendering disabled. The application viewport shares the selected
`World3D` to present it; camera nodes stay under the facade and query their injected world's physics space. This
presentation bridge preserves the facades. Actors carry camera-independent logical aim; explicit local views
attach cameras, HUDs and input sources. The Lab uses this assembly; split-screen layout remains a later package.
Authored dev fixtures can place geometry directly under their scene while explicitly presenting that fixture's context.
The presentation viewport owns the bridge in metadata. When several contexts present there, retirement restores
the most recently presented live context, or the viewport's original world when none remain, in either teardown order.

`WorldSpace` is an offscreen simulation viewport and receives no application device events. Each `LocalView`
owns a device endpoint in the application viewport, under itself or the session facade when its camera/HUD
render in a child viewport. `Game` and `Match` handle session shortcuts only. Primary devices accumulate unhandled
input after UI; explicit assignments also observe handled releases to keep their cached held state accurate.
`DeviceInput` retains capture gating and the existing pause/focus reset behavior. Closing or reassigning a view
synchronously releases its endpoint and intent source. Do not broadcast input into simulation viewports.
`Main`, the facades, `MenuNavigation` and `HsvColorSlider` live in the application viewport.

`menu_test.tscn -- --scenario=input` injects mouse motion, key presses and complete mouse-button clicks through
`Input.ParseInputEvent`, including menu navigation with Tab/Enter. It checks exact camera displacement, jump,
melee, single-click fire, single-player suburb replacement, match reload and logical Escape on physical Caps Lock
in both facades. Controller events also exercise primary-view look, jump, melee, complete trigger taps and
match reload. It also checks paused input and physical/logical system shortcut bindings. This runs in
`make test`. Headless Godot cannot capture a pointer, so that run substitutes only `DeviceInput`'s existing
capture predicate with menu visibility; rendered runs use the default production input source and capture frames
before/after the turn, in the jump, and with the remapped pause menu open. Gameplay keys stay physical; pause,
tuning and fullscreen shortcuts accept both physical and logical keys to honor desktop remaps.

`Tuning.SavedTemplate` is an application composition/UI accessor, never the gameplay target. Each context copies
its template. Matches validate and copy the creator-supplied snapshot without swapping any process-wide override.
Unexported local bot dials are copied explicitly too: they remain local and never enter the match snapshot.
The tuning panel receives an explicit `Target`: edits belong to that world; Save writes the target and updates the
application template for future worlds. Unsaved world edits disappear when that world ends.

Gameplay and effects draw from `World.Random`, an independently randomized generator disposed with its context.
Tests can seed a context explicitly without advancing another world. Layout generation retains its own seeded
generators; `FixedSeed` still controls layouts. Existing continuous ranges and discrete integer draws are preserved.

`WorldClock` measures simulation seconds, advanced once per world physics tick with Godot's simulation delta.
Tank drop shields and chain fuses use this clock, so fixed-FPS tests and hitstop cannot shorten their gameplay
durations through wall-clock divergence. Existing delta-driven reload, gather and movement timers keep their
simulation-time behavior. The victim's hit grace uses simulation time. Source-node retirement cancels scheduled
callbacks; world invalidation cancels all timers and deferred work, including callbacks due in the same tick.

Network timestamps, interpolation, transport fault delays, server-owned deadlines and diagnostic hit timestamps
continue to use `MonotonicClock` and the server clock. Never compare those timestamps with simulation seconds.

Call `Session.Dispose()` before freeing or replacing a session facade. World-only fixtures use `World.Dispose()`. Cleanup invalidates first, cancels timers/deferred
work, stops processing/physics, clears events and receiver registrations, runs stop adapters (including inbox detach and replication),
releases views, restores presentation, and synchronously frees content and physics resources. It is idempotent.
A presentation viewport owns its selection history; restoring skips disposed and invalidated predecessors.
`World.Construct` uses that same path on a construction failure. `Suburb.Build` frees its detached subtree if
construction fails before it can be attached to the world. Content spawn services retain their originating
root; retiring content removes its effects and cancels source callbacks before the next suburb is built.

`make test` discovers `[UnitSuite]` clock assertions and runs `world_services_tests.tscn`, which exercises two
independent physics worlds at overlapping coordinates with different tuning. It asserts blast, event, spawn and callback isolation, survivor continuity, replacement safety, partial
construction cleanup, tuning-panel target isolation, independent seeded puncture streams, and exact node/object/orphan counts across ten retirements after
warming immutable caches. A real single-player explosion also verifies hitstop admission and synchronous retirement
of its presentation node and engine time-scale lease.

`World.SetPaused` disables the simulation root and calls `PhysicsServer3D.SpaceSetActive` for that world's space.
Tracked cameras follow the pause; menus and the always-processing driver stay active. Connection polling remains
outside simulation roots. Pause resets queued input edges through process-disable notifications. Tree-wide pause
is no longer a gameplay control.

Reveal center, radius and glow are per-instance shader parameters owned by `WorldReveal`. Reveal uses slots 12–15
across all compatible materials; actor parameters explicitly occupy lower slots. New geometry inherits the world's
current state, and retiring geometry unregisters immediately. No shader global is shared between worlds.

The sole application-wide simulation primitive is `WorldTimeScale`: an exclusive hitstop lease. Only one live,
unpaused offline context can acquire it; construction of a second context, pause or retirement releases it. The
always-processing driver expires the presentation lease in real seconds while gameplay uses simulation seconds.
Network worlds cannot acquire it. The isolation scene also asserts actual physics pause/resume, independent reveal,
lease restrictions and a production client/server handshake completing while a world is paused.

The driver subscribes to [scene-tree frame signals](https://docs.godotengine.org/en/stable/classes/class_scenetree.html#signals),
which run before node processing. This keeps the native signal emitter alive while deferred navigation synchronously
retires its own world, including the driver. It unregisters both signals on exit. Work queued from a button waits
until its signal returns, then cleanup completes before replacement. Invalidation also stops the remaining callbacks
of an event currently being dispatched; a retired receiver is skipped in a blast's registration snapshot.

`menu_test.tscn -- --scenario=lifetime` drives the real main/pause/lobby/match buttons through ten single-player and
ten solo-match cycles after warming immutable catalog variants and lazy UI resources. It checks exact node, object
and orphan counts and verifies each retired world's timers and receiver registry are empty. Server-owned match
seeds still vary; the test never alters the server's construction inputs or messages. The ten-cycle sweep runs in
`make test-full`. Rendered runs capture first and last entries plus the final browser.

## Actors and aim

`PlayerCharacter` is the simulated character, including the skeleton used for ragdoll recovery and muzzle
placement. `ActorRuntime` owns its intent source and logical aim. No camera, viewport or HUD is needed to move,
shoot, strike or get up. `LocalView` explicitly attaches a `CameraRig`, HUD and optional device input to an actor;
closing it releases input but leaves the actor alive. Bot brains and navigation use the actor's aim directly.
The character's `CameraRig` accessor remains a convenience for existing rendered dev harnesses only.

The aim split preserves the old ray, including its over-the-shoulder origin, wall pull-in, target smoothing,
render-frame updates and the chest-plane projection that keeps shots from starting behind the character.
Shot recoil displaces logical pitch and yaw immediately. Most pitch and all sideways displacement remain until
look input pulls them back; only a small pitch budget settles slowly. Movement, facing and melee use that logical
yaw. Explosion/landing shake (rotation and displacement) retains its existing effect on the ray. A camera renders
the resulting transform and adds its own short recoil spring; that extra rotation never reaches logical aim.
Rifle/torso sway and the rifle recoil spring remain body presentation; they still affect muzzle placement and
its obstruction ray, never the primary aim ray. Before the obstruction ray, the rig aligns a reachable rifle pose
to the sampled endpoint; afterward the barrel rotates about the same muzzle to follow any nearer obstruction.
The firing frame retains that pose, and intervening burst frames track logical aim. See [recoil](evidence/recoil.md).
FOV affects projection/crosshair only. Hitstop remains the existing
exclusive offline-world time-scale lease: it changes simulation timing, never an aim angle.

## Session assembly

`SessionFactory` executes one synchronous order: construct scoped services, configure the mode while scoped nodes
are detached, attach the world, build its world source, add actors, add local views, register replication, attach the
matching inbox and acknowledge readiness. Loading presentation is initialized before attaching the inbox;
replayed Countdown/Go/End messages must not be overwritten by the ready acknowledgment.
`ISessionWorldSource` builds content; `ISessionMode` supplies assembly policy, not a new rules engine. Sandbox and
Tank Bank adapters retain their facade's existing gameplay. The shipped Lab uses `VoidWorldSource` and
`SuburbWorldSource`; a lobby or streamed source can implement those interfaces without editing the factory.
See [the Lab](lab.md) for its registry and offline banking adapter. `WorldContext` still owns content replacement
within a live session.

`SessionContext` owns an actor registry independent of peer keys, local views and overlays, a cancellation token,
inbox detach callbacks, subscriptions and replication stop adapters. Disposal invalidates world work first,
cancels the session, detaches the inbox, unsubscribes, stops control/replication, frees views, frees actors and
finally disposes world content/services. Callback failures are collected while cleanup completes. Partial assembly
uses exactly the same path. Actor/view readiness is checked explicitly because Godot does not propagate managed
`_Ready` exceptions through `AddChild`; failed initialization cannot reach the inbox or ready acknowledgment.
If construction and cleanup both fail, the factory retains both errors. Closing a local view removes its
camera/HUD/device source without retiring the actor.

Ammo, intent, the movement resolver, resolved state, logical aim, swing/publication sequence, snapshot buffer,
hit grace and drop predictions now belong to each actor. Match interpolation iterates the session registry.
A6 routes actor ids and lives independently of peer ids, including scoring, contact targets, snapshots and drop
allocation. Several owned actors share one body synchronizer; views remain process-local.
Headless `net_test` attaches no local view, so the existing smart/simple bots and network scenarios run on actors
with no camera. Rendered runs attach the same view used by the shipped facades.

`InputDeviceAssignment.Primary` retains the shipped single-view keyboard/mouse and gamepad switching. Explicit
keyboard/mouse or numbered gamepad assignments filter events before tracking per-binding strengths and edges;
one device cannot release another view's held action. Explicit views observe handled releases too, and disconnect
clears their cached actions. Pause/focus resets resample each assignment's own physical keys, mouse buttons and
controller bindings, discarding stale cached releases and queued presses without importing another view's held state.
Menus retain application-level keyboard/mouse and controller navigation. Split-screen
viewport layout is a dev fixture, not a new shipped mode.

`session_actors_tests.tscn` renders two subviewports sharing the session's `World3D`. It asserts independent device
routing through `Input.ParseInputEvent`, movement yaw, camera following, shot directions, melee contacts and
survivor continuity after closing one view. A third, camera-free actor navigates, shoots a tank twice, strikes a prop and recovers from the blast ragdoll.
The unit suite injects failure into every factory stage and checks cancellation, adapter/node retirement order,
completion after a throwing stop adapter and device isolation. Both run in `make test`.

A6's `ParticipantRoster` supplies server-owned actor records and monotonic allocation slots. Actor ids and lives
are independent of peer ids; retired ids/slots are never recycled. Drop entity ids use allocation slots, with
explicit exhaustion checks instead of masked counters. The ENet adapter keeps its backend ids private and issues
protocol peer ids through `ProtocolPeerMap`.

Actor lookup uses the session identity registry; retired actor ids cannot be reattached. Offline sessions allocate
positive actor ids through the same registry. Local views allocate process-local ids, including fixture views.
