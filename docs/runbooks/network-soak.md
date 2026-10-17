# Network soaks

How to run the multiplayer soaks and how their checks judge a match. The commands for single scenarios are in
[multiplayer.md](../multiplayer.md#testing) and [net-scenarios.md](../net-scenarios.md); the calibration runs behind
the limits, and the measured capacity results, are dated records in the
[tank agreement report](../evidence/network-soak.md).

## Every local match is checked

`tools/dev/net_bots.sh` runs `compare_tanks.py` and `check_scores.py` after every local match. Either failure makes the
launcher fail. `-k` skips both, including for an existing server (`-S`), whose audit log must be obtained separately.
Deliberate late-construction or early-leave probes also need `-k`, since their captures cannot cover a complete
match. A single-bot run checks scores but cannot compare tanks. `-t clean` selects the tighter tank limits (use it
when no network faults are injected). A new `-o` directory is needed for every run; `-f` replaces old logs on purpose.

```sh
tools/dev/net_bots.sh -n 4 -l 90 -o "$PWD/build/soak-fault" -- --melee-start --digest-every=0.5 --net-lag=120 --net-jitter=30 --net-loss=3
tools/dev/net_bots.sh -n 4 -l 90 -t clean -o "$PWD/build/soak-clean" -- --melee-start --digest-every=0.5
python3 -B tools/dev/compare_tanks.py --details "$PWD"/build/soak-fault/bot*.log
```

The faulted flags mean 60 ms delay each way, 0 to 30 ms extra jitter and 3% loss of outgoing unreliable messages. Debug
servers accept `-s '--match-seed=23'` to select the first generator candidate; check the seed in `server.log`, since an
invalid layout may retry. `--tune.FixedSeed` does not select multiplayer layouts. A fixed layout still does not
reproduce real-time bot actions or packet schedules, so every soak varies.

The `NetTest` probe is dev-only and enabled by the launcher with `--tank-check`. About 20 times a second it records
match ID, phase, completion, tank ID, intact or venting state, resting or moving classification (resting means
`AtRest` and not gathering) and a millimeter-rounded position. It changes no ownership, rule, tuner or message. Keep
`compare_vents.py` too: its puncture-race check verifies canonical vent parameters and predicted contact coverage,
which this soak cannot.

## How `compare_tanks.py` judges a match

All bots must run on the **same machine**. Their OS monotonic clock (`Stopwatch`) is the shared sampling clock for
each match. It avoids adjustable wall time and the packet-corrected `Match.TimeLeft` countdowns. There is no
nearest-sample pairing or ping-offset estimation. The checker interpolates positions onto a common 50 ms grid only
while the body keeps its identity and lifecycle state. Creation, freeing and state changes use the preceding observed
state until the next sample; they stay visible rather than being discarded.

Every unordered peer pair is checked. An identity or state difference, or an above-tolerance position difference, may
last only through the settle window. Episodes keep their start across resting and moving and across lifecycle and
position transitions; only actual agreement resets the window. Durations are quantized to the 50 ms grid. Intact
bodies resting at both interpolation endpoints on both peers use the resting tolerance; other bodies use the moving
tolerance. Brief flight, correction and replication transients are counted and reported. Persistent errors fail, even
if later interaction destroys the body or the peers agree at the end.

The last common observed grid must be after every peer's end plus the settle window. Any above-tolerance difference
there fails immediately, regardless of duration. Final completion samples must also agree exactly on IDs and states;
bodies still moving during results use a tighter final moving tolerance. Live motion ends at the client's receipt of
lobby Waiting, when the server has retired that match's replication, or at scene completion if that comes first.
`tank-final` closes that trajectory; `tank-completion` records exact scene-completion identities and intact
positions at the resting and final moving limits. The checker requires both terminal rows in this format and
rejects later trajectory rows. Venting physics after replication retirement is local, so its completion positions
are not shared motion.

Missing or malformed samples, nonfinite values, duplicate IDs, clock or phase reversals, differing matches and
incomplete play or end coverage fail. Loading gaps are outside the comparison interval. A loaded machine may start
logging play up to the smaller of 10 s or 10% of the requested duration late. A playing or results gap may last at
most 10 s, and gaps above 0.35 s may occupy at most 10% of the captured play and results interval. No positions or
lifecycle comparisons are invented inside gaps above 0.35 s: affected pairs are reported as unobserved, and a gap
pauses a disagreement's observed duration without proving agreement or resetting its clock. Smaller gaps interpolate.

The limits are `PROFILES` and `Limits` in `tools/dev/compare_tanks.py`. They are harness tolerances for the measured
creator tuning and stated faults, not game tuners.

| Limit | Faults (default) | Clean (`-t clean`) |
| --- | ---: | ---: |
| Settle window | 2.0 s | 1.5 s |
| Resting tolerance | 0.10 m | 0.10 m |
| Moving tolerance | 3.0 m | 2.5 m |
| Final moving tolerance | 0.50 m | 0.35 m |

A stronger fault schedule or a very slow capture can fail the coverage check rather than yield a misleading
comparison. `--details` prints every above-tolerance episode as (match, peer pair, body, kind, monotonic start, peak
drift, states or classification, duration). Do not relax a limit to make a run pass: the report's calibration shows
which runs the limits were set from and which failures they exist to catch.

## Proving detection

`--tank-check-nudge=Bot2` selects one peer by name. After 20 seconds of the harness clock it nudges an intact resting
tank 2 m and holds its physical pose against corrections while play continues. It changes actual scene state, not the
logged position, and is opt-in. Use the melee scenario so the staged players cannot shoot the selected map tank:

```sh
tools/dev/net_bots.sh -n 2 -l 35 -w 2 -s '--start-score=5' -o "$PWD/build/soak-nudge" -- --scenario=melee --tank-check-nudge=Bot2 --capture-dir="$PWD/build/soak-nudge/shots"
```

A nonzero exit with the injected body named in a sustained resting-position disagreement proves detection.

## The capacity gate

The creator selects actor capacity from 2 through the advertised ceiling (16); new lobbies start at `Tuning.MaxPlayers`
(5). Capacity counts actors, including actors sharing a peer. The wire safety ceiling is 64 actors and peers.
Raising the advertised ceiling means repeating this gate at the proposed size.

```sh
make setup
make check
make test-full
tools/dev/capacity_acceptance.sh -n 16 -r 3 -w 1 -o "$PWD/build/capacity"
```

`make test-capacity` runs the same script with its defaults: three clean and three faulted matches (`-r` sets the
count), each launching a dedicated headless server and `-n` separate actor-owning ENet clients (3 to 16). The creator
renders at 960×540 when `-w 1` (the default); the other clients have no local views. Each match lasts 120 seconds
with 8 initial banked tanks per actor (`--start-score=8`). The faulted runs use 120 ms round trip, 30 ms jitter and
3% loss of unreliable traffic with deterministic seeds; reliable events stay ordered. At least three clients are
needed to compare two survivors after the creator departs. The last client delays construction by 26 seconds,
beyond the server's 20-second loading deadline plus countdown.

The `capacity` scenario validates complete frozen actor and peer maps and capabilities, unique separated starts and
exact scaled supplies. Every client contests one body's expected epoch. After every owner has published its staged
pose, two actor owners contest its puncture and a second shot detonates it near all victim owners. The creator
leaves 14 seconds after all actors have loaded. Stage timing uses monotonic elapsed time, independent of stale
buffered match clocks. The remaining clients check actor removal, fresh ownership epochs, departed standings and final
per-actor score agreement. Actors stay idle after the staged blast; navigation and repeated combat are covered by the
other soaks. For the final 10 seconds owners hold their live tanks and publish final reliable rest poses, so
observers use the production frozen gather interpolation path and keep their correction targets through results.

`check_capacity.py` fails on incomplete roster maps, missing proofs, engine, protocol or assertion errors, or inbox
overflow. The existing rejection of disposable `BodyStates` that overtake reliable drop registration is counted
separately; final convergence is still mandatory. It compares every survivor's per-actor scores with the server
standings and the tank bank conservation audit. `compare_tanks.py` checks continuous aligned trajectories and exact
completion with the same limits as ordinary soaks, and `net_bots.sh` excludes only the capacity fixture's explicitly
departed creator. Clean capacity runs select the clean profile. The checkers have headless regression tests in
`make test`.

Server diagnostics (`--capacity-metrics`, on in this gate) print process and physics cost, resident memory, transport
payload bytes, managed packet and byte queues and loading-inbox peaks. Native ENet queue bytes are unavailable through
Godot's API, and payload counts exclude UDP headers and retransmissions. The server samples every 10 seconds and
clients once a second. These are sampled costs, not frame-time traces or a throughput promise: all clients share one
machine, so a run shows functional capacity under contention, not 60 FPS on sixteen distributed machines.

Loading peers keep a 20-second minimum and 30-second maximum native ACK grace; established players use the 3-second
minimum and 8-second maximum. Readiness and leaving end the loading grace. See the transport contract in
[multiplayer.md](../multiplayer.md#transport-contract).

## Shader instance capacity

The renderer reserves 16 uniform slots per shader-bearing mesh. The project budgets 131072 slots (8192 meshes, 2 MiB
of GPU storage) in `project.godot`; ordinary sixteen-player destruction exhausted the default 4096-mesh budget during the
return to the lobby. `check_shader_capacity.py` allocates and retires 6000 production-material meshes twice. Its headless
assertion runs in `make test`; add `--rendered --output "$PWD/build/shader-capacity"` to prove native allocation and
inspect the capture. Both paths require successful assertions and clean engine logs.

## Rendered layout and device probes

Reproduce the capacity layout matrix and the device and view probes after `make build`:

```sh
for count in 2 8 16; do
  for size in 1280x720 1920x1080; do
    godot-mono --resolution "$size" --fixed-fps 60 --path . res://scenes/dev/capacity_ui_tests.tscn -- \
      --players="$count" --capture-dir="$PWD/build/capacity-ui-$count-$size"
  done
done
godot-mono --resolution 1280x720 --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- \
  --scenario=input --port=24684 --capture-dir="$PWD/build/capacity-input"
godot-mono --resolution 1280x720 --fixed-fps 60 --path . res://scenes/dev/session_actors_tests.tscn -- \
  --capture-dir="$PWD/build/capacity-session-actors"
```

## Lobby-bot soaks

`--lobby-bots=A,B` gives the first client A lobby bots and the second B (one count per client), at
`--lobby-bot-difficulty=0..2`. `--leave-at=S --leave-who=BotI` makes one client leave; `net_bots.sh` then omits that
client's log from the tank and score comparison while the survivors and the server audit still run all checks.
Use `--melee-start` and a distinct `--match-seed` per run. Faulted runs add `--net-lag=120 --net-jitter=30 --net-loss=3`.

```sh
tools/dev/net_bots.sh -n 2 -l 90 -o "$PWD/build/bots-fault" -s "--match-seed=24" -- --melee-start --lobby-bots=2,1 --net-seed=1 --net-lag=120 --net-jitter=30 --net-loss=3
tools/dev/net_bots.sh -n 2 -l 90 -t clean -o "$PWD/build/bots-clean" -s "--match-seed=26" -- --melee-start --lobby-bots=2,1
tools/dev/net_bots.sh -n 3 -l 90 -o "$PWD/build/bots-leave" -s "--match-seed=28" -- --melee-start --lobby-bots=1,2,0 --leave-at=40 --leave-who=Bot2 --net-seed=3 --net-lag=120 --net-jitter=30 --net-loss=3
```

To measure what the creator's process pays for its bots, keep its own player still and read the frame columns from
`--frame-metrics`:

```sh
tools/dev/net_bots.sh -n 1 -l 90 -k -s "--match-seed=23" -- --human=idle --lobby-bots=7 --frame-metrics="$PWD/build/frames"
```

The host raises the lobby's capacity to fit every client's bots. Timings on a shared, GPU-less machine are indicative
only; rerun them on real hardware (rendered and headless, with 1 + 7 and 1 + 15 bots, and with a departure at 8 and 16
actors) before relying on a number. The measured soaks and the creator's cost are in the
[report](../evidence/network-soak.md#lobby-bots-a8).
