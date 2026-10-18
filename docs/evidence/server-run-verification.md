# Server run ownership verification — 2026-10-17

**Status:** Targeted preservation, retirement, rendered and native capacity proof passed. The final committed-head
`make check` and `make -j4 test-full` run after this record; their outcomes are in the branch handoff.

Production baseline: v0.4.2 `d54d3df`; immediate production parent `6b6a796` (only the proof harness was added above
that release). Candidate production: `e37a079`. Harness: `486ce42`, with stronger client-held-route assertions in
`0009526`. All measurements use the same current probe sources on parent and candidate. Linux, GTX 1660,
Godot 4.7.2 stable .NET (`ed1daf0bf`), SDK 10.0.113, normal Debug assemblies. No frozen release reference changed.
Raw artifacts are worktree-local under `build/b1-*`; the capacity gate uses `build/a6-capacity/` and exact Lab
references use `build/rfp0-equivalence-10/`. Existing gate evidence was moved to `build/b1-preexisting-*` before
those gate directories were reused. Every native matrix run used a fresh output directory.

## Ownership and lifetime

| Owner | Before | After | Responsibility |
| --- | ---: | ---: | --- |
| `LobbyServer.cs` | 1122 | 739 | Connections, lobby admission/roster, staging, transport, application diagnostics and run routing. |
| `LobbyServer.World.cs` | 169 | 92 | Lobby-world choice, creation/attachment and retirement routing. |
| `ServerRun.cs` | — | 602 | One match or roaming lifetime: actor/peer state, action admission, relay policy, ledger, pickups, readiness, mode/clock and native tuning. |

The facade is 831 lines across its two files. The run is a plain sealed class with private owned state, synchronous
narrow delivery/loading/phase/return/log/authority-observation callbacks, and no server or private-dictionary access.
Match actors/profiles and creator debug capability freeze at start; roaming actors follow roster observations.
The existing server random source still supplies generation draws. Processing nodes, callback positions, RNG draws,
ledger traversal, deadlines, echo recipients, traffic classes and the unsupported-world-rebuild fallback are preserved.

Retirement detaches routing before disposing tuning. It is guarded against repeats; predelete also covers servers
freed without entering the tree. The focused suite exercises results return (with/without roaming), empty match and
roaming lobbies, staging replacement, life/revision exhaustion and departure, generation/rules/start-delivery/peer-loss
failures, roaming construction failure before/after attachment, multi-lobby shutdown and bare-node free. Each run
observes one release, invalid native tuning, and no route from either advertised lobbies or client-held lobby references;
repeated disposal remains a no-op. MatchEnd precedes Rejected in native results exhaustion.

```sh
make setup
make build
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ServerRunTests --server-native --match-seed=23 --port=24681
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- --suite=ServerRunTests --server-native --match-seed=23 --port=24681 --net-lag=120 --net-jitter=30 --net-loss=3
```

Final focused result: **20 tests, zero failures**, both clean and faulted; build: **zero warnings/errors**. The native
probes use real ENet sockets and codec/delivery in one process; they do not construct client worlds. Separate-process
loading/departure and client reconstruction are covered by the matrix and capacity gate below.

## Exact packet equivalence

The scratch-only observer records every receive/send: direction, peer, traffic class and full bytes in call order.
The working production sources are not instrumented. Both builds use the same observer and fixed seed 23:

```sh
python3 -B tools/dev/server_equivalence.py --revision=6b6a796 --label=b1-parent-final-trace --observe
python3 -B tools/dev/server_equivalence.py --revision=working --label=b1-candidate-final-trace --observe
for suite in ServerTests ParticipantServerTests KingOnlineTests LobbyWorldTests LobbySupplyTests; do
    for revision in parent candidate; do
        godot-mono --headless --path "build/b1-$revision-final-trace/project" res://scenes/dev/unit_tests.tscn -- --suite="$suite" --match-seed=23 --server-transcript="$PWD/build/b1-$revision-final-$suite.packets"
    done
    cmp "build/b1-parent-final-$suite.packets" "build/b1-candidate-final-$suite.packets"
done
```

Every suite passed and every comparison was byte-identical. The `ServerTests` substring filter also discovers
ParticipantServerTests; its transcript intentionally includes that suite again.

| Selection | Packet records | SHA256, identical parent/candidate |
| --- | ---: | --- |
| ServerTests | 1500 | `ec5d0a358456c1826f1fa1dd1d08f6093cd194d387959c735ef39189d1a47884` |
| ParticipantServerTests | 728 | `a02a543b9da01a64f2b08487f10e05c03be88434386d5dc80eb7b81ca74f4b7d` |
| KingOnlineTests | 549 | `aebad8154117533d9dac35db07a264e70cc92f95cb49019ad2017dcb848be025` |
| LobbyWorldTests | 449 | `deb37466bd23a632a19e2dddfde1637ef5bc35c7c08b7d4bffcd49230bd229bc` |
| LobbySupplyTests | 398 | `c93cc7dc5e1425452ee03d9e7aff35a591568a78926bbcf1ddbf2097eeb7669b` |

## Paired run costs

[All six pairs](server-run-bench.csv) retain allocations, mean, p95/p99 and workload counts. Three parent/candidate
pairs were followed by three candidate/parent pairs to check process-order bias. Each process used CPU affinity
`{4,5,10,11}`; no local build or native match overlapped these windows. Other work on the machine was not controlled.
Each mode warms 20,000 ticks and measures 30,000: one real actor-pose receive, eight body states on reliable body lane
2, and the whole server run tick through the facade. Bank/King have four actors across two peers; roaming has two.
Manual time advances from the fixture's current clock, exercising clock publication and King sampling.

```sh
python3 -B tools/dev/server_equivalence.py --revision=6b6a796 --label=b1-parent-warm-bench
python3 -B tools/dev/server_equivalence.py --revision=working --label=b1-candidate-warm-bench
# Alternate the two runtimes, then reverse their order; use the same child-process affinity for both.
godot-mono --headless --path build/b1-parent-warm-bench/project res://scenes/dev/unit_tests.tscn -- --suite=ServerRunBenchTests --server-bench --match-seed=23
godot-mono --headless --path build/b1-candidate-warm-bench/project res://scenes/dev/unit_tests.tscn -- --suite=ServerRunBenchTests --server-bench --match-seed=23
```

| Mode | Bytes/tick, parent → candidate | Mean μs, parent → candidate | p95 μs | p99 μs |
| --- | --- | --- | --- | --- |
| Tank Bank | 6868.12 → 6868.12 | 16.83 → 16.43 | 22.90 → 21.35 | 25.75 → 28.55 |
| King of the Cul-de-sac | 7070.46 → 7070.46 | 16.99 → 16.33 | 23.55 → 22.55 | 27.85 → 27.55 |
| roaming | 6813.65 → 6813.65 | 15.59 → 15.37 | 21.85 → 19.15 | 24.20 → 23.90 |

These are medians of per-process results, not pooled percentiles. Packet and payload totals agree in every pair:
Bank 60,500 / 19,024,500 B; King 60,518 / 19,025,886 B; roaming 60,000 / 19,020,000 B. There is no new steady
allocation or repeatable whole-run mean regression. Tail differences vary across pairs/order (including Bank's higher
aggregate p99); the shared-machine tails do not establish a small speedup or slowdown. This measures server receive
and run work, including encode/decode allocation, but excludes unchanged transport polling, OS scheduling work and
client rendering. The server has no rendered simulation; rendered clients are covered by the native runs.

Early short probes are retained in `build/b1-{parent,candidate}-bench-{1,2,3}.log`. They reset a clock that starts at
100 back to zero, skipping clock/sampling work, so they were rejected as workload proof and replaced by the corrected,
warmed probe. Their large scheduling spikes are not hidden or used in the performance conclusion. The initial native
exhaustion probe similarly set absolute time to 11 instead of advancing from 100, and timed out twice; both failed logs
remain (`build/b1-native-retirement{,2}.log`). Fixing the fixture's relative clock made it pass without a gameplay change.

## Native matrix

All **23 cases passed**, with **26 server match audits**; all applicable conservation/score and tank checks passed at
the unchanged clean/fault thresholds. An independent scan of every native matrix and capacity log found **zero Godot
engine or protocol errors**. King-late intentionally uses `-k`: all three clients must pass catch-up assertions and
agree on final scores; full-match tank coverage is checked for the two undelayed peers, as documented by the scenario.
It became ready at 73.1 s with `playing True` and ended with 217 minted/banked tanks, agreed by all three clients.

The four 90-second soaks (Bank/King × clean/faulted) each had four bots, one rendered client, complete audits and zero
engine errors/disagreements. The repeat runs exercised four/eight actors across two/four peers, two matches per
connection, embedded bots and fresh roaming returns. Creator and guest departure runs kept departed banks and
transferred body authority. Every command below also saves its own capture directory; selections without capture
expressions create none. To repeat, select fresh output names instead of overwriting these artifacts.

```sh
tools/dev/net_bots.sh -o "$PWD/build/b1-net-watch" -n 2 -w 1 -l 20 -s --match-seed=23 -- --scenario=watch "--capture-dir=$PWD/build/b1-net-watch/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-duel" -n 2 -w 2 -l 20 -s '--start-score=8 --match-seed=23' -- --scenario=duel --matches=2 --duel-blocked --tune.DropScatter=0 --tune.DropToss=0 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 "--capture-dir=$PWD/build/b1-net-duel/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-gather" -n 2 -w 2 -l 45 -s '--start-score=8 --match-seed=23' -- --scenario=gather --tune.DropScatter=0.2 --tune.DropToss=1 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 "--capture-dir=$PWD/build/b1-net-gather/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-pickup" -n 1 -w 1 -l 40 -t clean -s --match-seed=23 -- --scenario=pickup "--capture-dir=$PWD/build/b1-net-pickup/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-melee" -n 2 -w 2 -l 25 -s '--start-score=5 --match-seed=23' -- --scenario=melee --assert-grip --assert-motion --view=player --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 "--capture-dir=$PWD/build/b1-net-melee/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-movement" -n 2 -w 1 -l 35 -s --match-seed=23 -- --scenario=movement --assert-grip --assert-motion --assert-feet --view=player "--capture-dir=$PWD/build/b1-net-movement/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-puncture-race" -n 2 -w 2 -l 20 -s --match-seed=23 -- --scenario=puncture-race --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 "--capture-dir=$PWD/build/b1-net-puncture-race/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-body-stream" -n 2 -w 0 -l 20 -s '--match-seed=23 --net-loss=100 --net-body-stall=2000' -- --scenario=body-stream "--capture-dir=$PWD/build/b1-net-body-stream/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-readiness" -n 2 -w 0 -l 20 -s --match-seed=23 -- --scenario=body-stream --tank-check-ready-stall=Bot2 "--capture-dir=$PWD/build/b1-net-readiness/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-vent-contact" -n 2 -w 0 -l 30 -s --match-seed=23 -- --scenario=vent-contact --vent-contact-axis=mixed --tank-motion-trace --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 "--capture-dir=$PWD/build/b1-net-vent-contact/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-drop-refusal" -n 2 -w 2 -l 20 -s '--start-score=8 --match-seed=23' -- --scenario=drop-refusal --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 "--capture-dir=$PWD/build/b1-net-drop-refusal/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-king" -n 2 -w 1 -l 55 -s --match-seed=23 -- --scenario=king --mode=king "--capture-dir=$PWD/build/b1-net-king/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-king-late" -k -n 3 -w 0 -l 110 -s --match-seed=23 -- --scenario=king-late --mode=king --mint-seconds=0.5 --tune.PlayerSendRate=20 --delay-who=Bot3 --match-delay=70 "--capture-dir=$PWD/build/b1-net-king-late/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-appearance" -n 3 -w 3 -l 10 -s '--start-score=8 --match-seed=23' -c 12ABEF,12ABEF,F6F6F6 -- --scenario=appearance "--capture-dir=$PWD/build/b1-net-appearance/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-appearance-dark" -n 3 -w 3 -l 10 -s '--start-score=8 --match-seed=23' -c 000000,0000FF,FFFFFF -- --scenario=appearance "--capture-dir=$PWD/build/b1-net-appearance-dark/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-roam-clean" -n 2 -w 1 -l 20 -t clean -s --match-seed=23 -- --lobby-roam=10 --matches=2 --lobby-bots=1 "--capture-dir=$PWD/build/b1-net-roam-clean/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-roam-fault" -n 4 -w 0 -l 25 -s --match-seed=23 -- --lobby-roam=10 --matches=2 --lobby-bots=1 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 "--capture-dir=$PWD/build/b1-net-roam-fault/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-depart-host" -n 3 -w 0 -l 45 -s '--start-score=8 --match-seed=23' -- --lobby-roam=5 --lobby-bots=1 --leave-who=Bot1 --leave-at=20 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 "--capture-dir=$PWD/build/b1-net-depart-host/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-depart-guest" -n 3 -w 0 -l 45 -s --match-seed=23 -- --lobby-roam=5 --lobby-bots=1 --leave-who=Bot2 --leave-at=20 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 "--capture-dir=$PWD/build/b1-net-depart-guest/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-bank-clean4" -n 4 -w 1 -l 90 -t clean -s --match-seed=25 -- --melee-start "--capture-dir=$PWD/build/b1-net-bank-clean4/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-bank-fault4" -n 4 -w 1 -l 90 -s --match-seed=25 -- --melee-start --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 "--capture-dir=$PWD/build/b1-net-bank-fault4/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-king-clean4" -n 4 -w 1 -l 90 -t clean -s --match-seed=25 -- --mode=king --melee-start "--capture-dir=$PWD/build/b1-net-king-clean4/shots"
tools/dev/net_bots.sh -o "$PWD/build/b1-net-king-fault4" -n 4 -w 1 -l 90 -s --match-seed=25 -- --mode=king --melee-start --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 "--capture-dir=$PWD/build/b1-net-king-fault4/shots"
python3 -B tools/dev/compare_vents.py build/b1-net-puncture-race/bot*.log
python3 -B tools/dev/check_scores.py build/b1-net-king-late/server.log build/b1-net-king-late/bot*.log
python3 -B tools/dev/compare_tanks.py build/b1-net-king-late/bot1.log build/b1-net-king-late/bot2.log
```

Puncture race: 280 trajectory pairs, median/p95/max drift 0.110/0.273/0.414 m. Native outcomes are convergence proof,
not deterministic physics transcripts.

## Capacity and real front end

```sh
make test-capacity
godot-mono --disable-vsync --resolution 960x540 --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=lobby --capture-dir="$PWD/build/b1-menu-lobby" --port=24681
```

Capacity: all three clean and three faulted 16-client matches passed the dedicated checker, with 15 survivors agreeing
on 49 total banked tanks after the creator's throw/departure. Late builders were ready while the match was live.
All six retained zero tank disagreements; bounded missing/transient observations remain visible in the faulted logs.
This is the documented controlled capacity fixture, not ordinary sixteen-client performance/convergence acceptance.

| Run | Grids | Body pairs | Dedicated capacity result |
| --- | ---: | ---: | --- |
| clean-1 | 2,499 | 41,891,309 | PASS |
| clean-2 | 2,438 | 40,841,863 | PASS |
| clean-3 | 2,489 | 41,726,711 | PASS |
| faulted-1 | 2,485 | 41,557,297 | PASS |
| faulted-2 | 2,489 | 41,613,283 | PASS |
| faulted-3 | 2,497 | 41,768,306 | PASS |

Rendered lobby flow: **60 assertions passed** through actual keyboard/controller/mouse menu events, roaming guest
join/departure, bot addition, start cancellation, staging replacement, match/results and a fresh-world return.
Reviewed captures (paths relative to `build/`):

- `b1-net-watch/shots/watch_009.0.png`: local/remote players and match HUD in the suburb.
- `b1-net-duel/shots/{shooter_04.3,victim_throw_0.15}.png`: player hits, bank changes and blast throw.
- `b1-net-gather/shots/gather_join_settled.png`: intact gathered drops remain in the shared scene.
- `b1-net-pickup/shots/{pickup_0_before,pickup_2_reveal_0.65}.png`: pickup fixture and returned can/reveal.
- `b1-net-melee/shots/remote_getup_handoff.png`: remote recovery, held rifles and spilled tanks.
- `b1-net-movement/shots/remote_10_reload.png`: replicated player/reload view; grip/motion/feet assertions passed.
- `b1-net-drop-refusal/shots/victim_drop_refused.png`: victim bank 7 after refusal rollback.
- `b1-net-king/shots/Bot1_king_moved_pile.png`: moved zone, new pile and mint HUD.
- `b1-net-appearance/shots/Bot1_appearance_tags_glow.png` and
  `b1-net-appearance-dark/shots/Bot3_appearance_results.png`: duplicate colors, drops/tags and black/blue/white result chips.
- `b1-menu-lobby/{07_stage_countdown,10_return}.png`: stage over the live world and fresh roaming return.

## Gates and limits

`make check` passed the first complete fast gate (510 unit tests) before the final strict-loading/clock-capture
boundary polish. The final production then passed the exact parent transcripts, all native proof above and warning-free
builds; the strengthened fixture passed all 20 native retirement tests clean and faulted. `make test-equivalence`
passed the immutable wire/schema/world/publication checks and Bank, King, spill-chain, Short Fuse and Dissolve exact
Lab traces (`build/rfp0-equivalence-10/`). No expected reference was regenerated. Final committed-head gate commands
run after this record and are reported in the handoff:

```sh
make check
make -j4 test-full
```

Production changes stay in B1's server files and run-facing ModeRunner documentation. New tests use the shared
fixture owners; no other package implementation was edited. Linux runtime proof only; Windows/macOS were not run.
There are no message/schema, generated-world, tank-rule, tuner-value/classification or exposure changes.
`Protocol.Version` remains **3**. No processing/scheduling framework, push, merge or release was introduced.
