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
