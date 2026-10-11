# Independent network integration review

Integration starts at `4a03c3d`, merging dev `d2aa9c9` into `wp/net-assert`.
The sole textual conflict was NetTest's loading-stall fixture beside A4's headless
actor assembly. Both remain. Match's five small integrations retain their behavior
on A4: nonpredictive gather leases, receipt-time authority targets, and per-body
checkpoint lane batching. No session, actor, view or logical-aim redesign was needed.

## Disconnect latency

The branch's unconditional 20/30 s ENet ACK timeouts regressed live-match departure.
Dev used a 3/8 s client timeout and the server's native default; the loading fix had
extended both endpoints permanently. A crashed player's actor and ownership would
therefore linger through a substantial part of a short round.

`IPacketTransport.SetLoading` now extends just the constructing connection before
MatchStart is sent and before the client's construction callback runs. Loaded
restores 3/8 s at both ends, even if it arrives after the loading deadline and Go.
Leaving construction restores it too. The fault decorator forwards the policy.
Only construction can retain the 20/30 s parameters; explicit disconnect remains
immediate. [Godot's timeout contract](https://docs.godotengine.org/en/stable/classes/class_enetpacketpeer.html#class-enetpacketpeer-method-set-timeout)
describes ACK deadlines checked on ENet's retry schedule, so the maximum parameter
is not an exact stopwatch guarantee.

The unit regression exercises the production client/server through decorators,
including a late Loaded after Go and leaving without Loaded. The completed suite
passed 226 tests with zero failures; make build had zero warnings/errors.

The native regression tool stops a separate process group during play (SIGSTOP),
sending neither Leave nor disconnect. It fails if detection exceeds 10 s. Logs
remain under build. Historical copies were built inside this worktree with
`git archive <revision>`, the same tuning.tres, and `make setup`.

| Revision | Silent endpoint | Detection |
| --- | --- | ---: |
| dev `d2aa9c9` | client | 6.311 s |
| dev `d2aa9c9` | server | 3.349 s |
| integrated branch before fix `4a03c3d` | client | 25.208 s |
| loading-specific grace | client | 3.972 s |
| loading-specific grace | server | 8.350 s |

Commands:

```sh
TMPDIR="$PWD/build/vna-scratch" make build
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn
python3 -B tools/dev/check_liveness.py --project="$PWD/build/vna-dev-baseline" --output="$PWD/build/vna-liveness-dev-client" --maximum=35
python3 -B tools/dev/check_liveness.py --project="$PWD/build/vna-branch-baseline" --output="$PWD/build/vna-liveness-branch-client" --maximum=35
python3 -B tools/dev/check_liveness.py --output="$PWD/build/vna-liveness-fixed-client"
python3 -B tools/dev/check_liveness.py --output="$PWD/build/vna-liveness-fixed-server" --silent=server
tools/dev/net_bots.sh -n 2 -l 35 -s '--start-score=5' -o "$PWD/build/vna-load-stall" -- --scenario=melee --tank-check-load-stall=Bot2
```

The nine-second construction stall survived: 899 comparison grids, zero unobserved
pair grids, peak rest/moving drift 0.010/0.491 m, final 0/0 m, and both score audits
agreed (10 initial minus 5 spilled equals 5 banked). The bound and tuner defaults
were unchanged. The native probe's first attempt used melee assertions while
deliberately stopping its victim; that invalidated its unrelated gameplay fixture.
The final probe uses ordinary simple bots. Its server-silence condition observes
either client, since the first timeout can cause that client to quit before the
other times out.

## Vent correction

BodySync wrote the owner's velocity plus correction steering before native integration,
then the observer tank applied another jet force using its own delayed vent age and
orientation. Fresh observers now integrate the corrected velocity without that
speculative impulse or spin clamp. Presentation and vent age still advance. Stale
observers, predicted local impulses and actual owners retain their original jet
physics. Claims and local grants clear suppression immediately, before another
correction tick; a nonpredictive gather lease continues observing the remote owner.

The native `body_correction_tests` fixture uses baked tuning, zero gravity/damping
and a continuously fresh zero-velocity publication to isolate the extra impulse.
The identical test fails before the change at 0.127 m/s and passes afterward at
0.000000 m/s. Ordinary owner peak speed is 2.338 m/s; stale, predicted and granted
ownership all resume thrust. This regression is in `make test`. Both new fixtures
were copied into the archived integrated branch for the before measurements;
its production replication and tank physics remained unchanged.

The `vent-contact` network fixture puts eight owner-started vents on clear ground,
sets their initial spin to the existing VentMaxSpin, and records actual contacts.
Two 30-second 120/30/3 captures, using the same fixture and saved tuning, show:

| Copy | Vent pair grids after first six vent seconds | Median / p95 / p99 / maximum drift | Final drift |
| --- | ---: | --- | ---: |
| Before force suppression | 5,112 | 0.111 / 0.235 / 0.293 / 0.448 m | 0.218 m |
| After force suppression | 5,128 | 0.107 / 0.213 / 0.267 / 0.411 m | 0.159 m |

The owner had floor contact in 718/768 and 726/776 contact samples; peak speed was
1.752/1.701 m/s and spin 30.576/30.594 rad/s. Observer contact histories differed,
confirming native collision integration still adds disagreement. These captures
show a reduction, not a proof that every historical contact trajectory is resolved.
No fault/clean final bound (0.50/0.35 m), resting bound (0.10 m), gameplay rule or
tuner was changed. Rapidly spinning floor vents remain subject to the same final
bound as free flight. Broader final-code soaks are recorded below.

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/body_correction_tests.tscn
tools/dev/net_bots.sh -n 2 -l 30 -s '--tune.FixedSeed=23' -o "$PWD/build/vna-contact" -- --scenario=vent-contact --tune.FixedSeed=23 --net-lag=120 --net-jitter=30 --net-loss=3 --tank-motion-trace
```

## Replication retirement and completion

The first batch after the force fix passed two faulted captures, then failed fault 3:
final drift 0.656 m for `10000005` and 0.564 m for `1000000b`. This failure remains
in `build/vna-final-fault-3-run.log`. Owner sequence advanced from 891 to 905 after
the server returned the lobby to Waiting, while all observers stayed at 891. Their
final quiet times reached 0.487/0.764 s; moving correction expires at 0.5 s.
The server's results deadline had retired its ledger before the clients' delayed
results timers completed. Comparing ongoing vent physics afterward measured a
world that no longer had replication, not an owner stream the corrector could follow.

The probe now closes its live trajectory on receipt of Waiting, or scene completion
if that happens first. It separately records actual completion; the checker still
requires exact IDs/states and the original 0.10 m intact resting bound there.
Intact bodies still moving at completion also retain the original 0.50/0.35 m
fault/clean moving bound; retirement does not exempt intact positions.
The original 0.50/0.35 m motion bounds apply to the last common live grid. No gameplay
timer, server retirement rule, motion predictor or tolerance changed.
New-format captures require `tank-final` followed by `tank-completion`, and reject
later trajectory rows. Legacy captures retain their original completion-time check.

For an offline regression, the same fault-3 positions were retained through the last
recorded snapshot before Waiting, with the original exact completions retained
separately. That replay passes at final 0.001/0.422 m, with the same 93 unobserved
pair grids and 1.95 s longest episode. Three synthetic regressions also prove that
post-retirement vent positions cannot extend the trajectory, live final motion
still fails at its original bound, missing completion/later trajectory data fail,
and exact completion identities/resting errors still fail. All 23 comparer tests pass.

## Six-player admission fixture

The initial six-bot attempt timed out without a match: the sixth bot received
"That lobby is full." `--tune.MaxPlayers=6` reached neither Main's server entry nor
its lobby admission; only the dev client scenes applied command-line tuners.
Debug server startup now applies its explicit overrides before listening. Release
startup and every default remain unchanged. The native `server_tuning_tests`
fixture uses the production Main entry without calling DevArgs.Setup itself:
before the fix it fails at advertised capacity 5, afterward it advertises 6 and
passes. It runs in `make test`, and the gate proof includes its invocation.
The repeated six-bot/90-second 120/30/3 soak passes: 1,997 grids, 1,228,329 body
pairs, zero unobserved pair grids, final rest/moving drift 0/0.267 m, longest
episode 0.85 s, and all six score audits agree. Subsequent builds have zero warnings/errors.

## Ordering, compatibility and capacity review

Control, lifecycle authority and final rests retain reliable logical channel 0.
Body checkpoints rotate independently per body through reliable logical channels
1–3; the relay preserves the incoming class. Godot reserves native channels for
its default modes, so these are logical transfer channels, not ENet's raw indices.
The native `body-stream` fixture blocks the first checkpoint lane for two seconds
and drops every outgoing disposable sample: the other checkpoint lanes still
correct the observer. Checkpoints replace ordinary motion publications; they do
not add a second stream or full-world snapshots. Cross-lane arrival order cannot
replace the epoch/sequence comparisons. Reliable control still has head-of-line
blocking; rotating body lanes does not make ownership changes bypass it.

Protocol.Version remains 2, which is unreleased. No message changed shape in this
integration. Native probes reject protocol 1 and a different game minor version
before constructing a match; the rendered version rejection offers the download
button. Build hashes remain diagnostic, deliberately allowing patch-compatible
builds of a minor version to play together. Release this branch's shared
replication changes as a minor release, not a patch.

The fault decorator bounds each connection's incoming and outgoing queues to
4,096 packets/4 MiB each; loading inboxes have the same bounds. Future motion is
one latest sample per registered body. Failed required sends disconnect rather
than silently losing reliable authority. Godot does not expose the native ENet
send queue's size or admission cap; accepted reliable packets under sustained
congestion therefore have no demonstrated memory bound here. Native retry
timeouts address silent peers, not all congestion from an acknowledging peer.

Owner publication cost scales with moving bodies; server relay cost multiplies
that by recipients. A BodyState is 64 payload bytes: 100 awake bodies at 15 Hz
and 16 players would relay about 1.44 MB/s before batch headers, UDP overhead
and retries. Quarter-second checkpoints reserve about 0.384 MB/s of that traffic,
rather than adding it. Tank counts also grow with player count, making the
combined growth approximately quadratic. Native six-player admission is proven;
sixteen-player admission is not supported by the existing eight-player
profile/codec limits. The 16-player suburb generator sweep is not a substitute
for a 16-player network test. Raising those limits belongs with participants
work and requires its own compatibility and performance verification.

## Gate and soak evidence before the cadence fix

This verification used production revision `fc76127`.
`TMPDIR="$PWD/build/vna-scratch" make check` and `make test-full` both pass.
The warning-as-error build reports zero warnings/errors, the unit suite has
226 passing tests, and the tank comparer has 23 passing tests. The deep upright
fixture covers 53 piles/738 tanks for 120 simulated seconds, maximum drift
0.001669 m/tilt 0.151 degrees; stand, topple and gather assertions pass. Ten
single-player and ten solo-match lifetime cycles leave no orphans. Generator
sweeps cover 200 ordinary/2-player seeds, 100 each at 8/16/24/32 players and
six seeds per forced formation/size. Navigation passes 700/700 courses with
zero stuck events, mean 3.20 s, and 600 jumps. Complete logs are
`build/vna-check-final.log` and `build/vna-test-full-final.log`.

The first final-code soak batch overlapped that gate. Seven of nine completed
faulted runs fail the unchanged two-second agreement window, despite all nine
passing final position and score checks. Longest episodes are 3.30, 1.30, 2.85,
2.80, 3.95, 0.60, 4.45, 4.30 and 2.05 seconds. Capture gaps account for at most
127 pair grids; these are real observed disagreements, not missing-data failures.
Fault 7 includes intact moving drops as well as vent lifecycle differences;
it must not be dismissed as a floor-vent-only residual. For drop `20100008`,
Bot2 observes epoch 2 near monotonic time 106486, while Bot1/Bot4 still have
the previous owner until roughly 106488. The owner continues publishing in
between. Median FPS for these peers is 46/33/25/15.5; median native RTT is
160.5/195/231/385 ms. This implicates delayed reliable control delivery under
load but does not prove a unique cause or excuse the failures.

The owned launcher was stopped between completed captures, without discarding
their logs (`build/vna-proof-fault-1` through `-9`). The next matrix runs
serially after the entire deep gate, preserving the same tuning, fault flags,
limits and traces in separate `build/vna-isolated-*` directories. Its results
are eight passes and one failure before stopping between captures for another
fix. Do not substitute the earlier ten passing
captures from before the immediate ownership flag resets for this final matrix.

## Publication clock regression

The isolated batch also fails fault 7, proving that overlapping the gate does not
explain every failure. Its 2.10-second episode for `10000010` begins with position
error (peak 5.048 m), then includes a brief predicted/confirmed explosion lifecycle
difference. Median FPS is 56/57/56/54. The owner advances only sequence 26 to 28
between monotonic times 109058.586 and 109059.931. Its frame reports 47.4 ms of
physics work; observers pass the 0.5-second receipt-silence deadline and resume
local vent simulation. Final rest/moving drift is 0.012/0.178 m and scores agree,
but neither excuses the persistent earlier disagreement.

Body publication and reliable checkpoint timers previously accumulated simulation
delta, while receiving freshness and sampling used monotonic time. Slow physics
therefore stretched a network deadline. Collect now takes Match's existing
monotonic clock. The existing tank/car send rates and checkpoint period are
measured in real time, and missed intervals do not create extra publications
during catch-up ticks at the same network time. Simulation timing, gameplay,
message shapes, tuners and agreement bounds are unchanged.

The regression invokes the production Match physics path for 60 ticks whose
network clock advances 0.1 s while simulation delta advances only 1/120 s. The
old code fails because due publications disappear; the fix passes, keeps reliable
checkpoint silence below 0.35 s, and emits no extra samples over eight catch-up
ticks with no clock advance. The full unit suite passes 227 tests, zero failures,
and make build has zero warnings/errors. These prove the timing defect and its
correction, not that this was the isolated soak's only cause. Subsequent native
verification must still pass the unchanged agreement limits.
The first native repeat uses the failed run's map seed, 1341647191, with the
same 4-bot/90-second 120/30/3 schedule. It passes 1,998 grids/342,637 body
pairs with no unobserved grids, final drift 0/0.194 m and a 0.95-second
longest episode; all score audits agree. This repeats the layout, not the
real-time action schedule, so it does not establish a deterministic replay.

The completion review found that the retirement comparer initially enforced
only the resting intact position bound. A fourth synthetic retirement regression
shows that a 0.75 m intact moving disagreement was incorrectly accepted at actual
completion (including mixed resting/moving classification). The unchanged original
moving bound is now applied to those intact positions too. The regression fails
before this correction and all 24 comparer tests pass afterward. Vent motion
after server retirement remains separate from the live, bounded trajectory.


## Offset mass-center correction

The first final batch after the publication-clock fix passes four of five faulted
captures, but fails fault 3 at a fresh final vent (`20300004`, 0.799 m).
There are no unobserved grids or persistent episodes beyond 0.85 s. Owner sequence
407 versus observer 405, receipt silence 0.014–0.017 s and sampling age
0.160–0.174 s rule out another retired or silent stream. The owner is bouncing
and spinning (2.197 m/s, 19.869 rad/s); an observer's orientation differs by
3.079 radians. This reproduces the residual under the original bound, rather
than proving it resolved by the earlier force fix.

Tank physics uses a custom local mass center 0.19 m above its node origin.
Godot linear velocity describes that mass center. Correction previously
extrapolated and steered the node origin with that velocity, introducing
translation solely from orientation error. The offset permits 0.38 m of pivot
displacement and up to 1.52 m/s of erroneous steering at the existing 0.25 s
blend time. Correction now extrapolates and steers the world mass center,
then converts the rotated offset back to an origin for snaps. Geometry recovery
also queries between sampled mass centers. Automatic bodies use the native
computed local center; tank centers use their existing custom value. Gameplay
forces, tuning, message shapes and captured root-position bounds are unchanged.

Two headless regressions fail before this fix: pure rotation incorrectly applies
linear velocity, and a spinning snap incorrectly displaces a translating mass
center. All 229 unit assertions pass afterward. The native body-correction
fixture proves the issue without gravity, damping or collisions: a freely
spinning reference has zero center drift, while the old observer drifts
0.023125 m. Custom and automatic centers both fail. The corrected observers
both drift 0.000002 m and pass. The existing owner/stale/predicted jet assertions
also pass; make build reports zero warnings/errors.

The `vent-contact-axis=mixed` fixture alternates vertical and horizontal initial
spin using the existing VentMaxSpin. Two 2-bot/30-second, FixedSeed=23, 120/30/3
runs pass their unchanged final checks before/after (0.150/0.197 m). Across
5,440 vent pair grids after the first six play seconds, median/p95/p99/maximum
root drift is 0.098/0.202/0.255/0.461 m before and
0.105/0.233/0.332/0.564 m after. Owner floor contacts are 751/776 versus
683/776, peak speed 1.752 versus 4.904 m/s, and spin 30.740 versus
30.706 rad/s. These nonidentical native trajectories do not prove a floor-drift
reduction; the force-free regression proves the mathematical defect directly.
Logs retain both results under `build/vna-center-floor-before` and `-after`.
No resting, intact, free-flight or venting bound has been relaxed.

The native repeat of the failed layout (seed 797598340, 4 bots, 90 seconds,
120/30/3 and melee-start) passes 1,999 grids/262,962 pairs with no unobserved
grids, final rest/moving drift 0/0.158 m, and longest episode 1.50 s.
All four bots agree with the server audit (21 banked). This is a new action
schedule on the same layout, not a deterministic replay or a substitute for
the final ten-fault/five-clean matrix.

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/body_correction_tests.tscn
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn
tools/dev/net_bots.sh -p 25090 -n 2 -l 30 -s '--tune.FixedSeed=23' -o "$PWD/build/vna-center-floor-after" -- --scenario=vent-contact --vent-contact-axis=mixed --tune.FixedSeed=23 --net-lag=120 --net-jitter=30 --net-loss=3 --tank-motion-trace
tools/dev/net_bots.sh -p 25090 -n 4 -l 90 -s '--tune.FixedSeed=797598340' -o "$PWD/build/vna-center-repro" -- --melee-start --digest-every=0.5 --net-lag=120 --net-jitter=30 --net-loss=3 --tank-motion-trace --tank-authority-trace
```


## Predicted creation and checkpoint ordering

The full gates pass on 15eb974: make check and make test-full both exit zero,
with 229 unit assertions and 24 comparer tests. Deep navigation passes 700/700
courses (zero stuck, 600 jumps); the deep pile/lifetime results remain unchanged.
Its first two completed 4-bot/90-second faulted soaks pass at final moving drift
0/0.349 m and longest episodes 1.00/1.35 s. The launcher received SIGTERM during
another capture; those logs are retained as `vna-center-final-fault-2-interrupted`
and excluded, with no final sample. Subsequent batches use shorter launchers.

The completed second soak also logs four "bad BodyStates ... unknown body id"
rejections. Local predicted drops register in BodySync immediately and can publish
on independent checkpoint lanes before their reliable Drop registration reaches
the server. One unknown ID invalidates an entire batch, delaying updates for
otherwise registered bodies. A bank debit precedes the creation echo, so the
bank's unconfirmed count cannot serve as a publication permission.

Match now passes the existing DropPredictions pending-ID set into collection.
Those entries retain PublishRequired and their awake history until the creation
echo removes them from the set. This avoids duplicating prediction state and
preserves the required first checkpoint even if the body stops before confirmation.
Filtering after collection would silently consume the sleeping body's sole rest.
Server validation, ownership, local physics, scoring and message shapes are unchanged.
The integration adds only one call-site edit to A4's Match structure.

Two production regressions simulate a relayed shot and its real drop/spawn path,
then drive Match physics while creation is pending. Both moving and sleeping
cases fail before: unregistered bodies publish and consume their first state.
After the fix, registered-body publications continue, a bank debit alone does
not release the drops, and a creation echo releases exactly one required state.
All 231 unit assertions pass, with zero build warnings/errors.

A native 4-bot/90-second 120/30/3 repeat of layout 2113692991 passes:
1,998 grids/282,791 body pairs, no unobserved grids, final rest/moving
0.001/0.242 m, longest episode 0.60 s, and all scores agree (27 spilled,
14 banked). Unknown-ID body-batch rejections fall from four to zero. This is a
layout repeat with a new action schedule, not a deterministic replay.
The final gates and complete matrix are repeated after this last product fix.

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn
tools/dev/net_bots.sh -p 25090 -n 4 -l 90 -s '--tune.FixedSeed=2113692991' -o "$PWD/build/vna-drop-native" -- --melee-start --digest-every=0.5 --net-lag=120 --net-jitter=30 --net-loss=3 --tank-motion-trace --tank-authority-trace
```
