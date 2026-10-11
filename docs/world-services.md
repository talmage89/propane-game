# World services

`Game` and `Match` are facades around `WorldContext`. A context owns its `World3D`, root, content/effects roots,
spawn service, tuning copy, random stream, events, blast receiver registry, simulation clock, deferred work, views, and mutable
magazine/decal/light accounting. Consumers receive a context or tuning explicitly before entering the tree.
Immutable asset, shader, material, mesh, collision and catalog caches remain shared.

Each simulation lives in a viewport with rendering disabled. The application viewport shares the selected
`World3D` to present it; camera nodes stay under the facade and query their injected world's physics space. This
small presentation bridge preserves today's single-view facades. Session factories, actor/view assembly, Lab,
and split-screen routing are later packages. Authored dev fixtures can place geometry directly under their scene
while explicitly presenting that fixture's context.
The presentation viewport owns the bridge in metadata. When several contexts present there, retirement restores
the most recently presented live context, or the viewport's original world when none remain, in either teardown order.

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

Call `World.Dispose()` before freeing or replacing a facade. Cleanup invalidates first, cancels timers/deferred
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
