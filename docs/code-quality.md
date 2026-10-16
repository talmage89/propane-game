# Code quality

The C# formatting rules in `.editorconfig` describe the existing code: four spaces, Allman braces, file-scoped
namespaces, `var` locals and System usings first. Inline statements and switch-case blocks keep their existing
layout. Long expressions are not reflowed. Expression bodies and naming remain editor suggestions rather than
build warnings. Run `dotnet format Propane.sln` to apply the baseline.

`make lint` verifies formatting, then performs a full build with warnings as errors. A previous incremental build
cannot hide warnings. `make check` runs lint before the headless regression tests, including with `make -j`, and ends with
`make test-infra`, the gate's own self-tests. It stops on a lint failure. All three tasks appear in `make`'s help listing.

`Propane.csproj` pins analysis to .NET 10's default rules, with the recommended Reliability, Usage and Security
sets. Nullability is enabled. CA1001 additionally checks ownership of disposable fields; CA2007 requires explicit
async context capture. `UpdateCheck` keeps Godot's main-thread context when it updates menu nodes.

CA2000 (local disposal) and CA2213 (field disposal) are outside this baseline: they treat Godot's scene-tree and
reference-counted lifetimes as ordinary CLR disposal. Excluding Godot-derived classes also hides CLR leaks inside
those classes, so there are no such exclusions. These rules need an engine-aware ownership policy before they
can be useful here. Disposable resources in new code still need an explicit lifetime.
The CA2213 trial also flagged `CaptureDirector`'s Process and BlockingCollection. They now live in its `FrameEncoder`,
which owns their disposal; every recorder wait is bounded (`--record-timeout`, default 30 s) and a recorder or screenshot
failure sets the exit status (`CaptureTests` fault-injects broken, stalled, lingering and file-less encoders).

Seven class-specific CA1001 suppressions preserve current lifetimes, with the reason beside each declaration:

- `Bot` and `SimpleBot`: the random generator is Godot RefCounted.
- `MeshKit`: SurfaceTool is Godot RefCounted.
- `CharacterAnimator`: the model root owns and frees the AnimationTree node.
- `NetTransport`: Close releases the connection while the RefCounted peer stays reusable for reconnects.
- `NetWriter` and `NetReader`: their streams contain managed byte arrays, without file or socket handles.

`make test-infra` (`python3 -B tools/dev/lint_tests.py`) runs last in `make check` and as its own CI job, `infra`. It
needs dotnet, make, git and python3 but stubs Godot, so the job needs neither the editor nor imported assets. It proves the
gate rejects nullable dereferences, disposable-field ownership,
implicit async context, ValueTask misuse, and whitespace and style drift. It also checks corrected code passes.
The fixtures compile in an isolated temporary project; they never run or modify game sources.
`check_world_tests.py` proves failed children retain their logs and exit status, while successful runs remove scratch
files. The script also stubs external tools to prove `make -j4 check` runs lint before every gate suite and
propagates a failure from each checker, scene, saved-plan writer and world-hash process. It checks that the core runner retains platform, movement and unit scenes with failure completion. It rejects missing or
different world hashes and checks that plain and parallel `test-full` run the gate and every deep command once, that the
Lab jobs add up to every `--full` session, and that a failure in any deep job fails the run with that job's name. It checks that Godot
setup reuses a valid cached editor and stops on a download failure.
The gate manifest includes session actors, upright piles, real device input, the animation review/metrics and the
benchmark smoke checker. The Makefile probe also checks that bench/play defaults stay local to their targets and
accept command-line and environment overrides.
The shutdown proof permits `AnimationReview.Quit` only with its guarded current-build branch and explicit
historical-release branch; direct current-build `SceneTree.Quit` calls still fail.

## Fast gate and deep sweeps

`make test` is the union of four targets. Each builds first, so it also works independently:

| Target | Assertions |
| --- | --- |
| `test-core` | Python timing/world-wrapper/score/vent/tank-agreement/launcher checkers, benchmark smoke, platform, movement, all unit suites, scripted mode rules (`mode_test`), normal shutdown and paused window-close witnesses |
| `test-behavior` | Native server admission overrides and vent correction, session actors and animation metrics, scoped worlds, blast completeness, upright piles, gather/races/appearance/real-game gather, melee collision/poses, navigation, gamepad/devices/input/mouse menus, grip/recovery, feet/corrections/events, movement/motion continuity |
| `test-generator` | 20 single-player and 2-player seeds, three each at 8/16/24/32 players, two forced seeds for every formation at all six sizes |
| `test-world` | All four saved formation plans built and hashed in two fresh processes |

The generator gate keeps every assertion and all 14 explicit pile counts (0–128), with fewer random layouts.
`make test-full` adds 200 single-player and 2-player seeds, 100 each at the four large sizes,
six forced seeds per formation/size, 100 navigation seeds, and the melee-reload pose sweep. It includes every seed formerly in the gate:
ordinary seeds 21–100, large seeds 4–10 and forced seeds 3–6 moved to this deeper sweep.

Each deep step is its own `test-full-*` target, so `make -j4 test-full` runs them side by side with the gate's four
partitions; [test-speed.md](test-speed.md#parallel-deep-sweeps) has the timings. `tools/dev/label.sh` prefixes every line
a deep job prints with its name (`[generator-3]`, `[lab-king]`, `[bots]`) and, on failure, prints that name, the exit
status and the command; make then names the failed target. The jobs:

| Target | Runs |
| --- | --- |
| `test-full-generator-1`…`4` | The generator sweep in four contiguous seed ranges: 50 single-player and 2-player seeds, 25 at each large size and segment stream, and two, two, one and one forced seeds |
| `test-full-generator-coverage` | `check_generator_shards.py`: the shards' `--list-seeds` listings hold exactly the 1,044 seeds of `--seeds=200 --large-seeds=100 --formation-seeds=6 --segment-seeds=100`, each once |
| `test-full-lab-*` | All 18 full Lab sessions in 25 jobs: independent rounds/seeds, four lifetime shards, eight dial shards, both soaks; King keeps its cross-round mint assertion |
| `test-full-lab-coverage` | Every full Lab session/setting/seed and all 530 dial fixtures occur exactly once |
| `test-full-engine` | Build isolated optimized Debug for deep probes; each probe verifies optimization and `DEBUG` |
| `test-full-navigation` | 100 navigation seeds in one process, since its pass rate is over all of them |
| `test-full-bots` | The rifle soak, duel lane and loading-readiness matches, one at a time and after every other job: they share this checkout's `build/` names and run in real time |
| `test-full-piles`, `test-full-range` | The deep awake pile trial; melee reloads, airborne recoil and the ten-magazine bot recoil run |
| `test-full-menu` | The menu lifetime run, after `test-behavior`, whose port and `user://menu_test_settings.cfg` it shares |

Every other deep job is offline and writes its own log names, so jobs never share a port, a settings file or an output
path. Deep probes borrow the imported resources into `build/test-full-engine/project`, compile optimized Debug
with separate intermediate/output directories, and verify both optimization and the `DEBUG` symbol at scene entry.
Normal `make test`, `make check`, CI and the final real-time bot tail retain unoptimized Debug; the benchmark smoke
can rebuild it without replacing a running deep probe's assembly. All Godot processes of a checkout still append to its one `user://logs`, as under `make -j4 check`. The bot
matches run alone, after every other job: their server and clients run in real time, and beside other jobs on four
CPUs, clients dropped to a few frames a second and failed tank agreement, and the loading-readiness match lost its
stalled client far more often ([test-speed.md](test-speed.md#parallel-deep-sweeps)). `-j` is not
the default: worktree agents share one machine, so each run chooses its own job count. Use `-j4` on a quiet machine,
fewer when other worktrees are busy. To shard a sweep differently, change the `GENERATOR_*` or `LAB_*` variables in
the Makefile; the coverage job and `lint_tests.py` reject a split that loses or repeats seeds or sessions.

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
matrix together. Run `make check` (which includes `make test-infra`) and the relevant deep sweep before submitting.

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
