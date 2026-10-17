# Tank agreement in network soaks

**Status:** Dated record (calibration and capacity 2026-10-11, lobby bots 2026-10-13). The procedures and the
checker's rules that this report introduced now live in the [network soak runbook](../runbooks/network-soak.md); this
report keeps the calibration, the validation and the measured results. Its Protocol 2 statements were true when
written (the current protocol is 3).

[Independent participant integration review](participant-integration.md) records the merged six-run capacity
proof, measurements, verifier fixes and unresolved ordinary-play failures. The staged capacity gate passes;
those failures still block merge approval.

The renderer reserves 16 uniform slots per shader-bearing mesh. The project budgets 131072 slots (8192 meshes,
2 MiB of GPU storage); ordinary sixteen-player destruction exhausted the default 4096-mesh budget during return
to the lobby. `check_shader_capacity.py` allocates and retires 6000 production-material meshes twice. Its headless
assertion runs in `make test`; use `--rendered --output "$PWD/build/shader-capacity"` to prove native allocation
and inspect the capture. Both paths require successful assertions and clean engine logs. This technical buffer
budget changes no effect or gameplay values. See [Godot's allocator](https://github.com/godotengine/godot/blob/master/servers/rendering/renderer_rd/storage_rd/material_storage.cpp).

The duel fixture checks its firing ray and chooses a clear road lane when the victim blocks a spilled tank.
`--duel-blocked --tune.DropScatter=0 --tune.DropToss=0` deliberately stages that obstruction after the ordinary
hit/grace assertions. Its two-round native regression runs in `make test-full`, through the same tank and score
gates, without changing the gameplay rules or assertion bounds.

`net_bots.sh` runs `compare_tanks.py` and `check_scores.py` after every local match. Either failure makes the
launcher fail. `-k` skips both checks, including for an existing server (`-S`), whose audit log must be
obtained separately. Deliberate late construction or early-leave probes also need `-k`, since their captures
cannot cover a complete match. A single-bot run checks scores but cannot compare tanks. `-t clean` selects
tighter tank limits.

The `NetTest` probe is dev-only and enabled by the launcher with `--tank-check`. It records match ID, phase,
completion, tank ID, intact/venting state, resting/moving classification and millimeter-rounded position,
roughly 20 times per second. Resting means `AtRest` and not gathering. No ownership, gameplay rule, tuner or
message changes. The old `--tank-trajectories` probe and `compare_tank_paths.py` are retired. Keep
`compare_vents.py`: its puncture-race check also verifies canonical vent parameters and predicted contact
coverage, which this soak cannot establish.

## Alignment and failures

All bots must run on the **same machine**. Their OS monotonic clock (`Stopwatch`) is the shared sampling clock
for each match. It avoids both adjustable wall time and the independent packet-corrected `Match.TimeLeft`
countdowns. There is no nearest-sample pairing or ping-offset estimation. The checker interpolates positions
onto a common 50 ms grid only while the body retains its identity and lifecycle state. Creation, freeing and
state changes use the preceding observed state until the next sample; they remain visible rather than being
discarded.

Every unordered peer pair is checked. An identity/state difference or above-tolerance position difference may
last only through the settle window. Episodes retain their start across resting/moving and lifecycle/position
transitions; only actual agreement resets the window. Observed durations are quantized to the 50 ms comparison grid.
Intact bodies resting at both interpolation endpoints on both peers use the resting tolerance; other bodies
use the moving tolerance. Brief flight/correction and replication transients are counted and reported.
Persistent errors fail, even if later interaction destroys the body or the peers agree at the end.

The last common observed grid must be after every peer's end plus the settle window. Any above-tolerance difference
there fails immediately, regardless of duration. Final completion samples must also agree exactly on
IDs/states; bodies still moving during results use a tighter final moving tolerance for position.
Live motion ends at the client's receipt of lobby Waiting, when the server has retired that match's replication,
or at scene completion if that comes first. `tank-final` closes that trajectory; `tank-completion` still records
exact scene-completion identities and intact positions at the original resting/moving limits. The checker requires
both terminal rows in this format, rejects later trajectory rows, and applies the unchanged moving limit to the last common live grid.
Venting physics after replication retirement is local, so its completion positions are not shared motion.
Historical `tank-check ... final True` captures remain readable with their original completion-time motion check.
Missing/malformed samples, nonfinite values, duplicate IDs, clock/phase reversals, differing matches or
incomplete play/end coverage fail. Loading gaps are outside the comparison interval. A loaded machine may
start logging play up to the smaller of 10 s or 10% of the requested duration late. A playing/results gap may
last at most 10 s, and gaps above 0.35 s may occupy at most 10% of the captured play/results interval. The 10 s
bound has 69% margin over the observed 5.923 s stall; the startup bound covers the observed 7.30 s late start.
No positions or lifecycle comparisons are invented inside these wide gaps: affected pairs are reported as
unobserved. Gaps pause a disagreement’s observed duration without proving agreement or resetting its clock.
Smaller gaps still interpolate. The final common observed grid must follow settlement; exact completion
identities/states and intact resting/moving positions are checked separately. The live results interval supplies the
settlement time, without extending or freezing gameplay.

Limits are centralized in `PROFILES`/`Limits` in `tools/dev/compare_tanks.py`. They are harness tolerances for
the measured creator tuning and stated faults, not new game tuners. A stronger fault schedule or very slow capture can
fail the coverage check rather than yielding a misleading comparison. `--details` prints every above-tolerance
episode as (match, peer pair, body, kind, monotonic start, peak drift, states/classification, duration).

## Calibration, 2026-10-11

Measured on Linux, Godot 4.7.2 .NET/Jolt, from dev `b907f7f`, with only this package's dev logging changes.
Each run uses four smart bots, a requested 90-second match, `--melee-start --digest-every=0.5`, and the debug creator’s unchanged saved
tuning. Faulted clients add `--net-lag=120 --net-jitter=30 --net-loss=3`; the server is clean. These flags
mean 60 ms delay each direction, 0–30 ms additional jitter and 3% outgoing unreliable loss. The default fault
seed is 1; layouts and real-time interaction schedules vary. Other work on the shared machine can affect frame
scheduling.
 The saved tuning was retained throughout calibration and correction verification, including
VentThrust 821, VentMaxSpin 28 and TankMass 30 (baked defaults 260, 12 and 17). This is a
more forceful vent setup than a release default; none of these values was changed for a passing run.

Commands (after `make build`):

```sh
tools/dev/net_bots.sh -n 4 -l 90 -o /tmp/nas-fault-N -- --melee-start --digest-every=0.5 --net-lag=120 --net-jitter=30 --net-loss=3
tools/dev/net_bots.sh -n 4 -l 90 -t clean -o /tmp/nas-clean-N -- --melee-start --digest-every=0.5
python3 -B tools/dev/compare_tanks.py --details /tmp/nas-fault-N/bot*.log
```

Calibration uses the six valid fault captures 2–7 and clean captures 1/3. Subsequent validation runs use the
held limits; their disagreements do not expand the tolerance budget.

Distance quantiles use every matching identity/state on every unordered peer pair at each common grid point,
including the results interval. Resting and moving populations use the same classification as the checker.
For a sorted population of size N, percentile p selects index `floor((N - 1) * p)`; the reported calibration
bound is the largest per-capture percentile. Lifecycle duration is measured with the same continuous episode
clock as the assertion. Scratch distributions are saved beside each capture as `metrics.json`.

The fault profile uses a 2.0 s settle window, 0.10 m resting tolerance and 3.0 m moving tolerance. Across
those calibration captures, lifecycle transients reached 1.60 s (25% time margin). The highest moving p99 was
2.045 m (47% distance margin to 3 m); resting p95 reached 0.0124 m (over seven times that distance at 0.10 m).
Resting outliers that persisted were **not** treated as expected replication noise. Maximum moving drift is
deliberately not a hard instantaneous limit: fast chains can briefly exceed it and then converge inside the
settle window.

The clean profile uses the same resting tolerance but a 1.5 s window and 2.5 m moving tolerance. Lifecycle
transients reached 0.80 s (87% time margin); the highest moving p99 was 1.719 m (45% distance margin). The
resting failures remain failures under both profiles. Final moving tolerance is 0.50 m under faults and 0.35 m
clean: observed final peaks were 0.280 m and 0.196 m, respectively (about 78% margin in each). Thus a body
cannot retain the full flight drift allowance at the end.

| Capture | Tank result | Rest/moving peak (m) | Longest lifecycle (s) | Evidence |
| --- | --- | --- | --- | --- |
| Fault 2 | FAIL | 0.307 / 6.622 | 0.65 | `10000010` intact/resting disagreed for 31.95 s. |
| Fault 3 | PASS | 0.147 / 10.198 | 0.60 | Longest above-tolerance episode 0.80 s. |
| Fault 4 | FAIL | 0.138 / 6.303 | 0.75 | `10000004` intact/resting disagreed for 2.80 s. |
| Fault 5 | FAIL | 0.200 / 10.875 | 1.60 | `20000002` intact/resting disagreed for 57.50 s, including final comparison. |
| Fault 6 (rendered) | PASS | 0.069 / 5.106 | 0.95 | Longest above-tolerance episode 0.95 s. |
| Fault 7 | PASS | 0.121 / 7.611 | 0.60 | Longest above-tolerance episode 0.85 s. |
| Clean 1 | FAIL | 0.608 / 4.774 | 0.80 | `2000000c` intact/resting differed 0.109 m for 4.05 s; `2000001d` differed 0.124 m for 8.35 s. |
| Clean 3 | PASS | 0.033 / 2.629 | 0.45 | Longest above-tolerance episode 0.45 s. |

These labels correspond to `/tmp/nas-fault-N` and `/tmp/nas-clean-N` logs. Every completed score audit agreed
on all four individual standings and conserved the bank. Fault 1 lacked startup samples and failed coverage;
clean 2 had playing gaps of 2.652/4.018 s. They were rejected rather than interpolated, and are excluded from
calibration. Rendered fault 6 had two playing gaps of 0.275/0.277 s, and clean 3 reached 0.251 s. The original
cadence limit was 0.35 s, a 25% margin over the rounded observed 0.28 s maximum. These captures are included
after rechecking with the calibrated limit. A preliminary run edited while its launcher was active is also
excluded.

In fault 2, at 50.60 s after common play start, peers 1/2/4 had `10000010` at (-42.617, 0.276, 7.602); peer 3
had (-42.923, 0.276, 7.575). All classified it intact/resting. This is simultaneous horizontal disagreement,
not a fast body paired to a different time. Fault 5 demonstrates an error surviving the full results interval
despite exact final identities/states and scores. The checker establishes disagreement; this package does not
identify its ownership/collision cause or alter network/gameplay behavior to fix it.

The pre-fix dev baseline did not reliably pass this check: it has persistent resting-position disagreement. The replication follow-up is recorded in [network-corrections.md](network-corrections.md). Increasing the resting limit to accept these captures would conceal the behavior this
check is intended to detect.

## Validation with held limits

Additional runs keep every threshold unchanged. Fault 8 found intact/resting drops `20200008`, `20200009` and
`2020000a` 0.122–0.138 m apart at the end, with above-tolerance episodes up to 37.20 s. Fault 9 found venting,
moving `1000001b` over 3 m apart for 2.20 s (peak 7.698 m) and `10000005` over 3 m for 2.90 s (peak 5.932 m).
Its final moving peak was only 0.373 m and all final identities/states agreed. This is a sustained flight
disagreement that the old final-only path check would miss. No gameplay or network fix is included. Fault 10
additionally found intact/resting `20100001` displaced 0.113 m for 36.25 s; fault 11 found intact/resting
`20000010` up to 0.185 m apart at the end. Fault 13 found continuous mixed lifecycle/position errors for
`20000000` (2.30 s, 4.494 m) and `10000001` (2.10 s, 3.764 m). Fault 12 was invalidated by a playing capture
gap above 0.35 s. Fault 14 also failed the final moving check: venting `20300007` remained 0.746 m apart
between peers 1/3. It had resting and lifecycle/mixed failures too, including `1000001e` up to 1.446 m apart
while intact/resting.

Fault 15 failed only at final moving comparison: venting `20300003` was 0.585 m apart between peers 1/4. Clean
5 found intact/resting `20300007` up to 0.386 m apart for 44.95 s, including final comparison. Clean 6 passed
with a longest above-tolerance episode of 1.40 s and final resting/moving peaks of 0.037/0.318 m. Clean 4
failed coverage: peer 4 captured only 82.70 s of the 90-second playing interval.

With the original pre-fix checker, fault captures 3/6/7 pass; 2/4/5/8/9/10/11/13/14/15 fail; 1/12 are invalid. Clean
captures 3/6 pass; 1/5 fail; 2/4 are invalid. All 21 completed soaks passed the independent score audit. The
requested five passing faulted captures were **not achieved** after 15 attempts. Two clean-network captures
passed the tighter profile. No threshold was increased to absorb the validation disagreements.

## Controlled failure

`--tank-check-nudge=Bot2` selects one peer by name. After 20 seconds of the harness clock, it nudges an intact
resting tank 2 m and holds its physical pose against corrections while play continues. It changes actual scene
state, not the logged position. This fixture is opt-in; ordinary bots never execute it. Use the melee scenario
so the staged players cannot shoot the selected map tank:

```sh
tools/dev/net_bots.sh -n 2 -l 35 -w 2 -s '--start-score=5' -o /tmp/nas-nudge -- --scenario=melee --tank-check-nudge=Bot2 --capture-dir=/tmp/nas-nudge/shots
```

A nonzero exit with the injected body named in a sustained resting-position disagreement proves detection.

The physical nudge run exited 1 with `10000000` intact/resting displaced 2.000 m for 29.20 s, still different
at final comparison. Both bots exited normally and passed the melee scenario; standings agreed with the audit
(10 initial - 5 spilled = 5 banked). The synthetic CLI fixture likewise exits 1 on a sustained 2 m resting
offset.

## Original assertion verification record

Verified the implementation in `b98fe2d`; no gameplay/network repair was made to improve these results.

- `make setup` and `make build`: passed, zero warnings/errors.
- `TMPDIR=/tmp/nas-gate make check`: passed formatting, warning-as-error compilation and all `make test`
  partitions, including real physics/menu/world fixtures and separate-process hashes for all four formations.
  The new 15 comparer tests took 0.504 s; the three launcher tests took 1.934 s under shared CPU load.
- `TMPDIR=/tmp/nas-gate python3 -B tools/dev/lint_tests.py`: seven wiring/negative proofs passed. An initial
  run found incomplete scene metadata in that proof; the final version retains melee and includes the existing
  world-services and clear-game fixtures. This proves `test-full` wiring, not an actual deep-sweep run.
- `python3 -B tools/dev/compare_tanks_tests.py`, `python3 -B tools/dev/net_bots_tests.py`,
  `bash -n tools/dev/net_bots.sh` and `git diff --check`: passed.
- The 21 soak commands above were re-audited with the final comparer profiles and `check_scores.py`.
  `/tmp/nas-final-audit.txt` records every result; capture directories contain the raw logs. Fault 6 also used
  `-w 1 --shot-every=30 --capture-dir=/tmp/nas-fault-6/shots` for rendered inspection.
- `python3 -B tools/dev/compare_tanks.py /tmp/nas-nudge/bot*.log`: exited 1 on the actual physical offset.
- `python3 -B tools/dev/compare_tanks.py /tmp/nas-synthetic/a.log /tmp/nas-synthetic/b.log`: exited 1 on a
  synthetic intact/resting 2 m offset lasting 7.05 s, including final comparison. Passing/asynchronous and
  bounded-transient synthetic captures are also exercised through disk logs in the unit tests.

Inspected rendered fault-6 `Bot1_030.0.png` (ragdoll flight) and `Bot1_060.0.png` (active tank chain,
fire/smoke and bank HUD). Inspected nudge-run `melee_host_1_06.png` and `melee_join_1_06.png`: knocked-down
players and shielded spills render on both peers, with the expected 5/0 bank standings. These images verify
the scenes render; the physical offset is established by scene mutation and simultaneous tank logs, not by
these camera views.

Linux on this machine only: macOS, stock Ubuntu CI execution and cross-machine alignment were not verified.
The probe explicitly requires same-machine clients. At the end of the original assertion package, the
ownership/collision causes remained unverified; that package changed dev logging, verification and documentation
only. The subsequent [correction investigation](network-corrections.md) records replication fixes and their
verification. Message shapes, suburb construction, tuner defaults and `Protocol.Version` (2) remain unchanged.

Debug servers accept `-s '--match-seed=23'` in `net_bots.sh` to select the
initial generator candidate. Check the actual seed in `server.log`: an invalid
layout may retry. `--tune.FixedSeed` does not select multiplayer layouts. Fixed
layouts still do not reproduce real-time bot actions or packet schedules.

# Participant capacity (original A6 evidence)

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
The integrated gate lasts 120 seconds with 8 initial banked tanks per actor (the original A6 runs below lasted
60 seconds). A later 90-second run captured only 80.355 seconds of the delayed builder's play, correctly failing
the required 81 seconds. The longer capture requires 110 seconds of observed play: the existing startup allowance
remains the smaller of 10 seconds and 10% of the match. A regression rejects the same delay at 90 seconds and
rejects delays beyond 10 seconds even at 120 seconds. No checker bound changes. At least three clients
are needed to compare two survivors after creator departure. The final client delays construction by
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
standings and the tank-bank conservation audit. The integrated `compare_tanks.py` checks continuous aligned trajectories and exact completion using the
same bounds as ordinary soaks. `net_bots.sh` excludes only the capacity fixture's explicitly departed creator;
all survivors must complete the ordinary tank and score gates. Clean capacity runs select the clean profile.
The original A6 measurements below used an endpoint-only checker; they are historical evidence, not proof
of the integrated continuous gate.
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

Loading peers retain the network-correctness package's 20-second minimum/30-second maximum native grace.
Established players use the original 3-second minimum/8-second maximum; readiness and leaving end the
loading grace. The transport translates connection handles to native backend ids for timeout updates,
independently of server-issued gameplay peer ids.

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

# Lobby bots (A8)

Measured on October 13, 2026 on a shared 4-CPU cloud VM without a GPU (Mesa renders on the CPU). Timings here are
indicative only; rerun them on real hardware before relying on a number.

## Soaks

Each run is 90 seconds with `--melee-start`; `--lobby-bots=A,B` gives the first client A lobby bots and the second B.
Faulted runs add `--net-lag=120 --net-jitter=30 --net-loss=3`.

```sh
tools/dev/net_bots.sh -n 2 -l 90 -s "--match-seed=24" -- --melee-start --lobby-bots=2,1 --net-seed=1 --net-lag=120 --net-jitter=30 --net-loss=3
tools/dev/net_bots.sh -n 2 -l 90 -s "--match-seed=25" -- --melee-start --lobby-bots=1,2 --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3
tools/dev/net_bots.sh -n 2 -l 90 -t clean -s "--match-seed=26" -- --melee-start --lobby-bots=2,1
tools/dev/net_bots.sh -n 2 -l 90 -t clean -s "--match-seed=27" -- --melee-start --lobby-bots=1,2 --lobby-bot-difficulty=2
tools/dev/net_bots.sh -n 3 -l 90 -s "--match-seed=28" -- --melee-start --lobby-bots=1,2,0 --leave-at=40 --leave-who=Bot2 --net-seed=3 --net-lag=120 --net-jitter=30 --net-loss=3
```

| Run | Actors | Tanks | Longest episode | Final rest/moving | Audit | Scores |
| --- | --- | --- | ---: | --- | --- | --- |
| Faulted, seed 24 | 2 + 3 bots | pass, 0 disagreements | 0.55 s | 0.000 / 0.236 m | 27 map + 35 drops − 45 spills = 17 | agree |
| Faulted, seed 25 | 2 + 3 bots | pass, 0 disagreements | 0.40 s | 0.001 / 0.116 m | 39 + 54 − 70 = 23 | agree |
| Clean, seed 26 | 2 + 3 bots | pass, 0 disagreements | 0.10 s | 0.001 / 0.109 m | 39 + 27 − 43 = 23 | agree |
| Clean, seed 27, Hard | 2 + 3 bots | pass, 0 disagreements | 0.95 s | 0.004 / 0.000 m | 27 + 37 − 49 = 15 | agree |
| Faulted, seed 28, creator leaves at 40 s | 3 + 3 bots | pass, 0 disagreements | 0.50 s | 0.000 / 0.000 m | 35 + 35 − 43 = 27 | agree |

Every run logged zero engine or protocol errors. In the departure run Bot2 left with its two bots: the server kept all
three in the standings (0, 0 and 2 banked), both survivors agreed with the server's standings, and their final digests
list only the surviving actors. The leaver's log is omitted from the tank and score comparison, as `net_bots.sh` now
does for any `--leave-at` client.

## The creator's cost

These A8 measurements predate shared navigation and the owner's bot-scale tradeoff. The
[bot scale verification](bot-scale.md) records the later implementation, three-pair Debug/Release benchmarks,
co-owned lobby-bot soaks and the remaining failed synchronization case.

`--human=idle` keeps the creator's own player still, so the brains measured are the lobby bots'. One client hosts
every bot on map seed 23 for a 90-second match; the frame columns cover play after the first 12 seconds and before
the last 8, from `--frame-metrics`.

```sh
tools/dev/net_bots.sh -n 1 -l 90 -k -s "--match-seed=23" -- --human=idle --lobby-bots=7 --frame-metrics="$PWD/build/frames"
```

| Headless | Wall p50 / p95 | Process p50 / p95 | Physics p50 / p95 | Brain per think | Thinks per bot-second |
| --- | --- | --- | --- | --- | ---: |
| 1 human | 16.7 / 17.4 ms | 1.8 / 5.8 ms | 2.0 / 6.4 ms | — | — |
| 1 human + 3 bots | 16.8–17.0 / 57–109 ms | 5.9–6.1 / 16–20 ms | 18–19 / 61–267 ms | 1.09–1.54 ms | 120 |
| 1 human + 7 bots | 21–155 / 300–840 ms | 9.9–11.9 / 19–22 ms | 24–35 / 52–118 ms | 1.09–1.84 ms | 55–80 |

Ranges span two runs on the same seed, one before and one after the navigation savings below; the bots' choices, and
so their route searches, differ from run to run more than those savings do. With 7
bots on this VM the physics step cannot keep 120 Hz and the match runs slower than real time. Bots also bring the
match to life (blasts, vents and drops) that an idle player alone does not, which these frame times include. The
brain allocates 14–64 kB per think, almost all of it inside `BankingBot` and the physics query results; the runtime
glue allocates nothing per tick.

Rendered runs (1280×720 window, Mesa on the CPU) ran at 1–2 frames a second with or without bots, about 0.8–1.3 s of
rendering per frame, so they cannot separate the bots' cost and are not reported as numbers.

Navigation is about 90% of a brain's time: route searches advance up to `BotSearchPerTick` nodes for each bot on each
tick, and every node costs dozens of capsule sweeps and rays. A8 removes repeated queries without changing a result:
query objects are reused, a jump link checks its range before its walk test, and a navigation world remembers
standable and supported points for its tick. On the five-seed course, queries fell from 210188 to 131896 (37%), and
the twenty-seed course's output is identical to dev's. Bounding the creator's total cost regardless of bot count
would change bot behavior (a shared search budget per peer, a lower think rate, or a cap on bots per peer); that is
the owner's decision.

## For real hardware

Rerun on the 12-thread machine, rendered and headless:

- The frame-time table above, including 1 + 7 bots, and 1 + 15 bots for a full lobby on one creator.
- 8 actors: `-n 4 -l 90 -- --melee-start --lobby-bots=1` and `-n 2 -l 90 -- --melee-start --lobby-bots=3`, faulted
  and clean. The host raises the lobby's capacity to fit every client's bots.
- 16 actors: `-n 4 -l 90 -- --melee-start --lobby-bots=3` and `-n 8 -l 90 -- --melee-start --lobby-bots=1`, faulted
  and clean.
- Departure at both sizes: add `--leave-at=40 --leave-who=Bot2` (with `-n` of 3 or more, so two survivors compare).
