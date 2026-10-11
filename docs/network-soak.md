# Tank agreement in network soaks

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
exact scene-completion identities and intact resting poses. The checker requires both terminal rows in this
format, rejects later trajectory rows, and applies the unchanged moving limit to the last common live grid.
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
identities/states and intact resting positions are checked separately. The live results interval supplies the
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
