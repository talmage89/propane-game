# Code quality

The C# formatting rules in `.editorconfig` describe the existing code: four spaces, Allman braces, file-scoped
namespaces, `var` locals and System usings first. Inline statements and switch-case blocks keep their existing
layout. Long expressions are not reflowed. Expression bodies and naming remain editor suggestions rather than
build warnings. Run `dotnet format Propane.sln` to apply the baseline.

`make lint` checks script/.uid pairs and every relative link and image in tracked Markdown: each must resolve to a
tracked file and, for a `#fragment`, to a heading in it (`tools/dev/check_links.py`, standard library only; links to
other sites are not fetched). It verifies game C# formatting and fully rebuilds Debug and ExportRelease with
warnings as errors; a previous incremental build cannot hide warnings. Both assemblies then pass the architecture
ratchet, followed by source checks for global writes, clocks/timers and worker entries. [Architecture](architecture.md)
owns the module/lifetime contract; [checker operation](../tools/architecture/README.md) describes approvals and fixtures.
`make check` runs lint before the headless regression tests, including with `make -j`, and ends with `make test-infra`,
the gate's own self-tests. It stops on a lint failure. All three tasks appear in `make`'s help listing. When you move
or rename a document, fix its links in the same commit; the check names each broken one with its line.

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

Class-specific CA1001 suppressions preserve current lifetimes, with the reason beside each declaration:

- `SimpleBot`: the random generator is Godot RefCounted.
- `MeshKit`: SurfaceTool is Godot RefCounted.
- `CharacterAnimator`: the model root owns and frees the AnimationTree node.
- `LobbyDialogs`: the menu scene tree owns and frees the open dialog nodes.
- `NetTransport`: Close releases the connection while the RefCounted peer stays reusable for reconnects.
- `NetWriter` and `NetReader`: their streams contain managed byte arrays, without file or socket handles.

`make test-infra` (`python3 -B tools/dev/lint_tests.py`) runs last in `make check` and as its own CI job, `infra`. It
needs dotnet, make, git and python3 but stubs Godot, so the job needs neither the editor nor imported assets. It proves the
gate rejects nullable dereferences, disposable-field ownership,
implicit async context, ValueTask misuse, and whitespace and style drift. It also checks corrected code passes, and
(`check_links_tests.py`) that the link check rejects a missing file, image or definition target, a missing heading, an
untracked target and a path leaving the repository, while ignoring links inside code.
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

## Menu composition

`Menus` keeps application routing, the main menu's application actions and its public facade. `ConnectScreen` and `LobbyBrowserScreen` build their
forms; both lobby layouts use `LobbyScreen`, `LobbyRoster`, `LobbySettingsPanel` and `LobbyDialogs`.
`MenuCommands` dispatches connection/lobby/profile commands, `MenuProfile` keeps identity defaults and persistence, and
`MenuContent` supplies shared presentation helpers. `MenuRefreshState` owns the rebuild transaction: name/server
drafts, name caret, stable focus and the removal guard; open dialogs retain focus until they close.

The composition probe renders the production UI against decoded packets on a fixed backdrop. It covers
classic/docked lobbies, host/guest, 2/8/16 participants, controller focus, King settings and appearance/bot
dialogs. Its headless assertions preserve draft/caret/focus and overlay ownership across refreshes.
The real-device and network menu scenes remain responsible for input delivery and roaming-world behavior.

## Runtime launch options and the Dev boundary

`LaunchArgs` (`src/Core`) parses the user arguments after `--` once per process and caches them as `LaunchArgs.Current`.
`--key=value` is read as written and a bare `--key` is `"true"`; leading dashes are dropped, only the first `=` splits
(an `=` in first place is part of the key), a later argument replaces an earlier one, and numbers use the current culture
and fall back when absent or not numbers. `Has` means the argument was given with any value, so `--classic-lobby=false`
still counts. `Dev.DevArgs` reads the same cache for the harnesses and keeps their `Setup`, `MarkReady` and optimized-Debug
verification; nothing outside `src/Dev` reads `DevArgs`; the shipped smoke reads `LaunchArgs.Current` directly.

Normal game entry points read the options below. The harness options (`--scenario`, `--capture-dir`, `--tune.*`
and the rest) go through `DevArgs`; a Debug server applies `--tune.*` too, before it listens.

| Option | Read by | Effect |
| --- | --- | --- |
| `--server`, `--port=N`, `--start-score=N` | `Main` | Run only the multiplayer server, on `N` (default `Protocol.DefaultPort`), starting scores at `N` (default 0). |
| `--match-seed=N` | `ServerRun` | Debug builds: the next match plan uses seed `N`. |
| `--connect=host[:port]`, `--name=Name` | `Main` | Open the Connect screen on that server as that name. |
| `--windowed` | `Main` | Do not maximize a windowed game. |
| `--release-test` | `Main` | Run the release smoke test in place of the front end ([Release smoke](#release-smoke)). |
| `--classic-lobby` | `LobbyWorldDefault` | Lobby menu over the backdrop instead of the lobby world. |
| `--update-check`, `--update-url=URL` | `UpdateCheck` | Check for a newer release when run from the editor; ask `URL` instead of GitHub. |
| `--fake-version=X.Y.Z`, `--fake-protocol=N` | `BuildInfo`, `NetClient` | Pretend to be another game version or protocol in the Hello and in version checks. |
| `--net-lag`, `--net-jitter`, `--net-reliable-stall`, `--net-body-stall` (ms), `--net-loss` (%), `--net-seed=N` | `PacketTransports` | Wrap the transport in `FaultPacketTransport` when any fault is positive. |
| `--net-throttle-trace`, `--capacity-metrics`, `--tank-authority-trace` | `LobbyServer` | Server diagnostics: per-peer transport lines every 0.25 s instead of every 10 s; a process and transport metrics line; tank authority publications. |

The `LaunchArgsTests` unit suite checks the parser against the parser as it stood before it left `DevArgs`, and each
option above that a headless unit can reach through its real consumer. The native `server_tuning_tests` and `server_seed_tests`
scenes cover Main's server entry; the exported launches (ordinary start, `--server`, `--classic-lobby`, `--release-test`)
cover the rest. Add a new runtime option to `LaunchArgs`'s consumers, this table and that suite together.

### Release smoke

`--release-test` lives in `src/ReleaseSmoke` (`Propane.ReleaseSmoke`). Official export templates disallow scene
overrides, so an archive's own main scene hosts the test: `Main._Ready` adds `ReleaseTest` in place of the front end when
the argument is given and `Main` is a child of the root. `ReleaseTest` then builds a nested `Main`, presses the real menu
buttons and exits 0 on PASS and 1 on a missing button, empty suburb or stage timeout. With `--scenario=network
--connect=host:port` it creates a lobby, waits for `--players` (default 2) and plays a `--length` match with a bot.
It opens the roaming lobby's panel with a real Tab event, or uses the classic lobby menu when presented, then
verifies received final standings and return to that lobby. The roaming server can replace the match before its
local results timer invokes `Finished`, so that timer is not a completion prerequisite. After returning, actual
Tab/Escape events open and close the panel and a held W event must move the player. Stage deadlines accumulate
process delta; no game/network deadlines change. Its support code shares that directory and namespace:

| Class | File | Used for |
| --- | --- | --- |
| `ReleaseTest` | `ReleaseTest.cs` | The staged smoke run, its timeouts and exit codes. |
| `CaptureDirector`, `FrameEncoder` | `CaptureDirector.cs` | `--capture-dir` screenshots and `--record` with its fps and timeout. |
| `FrameMetrics` | `FrameMetrics.cs` | Optional `--frame-metrics` telemetry. |
| `Bot`, `IBotBrain` | `Bot.cs` | The bot that plays the network scenario's match for the first client. |

These helpers read smoke options through `LaunchArgs.Current`; they do not apply harness tuning or optimized-Debug
checks. Everything else they touch is ordinary game code. Dev harnesses reuse the same helpers.

Godot's release exporter publishes the **ExportRelease** configuration. `Propane.csproj` removes `src/Dev/**/*.cs`
from that configuration's compiler inputs, including Godot's generated script registration. The export presets omit
both `scenes/dev/*` and `src/Dev/*` resources, including empty script placeholders. No Dev types ship.
Debug editor builds, ExportDebug debug exports and local optimized Release builds keep Dev; `make bench CONFIG=Release`
and every scripted scene remain available. Local Release is not the shipping configuration.

`make lint` (also CI's lint partition) compiles ExportRelease with warnings as errors, without exporting;
a runtime reference to an excluded Dev type fails compilation. `make test-infra` additionally checks the source
boundary with no exception for Main or the smoke, and proves a failed shipping compile stops the gate. One assembly keeps
Godot's scene-attached scripts and resource identity in their existing project without another assembly contract.

Run these from an exported Linux build (`tools/release/build.sh` stages one in `build/package/linux`) to check the
launch options and the smoke on the real binary. The clients exit 0 on success; a server runs until stopped:

```sh
./Propane.x86_64 --headless --quit-after 300                  # ordinary start: the front end builds and quits
./Propane.x86_64 --headless -- --server --port=24683          # logs "listening on UDP 24683"
./Propane.x86_64 -- --release-test --capture-dir=/tmp/release # main menu and single player
# The network scenario supports the default roaming lobby and needs a second player in the lobby.
# To check the classic path too, repeat with --classic-lobby on both server and smoke client:
./Propane.x86_64 --headless -- --server --port=24683
./Propane.x86_64 -- --release-test --scenario=network --connect=127.0.0.1:24683 --capture-dir=/tmp/release
tools/dev/add_bots.sh -n 1 -S 127.0.0.1:24683                 # once the smoke has created its lobby
```

## Fast gate and deep sweeps

`make test` is the union of four targets. Each builds first, so it also works independently:

| Target | Assertions |
| --- | --- |
| `test-core` | Python timing/world-wrapper/score/vent/tank-agreement/launcher checkers, benchmark smoke, platform, movement, all unit suites, scripted mode rules (`mode_test`), normal shutdown and paused window-close witnesses |
| `test-behavior` | Native server admission overrides and vent correction, session actors and animation metrics, scoped worlds, blast completeness, upright piles, gather/races/appearance/real-game gather, melee collision/poses, navigation, menu composition/refresh and gamepad/devices/input/mouse/bots/King/lobby menus, grip/recovery, feet/corrections/events, movement/motion continuity |
| `test-generator` | 20 single-player and 2-player seeds, three each at 8/16/24/32 players, two forced seeds for every formation at all six sizes |
| `test-world` | All four saved formation plans built and hashed in two fresh processes |

The generator gate keeps every assertion and all 14 explicit pile counts (0–128), with fewer random layouts.
`make test-full` adds 200 single-player and 2-player seeds, 100 each at the four large sizes,
six forced seeds per formation/size, 100 navigation seeds, and the melee-reload pose sweep. It includes every seed formerly in the gate:
ordinary seeds 21–100, large seeds 4–10 and forced seeds 3–6 moved to this deeper sweep.

Each deep step is its own `test-full-*` target, so `make -j4 test-full` runs them side by side with the gate's four
partitions; [test-speed.md](evidence/test-speed.md#parallel-deep-sweeps) has the timings. `tools/dev/label.sh` prefixes every line
a deep job prints with its name (`[generator-3]`, `[lab-king]`, `[bots]`) and, on failure, prints that name, the exit
status and the command; make then names the failed target. The jobs:

| Target | Runs |
| --- | --- |
| `test-full-generator-1`…`4` | The generator sweep in four contiguous seed ranges: 50 single-player and 2-player seeds, 25 at each large size and segment stream, and two, two, one and one forced seeds |
| `test-full-generator-coverage` | `check_generator_shards.py`: the shards' `--list-seeds` listings hold exactly the 1,044 seeds of `--seeds=200 --large-seeds=100 --formation-seeds=6 --segment-seeds=100`, each once |
| `test-full-lab-*` | The full Lab sessions in 25 jobs: independent rounds/seeds, four lifetime shards, eight dial shards, both soaks; King keeps its cross-round mint assertion |
| `test-full-lab-coverage` | Every full Lab session, setting and seed and every dial fixture occur exactly once (550 cases, 514 of them dial fixtures) |
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
stalled client far more often ([test-speed.md](evidence/test-speed.md#parallel-deep-sweeps)). `-j` is not
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

Time-sensitive network unit tests use manual clocks. Server and match fixtures (`src/Dev/UnitTests/Fixtures`) share a
`ManualClock` for message receipt, ping estimates, sample timestamps and phase advancement. `Match.Clock` defaults to `MonotonicClock.Instance`
and can be injected before the node enters the tree, like the existing client and server clocks. Production uses the same process-relative time
as before. The server's gather deadline also uses its injected clock.

Tests advance time explicitly, including expiry before a phase tick. Do not sample wall time around a message and
then correct at the earlier sample: receipt can occur later, making the correction age negative. Under CPU load,
that used to extrapolate the authority test's observer backward far enough to fail its exact pose assertion.
The observer test now asserts its receipt timestamp and corrects at the same manual instant, after an explicit
30-second pause. Relay timestamp assertions now check exact clamped times; the gather deadline test checks
acceptance before and refusal exactly at expiry. No tolerance or retry masks scheduling delays.
The October 2026 regression proof (eight concurrent runs under CPU load, before and after) is in the
[unit time regression record](evidence/unit-time-regression.md).

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

## Network unit fixtures

Network unit suites stand on `src/Dev/UnitTests/Fixtures`, not on each other. Four fixtures keep separate identities
and setup defaults because their worlds differ: `ServerFixture` (two peers, one lobby, a started match, lobby world off),
`AuthorityFixture` (that match plus peer 2's client `Match`, relaying authority requests when the test says so),
`GatherFixture` (the same with peer 3's client, fed relayed messages directly) and `LobbyWorldFixture` (world on, no
match started). Add a new one there, built from the same mechanics, rather than borrowing another suite's, and do not
grow one configurable fixture.

The mechanics: `ManualClock` (test-owned time); `ServerDriver` (a hand-built `LobbyServer` fed bytes through its
receive handler, every sent packet decoded and recorded, `Advance` moving the clock and running the phase tick);
`Handshake` (the Hello a test client sends, the Welcome it receives); `ActionSequence` (action ids per sender);
`ActorMessages` (explicit actor-message header fields); `MatchClient` (a welcomed, unconnected `NetClient` and its client
`Match`, torn down in one order).

`ServerFixture.Receive` and `Reject` send exactly the bytes they are given, so malformed-packet tests build raw bytes. An
actor message needs the header a real client sends: take it from `ActorWriter` (a payload writer) or `ActorPacket` (an
encoded message), which use the peer's actor, life 1 and the next action id of that peer's sequence, or give every field
to `ActorMessages.Encode`. A packet with a zero actor id is rejected, as in production.

## Ledger accounting checks

`TankLedgerTests` compares `LiveTanks` with the parent implementation's scan over canonical records after each
scripted or seeded random operation. It covers mixed tanks/cars, refused atomic registrations, repeated refills,
claims, punctures, detonations and their causal refusals, motion, gather and transfers. Its per-sequence digests
include canonical record encounter order, lifecycle, authority and counts, for comparison with the immediate parent.
It runs in the ordinary discovered unit gate; no reference fixtures are regenerated.

After `make build`, opt into controlled getter and whole server stock-tick measurements:

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=TankLedgerTests --ledger-bench
```

The benchmark uses the real roaming-world ledger with 60 standing tanks and 0/4,096/16,384 tombstones, a manual
clock advanced by the stock interval (0.5 s), and a send sink. Each case warms 128 calls before 1,024 samples.
Getter samples batch 16 reads and report per-read costs; server samples run `AdvanceMatches`, including stock and
rule processing. It reports mean/p95/p99 microseconds and thread allocation bytes per read or tick. Alternate
parent/candidate builds with identical instrumentation for at least three pairs. Rendered runs can replace
`--headless` with `--disable-vsync --resolution 960x540`; this synchronous server workload does not measure GPU
render time or live socket delivery. See the [B1a evidence](evidence/ledger-count-verification.md).

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
See [the gate timing report](evidence/test-speed.md) for the measurements and the CI estimate.

## Match player selection

`MatchPlayerTests` uses `Fixtures/PlayerRosterFixture` to drive real actor insertion/removal and lobby profile
replacement, checking the cache against the previous ordered LINQ rule after each mutation and during native
character retirement. It covers Human preference, bot fallback, empty ownership, authority handover, and a rebuilt
roster with changed actor ownership. An empty live roster is a fixture-reachable state; normal assembly rejects it.
`scenes/dev/match_player_benchmark.tscn` instead measures 2,000 whole Match presentation callbacks with
eight owned bots preceding the Human, after at least 180 real frames and five seconds of warm-up. Actors and
camera stay fixed. It reports managed bytes per callback and mean/p95/p99 microseconds; run identical
instrumentation on alternating parent/candidate builds, headlessly and rendered, for comparisons.

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/match_player_benchmark.tscn
godot-mono --disable-vsync --resolution 960x540 --fixed-fps 60 --path . res://scenes/dev/match_player_benchmark.tscn
```

## Server run differential probes

`tools/dev/server_equivalence.py --revision=REV --label=NAME [--observe]` builds a private runtime below
`build/NAME/project`, using that revision's production sources and the current server proof harness. It does not
change the checkout or release references. With `--observe`, the scratch server records every incoming/outgoing
packet at its receive/send boundary when given `--server-transcript=ABSOLUTE_PATH`: direction, peer, traffic class
and exact bytes, in call order. Use a fresh file and `--match-seed=23` for each ServerTests, ParticipantServerTests,
KingOnlineTests, LobbyWorldTests and LobbySupplyTests run; compare the parent and candidate files byte for byte.

Without observation, `--suite=ServerRunBenchTests --server-bench --match-seed=23` measures production receive,
relay and whole run ticks for Bank, King and roaming. Each measured tick delivers one actor pose and eight body
states. Time advances from the fixture clock, so match clocks and King actor sampling remain active. Packet
preparation and 20,000 warm-up ticks are excluded; 30,000 ticks are measured per mode. Alternate at least three parent/candidate pairs. The report
includes allocations, mean, p95/p99, packet counts and payload bytes; packet instrumentation stays off.

## Mode integration inventory

`ModeInventoryTests` asserts shipped network, lobby and prototype order. After `make build`,
`godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ModeInventoryTests --mode-inventory=ABSOLUTE_PATH`
writes an immediate-parent comparison inventory: IDs, eligibility, factory types, tuner order and all supply settings
for six actor counts and each supply multiplier. Compare parent/candidate files byte for byte; this is separate
from the immutable release references.

`ServerModeTests` additionally checks the actual compiled server projections through `Fixtures/ServerModeFixture`:
metadata/schema, eligibility/order, 192 network supply cases against the definition policy, live rule tuner copies,
scoped-factory capture, prototype exclusion and isolated initial overrides. A separate real-run assertion checks
plain pile observations after native tuning changes and zero allocation over 1,000 warmed reads. The fixture
observes the constructor-injected callbacks without generating hundreds of suburbs; ordinary server/transcript
fixtures retain the end-to-end path.

## Actor retirement probes

`ActorLifetimeTests` checks live-world registration/cache counts, character/runtime/registration collection,
callback removal, stop-time reentrancy and cleanup failures. `ActorTacticsTests` compares complete intent,
status, RNG and query transcripts across a native rival departure in Banking combat/cover and King points.
`ActorTickBenchTests --actor-tick-bench` is opt-in: 20,000 warm-up and 60,000 measured whole Match plus
owned-character physics callbacks on stationary native bodies; it reports allocation, mean, p95/p99 and workload.
These controlled callbacks do not advance Jolt and do not replace native network runs.
`BotTacticsTests --tactics-only=bank/Normal/combat,bank/Normal/cover,king/Hard/points --tactics-bench`
selects complete service/brain/read workloads for paired measurements amid shared compilation. Omit the selector
to retain all 24 cases; an unknown/empty selection fails the discovered-suite coverage assertion.

`tools/dev/actor_equivalence.py --revision=REV --label=FRESH_NAME` prepares isolated identical packet/actor
ordering observers under `build/`. Run existing roster/departure suites with `--actor-order --match-seed=23`
and `--server-transcript=ABSOLUTE_PATH`; compare packet files exactly and the `[actor-order]` lines. Native
departure runs use the scratch project's copied `tools/dev/net_bots.sh`; peer numbers in actor-order lines are
normalized to the initial peer order. `--actor-tactics-transcript=ABSOLUTE_PATH` writes the focused departure
decisions. No release fixture or checkout production source is instrumented.
The prepared project's `ActorLifetimeCycleProbe` transplants the exact gate cycle block, so both revisions run
the same human/remote/bot inputs in roaming, Bank and King. Pass `--actor-lifetime-parent` only on the parent
to characterize its known retention and attached handlers; the candidate requires flat counts, detached handlers
at native free and collection. Native signal observations are asserted after `Free` returns so failures exit 1.

## Decode and dispatch probes

`DispatchBoundaryTests` and `DispatchTransportTests` exercise the production receive, tick, departure and inbox
replay boundaries, native run-resource retirement and continued service in another lobby. `Fixtures/NetLogCapture`
observes the actual Godot diagnostics; `Fixtures/InjectedPacketTransport` throws before or after the real inner
adapter accepts bytes, on clean loopback and delayed/jittered transports. Cleanup-observer and persistent-send
failures must retain their own causes without suppressing normal retirement. The ordinary discovered gate runs
these assertions. `TransitionBoundaryTests` also exercises completed source departures followed by construction
or destination admission failure, including a synchronous roster change before client membership attachment.
The initial harness commit's `--dispatch-parent` run explicitly characterized the old defect;
on the corrected checkout that flag is a deliberate negative expectation, not an acceptance mode.

`DecodeContextTests` checks injected schema/eligibility/tuner inputs, scoped catalog restoration and sequence
consumption before expected run admission rejection. `DecodeTranscriptTests` enumerates every prefix of every
protocol fixture plus doubly invalid inputs, and writes first-rejection type/text when given
`--decode-transcript=ABSOLUTE_PATH`. `server_equivalence.py` copies this same source into either revision for an
exact immediate-parent comparison. No frozen reference changes.

The same helper copies `ClientReceiveBenchTests`. Pass `--suite=ClientReceiveBenchTests --client-receive-bench`
to measure 20,000 warm-up and 30,000 whole client network ticks: decode/dispatch one actor pose, eight body states
and one lobby list, then both existing poll callbacks. It asserts 90,000 decoded packets and 60,000 deliveries to
the attached receiver, and reports thread allocations and mean/p95/p99 microseconds. Preparation is excluded;
physical Match handling and GPU frame time are outside this connection-boundary workload. Both network benchmarks
accept `--network-bench-long` for 300,000 measured ticks, keeping the same 20,000 warm-up ticks. `cpu_us` is
process CPU time per tick (including runtime/GC threads), observed outside the allocation/sample loop; it helps
separate CPU work from wall-time preemption. Pair with the server
workload above, alternate at least three parent/candidate pairs, and retain machine-load observations.
