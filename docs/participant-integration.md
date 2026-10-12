# Participant integration review

Independent verification of `wp/a6-participants`, Linux, October 11, 2026. **DO NOT MERGE**: the staged
sixteen-client gate passes, but ordinary play still fails engine and tank agreement checks. These failures were
not converted into passing evidence by changing gameplay rules or checker bounds.

## Integration and fixes

Merged `dev` at `5a82ac9`: recoil, animation review, and network correctness. Kept actor-keyed identity,
authority and scoring alongside rotating reliable checkpoint lanes, correction freshness, gather leases,
loading-time rests and one continuous tank checker through `net_bots.sh`.

- `50e6444`: integrate both models, translate loading-grace handles to native backend ids, and adapt the
  default-capacity and drop-publication fixtures to actor envelopes.
- `f5bfc85`: suppress body publications for every owned actor's unconfirmed drops. Moving and resting bot
  regressions failed before the fix and passed afterward.
- `eb9bbe7`: report `Loaded` after the first complete frame. A nine-second callback stall disconnected before
  the fix and completed afterward, preserving short established-peer failure detection.
- `fe5f412`: reject network runs with engine/protocol errors even when tank and score checks pass.
- `4eec41d`: test real keyboard, mouse and gamepad delivery against production layouts at 2, 8 and 16 entries.
- `3750dc8`: use 90-second capacity matches. The intentional 26-second delay plus construction yielded only
  53.23 seconds of a 60-second match, correctly failing the ordinary 90% coverage requirement. No comparison
  bound was relaxed. At least three clients leave two surviving replicas for comparison.
- `9c64370`: stop the loading frame if a required-send failure synchronously retires its session. The regression
  previously accessed a freed HUD label; afterward all 265 unit tests passed.
- `2b65c15`: isolate the native readiness regression with the controlled body stream. A separate melee-based
  attempt reached readiness but missed its second contact; the dedicated rendered melee scenario passes.

Repeated `git merge dev` checks remained up to date. Lab was still on its separate branch, not on `dev`, at
final proof; its bot extraction and firing-frame fixes were therefore not integrated or independently proved here.

## Cold review

Reviewed `git diff dev...HEAD`. Server actor ownership, membership and LifeId checks precede accepted actions;
pose sequences are separate from reliable actions. Roster requests have monotonic request ids and server revisions;
foreign/stale updates, replay, capacity bypass and exhausted actor/slot/life allocation are covered by assertions.
Body updates validate peer ownership, epoch and sequence. Same-peer contacts use the ordinary actor handler,
with predicted contacts and echoes deduplicated; ammo, grace, bank reservations and drop namespaces remain
per actor. Delayed chain credit cannot switch to another actor on the same peer.

Departure removes all owned actors, preserves their standings, transfers body authority with fresh epochs and
transfers creator controls without transferring the original debug-tuning permission. Readiness and broadcasts
remain per peer; starts and supplies count actors. The server still trusts the planned physical reporters for
contact geometry and blast distance and runs no physics.

Actor lookup is keyed; body correction is linear per peer. There is one world and one body synchronizer per peer.
Relay work and bandwidth grow with moving bodies times recipients. Full roster broadcasts are quadratic only
on waiting-lobby mutations, not each physics tick. Native ENet queue byte counts remain unavailable.

## Final gates and device/UI proof

All scratch, logs and captures are under `build/va6-*`; commands use the invisible `godot-mono` wrapper.
`TMPDIR` was set to a worktree build directory throughout.

```sh
make setup
make -j4 check
make -j4 test-full
godot-mono --resolution 1280x720 --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- \
  --scenario=input --port=24984 --capture-dir="$PWD/build/va6-input"
```

Warning-free builds and `make check` pass; final `make test-full` passes with 265 unit tests, deep generation at
200 solo/two-player seeds and 100 seeds each at 8/16/24/32 actors, forced formations, lifetime/reload/melee probes,
and 700/700 navigation courses, zero stuck events and 600 jumps. The final native readiness stream passes after
its nine-second callback stall. A previous full attempt failed the unrelated melee second-contact fixture;
that failure remains in `build/va6-readiness-melee-gate-failed/` and `build/va6-proof-full.log`.
Some earlier solo-clear runs logged the existing rifle reach error despite successful suite exit status.

Rendered `menu_test --scenario=input` passes 74 assertions. Reviewed before/after turn and jump captures in
single player and multiplayer; mouse, keyboard and controller actions reach the production world viewport.
All twelve original production lobby/results captures for 2/8/16 entries at 1280×720 and 1920×1080 fit, including
ranks, scores and departed markers. Expanded layout probes also pass real `Input.ParseInputEvent` Tab/Enter,
gamepad row navigation/A and scaled mouse clicks at every count/resolution, plus all three headless counts.

```sh
for count in 2 8 16; do
  for size in 1280x720 1920x1080; do
    godot-mono --resolution "$size" --fixed-fps 60 --path . res://scenes/dev/capacity_ui_tests.tscn -- \
      --players="$count" --capture-dir="$PWD/build/va6-ui-input-$count-$size"
  done
done
python3 -B tools/dev/check_liveness.py --output build/va6-final-liveness-client
python3 -B tools/dev/check_liveness.py --silent server --output build/va6-final-liveness-server
```

Native silent client/server detection passed in 4.275/3.422 seconds. The ordinary nine-second loading-stall
fixture and the new readiness-callback stall both survive. Native grace remains 20/30 seconds only while loading,
3/8 seconds after readiness; no blanket 30-second established-peer timeout remains.

## Sixteen-client acceptance

```sh
tools/dev/capacity_acceptance.sh -n 16 -r 3 -w 1 -o "$PWD/build/va6-capacity-isolated"
```

All six isolated runs pass: three clean and three at 120 ms lag, 30 ms jitter and 3% unreliable loss. Each proves
16 actors/peers, 16 separated starts, 84 map tanks and 19 ammo supplies, delayed loading, contested authority,
throws, creator departure and fresh epochs. Every survivor agrees on all sixteen standings, including the
creator's departed standing. Every audit conserves `128 initial + 1 map - 80 spills = 49 banked`.

The ordinary continuous tank checker compares roughly 31 million body pairs per run, with zero disagreements.
Final drift is at most 0.001 m; longest above-tolerance episodes are 0.25–0.45 seconds. No protocol error,
disposable registration rejection or inbox overflow occurs. Earlier exploratory capacity runs failed early
construction liveness and later insufficient late-client coverage under simultaneous suites; those failures
remain recorded. The final matrix was isolated from those suites.

| Run | Server process mean/p95/max (ms) | Client process mean/p95/max (ms) | Peak server/client RSS (MiB) | Client queue packets/bytes | Loading inbox packets/bytes |
|---|---|---|---|---|---|
| clean-1 | 1.887/6.473/6.473 | 34.954/68.961/410.643 | 208.8 / 1215.2 | 0/0B | 2541/314541B |
| clean-2 | 2.116/11.421/11.421 | 35.092/68.928/472.832 | 208.4 / 1210.6 | 0/0B | 2494/326954B |
| clean-3 | 2.072/6.444/6.444 | 33.863/68.696/403.985 | 210.1 / 1209.5 | 0/0B | 2538/303330B |
| faulted-1 | 2.772/17.990/17.990 | 34.123/67.720/437.690 | 208.1 / 1214.2 | 170/17300B | 2468/310760B |
| faulted-2 | 47.307/811.566/811.566 | 35.251/72.193/429.559 | 209.4 / 1213.7 | 181/16985B | 2460/327184B |
| faulted-3 | 51.320/895.894/895.894 | 33.708/60.390/485.191 | 209.2 / 1211.5 | 147/14715B | 2478/302020B |

Server physics mean/p95/max ranges from 0.114/0.753/0.753 to 0.386/2.961/2.961 ms. Client physics means are
29.482–32.966 ms; p95 is 64.906–67.089 ms. The largest client physics sample is 3626.286 ms during loading.
Keep the 811.566/895.894 ms server startup samples in the record. All processes shared an i5-10400F and GTX 1660;
these are functional capacity measurements under contention, not distributed 60 FPS guarantees. RSS excludes
GPU memory. Sampled server queues were empty; delayed client queues and loading inboxes stayed bounded.

Payload bandwidth excludes UDP headers, ACKs and retransmissions. From per-peer server counter intervals:

| Run | Server mean/peak upload (Mbps) | Client mean/peak upload (Mbps) | Client mean/peak download (Mbps) |
|---|---:|---:|---:|
| clean-1 | 4.304/8.955 | 0.022/0.083 | 0.281/0.565 |
| clean-2 | 4.217/9.001 | 0.021/0.095 | 0.275/0.566 |
| clean-3 | 4.424/8.795 | 0.022/0.089 | 0.289/0.552 |
| faulted-1 | 3.742/7.970 | 0.019/0.081 | 0.244/0.500 |
| faulted-2 | 4.212/8.651 | 0.021/0.092 | 0.275/0.544 |
| faulted-3 | 3.888/8.319 | 0.019/0.109 | 0.254/0.524 |

A 10 Mbps host uplink has little margin at the measured 9 Mbps payload peak once overhead/retransmissions are
included. An uplink comfortably above that peak fits these observed rates; 20 Mbps leaves substantially more
room. Actual WAN congestion and distributed clients were not measured. Do not extrapolate a larger ceiling
from these runs or from the generator's 24/32-actor sweeps.

## Ordinary soaks and unstaged stress: unresolved failures

Commands are recorded exactly in `build/va6-network-commands.txt`; `build/va6-network-proof.sh` runs the matrix.
Five faulted and three clean four-bot, 90-second `--melee-start` soaks completed, using map seeds 24–28 and
fault seeds 1–5. **None is a passing run**: all eight log rifle reach errors and the launcher rejects them.
All score/conservation audits agree. Seven continuous tank gates pass; clean-1 fails vent `10000010`, peer pair
2/3, for 1.75 seconds above the clean 1.5-second bound, peaking at 3.300 m from monotonic time 133598.85.
Final tank poses converge. The motion failure remains unclassified under the error-heavy run; repeat after the
firing-frame issue is fixed rather than enlarging the bound.

The rifle error reproduces on isolated **dev `5a82ac9` offline**, with:

```sh
godot-mono --headless --fixed-fps 60 --path BASELINE res://scenes/dev/game_test.tscn -- \
  --scenario=clear --quit=32 --tune.FixedSeed=23 --effects-seed=4
```

`build/va6-baseline-clear-4.log` records the unreachable-wrist assertion. Baseline source/import copies were
removed after proof; logs remain. This is an animation/firing-frame defect predating participant identity changes,
not an A6 actor mismatch. The unmerged Lab contains related firing-frame fixes; those are still unverified here.
No character-animation or gameplay-rule change was made to disguise it.

```sh
tools/dev/net_bots.sh -n 16 -l 90 -w 1 -s '--capacity-metrics --match-seed=23' \
  -o "$PWD/build/va6-unstaged16" -- --bot-log --digest-every=1 --shot-every=15 \
  --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/va6-unstaged16/shots"
```

The unstaged run also fails: 5128 rifle errors and sixteen final tank disagreements on one venting body, with
maximum final drift 0.643 m above the unchanged 0.5 m limit. The longest active above-tolerance episode is
1.50 seconds. All sixteen client standings agree: `60 map + 2 recovered drops - 14 spills = 48 banked`.
Reviewed its ordinary navigation captures. Server payload upload mean/peak is 3.136/5.618 Mbps; client payload
upload is 0.013/0.120 and download 0.196/0.360 Mbps. The reduced live-body count and machine contention differ
from the staged fixture, so the lower rate does not supersede its 9 Mbps peak. Native packet/queue pressure and
frame budget on separate machines remain unproved.

## Scripted network scenarios

All four rendered probes at 120/30/3 pass engine, continuous tank and score checks. Reviewed melee spills, the
duel throw, gather shimmer/settled pile and both canonical vent views.

```sh
tools/dev/net_bots.sh -n 2 -w 1 -l 35 -s '--start-score=5 --match-seed=23' -o "$PWD/build/va6-melee" -- \
  --scenario=melee --assert-grip --assert-motion --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/va6-melee/shots"
tools/dev/net_bots.sh -n 2 -w 2 -l 35 -s '--start-score=8 --match-seed=23' -o "$PWD/build/va6-duel" -- \
  --scenario=duel --matches=2 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/va6-duel/shots"
tools/dev/net_bots.sh -n 2 -w 2 -l 45 -s '--start-score=8 --match-seed=23' -o "$PWD/build/va6-gather" -- \
  --scenario=gather --tune.DropScatter=0.2 --tune.DropToss=1 --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/va6-gather/shots"
tools/dev/net_bots.sh -n 2 -w 2 -l 20 -o "$PWD/build/va6-puncture-race" -- \
  --scenario=puncture-race --net-lag=120 --net-jitter=30 --net-loss=3 --capture-dir="$PWD/build/va6-puncture-race/shots"
python3 -B tools/dev/compare_vents.py build/va6-puncture-race/bot1.log build/va6-puncture-race/bot2.log
```

Melee conserves `10 - 5 = 5`; each duel round conserves `16 + 1 - 7 = 10`, with stable actor ids and new lives;
gather conserves `16 - 2 = 14`. Canonical vent agreement covers 280 trajectory pairs, median/p95/max drift
0.097/0.190/0.302 m. Maximum final motion drift in these probes is 0.175 m.

## Compatibility and follow-up

Participant message shapes, shared identities/roster mapping and entity allocation require a minor release.
`Protocol.Version` stays **2**, as mandated for this unreleased cycle. Hello, Welcome and Rejected retain their
layouts. Verifier fixes add no wire fields or gameplay-rule changes. The actor ceiling must not be treated as
ordinary-play approval until the failed soaks and unstaged agreement are resolved. Integrate and prove Lab when
it lands; embedded-bot topology remains A8 work. Windows/macOS and real WAN operation were not verified.
