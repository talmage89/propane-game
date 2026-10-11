# Tested participant capacity

The creator selects actor capacity from 2 through the advertised technical ceiling, initially 16; new lobbies
start at `Tuning.MaxPlayers` (5). Capacity counts actors, including actors sharing a peer. Generation uses the
frozen actual actor count for world size, separated starts, tanks and ammo. Color presets do not bound capacity.
The wire safety ceiling remains independently bounded at 64 actors/peers.

## Reproduce the acceptance gate

```sh
make setup
make check
make test-full
tools/dev/capacity_acceptance.sh -n 16 -r 3 -w 1 -o "$PWD/build/a6-capacity"
```

`make test-capacity` runs the same six matches. Each launches a dedicated headless server and 16 separate
actor-owning ENet clients. The creator renders at 960×540; the other clients have no local views or cameras.
Every match lasts 60 seconds with 8 initial banked tanks per actor. The final client delays construction by
26 seconds, exceeding the server's 20-second loading deadline plus countdown. Three runs use a clean transport;
three use 120 ms round-trip lag, 30 ms jitter and 3% loss of unreliable traffic, with deterministic fault seeds.
Reliable events remain ordered under those faults.

The scenario validates complete frozen actor/peer maps and capabilities, unique separated starts and exact
scaled supplies. Every client contests one body's expected epoch. After every owner has published its staged pose,
two actor owners contest its puncture; a second shot detonates it near all victim owners. The creator leaves
14 seconds after all actors have loaded. Stage timing uses monotonic elapsed time, independent of stale buffered
match clocks. The remaining clients check actor removal, fresh ownership epochs, departed standings and final
per-actor score agreement.
Actors remain idle after the staged blast; navigation and repeated combat are covered by the separate soak.
For the final 10 seconds, owners hold their live tanks and explicitly publish final reliable rest poses. Observers
use the production frozen gather interpolation path, preserving correction targets through results; vents otherwise
wake and move after a rest pose. Merely freezing ordinary observers would prevent their velocity correction.
This allows 10 seconds of play plus results for a stationary convergence check.
The delayed actor is staged and its callbacks are attached before the ordered inbox is replayed.
The staged tank is held in place to make the blast/throw assertion repeatable; the normal melee, duel, gather and
90-second soak probes separately exercise moving bodies and unstaged play.

`check_capacity.py` fails on incomplete roster maps, missing proofs, engine/protocol/assertion errors or inbox
overflow. The existing rejection of disposable `BodyStates` that overtake reliable drop registration is counted
separately; final convergence is still mandatory. It compares every survivor's per-actor scores with server
standings and the tank-bank conservation audit. `compare_tanks.py --assert` requires completed-match snapshots from every client, identical live entity
ids and states, and at most 0.5 m final positional drift. Its observational mode still reports transient differences.
The checkers have headless regression tests in `make test`.

## Measurements and evidence

Measurements are engine process/physics cost (ms), process resident memory, transport payload bytes, managed
packet/byte queues and loading-inbox peaks. Native ENet queue bytes are unavailable through Godot's API; payload
counts exclude UDP headers and retransmissions. Costs are sampled, not frame-time traces or a throughput promise.
Packet and byte peaks are computed independently.
Server diagnostics run every 10 seconds; clients sample once a second, including loading and results. Bandwidth
uses the last per-peer server sample, retaining the departed creator's earlier sample.

The acceptance results below record this branch's Linux verification. Raising the ceiling requires repeating this
gate at the proposed size. A8 must also verify mixed humans and embedded bots inside a creator's process.

Established ENet peers allow a 30-second polling stall during synchronous scene construction. Connection
attempts still time out after eight seconds. The original attempt timeout also applied after connecting and
caused departures during simultaneous sixteen-client construction; the acceptance gate covers this correction.

Verified on Linux, 2026-10-11, with Godot 4.7.2 .NET, an i5-10400F (6 cores / 12 threads) and GTX 1660.
All clients and the server shared this machine; other worktrees also ran tests. The runs therefore demonstrate
functional capacity under contention, not 60 FPS on sixteen distributed machines. Samples include synchronous
construction and results; the largest process samples include loading stalls. RSS is per process, not GPU memory.

All six final runs on source commit `c37782f` passed with 16 actors/peers, 16 valid separated starts, 84 map tanks
and 19 ammo supplies. Each actor resolved a throw; the creator departed and every survivor received a fresh
authority epoch. The delayed owner's inbox was populated and no inbox overflow occurred. All 15 survivor score
maps matched all 16 server standings,
including the departed creator. Every audit conserved `128 initial + 1 map + 0 drop gains - 80 spills = 49 banked`.
The final tank check found 163 identical ids/states across every survivor.

| Run | Final drift (m) | Server process mean / p95 / max (ms) | Server physics mean / p95 / max (ms) |
|---|---:|---|---|
| clean-1 | 0.000 | 3.187/10.979/10.979 | 0.250/1.818/1.818 |
| clean-2 | 0.000 | 102.873/1203.638/1203.638 | 0.238/2.262/2.262 |
| clean-3 | 0.010 | 3.247/11.460/11.460 | 3.186/34.341/34.341 |
| faulted-1 | 0.010 | 1.742/4.690/4.690 | 0.037/0.148/0.148 |
| faulted-2 | 0.010 | 2.898/7.972/7.972 | 0.309/1.810/1.810 |
| faulted-3 | 0.010 | 2.486/10.089/10.089 | 0.218/1.032/1.032 |

| Run | Client samples | Client process mean / p95 / max (ms) | Client physics mean / p95 / max (ms) |
|---|---:|---|---|
| clean-1 | 1213 | 56.043/118.880/354.429 | 26.206/81.598/252.209 |
| clean-2 | 1149 | 62.212/139.402/530.062 | 26.085/81.619/217.032 |
| clean-3 | 1262 | 53.416/108.160/392.222 | 24.181/73.145/201.820 |
| faulted-1 | 1196 | 55.286/118.441/539.772 | 25.154/75.415/244.959 |
| faulted-2 | 1181 | 63.740/136.793/4816.309 | 25.835/76.541/143.759 |
| faulted-3 | 1159 | 68.967/137.350/7628.177 | 26.217/82.111/276.631 |

| Run | Peak server / client RSS (MiB) | Sampled client queue packets / bytes | Loading inbox peak packets / bytes | Server payload in / out (bytes) |
|---|---|---|---|---|
| clean-1 | 208.6 / 1211.4 | 0 / 0 | 2718 / 277736 | 3802675 / 46619739 |
| clean-2 | 210.7 / 1219.5 | 0 / 0 | 2234 / 227770 | 3753840 / 45808464 |
| clean-3 | 208.7 / 1216.9 | 0 / 0 | 2732 / 279064 | 4193782 / 53076920 |
| faulted-1 | 208.3 / 1221.2 | 189 / 19711 | 2482 / 253414 | 3534840 / 44138619 |
| faulted-2 | 208.3 / 1222.8 | 277 / 19844 | 1582 / 160774 | 3636001 / 44744178 |
| faulted-3 | 208.9 / 1223.0 | 223 / 27665 | 2308 / 235652 | 3421253 / 42195110 |

The sampled server queue was empty in every run. Faulted clients retained bounded delayed queues; their deliberate
unreliable losses did not lose required events. The largest loading inbox stayed below 4096 packets / 4 MiB.
Known disposable drop-before-registration snapshot rejections were absent from all six final runs. The final
identity/state/pose check passed in every run. Raw server/client logs and checker summaries are under
`build/a6-capacity-final-proof/`. An earlier six-run matrix also passed and remains under `build/a6-capacity-evidence/`.

Earlier development runs caught an established-peer timeout during construction, stale-clock stage timing and
invalid final comparisons of moving or incorrectly frozen observers. Those runs failed and drove the timeout,
barrier/timing and endpoint corrections described above; their failures were not converted into passing evidence.
An exploratory unstaged sixteen-bot run also reported the existing rifle get-up reach/contact assertion under
heavy contention. A6 leaves animation unchanged; its capacity scenario stages the required interactions, while
ordinary navigation and repeated combat remain separate probes. Animation and distributed frame budgets need
follow-up before treating this evidence as a performance guarantee or raising the ceiling.

## Additional regression evidence

`make check` passed with warning-as-error compilation, 230 unit tests, all fast suites and 68 real device input
assertions. `make test-full` also passed: 200 single-player and two-player seeds, 100 seeds at each of 8/16/24/32
actors, six forced seeds per formation/size, 53 upright piles (738 tanks), disturbed-pile rebuilding, 10 single-player
and 10 match lifetime cycles without orphans, repeated melee/reload cancellation checks and 700/700 navigation
courses (zero stuck events, 600 jumps). These larger generator sweeps do not raise the advertised network ceiling.

The rendered two-client probes used 120/30/3 faults and `--capture-dir="$PWD/build/a6-<scenario>-shots"`:

- Melee: `net_bots.sh -n 2 -w 1 -l 35 -s '--start-score=5' -- --scenario=melee --assert-grip --assert-motion --view=side`.
  Both grip/motion assertions passed, including two falls/get-ups and no additional spill from the empty bank.
  The bank audit was `10 initial - 5 spills = 5 banked`; 33 final tank ids/states agreed at 0.014 m drift.
- Duel: `net_bots.sh -n 2 -w 2 -l 35 -s '--start-score=8' -- --scenario=duel --matches=2`.
  Both consecutive rounds passed on the same connections. In each, both actors agreed on scores 9/1 and
  `16 initial + 1 drop gain - 7 spills = 10 banked`. The final round had 34 identical tank ids/states at 0.010 m drift.
- Gather: `net_bots.sh -n 2 -w 2 -l 45 -s '--start-score=8' -- --scenario=gather --tune.DropScatter=0.2 --tune.DropToss=1`.
  Both rendered clients asserted accepted shield colors, shimmer, atomic acquisition, settling and a stable pile.
  The audit was `16 initial - 2 spills = 14 banked`; 30 final tank ids/states agreed with zero drift.

Reviewed captures included both melee falls, the duel throw and the gather shimmer/settled pile. Production lobby
and results layouts were also rendered and reviewed with 2, 8 and 16 entries at 1280×720 and 1920×1080: all entries,
capacity controls, ranks, names and scores fit, including the departed standing. The real device input scenario
passed 68 assertions (keyboard/mouse and gamepad, menus, gameplay and the new capacity controls), with rendered
single-player/match turn, jump and pause captures. The shared-session two-view fixture passed and its captures were
reviewed after closing one view.

Reproduce the rendered layout matrix and device/view probes after `make build`:

```sh
for count in 2 8 16; do
  for size in 1280x720 1920x1080; do
    godot-mono --resolution "$size" --fixed-fps 60 --path . res://scenes/dev/capacity_ui_tests.tscn -- \
      --players="$count" --capture-dir="$PWD/build/a6-ui-$count-$size"
  done
done
godot-mono --resolution 1280x720 --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- \
  --scenario=input --port=24684 --capture-dir="$PWD/build/a6-input"
godot-mono --resolution 1280x720 --fixed-fps 60 --path . res://scenes/dev/session_actors_tests.tscn -- \
  --capture-dir="$PWD/build/a6-session-actors"
```

The ordinary four-client, 90-second soak used `-n 4 -w 0 -l 90 -- --melee-start --bot-log --digest-every=0.5
--tank-trajectories --net-lag=120 --net-jitter=30 --net-loss=3`. It recorded 21 swings and nine contacts. All four
final actor standings agreed (0/6/3/3); `28 map + 41 drop gains - 57 spills = 12 banked`. No engine/assertion errors
were logged. The trajectory checker asserted 24 final tank ids/states and compared 288,873 aligned playing pairs:
median/p95/max drift 0.000/0.273/5.438 m overall, 0.168/1.049/5.438 m on 31,311 venting pairs. These active-motion
distributions are observational, not the capacity gate's stationary endpoint limit. The coarse comparer recorded
488 comparisons. Artifacts are in `build/a6-melee/`, `build/a6-duel/`, `build/a6-gather/` and `build/a6-soak/`.
