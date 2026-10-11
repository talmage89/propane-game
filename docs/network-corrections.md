# Tank correction investigation

Follow-up to [the soak assertion](network-soak.md), on `wp/net-assert`, after merging dev `a50b9e5`.
Gameplay rules and tuner defaults remain unchanged.

## Resting bodies

The loading relay filter dropped body publications before countdown. A tank could sleep during construction,
publish its only reliable rest while another client was still building, and never publish again. Initial map
bodies also registered asleep without requesting any publication. The server now accepts owner/epoch/sequence
validated motion and rests during loading; the existing match inbox buffers them until bodies exist. An initially
sleeping owner publishes once on registration. No message shape changed.

Observers discarded the rest target once they reached it. Later local collision/solver drift therefore persisted
without any new owner packet: the owner was still asleep. Body sync now retains the target, restoring displaced
copies without modifying an already sleeping copy at the correct pose. A newer owner sequence, epoch or predicted
local claim still replaces/clears it. Gather uses the same ordered stream; its completion and body rest remain
reliable. Blast impulses continue to execute locally, with confirmed owner publications determining lasting pose.

In `/tmp/nas-rest-trace-baseline`, Bot1 owned map tank `10000023`, epoch 1. It published moving sequences 1–7,
then rest sequence 8 at monotonic 85580.923798 during loading. The other peers received none of these states.
Their final intact/resting positions differed by up to 0.481 m. For drop `2000000d`, Bot1 owned epoch 1 and
published rest sequence 122 at 85658.835081. Every receiver accepted and applied it, then drifted after discarding
that target; final resting disagreement reached 0.133 m. Neither case required an ownership handoff or gather.

Three unit regressions failed before the fix and passed afterward: initial sleeping publication, loading relay
(including rejection of nonowners/stale sequences), and an observer drifting after applying its rest. Commands:
`make build`, then `godot-mono --headless --path . res://scenes/dev/unit_tests.tscn`. The merged baseline recorded
198 tests/3 failures in `/tmp/nas-rest-before-all.log`; the fix recorded 198 tests/0 failures in
`/tmp/nas-rest-after.log`. Both builds had zero warnings/errors.

`--tank-trace` is optional dev logging. It reports owner, epoch, sequence, pending claim, accepted state/rest,
correction target, sleep, position/velocity and gathering on body-sync transitions. Publish traces precede the
caller's sequence increment, so a trace showing sequence N publishes N+1. Ordinary matches have no listener.
Use `--tank-trace=1000000d` to restrict output to one body; tracing every settled body adds substantial logging.

## Moving correction freshness

Motion targets backdate sampling time by up to 0.3 s to include transport delay in extrapolation. The same
backdated timestamp incorrectly drove the 0.5 s silence timeout, expiring a freshly received delayed packet
after only 0.2 s without another arrival. Sampling age and receipt freshness are now separate. Both timestamps
survive a motion packet overtaking its reliable ownership grant; sequence/epoch ordering remains unchanged.

The new delayed-motion regression, including the overtaking-grant case, failed before and passed afterward:
`make build`, then `godot-mono --headless --path . res://scenes/dev/unit_tests.tscn` recorded 199 tests/1 failure
in `/tmp/nas-motion-before.log` and 199 tests/0 failures in `/tmp/nas-motion-after.log`. Builds were warning-free.

The baseline trace also establishes delays beyond the configured fault schedule under shared-machine load.
Matching owner publications to received epochs/sequences on the shared monotonic clock, drop `20000000`
arrived at Bot1 1.160–1.384 s after publication during its 3.65 s moving disagreement, while Bot3 received the
same stream mostly 0.142–0.262 s old. Bot1's receive interval peaked at 2.063 s. The configured two transport
legs alone total 0.120–0.180 s; those late copies are not evidence that a 5.9 m error is ordinary flight under
just that delay. Distance, settlement and final motion thresholds have not been increased.

## Observer spin steering

Body sync adds rotation-error steering to the owners angular velocity. The vent physics callback ran later
and clamped that combined velocity to the gameplay spin limit, removing steering when the owner was already
spinning at the limit. Fresh observer corrections now bypass this clamp; the owner and stale observers retain
the existing limit. Vent thrust, owner physics and all tuner defaults are unchanged.

The regression fails before (`/tmp/nas-spin-before.log`: vent spin limiter erased observer correction) and
passes afterward (`build/net-assert-artifacts/spin-after.log`: 200 tests, zero failures). It also checks that
stale copies and a newly granted owner resume the ordinary limit. The before run was interrupted after its
failure; the after suite completed. Both builds were warning-free. Network measurement remains necessary
to establish which moving errors this correction resolves.

## Reliable authority as correction evidence

Body sync cached a reliable canonical grant/refusal but did not install it as an observer target. If the
new owner had not received its grant or had a pending exploded prediction, observers continued local blast
flight until disposable publications arrived. Faulted run `/tmp/nas-fix-fault-1` shows `1000000e`: Bot1
received epoch 2 at 87998.221, but the new owner Bot2 first published at 87999.686. Bot1 first accepted
motion at 88000.763, after drifting 14.336 m from that target. The canonical grant itself was unused.

Reliable authority now supplies an observer target immediately. A newer publication that overtook the
grant retains its original sampling/receipt times; older refusals cannot rewind that publication. Resting
authority remains an anchor. Owners continue their predicted trajectory. Match passes its existing receipt
clock at two call sites, isolated in a separate commit for the parallel restructuring package. No packet
changes are needed.

The regression, covering moving and resting handoffs before any owner publication, failed before
(`build/net-assert-artifacts/grant-before.log`: 201 tests, one failure) and passed after
(`build/net-assert-artifacts/grant-after.log`: 201 tests, zero failures). Both builds were warning-free.

## Construction stalls and native liveness

A second mechanism invalidated whole runs: native peers disconnected while a valid builder was stalled.
The client used a 3 s minimum/8 s maximum ACK timeout; the server used ENet's shorter default minimum.
Increasing only the client's maximum to 30 s still failed because the server could reach its minimum first.
On connection, both ends now permit 20 s minimum/30 s maximum ACK silence, covering the server's existing
20 s loading grace. Explicit leaves/disconnects remain immediate; detecting a silent failed endpoint can now
require 20–30 s. These are transport liveness bounds, not gameplay tuner changes.

The `--tank-check-load-stall=Bot2` native fixture blocks that client's construction frame for 9 s. The command
`tools/dev/net_bots.sh -n 2 -l 35 -s '--start-score=5' -o /tmp/nas-load-stall-fixed -- --scenario=melee
--tank-check-load-stall=Bot2` failed before (`/tmp/nas-load-stall-before`, bot status 2, absent final scores)
and passed after (status 0, 898 grids, resting/moving peak 0.007/0.231 m, zero final disagreement, score audit
10 initial - 5 spilled = 5 banked). The resumed builder was ready at 14.43 s. A maximum-only intermediate
attempt also failed (`/tmp/nas-load-stall-after`), confirming the two-ended minimum matters.

The timeout API's minimum and maximum interact with the RTT retransmission multiplier; see the
[Godot ENet timeout documentation](https://docs.godotengine.org/en/stable/classes/class_enetpacketpeer.html#class-enetpacketpeer-method-set-timeout).
A native 64-datagram burst probe also received all 64 in one poll, ruling out the suspected single-datagram
poll drain problem; no poll/drain rewrite is included.

## Freed dev-bot targets

An independent harness defect invalidated `/tmp/nas-proof-fault-2`: Bot1 attempted to blacklist its former
tank while fleeing another one. The former tank had been freed by authoritative lifecycle handling, but
the fleeing branch bypassed normal retargeting. `GetInstanceId` threw 2,251 times before logging could
run, losing 18.996 s of samples. The 10% missing-time rejection correctly identified a broken harness.

Blacklisting now ignores freed native objects; route failure then clears the stale target as usual. Live
target blacklisting is unchanged. The new freed-tank/live-target regression failed before
(`build/net-assert-artifacts/bot-before.log`: 202 tests, one failure) and passed after
(`build/net-assert-artifacts/bot-after-clean.log`: 202 tests, zero failures, no leaked node). The initial
fixture failed compilation because required world/spawn fields were omitted; it now uses the tank factory.
Both final before/after builds were warning-free.

## Optional native motion diagnostics

`--tank-authority-trace` records server/receiver grants with the shared monotonic clock.
`--tank-motion-trace` records awake tank correction targets and native throttle/RTT at 4 Hz;
`--net-throttle-trace` raises the server's native diagnostic cadence to 4 Hz. These are opt-in
probes, including geometry rays; ordinary matches do not perform those diagnostic queries.
In `/tmp/nas-throttle-1`, native packet throttle reached 0.125, permitting only one in eight
disposable packets before the additional configured 3% fault loss. Reliable packet loss statistics
do not measure that suppression. The second traced run still failed a 2.05 s moving episode.
