# Tank correction investigation

**Status:** Dated record (2026-10-11 through 2026-10-16), the investigation that followed the soak assertion. Its
fixes are in the code and described in [multiplayer.md](../multiplayer.md#networking). It records a residual vent
outlier (0.531 m) that the unchanged 0.50 m final bound still catches.

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

## Keeping checkpoints off the control stream

The first checkpoint/barrier validation series exposed a remaining mechanism: dense captures
with delayed canonical lifecycle delivery. `/tmp/nas-deliver-fault-1` failed only dropped tank
`20300007`: Bot1 first vented at 93105.163823, Bot3/4 at 93105.665484/.668747, but Bot2 at
93106.884699. Removal followed at 93106.354881, .704238, .750669 and 93107.733034 respectively.
Final positions agreed; the continuous lifecycle episode lasted 2.55 s. In traced fault 3,
Bot1 received canonical explosion grants for `1000000f`, `10000010` and `10000013`
1.607/1.548/1.540 s after server publication. Six neighboring bodies disagreed across
2.20–2.55 s episodes. Native reliable loss was nonzero; sampling was dense. Faults 2/4
passed. This stage was stopped after four runs rather than treating those failures as noise.

Checkpoints shared ENet's ordered reliable control stream. A missing checkpoint or fragment
could hold later grants behind its retransmission. Their new `ReliableBodyStates` transport
class now uses channel 1, separately ordered from channel-0 control. The fault decorator
preserves independent order, loss exemption, receive overflow and retry behavior for both
classes. The server retains the checkpoint class on relay, and required-send handling
recognizes both reliable classes. No application message fields or handshake shape changed;
Protocol.Version remains 2 under the unreleased-protocol instruction. Older implementations
can decode the same payloads but lack these correction/delivery guarantees.

The deterministic two-second checkpoint-stall regression failed before (206 tests, one
failure, `build/net-assert-artifacts/lane-before.log`) and passed after (206 tests, zero
failures, `lane-after.log`). It proves control passes a blocked checkpoint while that
checkpoint remains reliable under 100% disposable loss. Additional regressions exercise
independent retry and both-direction jitter ordering; server tests verify the delivery
class survives relay. The native 100%-disposable-loss flight passed on the new channel (`/tmp/nas-stream-lanes`):
599 grids, peak motion 0.477 m, final motion 0.137 m, zero disagreements and matching scores.
The expanded suite passed 208 tests/zero failures (`build/net-assert-artifacts/lanes-units.log`).
All final builds had zero warnings/errors.
The traced capture identifies delayed canonical delivery, but does not identify the exact
original UDP datagram responsible; attributing that datagram to a checkpoint is an inference.

## Preserving resting evidence through gather leases

An extra gather check caught a regression after the standard twelve soaks had passed. The unchanged
`dev e68ce64` passed the native seed-23 gather scenario; this branch failed it. Retaining resting
anchors exposed a distinction previously hidden by locally settled copies: an atomic gather lease
has not disturbed its members yet. Treating that lease as predicted motion discarded the target,
woke the observer, incremented disturbance revisions, and invalidated the accepted grant.

In `/tmp/nas-gather-diagnostic`, Bot1 requested drops `20100000`/`20100001` at shared time
96542.997183, asleep with revisions 2/1 and zero velocities. The accepted epoch-2 grant arrived at
96543.213260; revalidation at .216350 saw revisions 3/3, awake despite zero instantaneous velocity.
No gather started. Both body sync and Match's outgoing authority queue cleared the target. A trial
fixing only body sync still failed because the queue cleared it again.

Body sync now distinguishes a gather lease from predicted motion. It keeps following the existing
owner while that lease is pending; an actual local push during the lease switches back to ordinary
prediction. Grants/refusals clear that distinction. The Match integration separately marks gather
requests nonpredictive and preserves their target through its queue. Eligibility, disturbance
revision checks, collision validation and gather timings remain unchanged. Optional dev-only
`--gather-trace` records request/revalidation revisions; `--tank-trace` includes revisions too.

Two regressions failed before (210 tests/two failures, `gather-queue-before.log`) and passed after
(210 tests/zero failures, `gather-queue-after.log`): body sync continues applying the resting owner
through a lease without erasing a real predicted push, and the production queue preserves that
target. Builds were warning-free. Logs live in ignored `build/net-assert-artifacts/`.

The accepted lease exposed a second issue in the extra native scenario: the owner acquired and
settled both drops, but the observer counted only one glide. `/tmp/nas-gather-queue-repeat` traced
`20100000` epoch 2 receiving rest sequence 2 while gathering at 97084.716883, immediately after the
glide began. Godot can sleep a frozen body even though the gather owner animates its transform.
The new required initial publication therefore produced a premature rest; Match correctly treated
that packet as the end of gathering. A glide and that rest could arrive in one poll, hiding the glide
entirely from a rendered observer. Body sync now treats `Gathering` as moving for publication, and
only publishes rest after actual completion. No gather eligibility or animation rules changed.
The frozen-body regression failed before (211 tests/one failure, `gather-freeze-before.log`),
checking both motion during the frozen glide and final rest after completion.
Afterward the suite passed all 211 tests (`gather-freeze-after.log`) and the seed-23 native scenario
passed its gameplay assertions on both peers (`/tmp/nas-gather-freeze-after`). The diagnostic
assertion now prints shield/glide counts if it fails, without changing its criteria.
The repeated seed-23 faulted gather also passed (`/tmp/nas-gather-freeze-repeat`): 1100 grids,
peak rest/moving 0.015/0.604 m, final 0/0 m, longest disagreement episode 0.20 s, and matching
individual scores with a conserved bank of 14. The first corrected run had 1099 grids,
0.010/0.632 m peak, final 0/0 m and the same 0.20 s maximum. Neither capture had unobserved grids.

## Integrated verification after upright-pile dev merge

Merged dev `e61840d` in `3ee8c44`, then ran `make setup` and the complete gate before completing
the soak series. Two soaks overlapped the gate initially; subsequent runs were separated from it.
All use unchanged creator `tuning.tres` exported to peers, including VentThrust=821 and
VentMaxSpin=28. No tuner defaults or saved settings were edited for these results.

`/tmp/nas-complete-fault-1` **failed**, not invalid: 2000 grids, 93 unobserved pair grids, four
moving/lifecycle failures, zero persistent/final resting disagreement, final motion 0.307 m, and
matching score audits. Bot4 owned `10000006`, epoch 2. Bot1 stayed on sequence 30 at 97498.342799,
then reached correction silence 1.336 s at 97499.475091 while the owner advanced through sequence
46. Bot1 only reached 57 at 97500.721516. Its position episode lasted 2.25 s, peak 7.639 m;
the path to the sample had no obstacle. This is delivery starvation, not an anchor being discarded.
Drop `20000001`'s vent grant reached Bot2 1.182 s after server publication, and its mixed episode
lasted 3.05 s (peak 6.409 m across Bot2/3). `20200008` had a 2.15 s mixed lifecycle episode.
Maximum canonical delay over this capture was 1.897 s. The additional gate load is a confounder,
not proven to be the cause. No threshold is increased to accept this capture.

`/tmp/nas-complete-fault-2` **passed** during the gate: 1999 grids, no unobserved grids, longest
episode 1.70 s, final rest/moving 0.001/0.265 m, matching score audits, max canonical delay 0.949 s.

Fault 3 also **failed**, with 13 mixed/lifecycle failures, longest 4.90 s, final resting drift
0.001 m and final motion 0.461 m (within the final bound). All scores agreed. In this capture,
Bot1 received `20300011`'s vent grant 3.283 s after server time 97872.833921 and `20300010`'s
2.866 s after 97873.276324. Bot2 received `20100004`'s vent grant 2.116 s late; its removal
followed before all peers had displayed the vent. Native server loss statistics reached
0.6–0.9%, despite simulated loss applying only to disposable traffic. Other worktrees were
running generator/upright/rendered fixtures during this period; they were neither modified
nor interrupted. Native control retransmission/queue timing under load remains a different
residual mechanism from the fixed resting anchors, stale target age, vent spin clamp and
solid-surface traps. Exact lost UDP datagrams were not captured, so that attribution is an
inference. Fault 4 passed (longest 1.75 s, final rest/moving 0/0.238 m).

## Letting newer checkpoints pass an older retry

Fault 5 failed only drop `20100005`, epoch 2, owned by Bot2: Bot4 retained sequence 16 at
98130.467037 and had no fresh target by .765179, while Bot1 had reached 31 at .541336 and 36
at 98131.108425. Its mixed episode lasted 2.15 s. Canonical delivery in this capture stayed
below 0.810 s, and the path to the sample was clear. One reliable checkpoint lane prevented
newer checkpoints from passing an older retry even after moving them off control.

Every body's successive reliable checkpoints now rotate through three independently ordered
ENet lanes (1–3), with control still on 0. Selection is per body, not per batch: a repeatable batch
layout cannot pin a tank to one lane. The server preserves each lane; the fault decorator maintains
separate order and retry state. Existing owner/epoch/sequence ordering rejects late snapshots, so
an old retry cannot rewind the copy. Publication cadence and sample payloads remain unchanged;
additional batch headers are the only extra application traffic. This does not bypass congestion
control or guarantee delivery when all lanes stall. The Match grouping integration is isolated.

Two new regressions failed before (213 tests/two failures, `rotate-before.log`) and passed after
(213/zero, `rotate-after.log`): per-body rotation and fresh reliable replacements passing a two-second
first-lane stall under 100% disposable loss. Server tests also verify all three lane classes survive relay.
`--net-body-stall=2000` exposes that first-lane fault for the native harness only.

The native fixture command is `tools/dev/net_bots.sh -n 2 -l 20
-s '--net-loss=100 --net-body-stall=2000' -o OUT -- --scenario=body-stream`.
Before, `/tmp/nas-rotate-before` failed its observer movement assertion and lacked completion.
The first after-run (`/tmp/nas-rotate-after`) passed actual movement but failed the final moving
bound by 0.004 m (0.504 m; peak 0.846 m). The fixture now ends its straight flight at 15 s and
sleeps the owner, additionally verifying the final reliable rest instead of indefinite motion during
results. This only changes the dev fixture. Builds were warning-free.
The completed flight/rest fixture passed (`/tmp/nas-rotate-rest`): 600 grids, peak rest/moving
0.014/1.140 m, final 0/0 m, no above-tolerance episodes or missing grids, and matching scores.

Before the upright-pile merge and extra gather/lane fixes, the integrated `266d990` series
`/tmp/nas-proof-final-fault-{1..8}` and `/tmp/nas-proof-final-clean-{1..4}` passed every tank/score
check, with zero invalid captures. Fault longest episodes were 0.85/0.90/0.70/0.70/0.65/1.00/1.25/0.85 s;
clean were 0.70/0.45/0.30/1.15 s. Fault final motion peaked at 0.342 m, clean at 0.088 m.
This historical series is retained, not substituted for verification after the subsequent merges.

## Final rotated-lane gate

Merged dev `21a2ac2` in `2eff30d`; `make setup` passed with zero warnings/errors.
`TMPDIR="$PWD/build/net-assert-artifacts/tmp" make check` passed on this revision
(`rotated-check.log`): format verification, warning-as-error compilation, 213 unit tests with
zero failures, 20 comparer tests (0.272 s), three launcher tests (0.567 s), the existing physics/menu/input
fixtures, upright stand/topple/gather checks, bounded generation sweeps, and separate-process
world hashes for all four formations. The malformed-packet unit intentionally logs a runtime
warning; it is not a compiler warning. No test-full deep sweep or non-Linux native run was attempted.

## Final soak results and residual

The final code revision is `2eff30d` (only documentation/calibration comments follow it). Commands:

```sh
tools/dev/net_bots.sh -n 4 -l 90 -t faults -s '--tank-authority-trace' -o OUT -- --melee-start --digest-every=0.5 --net-lag=120 --net-jitter=30 --net-loss=3 --tank-authority-trace --tank-motion-trace
tools/dev/net_bots.sh -n 4 -l 90 -t clean -s '--tank-authority-trace' -o OUT -- --melee-start --digest-every=0.5 --tank-authority-trace --tank-motion-trace
```

`OUT` is `/tmp/nas-rotated-fault-N` or `/tmp/nas-rotated-clean-N`; the corresponding launcher
outputs are in ignored `build/net-assert-artifacts/nas-rotated-*-run.txt`. Fault 1 also used
`-w 1 --capture-dir=OUT/shots --shot-every=30`. Faults 9/10 added `--net-throttle-trace` at both
ends. Each comparison used the original thresholds, including final motion 0.50 m for faults
and 0.35 m for clean. Every capture was valid, with zero unobserved pair grids; every score audit
agreed and conserved the bank. Nine faulted soaks passed, plus all four clean ones. **The target
of all passing was not reached:** fault 7 failed only the final moving bound.

| Run | Result | Rest / moving peak (m) | Final rest / moving (m) | Longest episode (s) |
|---|---|---:|---:|---:|
| Fault 1 | PASS | 0.108 / 16.772 | 0.001 / 0.265 | 1.65 |
| Fault 2 | PASS | 0.015 / 8.782 | 0.000 / 0.171 | 0.85 |
| Fault 3 | PASS | 0.116 / 5.779 | 0.000 / 0.256 | 0.85 |
| Fault 4 | PASS | 0.067 / 10.512 | 0.001 / 0.153 | 1.30 |
| Fault 5 | PASS | 0.102 / 6.634 | 0.001 / 0.187 | 1.55 |
| Fault 6 | PASS | 0.093 / 4.475 | 0.001 / 0.274 | 0.45 |
| Fault 7 | FAIL | 0.212 / 15.162 | 0.000 / 0.531 | 1.60 |
| Fault 8 | PASS | 0.052 / 10.324 | 0.000 / 0.288 | 0.85 |
| Fault 9 | PASS | 0.063 / 12.646 | 0.000 / 0.152 | 1.70 |
| Fault 10 | PASS | 0.022 / 6.409 | 0.000 / 0.054 | 0.70 |
| Clean 1 | PASS | 0.058 / 3.455 | 0.000 / 0.094 | 0.35 |
| Clean 2 | PASS | 0.100 / 3.483 | 0.001 / 0.052 | 0.55 |
| Clean 3 | PASS | 0.019 / 4.568 | 0.000 / 0.070 | 0.35 |
| Clean 4 | PASS | 0.023 / 2.460 | 0.000 / 0.060 | 0.25 |

Fault 7's drop `2000000a` remained venting/moving at the end: owner Bot1, epoch 2, sequence 367.
At time 99973.966010 its speed was 2.293 m/s and spin 28 rad/s. Bot2/3 had sequence 364,
receipt silence 0.053 s and sampling age about 0.23 s; all targets were present, claims were
resolved, and no solid barrier was detected. Final positional differences were 0.504/0.531 m
against Bot1. During the shared results interval, this body's 1176 pair samples had median
0.166 m, p95 0.403 m, p99 0.504 m and peak 0.655 m. None exceeded the 3 m moving limit for
2 s, and final intact/resting tanks agreed. This is a different, rarer residual than discarded
rests or correction starvation: fresh physics tracking of a slowly translating, rapidly spinning
vent on the floor. Finite prediction/blend under nonlinear contact is the likely explanation;
that attribution is an inference, not an isolated proof of the contact dynamics. The strict
0.50 m final bound remains unchanged. Investigating that residual or calibrating a distinct
terminal active-vent budget is follow-up work, not silently folded into the noise tolerance.

No sustained resting, lifecycle or moving-window failure remained in this final series. Maximum
final resting drift was 0.001 m; longest fault episode 1.70 s (17.6% settle-window margin), clean
0.55 s (172.7% margin). Moving p99 across runs peaked at 2.340 m faulted (28.2% margin under
3 m) and 1.141 m clean (119.1% under 2.5 m). Maximum canonical delay was 1.160 s faulted and
0.265 s clean. The original fault-9 capture lacked owner/sequence tracing, so its exact 5.9 m
mechanism cannot be assigned retrospectively; traced reproductions established the corrected
freshness, clamp, obstacle and delivery mechanisms. Increasing the general moving or resting
thresholds was unnecessary.

The final gather check passed too: `tools/dev/net_bots.sh -n 2 -l 45 -s '--start-score=8'
-o /tmp/nas-rotated-gather -- --scenario=gather --tune.DropScatter=.2 --tune.DropToss=1
--tune.FixedSeed=23 --net-lag=120 --net-jitter=30 --net-loss=3`: 1099 grids, rest/moving peaks
0.007/0.527 m, final 0/0 m, longest 0.20 s, no missing grids and bank 14 on both peers.

The controlled physical failure also remained detectable on final code:
`tools/dev/net_bots.sh -n 2 -l 35 -s '--start-score=5' -o /tmp/nas-nudge-final --
--scenario=melee --tank-check-nudge=Bot2` exited 1 as expected. Both bots exited normally,
but intact/resting `10000000` differed by 2.000 m for 19.20 s. Eventual final agreement was
0/0 m and the score audit still conserved 10 initial - 5 spills = 5 banked. The 20 Python tests
also cover synthetic sustained/final divergences, transients, passes and invalid evidence.

Personally inspected final fault-1 `shots/Bot1_030.0.png` (shouldered rifle, suburb and HUD)
and `Bot1_060.0.png` (remote players, fire/smoke, broken fences, bank/ammo HUD). Earlier
faulted captures and frames remain recorded above. Visual inspection verifies rendering;
simultaneous logs establish body agreement.

Compatibility: no application message fields, Hello/Welcome/Rejected shapes, gameplay rules
or tuner defaults changed in this package. Production replication and ENet channel use changed;
Protocol.Version remains the unreleased 2, as instructed. Older implementations can decode the
same payloads but do not provide the new correction guarantees. Dev-only fault/trace/probe
flags remain outside gameplay. Native silent-peer detection is now 20–30 s to cover legitimate
construction stalls; that changes disconnect latency. Each protected Match integration is isolated:
`f30e21a`, `49bcb71`, `7d8b6f9`, `2eb194e`, `e75ad19`. No edits were made to PlayerCharacter,
Main, Game or PlayerInput; upstream changes, including upright construction, came through dev merges.
Linux/same-machine verification only; macOS, Windows, stock Ubuntu CI execution and cross-machine
capture alignment were not verified. No push or merge into dev/main was performed.
After the final documentation/calibration-comment update, `python3 -B tools/dev/compare_tanks_tests.py`
passed 20 tests in 0.174 s and `python3 -B tools/dev/net_bots_tests.py` passed three in 0.414 s.
`bash -n tools/dev/net_bots.sh` and `git diff --check` passed. There were no further behavioral changes.
Final targeted canonical-vent verification also passed:
`tools/dev/net_bots.sh -n 2 -l 20 -o /tmp/nas-race-complete -- --scenario=puncture-race
--net-lag=120 --net-jitter=30 --net-loss=3`, then
`python3 -B tools/dev/compare_vents.py /tmp/nas-race-complete/bot*.log`.
The new tank check passed 599 grids (fixture-teleport peak 35.683 m for 0.20 s, final motion
0.178 m); scores agreed. The retained vent tool passed canonical parameters and 280 trajectory
pairs with median/p95/max 0.093/0.194/0.328 m. This was run on final rotating-lane code.
