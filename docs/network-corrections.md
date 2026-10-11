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

## Motion survives native disposable throttling

In `/tmp/nas-motion-trace-1`, Bot4 owned `10000001`, epoch 2. Bot2 received the grant at
91137.726554, but remained at last sequence 0 until 91139.84 despite owner publications at
91137.687, .969, 91138.252, .536, .819, 91139.103, .371 and .654. Its moving/lifecycle
disagreement lasted 2.30 s, peaking at 5.667 m. This was missing correction evidence, not
ordinary vent chaos within the configured 120–180 ms two-leg delay. Native throttling is
additional to the injected 3% loss and is not represented by the reliable loss statistic.

Owners now mark an existing moving publication reliable at least every half silence bound
(0.25 s plus up to one normal send interval), and immediately when publication is required.
The server preserves that reliability on the second leg. Other snapshots remain disposable;
there are no extra samples, message fields, protocol bump or gameplay/tuner changes. Reliable
checkpoints share the existing epoch/sequence ordering, so a late checkpoint cannot rewind
a newer disposable sample. Sustained congestion can still delay reliable delivery; native
congestion control remains enabled.

Two unit regressions failed before (204 tests, two failures,
`build/net-assert-artifacts/checkpoint-before.log`) and passed after (204 tests, zero failures,
`checkpoint-after.log`). Initial test compilation caught the packet sink's Boolean reliability
signature; it was corrected before the recorded regression run. Final builds had zero warnings.
The native fixture `tools/dev/net_bots.sh -n 2 -l 20 -s '--net-loss=100' -o /tmp/nas-stream-after
-- --scenario=body-stream` drops every server-side disposable packet while the owner moves at
8 m/s without collisions. Before the fix (`/tmp/nas-stream-before`), the observer's motion
assertion failed and it exited without completion. Afterward it passed: 599 grids, 0.324 m peak
moving drift, 0.152 m final drift, and matching score audits. These fixture physics overrides
are dev-only, not tuner changes. The Match send partition is isolated in its own commit.

## Recovering across collision geometry

Fresh velocity steering cannot carry a simulated observer through a solid surface. In
`/tmp/nas-motion-trace-1`, vent `10000016` epoch 3 was owned by Bot3. Bot4 at shared time
91156.970936 had position (10.961, -0.042, 26.784), predicted goal (10.869, 0.613, 26.871),
receipt quiet time 0.048 s, sample age 0.348 s, and 2.901 rad rotation disagreement. The ray
to the actual owner sample hit SlabBody. Similar fresh corrections remained across that slab
for seconds, also after an epoch-4 handoff changed which peer owned the low copy.

Body sync now tests the path to the actual owner sample once per accepted moving target.
If solid world/prop/floor geometry blocks it, an observer restores that pose and its owner's
velocities instead of indefinitely steering into the surface. It does not extrapolate through
the obstacle. Owners and predicted local claims do not take this path; free-space copies keep
the existing smoothing and distance threshold. Gather retains its existing frozen stream.

The native wall regression proved a hit before correcting a 2 m discrepancy below the 3 m
normal snap threshold. Before it failed (205 tests, one failure,
`build/net-assert-artifacts/geometry-before.log`); afterward it passed, including unobstructed
smoothing (205 tests, zero failures, `geometry-after-clean.log`). An intermediate fixture
tried moving the wall and failed its free-space check because native geometry had not updated
within that synchronous test; the final fixture uses a separate demonstrably clear path.
Final builds were warning-free. This adds at most one geometry ray per received moving sample.
