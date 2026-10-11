# Code quality

The C# formatting rules in `.editorconfig` describe the existing code: four spaces, Allman braces, file-scoped
namespaces, `var` locals and System usings first. Inline statements and switch-case blocks keep their existing
layout. Long expressions are not reflowed. Expression bodies and naming remain editor suggestions rather than
build warnings. Run `dotnet format Propane.sln` to apply the baseline.

`make lint` verifies formatting, then performs a full build with warnings as errors. A previous incremental build
cannot hide warnings. `make check` runs lint before the headless regression tests, including with `make -j`. It stops on a
lint failure. Both tasks appear in `make`'s help listing.

`Propane.csproj` pins analysis to .NET 10's default rules, with the recommended Reliability, Usage and Security
sets. Nullability is enabled. CA1001 additionally checks ownership of disposable fields; CA2007 requires explicit
async context capture. `UpdateCheck` keeps Godot's main-thread context when it updates menu nodes.

CA2000 (local disposal) and CA2213 (field disposal) are outside this baseline: they treat Godot's scene-tree and
reference-counted lifetimes as ordinary CLR disposal. Excluding Godot-derived classes also hides CLR leaks inside
those classes, so there are no such exclusions. These rules need an engine-aware ownership policy before they
can be useful here. Disposable resources in new code still need an explicit lifetime.
The CA2213 trial also flagged `CaptureDirector`'s Process and BlockingCollection; their disposal and shutdown lifetimes remain
a separate audit item rather than being changed as part of the gate.

Seven class-specific CA1001 suppressions preserve current lifetimes, with the reason beside each declaration:

- `Bot` and `SimpleBot`: the random generator is Godot RefCounted.
- `MeshKit`: SurfaceTool is Godot RefCounted.
- `CharacterAnimator`: the model root owns and frees the AnimationTree node.
- `NetTransport`: Close releases the connection while the RefCounted peer stays reusable for reconnects.
- `NetWriter` and `NetReader`: their streams contain managed byte arrays, without file or socket handles.

`python3 tools/dev/lint_tests.py` proves the gate rejects nullable dereferences, disposable-field ownership,
implicit async context, ValueTask misuse, and whitespace and style drift. It also checks corrected code passes.
The fixtures compile in an isolated temporary project; they never run or modify game sources.
`check_world_tests.py` proves failed children retain their logs and exit status, while successful runs remove scratch
files. The script also stubs external tools to prove `make -j4 check` runs lint before every gate suite and
propagates a failure from each checker, scene, saved-plan writer and world-hash process. It checks that the core runner retains platform, movement and unit scenes with failure completion. It rejects missing or
different world hashes and checks that `test-full` retains the gate and every deep sweep, including melee reloads, and propagates their failures. It checks that Godot
setup reuses a valid cached editor and stops on a download failure.

## Fast gate and deep sweeps

`make test` is the union of four targets. Each builds first, so it also works independently:

| Target | Assertions |
| --- | --- |
| `test-core` | Python timing/world-wrapper/score/vent/trajectory checkers, platform, movement, all unit suites, normal shutdown and paused window-close witnesses |
| `test-behavior` | Blast completeness, gather/races/appearance/real-game gather, melee collision/poses, navigation, all three menu scenarios, grip/recovery, feet/corrections/events, movement/motion continuity |
| `test-generator` | 20 single-player and 2-player seeds, three each at 8/16/24/32 players, two forced seeds for every formation at all six sizes |
| `test-world` | All four saved formation plans built and hashed in two fresh processes |

The generator gate keeps every assertion and all 14 explicit pile counts (0–128), with fewer random layouts.
`make test-full` runs the gate, then 200 single-player and 2-player seeds, 100 each at the four large sizes,
six forced seeds per formation/size, 100 navigation seeds, and the melee-reload pose sweep. It includes every seed formerly in the gate:
ordinary seeds 21–100, large seeds 4–10 and forced seeds 3–6 moved to this deeper sweep.

`core_tests.tscn` runs platform, movement and unit scenes sequentially, prints BEGIN/PASS per suite and exits
nonzero on failure. Those scenes also work alone: `CoreTests.Complete` routes standalone completion through `ShutdownGuard.Quit`. The consolidated runner also uses
that guard on success, assertion failure and setup failure; no completion bypasses orderly shutdown.
The wiring check also rejects direct quits throughout dev harnesses, except the shutdown witness, whose assertion
runs during final teardown. The runner frees each suite before starting the next and releases injected actions. Synchronous unit suites run
last because they build/free worlds without advancing physics. Stateful game probes retain separate processes;
sharing their tuning, spawn roots, network sessions and animation state would need a larger isolation contract.
Fixed-rate headless execution advances the same scripted simulation without waiting for real time; menu network
waits still have their existing wall-clock deadlines.

CI runs lint independently, then the four test partitions in parallel after an import job. A final `check` job
requires every result to succeed, keeping a stable branch-protection check. This is the same union as local
`make check`; locally lint finishes before any suite starts, even with `make -j`. Parallel make shares one build
and propagates failures from any partition. CI and release share .NET 10/Godot 4.7.2 .NET setup.
CI caches `.godot/imported`, keyed by Godot version, project settings and all asset bytes/import settings.
The import job populates the cache before the matrix starts; each partition restores it and runs import to rebuild
its checkout's resource index. A cache miss safely imports from source. Imports must succeed; caches never replace
compilation or assertions. Editor setup remains shared with release, whose tool cache is unchanged.

## Deterministic unit time

Time-sensitive network unit tests use manual clocks. Server and match fixtures share a `ManualClock` for message
receipt, ping estimates, sample timestamps and phase advancement. `Match.Clock` defaults to `MonotonicClock.Instance`
and can be injected before the node enters the tree, like the existing client and server clocks. Production uses the same process-relative time
as before. The server's gather deadline also uses its injected clock.

Tests advance time explicitly, including expiry before a phase tick. Do not sample wall time around a message and
then correct at the earlier sample: receipt can occur later, making the correction age negative. Under CPU load,
that used to extrapolate the authority test's observer backward far enough to fail its exact pose assertion.
The observer test now asserts its receipt timestamp and corrects at the same manual instant, after an explicit
30-second pause. Relay timestamp assertions now check exact clamped times; the gather deadline test checks
acceptance before and refusal exactly at expiry. No tolerance or retry masks scheduling delays.

### Loaded regression proof

The October 2026 regression repeated
`godot-mono --headless --path . res://scenes/dev/unit_tests.tscn` with eight concurrent processes on 12 available
CPUs. Additional Python processes ran `while True: pass` throughout each batch. Every child's exit status, named
assertion failures, final test count and suite BEGIN markers were checked; a successful parent alone is insufficient.

| Code | Runs | CPU hogs | Assertion failures | Native crashes |
| --- | --- | --- | --- | --- |
| Before manual time | 300 | 4 | 8 | 1 |
| After manual time | 300 | 4 | 0 | 0 |
| After manual time, added load | 300 | 6 | 0 | 0 |

All eight assertion failures were `authority/new canonical vent reconciles an intact observer (moving True)`.
Each fixed run completed all 13 suites and 190 tests (189 before the additional gather deadline assertion).
The baseline's separate signal-11 crash interrupted ProtocolTests, with a Godot `propagate_notification()` caller-thread
error; this change does not diagnose or fix that engine crash. The baseline consequently has 299 complete suite runs.

A full `/tmp` truncated 16 baseline logs whose recorded child exits were zero. Those incomplete records were
retained and replaced with 16 additional baseline invocations; all assertion failures and the native crash remain
in the reported 300 records. No failed assertion was retried or discarded. Proof logs were then stored under ignored
`build/wpflaky-proof/`, along with the repetition driver and validated summaries.

The added-load batch used an isolated copy matching all 305 committed source, scene and project-setting files,
excluding preexisting unrelated shutdown edits. `make check` passed both there and in the working checkout.
The rendered three-client puncture race also passed with 120 ms lag, 30 ms jitter and 3% loss: canonical vent
comparison covered 1,658 trajectory pairs (median/p95/max separation 0.104/0.246/0.390 m), and all score audits agreed.
Four captured frames were reviewed, showing the tank venting, tipping and tumbling.

## Scene-thread assertions

The core and standalone unit runners install `TestThreadGuard` before running assertions. It checks the managed
thread at unit entry and listens for Godot's native scene-thread rejection diagnostics, including calls from CLR
finalizers. A violation prints `[thread] FAIL`, the originating thread and managed caller stack, then exits 1.
It bypasses native teardown on this failure path: a worker cannot safely free the scene, and native atexit cleanup
while the tree is live produces unrelated errors. Successful tests still quit through `ShutdownGuard`.
`check_test_threads.py` launches separate managed-entry, native-node and CLR-finalizer violations and requires exit 1
and the expected caller, so a lost logger, a swallowed assertion or a signal termination fails the gate.

A root `propagate_notification()` caller-thread error immediately before a signal report does not alone identify
an application scene-thread call. Godot's Linux [signal handler](https://github.com/godotengine/godot/blob/ed1daf0bf/platform/linuxbsd/crash_handler_linuxbsd.cpp#L107)
sends `NOTIFICATION_CRASH` to the tree before printing the report; the tree propagates that notification to the root
on the signaling thread. Keep the original signal and caller evidence when diagnosing such an error.

## Adding a suite

For a unit suite, add a static class in `src/Dev/UnitTests`, mark it `[UnitSuite]`, and expose
`public static void Register(TestRunner runner)`. Register named assertions with `runner.Run`. The runner discovers
marked classes in its own assembly and invokes them in ordinal name order; `[UnitSuite(-1)]` keeps world construction
first. Each suite must register at least one assertion. No registration list or shared line needs editing.
Transport assertions are now discovered directly, rather than nested under the server suite. The wiring check audits
every unit registration method for the marker, so forgetting it fails verification. Keep suites independent of order.

For a separate scene, add a headless command to the appropriate `test-*` recipe in `Makefile`. It automatically joins both local
`make test`/`make check` and the corresponding CI job. Use a nonzero exit for every failed assertion and missing
coverage; do not hide failures with a pipeline or `|| true`. Add the command to `check_make_order` in
`tools/dev/lint_tests.py` so its presence and failure propagation are checked. Call `DevArgs.MarkReady()` at
the start of a new scene's `_Ready` so the profiler separates startup from work. For a compatible core scene, add its
name to `CoreTests.Scenes` and the required map in `check_core_wiring`, complete through `CoreTests.Complete`,
and check its isolation and assertion failures both alone and consolidated. Keep synchronous unit tests last.

Keep bounded regression seeds in the gate, and put deeper repetitions in `test-full` while retaining every
assertion kind and required fixture in the gate. If adding a new partition, update `test` dependencies and the CI
matrix together. Run `python3 tools/dev/lint_tests.py`, `make check` and the relevant deep sweep before submitting.

Measure rather than guessing:

```sh
python3 tools/dev/time_gate.py --output=/tmp/wptest-speed/test -- make test
python3 tools/dev/time_gate.py --output=/tmp/wptest-speed/check -- make check
```

The profiler uses Python's standard library and preserves command failures, including with parallel make.
It records total gate time and every dotnet/Python/Godot invocation in JSON. With `PROPANE_GATE_TIMING=1`, dev
scenes print a marker at the start of `_Ready`: launch-to-marker is startup, marker-to-exit is work plus shutdown.
Runtime scenes use imported assets; cold import and restored-import index rebuilds should be timed separately.
See [the gate timing report](test-speed.md) for the measurements and the CI estimate.
