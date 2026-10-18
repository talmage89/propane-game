# Match player cache verification

**Status:** Complete. Current contract: [multiplayer participants](../multiplayer.md#participants-a6-protocol-2).

2026-10-17, `wp/rf-m0-player-cache`, parent `d54d3df` (v0.4.2), implementation `b5ea042`, harness `bbd2afa`.
CachyOS/Linux, GTX 1660, Godot 4.7.2 .NET, SDK 10.0.113, Debug.

## Scope and lifecycle

The selection remains the first owned Human in session actor order, otherwise the first owned actor.
`SessionContext.ActorsChanged` refreshes Match's field synchronously when list membership changes, before native
actor initialization or retirement. The only subscriber is Match. A lobby profile Kind change also refreshes it.
Stopping clears it before actors are freed. No processing callback, gameplay rule, tuner or wire format changes.

`IsRemote` is init-only on PlayerCharacter. Actor ownership is fixed for that character's lifetime; changing body
host or lobby creator is not actor handover. A new Match builds the new ownership mapping. Updating a lobby profile's
OwnerPeerId alone does not change an existing character's IsRemote, exactly as before this change.

Normal match construction rejects an empty owned roster; network admission also requires a local owned actor.
Lobby reconciliation protects the selected character, and the server sends PlayerLeft only to surviving peers.
Lab constructs LabSession, not Match. An empty roster nevertheless exists before assembly and after retirement,
and public Session.RemoveActor can reach it in a live fixture. The unit suite exercises that formerly crashing
state and a synthetic owned-peer departure. Cache refresh happens before native TreeExiting callbacks, not a frame
later. Session invalidation, disposal, failed assembly and Match replacement all retire the cache with the session.

With no actor, UpdateHud skips local presentation, results and King pile reporting skip, score/hit feedback
comparisons do not match, MyScore and LastHitBy return zero and LastHitTime is negative infinity. PresentHud uses
actor id zero if asked without a player; view creation, reveal centering and lobby floor facing skip their local
actor work. Public Match.Player retains its required-actor contract, with an explicit InvalidOperationException.
Menus still permit leaving; per-actor simulation and replication keep their existing iteration/order.

## Focused proof

`MatchPlayerTests` uses `Fixtures/PlayerRosterFixture`, a real Match with the previous ordered LINQ selection as its
oracle (returning null for the empty roster, where the old production fallback threw). It drives Human/bot additions and removals in both orders, profile replacements, bot-only initial assembly,
empty ownership and both frame callbacks, results, authority handover, ownership on rebuild, and retirement.
It checks after every mutation and during native character TreeExiting. The fixture invokes Match's existing
private insertion/profile entry points; it does not introduce production test setters. Existing lobby tests cover
packet-driven reconciliation. MatchClient accepts a Node fixture owner so the same setup can drive a frame-based
benchmark without running the unit runner.

## Measurements

The frame-based `scenes/dev/match_player_benchmark.tscn` controls ten actors: eight owned bots before the Human,
plus a remote Human. It keeps actors/camera fixed and invokes the complete Match._Process callback once per actual
scene frame. At least 180 frames and five seconds precede 2,000 measured callbacks. Time and managed thread bytes
bracket only Match._Process; the probe verifies a live session and local view. This measures Match presentation,
not whole-engine/GPU frame time or physics. Pickup feedback and event-driven comparisons are covered by the
regression/native suites.

Parent and candidate use the same frame measurement code and workload. Only Match's four modified files and SessionContext are swapped
from the parent or candidate copies, then make build runs before each process. Three pairs alternate parent then
candidate, first headless, then rendered at 960×540, with --fixed-fps 60 and rendered --disable-vsync.

Early synchronous unit-runner measurements are retained under build/m0/pair-* and render-pair-*; they mix renderer
startup compilation with measurement and are not steady-state evidence. A five-second synchronous warm-up attempt
(steady-render-pair-*) was also rejected: actual frames must run to warm native rendering. Its last build caught a
missing namespace import in the newly added benchmark, corrected before the frame-based runs. One completed
rendered unit run left a busy gamescope wrapper after the Godot child exited 0; that owned wrapper was terminated.
No machine configuration changed.

Headless frame-based pairs (`build/m0/frame-headless-pair-*`, microseconds per callback):

| Pair | Parent mean / p95 / p99 | Candidate mean / p95 / p99 | Parent / candidate bytes |
| --- | --- | --- | --- |
| 1 | 37.872 / 28.2 / 38.6 | 12.236 / 16.6 / 26.4 | 2840 / 256 |
| 2 | 14.681 / 15.5 / 18.1 | 8.556 / 9.3 / 14.4 | 2840 / 256 |
| 3 | 27.331 / 27.7 / 38.5 | 10.701 / 14.9 / 17.1 | 2840 / 256 |

Allocations fall by exactly 2,584 bytes per callback (91%). Headless mean and both tail percentiles improve in
every pair. The first parent mean exceeds its p95; no samples are trimmed from the aggregates.

Rendered frame-based pairs (`build/m0/frame-render-pair-*`, 960×540, microseconds per callback):

| Pair | Parent mean / p95 / p99 | Candidate mean / p95 / p99 | Parent / candidate bytes |
| --- | --- | --- | --- |
| 1 | 54.678 / 70.1 / 92.9 | 35.617 / 43.9 / 48.2 | 2840 / 256 |
| 2 | 51.718 / 67.2 / 86.8 | 52.632 / 64.3 / 88.8 | 2840 / 256 |
| 3 | 49.826 / 65.0 / 79.1 | 36.928 / 46.4 / 66.5 | 2840 / 256 |

Rendered mean averages 52.074 → 41.726 µs (20% lower). Pair 2's mean is 1.8% higher and p99 is 2.3% higher;
p95 improves in all three pairs. There is no repeatable regression. Allocations fall by the same exact amount in
every pair. Godot reports that gamescope does not support disabled V-Sync and falls back to enabled; the flag was
passed as required. The metric is CPU time inside Match._Process, not the presentation interval. Gamescope also
prints its existing DRM/xkb startup diagnostics; the final frame-based engine logs contain no `ERROR:` lines.

## Mutation points

| Point | Source |
| --- | --- |
| Initial roster and profile insertion | `Match.Assembly.cs:93` BuildActors, `:108` AddActor; profiles are installed before Session.AddActor. |
| Live lobby arrival/removal | `Match.Lobby.cs:121` ReconcileRoster, `:142` AddActor, `:164` RemoveLobbyActor. |
| Profile replacement | `Match.Lobby.cs:154` UpdateLobbyActor; Kind is the only profile field used by selection. |
| Peer departure | `Match.cs:1014` PlayerLeft removes every actor owned by that peer. |
| Underlying list mutation and native freeing | `SessionContext.cs:54` AddActor, `:74` RemoveActor; notifications at `:68`/`:80` precede native callbacks/freeing. |
| Stop, disposal, construction failure | `Match.Assembly.cs:32` registers StopReplication (`Match.cs:186`); SessionContext Stop/Dispose at `:119`/`:134`, SessionFactory catch at `:47`. |
| Rebuild/replacement | `Main.cs:281`/`:315` construct fresh Match/lobby Match; `:370` EndMatch disposes the previous session. |

`PlayerCharacter.cs:123` declares IsRemote init-only. `NetClient.cs:412` requires owned membership at admission;
`LobbyServer.cs:453` Depart removes the departing peer before sending PlayerLeft to survivors (`:469`–`:474`).
There is no live actor-ownership handover path to hook. Profile replacement that keeps Kind does not change the
selection predicate, even if OwnerPeerId differs; preserving that behavior is intentional.

## Commands and gates

`make setup` imported all three new scripts and generated their committed `.uid` files. `make build` ran before
every benchmark process; the final setup build and make check's full warnings-as-errors build each reported
zero warnings and zero errors. `dotnet format Propane.sln --no-restore --include ...` formatted only changed C# files;
make check independently verifies formatting for the entire project.

```sh
godot-mono --headless --path . scenes/dev/unit_tests.tscn -- --suite=MatchPlayer
make test-equivalence
make check
godot-mono --headless --fixed-fps 60 --path . scenes/dev/match_player_benchmark.tscn
godot-mono --disable-vsync --resolution 960x540 --fixed-fps 60 --path . scenes/dev/match_player_benchmark.tscn
```

The focused run passes three tests. The full gate's unit runner passes 495 tests, zero failures. The focused
names are ordered human preference/fallback/removal/profile replacement; bot-only assembly and view ownership;
and authority handover/rebuilt actor ownership. Direct removal is checked during native retirement, and synthetic
PlayerLeft also removes the final owned actor. Initial bot-only selection gets a view; arrival of a Human changes
selection without retargeting that existing camera/input view, preserving the old behavior.

The release reference command passes wire/schema/world/publication and all five exact Lab cases: Bank, King,
spill-chain, Short Fuse and Dissolve. References were neither regenerated nor modified. Logs are
`build/m0/unit-final.log`, `equivalence.log`, and `check.log`; detailed release outputs are in
`build/rfp0-equivalence-7`. The original focused fixture attempt incorrectly used the pre-guest MatchSetup;
its missing-remote assertion failed. The fixture now builds from the current lobby roster, and the assertion remains.

The full unit gate intentionally prints two capture-save errors and the existing malformed Score warning in
CaptureTests/LifecycleTests. They are expected negative probes, not compiler warnings or native soak errors.
The frame benchmark is opt-in and adds no work to make test beyond the three focused lifecycle tests.

## Native runs and reviewed captures

Every run uses four independently connected clients, 90 seconds, one rendered client at 960×540, a fresh output
directory and all launcher checks enabled. Commands:

```sh
tools/dev/net_bots.sh -n 4 -l 90 -w 1 -t clean -s '--match-seed=23' -o "$PWD/build/m0-net-clean" -- --capture-dir="$PWD/build/m0-clean-shots" --shot-every=30
tools/dev/net_bots.sh -n 4 -l 90 -w 1 -t faults -s '--match-seed=23' -o "$PWD/build/m0-net-faults" -- --net-lag=120 --net-jitter=30 --net-loss=3 --net-seed=1 --capture-dir="$PWD/build/m0-faults-shots" --shot-every=30
tools/dev/net_bots.sh -n 4 -l 90 -w 1 -t clean -s '--match-seed=24' -o "$PWD/build/m0-net-lobby" -- --lobby-bots=2 --lobby-roam=3 --capture-dir="$PWD/build/m0-lobby-shots" --shot-every=30
```

Clean exits 0: 2,000 grids, 128,143 body pairs, zero unobserved grids and zero disagreements. Longest
above-tolerance episode is 0.25 s; final resting/moving drift is 0.000/0.103 m. Complete audit:
`0 initial + 36 map + 61 drops + 0 minted - 64 spills = 33 banked`; all four clients agree. Zero engine/protocol errors.
Reviewed `build/m0-clean-shots/Bot1_{030.0,060.0,090.0}.png`: correct local orange actor/camera, highlighted
Bot1 standing and matching bank total, ammo, readable remote tags, suburb and blast smoke/fire. The initial
`000.0` capture is not used as visual acceptance evidence.

Faulted exits 0: 2,000 grids, 114,748 body pairs, zero unobserved grids and zero disagreements. Longest
above-tolerance episode is 1.10 s; final resting/moving drift is 0.004/0.220 m. Complete audit:
`0 initial + 36 map + 38 drops + 0 minted - 52 spills = 22 banked`; all four clients agree. Zero engine/protocol errors.
Reviewed `build/m0-faults-shots/Bot1_{030.0,060.0,090.0}.png`: the local orange actor remains the camera target,
bank total matches the highlighted Bot1 entry (2, 4, 1), ammo remains visible, and the final capture follows its
ragdoll at ground level while suppressing the crosshair. Remote score tags and score popups remain readable.

Client-owned lobby bots exits 0: four clients, twelve actors, two bots owned by each client, with roaming before
and after the match. Bot1's log records both accepted bot additions and the twelve-actor rebuild. Tank comparison:
1,999 grids, 513,008 body pairs, zero unobserved grids and zero disagreements; longest above-tolerance episode
1.10 s, final resting/moving drift 0.001/0.116 m. Complete audit:
`0 initial + 64 map + 119 drops + 0 minted - 167 spills = 16 banked`; all four clients agree, including all twelve
actor scores. Zero engine/protocol errors. Reviewed `build/m0-lobby-shots/Bot1_{030.0,060.0,090.0}.png`:
Human Bot1 remains selected, with center bank 15/11/6 matching its highlighted entry rather than either owned
bot; bot names, remote tags, ammo, smoke/fire and the local camera remain visible and coherent.

A post-cleanup scan of all fifteen native logs again found zero engine/protocol errors
(`build/m0/error-scan.log`). Launcher summaries are `build/m0/net-{clean,faults,lobby}.log`. No failed native run,
skipped checker or changed threshold is omitted.

The first full make check passed, including all four fast partitions, four formation hashes in separate processes,
five runs of twelve segment hashes and all gate self-tests. Review then found the focused authority case was
assigning the host it already had. The test now first sets a remote world host, checks unchanged selection, removes
that remote peer through PlayerLeft, and asserts host transfer back to peer 2 with the same local actor. This is a
harness assertion improvement only. The production implementation and measured callback are unchanged.
`make build` and the strengthened focused run pass with zero warnings and three tests/zero failures
(`build/m0/build-final.log`, `unit-handover.log`). The final repeated make check passed completely: zero compiler/analyzer warnings, 495 unit tests with zero
failures, every scene suite (including real device input), generator sweep, separate-process world/segment
comparison, and every gate self-test. Its log is `build/m0/check-final.log`.

The delivered harness also ran against the immediate parent's five production files. The parent exits 1:
three tests, one failure, with `Sequence contains no elements` when the final owned actor is removed while a
remote actor remains. The other two cases pass. Restoring the committed candidate and rebuilding yields exit 0,
three tests and zero failures; git diff confirms all five production files exactly match the committed versions.
Logs: `build/m0/compare-unit.log`, `unit-parent.log`, `unit-restored.log` and their build logs. Both builds are
warning-free. To reproduce, use harness commit `bbd2afa` atop parent `d54d3df` and implementation `b5ea042`;
the parent empty-ownership regression is intentionally expected to fail. No branch switch or reference refresh
was used for these comparisons.

## Limits and compatibility

The only production file outside Match is SessionContext: four lines for a narrow live-roster notification,
needed so direct session removals refresh before native freeing. No authority transaction or mode policy moved.
The shared fixture MatchClient accepts a Node owner for the opt-in frame benchmark; its disposal order is unchanged.
The benchmark's processing node exists only in that development scene. Ordinary game trees and processing order
are unchanged. Future live actor ownership mutation would need an explicit refresh; IsRemote is currently init-only.
Public Match.Player still requires an active owned actor; absence-aware presentation uses the nullable private field.

Verification is Linux/Debug. Windows/macOS and optimized Release performance were not measured. The controlled
frame probe measures Match CPU work; captures are visual smoke checks, not a pixel-difference oracle. The focused
selection oracle supplements release references where they do not exercise this cache.

Messages, bytes, generation, tanks, tuners/classification, rules, RNG, traversal and camera/input targets are
unchanged. Protocol.Version remains 3; no minor release is needed for this local cache. No design or release
reference file changed. All raw logs and captures remain under build; no large recording/baseline copy was created.
