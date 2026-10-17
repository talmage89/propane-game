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
The first native follow-up requested FixedSeed=1341647191, but multiplayer
ignores that single-player tuner: the transmitted layout was seed 331150887.
With the same 4-bot/90-second 120/30/3 schedule, it passes 1,998 grids/342,637 body
pairs with no unobserved grids, final drift 0/0.194 m and a 0.95-second
longest episode; all score audits agree. This is a new layout and action schedule, not a repeat of the failed layout.

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
spin using the existing VentMaxSpin. Two 2-bot/30-second, 120/30/3
runs pass their unchanged final checks before/after (0.150/0.197 m). Across
5,440 vent pair grids after the first six play seconds, median/p95/p99/maximum
root drift is 0.098/0.202/0.255/0.461 m before and
0.105/0.233/0.332/0.564 m after. Owner floor contacts are 751/776 versus
683/776, peak speed 1.752 versus 4.904 m/s, and spin 30.740 versus
30.706 rad/s. These nonidentical native trajectories do not prove a floor-drift
reduction; the force-free regression proves the mathematical defect directly.
Logs retain both results under `build/vna-center-floor-before` and `-after`.
No resting, intact, free-flight or venting bound has been relaxed.

The native follow-up requested FixedSeed=797598340, but used random layout
430818032 (4 bots, 90 seconds, 120/30/3 and melee-start). It passes 1,999 grids/262,962 pairs with no unobserved
grids, final rest/moving drift 0/0.158 m, and longest episode 1.50 s.
All four bots agree with the server audit (21 banked). This is a new layout and action
schedule, not a repeat of the failed layout or a substitute for
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

A native 4-bot/90-second 120/30/3 follow-up requested FixedSeed=2113692991,
but used random layout 1710049523. It passes:
1,998 grids/282,791 body pairs, no unobserved grids, final rest/moving
0.001/0.242 m, longest episode 0.60 s, and all scores agree (27 spilled,
14 banked). Unknown-ID body-batch rejections fall from four to zero. This is a
new layout and action schedule, not a repeat of the failed layout.
The final gates and complete matrix are repeated after this last product fix.

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn
tools/dev/net_bots.sh -p 25090 -n 4 -l 90 -s '--tune.FixedSeed=2113692991' -o "$PWD/build/vna-drop-native" -- --melee-start --digest-every=0.5 --net-lag=120 --net-jitter=30 --net-loss=3 --tank-motion-trace --tank-authority-trace
```


## Controlled debug match seeds

The integration review found that earlier claimed layout repeats used
`--tune.FixedSeed`, which applies only to single-player generation. Those
claims above are corrected against the actual server logs. Debug servers now
accept `--match-seed=<int32>` as the existing generator's initial candidate;
invalid layouts still retry through the existing generator. Verify the actual
seed printed by the server before calling a run a layout repeat. Release seed
selection, generation rules, message shapes and tuners are unchanged.

The native server fixture checks the actual transmitted PlanCodec seed against
`--match-seed=23`. Before the selector, it fails with seed 919984183; afterward
it passes with seed 23. Its client uses ordinary process frames, independently
of the separate client physics-poll correction.


## Packet delivery during catch-up physics

After the drop fix, ten faulted 4-bot/90-second captures passed (largest final
moving error 0.368 m, longest episode 1.25 s). The second clean capture failed
a 1.55 s ownership/position episode against the unchanged 1.50 s clean limit.
It used layout 832642757. Scores and final positions agreed, but the earlier
episode reached 5.435 m and cannot be dismissed as a final floor-vent residual.

The receiving bot reported 1–2 FPS while catch-up physics continued at
103–105 ms per step. A grant arrived 331 ms after owner activation, and reliable
samples remained undelivered for up to 441 ms; RTT reached 399 ms. Publication
on a monotonic clock alone cannot fix client delivery polled only in _Process.
NetClient now polls the same transport in _PhysicsProcess too, with unchanged
monotonic timeout and ping deadlines. Server dispatch, message shapes, gameplay,
tuners and comparison bounds stay unchanged.

The production loopback regression invokes only client physics callbacks for
handshake and lobby creation, then mixes eight physics/process polls at one
clock value and requires exactly one ping. It fails before the fix and all
232 unit assertions pass afterward. The native regression disables client
process callbacks and requires actual UDP handshake and reliable lobby control:
before it fails the handshake; afterward both succeed.

A verified-layout clean repeat requests `-s '--match-seed=832642757'`; the
server confirms actual seed 832642757. Four bots over 90 seconds pass 1,999
grids/286,449 body pairs, zero unobserved grids, final rest/moving error
0.001/0.100 m and longest episode 0.10 s; all scores agree (17 banked).
The earlier failing episode was 1.55 s. Real-time bot actions differ, so this
is supporting native evidence alongside the direct regressions, not a replay.

```sh
godot-mono --headless --path . res://scenes/dev/transport_physics_tests.tscn -- --server --port=25090
tools/dev/net_bots.sh -p 25090 -n 4 -l 90 -t clean -s '--match-seed=832642757' -o "$PWD/build/vna-polled-clean-repro" -- --melee-start --digest-every=0.5 --tank-motion-trace --tank-authority-trace
```


## Settled intact contact recovery

The first final matrix after client physics polling passed faulted runs 1 and 2,
then run 3 failed a resting intact drop, 20100004. Three pairs stayed
0.119–0.168 m apart for 3.15–3.20 s, despite agreeing scores and final error
0.008/0.188 m. This failure is retained in `build/vna-polled-final-fault-3-run.log`.
Its actual layout seed is 930682136. Run 4 passed before the batch was stopped.

The owner continued publishing: sequence 51–78, speed 0–0.005 m/s. The failed
observer received sequence 47–78 with receipt silence only 0.002–0.082 s. Its
root stayed roughly 0.15 m high and 0.46 rad tilted, with actual speed/spin
nearly zero. A ray from observer mass center to sampled owner center found
no obstruction. Velocity steering was consumed by contact rather than delayed
by the network; a low-motion owner need not be natively Sleeping.

Correction now recovers a persistent settled mismatch on a remote intact tank.
Both owner motion and observer motion must satisfy the existing gather rest
speed/spin tuners; existing rest distance/angle tuners define pose mismatch.
After three CorrectionBlendTime intervals, the copy takes the owner pose and
motion. Resumed motion restarts the interval; epochs and local predictions
clear it. This does not fabricate a rest message or change owner physics,
gather rules, vent physics, tuners or comparison bounds.

The direct production regression supplies fresh awake states on every tick
while contact consumes observer steering. It fails before (233 tests, one
failure) and passes afterward. A second regression requires resumed owner
motion to restart the settling window; all 234 tests pass. A native flat-floor
case already passed before and is a safety check, not a reproduction of the
failed map contact: last-half peak/final root drift is 0.005530 m before and
0.000056 m after. Custom/automatic spinning centers remain at 0.000002 m drift
and the fresh observer jet remains 0 m/s; owner, stale and prediction jets run.

The verified-layout repeat requests `-s '--match-seed=930682136'`; its actual
server seed is 930682136. Four bots over 90 seconds at 120/30/3 pass 1,997
grids/334,868 body pairs, no unobserved grids, final rest/moving 0.001/0.154 m
and longest episode 0.50 s. All scores agree (10 banked). Actions differ from
the failed run, so this supports the direct regression rather than replaying
the exact failed contact.


## Unset shield override in the rendered gather probe

The final gather probe first aborted on Bot2: a new shield read back as
0,0,0,1 rather than the accepted 0.2,0.52156866,0.9490196. A diagnostic
repeat on actual seed 1477546872 reproduced it. The new tank's shader instance
override is Nil until its first process update. Godot's AsColor converts Nil
to opaque black; the material's initial shield is transparent. No player
appearance, shader, or drop rule was wrong.

The probe treats Nil as transparent and waits to observe an actual shield
override. Every available nontransparent color still must match the accepted
appearance exactly, including legitimate black. Failure diagnostics retain
actual RGB/alpha and expected RGB. A gated native regression reads the actual
new tank's Nil override: before it fails as opaque black; afterward it passes
and preserves a real black value. A rendered run also verifies the processed
tank publishes its accepted appearance. Production rendering is unchanged.

The verified-layout rendered gather repeat passes 1,100 grids/32,905 body pairs,
zero gaps, zero final drift and a 0.20 s longest episode. Both clients observe
two accepted-color shields and glides, host ownership, and a stable intact
pile. Scores agree at 14 banked. This fixture-only correction does not affect
the ordinary melee-start soak path or any replicated gameplay.

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/body_correction_tests.tscn
godot-mono --fixed-fps 60 --resolution 960x540 --path . res://scenes/dev/body_correction_tests.tscn
tools/dev/net_bots.sh -p 25087 -n 2 -l 45 -w 2 -s '--start-score=8 --match-seed=1477546872' -o "$PWD/build/vna-gather-fixed" -- --scenario=gather --tune.DropScatter=0.2 --tune.DropToss=1 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/vna-gather-fixed/shots"
```


## Final independent proof

The final shared implementation is 336b953; 5d35a5c adds only the gather probe's
unset-uniform correction and regression. All matrix captures below run the
final shared code through the unchanged comparer. The corrected gather case
and both final gates run with 5d35a5c. Logs and reviewed frames remain in build.

Ten faulted and five clean 4-bot/90-second melee-start captures all pass:
4,090,209 body-pair checks, no unobserved pair grids, no final lifecycle or
completion disagreement, and all server score audits agree. These are a new
complete matrix, not selected successes from earlier failed sets.

| Capture | Grids / body pairs | Final rest / moving (m) | Longest episode (s) | Result |
| --- | ---: | ---: | ---: | --- |
| fault 1 | 1998 / 270,676 | 0.000 / 0.246 | 0.55 | PASS |
| fault 2 | 1999 / 267,798 | 0.000 / 0.089 | 0.70 | PASS |
| fault 3 | 1996 / 183,565 | 0.000 / 0.182 | 0.65 | PASS |
| fault 4 | 1999 / 291,792 | 0.000 / 0.118 | 0.45 | PASS |
| fault 5 | 1999 / 144,382 | 0.001 / 0.161 | 0.60 | PASS |
| fault 6 | 1996 / 251,069 | 0.006 / 0.133 | 1.90 | PASS |
| fault 7 | 1996 / 419,561 | 0.001 / 0.259 | 0.75 | PASS |
| fault 8 | 2000 / 216,231 | 0.000 / 0.047 | 0.60 | PASS |
| fault 9 | 1995 / 254,077 | 0.005 / 0.216 | 0.75 | PASS |
| fault 10 | 1999 / 345,151 | 0.001 / 0.197 | 0.65 | PASS |
| clean 1 | 1996 / 285,049 | 0.001 / 0.000 | 0.15 | PASS |
| clean 2 | 1996 / 328,271 | 0.001 / 0.068 | 0.70 | PASS |
| clean 3 | 1999 / 343,892 | 0.001 / 0.086 | 0.10 | PASS |
| clean 4 | 1997 / 217,064 | 0.000 / 0.000 | 1.20 | PASS |
| clean 5 | 1996 / 271,631 | 0.001 / 0.073 | 0.75 | PASS |

The faulted moving maximum is 0.259 m; the clean moving maximum is 0.086 m.
Fault 6's longest episode is resting drop 20100007 at 0.140–0.147 m across
three pairs for 1.90 s, just below the unchanged 2.00 s fault limit. Clean 4
reaches 1.20 s against 1.50 s. Preserve these margins when reviewing future
performance changes; passing this finite sample is not a guarantee against
every later machine stall. No resting, intact, venting or free-flight limit
has been relaxed. The floor-vent residual does not need a distinct final bound.

The 6-bot/90-second 120/30/3 capture passes 1,998 grids/751,747 body pairs,
zero gaps, final 0.005/0.239 m and longest episode 1.85 s; all six scores agree.
The 4-bot/90-second 250/50/5 capture also passes: 1,999 grids/270,945 body pairs,
zero gaps, final 0.001/0.345 m, longest episode 0.95 s, and all scores agree.
These use `vna-settled-final-six-bot` and `-heavy-fault`. No unknown-ID body
batch rejection appears in the completed matrix/stress server logs.

| Final probe | Result |
| --- | --- |
| Loading stall (9 s) | PASS, 899 grids, final 0/0 m, episode 0.25 s, scores agree |
| Melee (35 s, grip/motion asserted) | PASS, 900 grids, final 0/0 m, episode 0.20 s |
| Duel (35 s) | PASS, 900 grids, final 0/0.249 m, episode 0.40 s |
| Gather (45 s, both rendered, corrected probe) | PASS, 1,100 grids, final 0/0 m, both shields/glides/ownership/stable pile asserted |
| Puncture race (20 s, both rendered) | PASS, 599 grids, final 0/0.141 m, episode 0.25 s |
| compare_vents.py | PASS, 278 pairs, median/p95/max 0.097/0.211/0.346 m, canonical vent agrees |
| Rotating lanes (20 s, first lane stalled 2 s, all disposable samples lost) | PASS, 599 grids, final 0/0 m |
| Mixed-axis floor vents (30 s, 120/30/3) | PASS, 799 grids, final 0/0.170 m, episode 0.15 s |
| Rendered menu input | PASS, 66 real-device assertions, zero failures |
| Native silent client / server | PASS, detection 4.242 / 3.320 s (10 s cap); dev 6.311 / 3.349 s, pre-fix branch client 25.208 s |
| Native protocol / minor-version mismatch | PASS, clean rejection before match assembly; version view offers download |
| Planted 2 m resting error | Expected FAIL, 19.15 s at 2.000 m while both scores agree |

The first final gather probe and its diagnostic repeat fail on the unset shader
override, as recorded above. The corrected repeat passes; those failures are
not renamed or discarded. The planted error is deliberately expected to fail.
All other final scenario probes pass.

Commands (TMPDIR is the worktree's build/vna-scratch; outputs use absolute
worktree-local paths):

```sh
make build
make check
make test-full
# Ten independent faulted runs, with N=1..10 and unique output directories:
tools/dev/net_bots.sh -p 25085 -n 4 -l 90 -t faults -o "$PWD/build/vna-settled-final-fault-N" -- --melee-start --digest-every=0.5 --tank-motion-trace --tank-authority-trace --net-lag=120 --net-jitter=30 --net-loss=3
# Five clean runs, with N=1..5:
tools/dev/net_bots.sh -p 25085 -n 4 -l 90 -t clean -o "$PWD/build/vna-settled-final-clean-N" -- --melee-start --digest-every=0.5 --tank-motion-trace --tank-authority-trace
# Fault 1 adds -w 1, --shot-every=30 and --capture-dir=<absolute output>/shots.
tools/dev/net_bots.sh -p 25085 -n 6 -l 90 -s '--tune.MaxPlayers=6' -o "$PWD/build/vna-settled-final-six-bot" -- --melee-start --digest-every=0.5 --tank-motion-trace --tank-authority-trace --net-lag=120 --net-jitter=30 --net-loss=3
tools/dev/net_bots.sh -p 25085 -n 4 -l 90 -o "$PWD/build/vna-settled-final-heavy-fault" -- --melee-start --digest-every=0.5 --tank-motion-trace --tank-authority-trace --net-lag=250 --net-jitter=50 --net-loss=5
# Scenario runs use --net-lag=120 --net-jitter=30 --net-loss=3 and port 25087:
tools/dev/net_bots.sh -p 25087 -n 2 -l 35 -s '--start-score=5' -o "$PWD/build/vna-settled-final-scenario-load-stall" -- --scenario=melee --tank-check-load-stall=Bot2 --net-lag=120 --net-jitter=30 --net-loss=3
tools/dev/net_bots.sh -p 25087 -n 2 -l 35 -w 1 -s '--start-score=5' -o "$PWD/build/vna-settled-final-scenario-melee" -- --scenario=melee --assert-grip --assert-motion --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/vna-settled-final-scenario-melee/shots"
tools/dev/net_bots.sh -p 25087 -n 2 -l 35 -w 2 -s '--start-score=8' -o "$PWD/build/vna-settled-final-scenario-duel" -- --scenario=duel --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/vna-settled-final-scenario-duel/shots"
# Gather's exact command is in the preceding section.
tools/dev/net_bots.sh -p 25087 -n 2 -l 20 -w 2 -o "$PWD/build/vna-settled-final-scenario-puncture-race" -- --scenario=puncture-race --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/vna-settled-final-scenario-puncture-race/shots"
python3 -B tools/dev/compare_vents.py "$PWD"/build/vna-settled-final-scenario-puncture-race/bot*.log
tools/dev/net_bots.sh -p 25087 -n 2 -l 20 -s '--net-loss=100 --net-body-stall=2000' -o "$PWD/build/vna-settled-final-scenario-lanes" -- --scenario=body-stream
tools/dev/net_bots.sh -p 25087 -n 2 -l 30 -s '--match-seed=23' -o "$PWD/build/vna-settled-final-scenario-vent-contact" -- --scenario=vent-contact --vent-contact-axis=mixed --tune.FixedSeed=23 --net-lag=120 --net-jitter=30 --net-loss=3 --tank-motion-trace
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/menu_test.tscn -- --scenario=input --port=25086 --capture-dir="$PWD/build/vna-settled-final-input"
python3 -B tools/dev/check_liveness.py --output="$PWD/build/vna-settled-final-liveness-client" --silent=client
python3 -B tools/dev/check_liveness.py --output="$PWD/build/vna-settled-final-liveness-server" --silent=server
# The native rejection probes start/stop each server as its own process group:
godot-mono --headless --path . -- --server --port=25088
godot-mono --headless --path . res://scenes/dev/net_test.tscn -- --role=join --server=127.0.0.1 --port=25088 --fake-protocol=1 --quit=15
# Stop that server, start a separate fake-version server, and run the UI probe:
godot-mono --headless --path . -- --server --port=25088 --fake-version=9.9.0
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/menu_test.tscn -- --scenario=rejection --server-address=127.0.0.1:25088 --capture-dir="$PWD/build/vna-settled-final-rejection-version/shots"
tools/dev/net_bots.sh -p 25087 -n 2 -l 35 -s '--start-score=5' -o "$PWD/build/vna-settled-final-nudge" -- --scenario=melee --tank-check-nudge=Bot2
```

Personally reviewed fault 1's Bot1_030.0.png and Bot1_060.0.png: actor/rifle,
readable HUD, loose tanks, debris, and a remote actor render normally. Reviewed
melee_host_2_06.png (throw), victim_throw_0.80.png (blast and spill),
Bot2_race_600.png (canonical jet), and gather's victim_02.5.png,
gather_host_shimmer.png and gather_host_settled.png (blue shield, glide,
upright pile). Match input's before/after-turn, jump and remapped-pause frames
show the delivered device paths; 01_version_rejected.png shows a clean update
message and download button. Frames are under the corresponding capture
directories above. No recording or baseline copy is required to repeat these
commands. Windows/macOS native execution and 16-peer admission remain unverified.

Final `make check` and `make test-full` both exit zero on 5d35a5c. Build has
zero warnings/errors; the unit runner passes 234 assertions in 15 suites, and
the tank comparer passes all 24 regressions. Deep piles cover 53 fixtures,
738 tanks and 120 simulated seconds: maximum drift 0.001669 m and tilt
0.151 degrees. Lifetime checks pass ten single-player and ten solo-match
cycles with zero orphans. Generator checks pass 200 ordinary seeds, 200
two-player layouts, 100 each at 8/16/24/32 players, and six seeds per forced
formation/size. Separate-process hashes agree for all four formations.
Navigation passes 700/700 courses, zero stuck, mean 3.20 s and 600 jumps.
Logs are `build/vna-reviewed-final-check.log` and
`build/vna-reviewed-final-test-full.log`. The earlier whitespace-only lint
failure was corrected in the contact commit before either final gate.

Integration's dev base remains d2aa9c9. Dev advanced afterward with the
independent R19 recoil correction; this work does not depend on it. Participants
work must reconcile its Match/lobby changes and rerun the gate after landing.
Native ENet queue-depth/admission bounds and 16-peer performance remain the
capacity follow-ups described above. No gameplay rule, design file, tuner
default, release version or Protocol.Version was changed by these integration
fixes. Shared replication changes still require a minor release.
