# Process shutdown

Use `ShutdownGuard.Quit(tree, exitCode)` for game and harness exits. The first autoload owns shutdown:

1. Defer cleanup until the current callback returns, then free the scene and other root children.
2. Collect unreachable managed wrappers and wait for their finalizers while Godot's native services still exist.
   Collect and wait a second time: a native query can retain a shape wrapper until the query's finalizer releases it.
   One collection leaves that second wave for engine shutdown.
3. Wait for a physics tick to flush RID frees queued by finalizers on the CLR thread, then quit with the requested
   status. A successful duplicate request cannot overwrite a failure.
4. Drain again at the guard's predelete notification, before C# language shutdown. Godot destroys root children in
   reverse insertion order, so keep the guard first in the autoload list.

The guard runs while paused and handles the window's close request as well as menu and harness quits.
`FreeScenes` has a separate, non-inlined stack frame so temporary managed node references do not keep their scene
resources alive during collection. Recorder cleanup still runs in `CaptureDirector._ExitTree` before collection.
Static resource caches stay alive; Godot disposes their reachable wrappers through its own shutdown tracker.

## Failure mechanism

Godot's managed shutdown tracker holds **short weak references**. Once collection queues a resource wrapper for
finalization, the tracker cannot obtain it to dispose it on the main thread. Its finalizer can consequently run
after C# binding teardown and call `_instance_binding_free_callback` with a deleted `GDMono` instance. The debug
engine's binding assertion then aborts on the CLR finalizer thread.

Relevant Godot 4.7.2 sources:

- [DisposablesTracker.cs](https://github.com/godotengine/godot/blob/ed1daf0bf/modules/mono/glue/GodotSharp/GodotSharp/Core/DisposablesTracker.cs)
  enumerates weak targets without collecting or waiting for pending finalizers.
- [csharp_script.cpp](https://github.com/godotengine/godot/blob/ed1daf0bf/modules/mono/csharp_script.cpp#L140)
  disposes tracked instances, releases bindings and deletes `GDMono`; its binding-free callback contains the fatal
  assertion at line 1220.
- [physics_server_3d_wrap_mt.cpp](https://github.com/godotengine/godot/blob/ed1daf0bf/servers/physics_3d/physics_server_3d_wrap_mt.cpp#L66)
  flushes commands from other threads in `sync`. Draining finalizers only at final teardown leaves physics RID frees
  queued after the last tick.

An isolated .NET scene reproduced this race by allocating 10,000 `BoxShape3D` / `StandardMaterial3D` pairs, dropping
their managed references, calling `GC.Collect()` without waiting and immediately calling the engine's `Quit()`.
In 300 runs, every run logged the binding fatal and 15 terminated with SIGABRT. The captured core identified
`.NET Finalizer` as the aborting thread while the main thread was inside process exit. The orderly quit path ran
300 times with no aborts, binding fatals or RID leak warnings. There was no managed exception in these failures.

The original CI/agent crash logs were lost. The ordinary game baseline did not reproduce SIGABRT in 300 runs each
of writer, hash, unit, movement and ragdoll invocations, so the isolated backtrace does not prove that every reported
historical crash had this cause.

## Regression checks

`shutdown_tests.tscn` creates 20,002 resource wrappers during scene destruction, including a query that natively retains
its shape. A test-only witness checks that
all weak targets have disappeared and a managed finalizer has completed before final engine teardown. Failures set
exit 1. Collecting before scene destruction or draining only one wave fails this test.
`make test` also exercises a paused window-close request
and duplicate quit requests.

```sh
godot-mono --headless --path . res://scenes/dev/shutdown_tests.tscn
godot-mono --headless --path . res://scenes/dev/shutdown_tests.tscn -- --paused --window-close --repeat-quit
# Expected exit 7, with the shutdown assertion still passing:
godot-mono --headless --path . res://scenes/dev/shutdown_tests.tscn -- --paused --exit-code=7 --repeat-quit
```

`check_world.sh` prints child log tails and keeps its scratch directory on any failure. Deployment exports print
the log tail before deleting their scratch directory, even when an earlier `ERROR` line is present. Neither wrapper
retries a failed child or converts an abort into success.
