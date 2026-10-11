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

All six runs passed with 16 actors/peers, 16 valid separated starts, 84 map tanks and 19 ammo supplies. Each actor
resolved a throw; the creator departed and every survivor received a fresh authority epoch. The delayed owner's
inbox was populated and no inbox overflow occurred. All 15 survivor score maps matched all 16 server standings,
including the departed creator. Every audit conserved `128 initial + 1 map + 0 drop gains - 80 spills = 49 banked`.
The final tank check found 163 identical ids/states across every survivor.

| Run | Final drift (m) | Server process mean / p95 / max (ms) | Server physics mean / p95 / max (ms) |
|---|---:|---|---|
| clean-1 | 0.010 | 2.941/8.340/8.340 | 0.246/0.717/0.717 |
| clean-2 | 0.010 | 2.924/13.089/13.089 | 0.442/1.467/1.467 |
| clean-3 | 0.000 | 3.609/14.022/14.022 | 0.077/0.401/0.401 |
| faulted-1 | 0.010 | 2.776/10.863/10.863 | 0.418/3.349/3.349 |
| faulted-2 | 0.010 | 106.910/1147.243/1147.243 | 0.023/0.040/0.040 |
| faulted-3 | 0.010 | 1.579/3.537/3.537 | 0.076/0.393/0.393 |

| Run | Client samples | Client process mean / p95 / max (ms) | Client physics mean / p95 / max (ms) |
|---|---:|---|---|
| clean-1 | 1281 | 57.001/115.150/4445.633 | 22.806/66.645/155.137 |
| clean-2 | 1306 | 52.391/105.346/301.559 | 20.991/67.281/117.224 |
| clean-3 | 1311 | 51.164/115.006/364.457 | 21.398/68.763/192.346 |
| faulted-1 | 1300 | 53.278/97.614/3301.568 | 21.467/69.227/159.642 |
| faulted-2 | 1322 | 50.951/102.455/297.108 | 20.696/67.773/147.556 |
| faulted-3 | 1244 | 55.830/115.661/461.341 | 23.692/75.460/160.301 |

| Run | Peak server / client RSS (MiB) | Sampled client queue packets / bytes | Loading inbox peak packets / bytes | Server payload in / out (bytes) |
|---|---|---|---|---|
| clean-1 | 207.8 / 1208.2 | 0 / 0 | 2735 / 279333 | 3897426 / 48841021 |
| clean-2 | 209.1 / 1215.4 | 0 / 0 | 2642 / 269948 | 4117999 / 51295908 |
| clean-3 | 205.2 / 1213.2 | 0 / 0 | 2683 / 274111 | 4127630 / 51705191 |
| faulted-1 | 209.1 / 1214.8 | 206 / 19138 | 2521 / 257431 | 3736360 / 45155586 |
| faulted-2 | 207.7 / 1209.0 | 263 / 18853 | 2543 / 259871 | 3873076 / 47042932 |
| faulted-3 | 209.0 / 1219.7 | 309 / 19739 | 2173 / 221901 | 3914338 / 47669858 |

The sampled server queue was empty in every run. Faulted clients retained bounded delayed queues; their deliberate
unreliable losses did not lose required events. The largest loading inbox stayed below 4096 packets / 4 MiB.
The last faulted run recorded one known disposable drop-before-registration snapshot rejection; the other five
had none. The final identity/state/pose check still passed. Raw server/client logs and checker summaries were written under `build/a6-capacity-evidence/`.

Earlier development runs caught an established-peer timeout during construction, stale-clock stage timing and
invalid final comparisons of moving or incorrectly frozen observers. Those runs failed and drove the timeout,
barrier/timing and endpoint corrections described above; their failures were not converted into passing evidence.
An exploratory unstaged sixteen-bot run also reported the existing rifle get-up reach/contact assertion under
heavy contention. A6 leaves animation unchanged; its capacity scenario stages the required interactions, while
ordinary navigation and repeated combat remain separate probes. Animation and distributed frame budgets need
follow-up before treating this evidence as a performance guarantee or raising the ceiling.
