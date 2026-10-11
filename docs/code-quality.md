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

Network unit tests use a per-fixture `ManualClock`; message receipt, ping estimates, sample timestamps and phase
advancement share that clock. `Match.Clock` defaults to `MonotonicClock.Instance` and can be injected before the
node enters the tree, like the existing client and server clocks. Production uses the same process-relative time
as before. The server's gather deadline also uses its injected clock.

Tests advance time explicitly, including expiry before a phase tick. Do not sample wall time around a message and
then correct at the earlier sample: receipt can occur later, making the correction age negative. Under CPU load,
that used to extrapolate the authority test's observer backward far enough to fail its exact pose assertion.
The observer test now asserts its receipt timestamp and corrects at the same manual instant, after an explicit
30-second pause. Relay timestamp assertions now check exact clamped times; the gather deadline test checks
acceptance before and refusal exactly at expiry. No tolerance or retry masks scheduling delays.

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
