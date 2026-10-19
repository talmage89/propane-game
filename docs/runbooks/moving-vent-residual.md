# Moving-vent residual observation

FU2a observes the immediate parent in a scratch Debug project. It changes no checkout runtime source, release
reference, correction threshold or fixture ending. The helper lives under `src/Dev/NetScenarios` (Dev module),
which `ExportRelease` excludes. Ordinary runs have no calls to it, even with the diagnostic flag. Scratch source
hooks deliberately depend on Dev and are for source Debug runs only; never export that project.

Run `make setup`, then prepare and **wait for the entire command to succeed** (it builds twice):

```sh
python3 -B tools/dev/vent_residual.py --revision=HEAD --label=fu2a-observed
```

The output project is `build/fu2a-observed/project`. `observation.json` records changed-source hashes and
`observation.patch` records its insertion-only runtime/host deltas and the added Dev ballistic case/registration. The rest of its runtime comes from the
specified revision. Asset imports are borrowed from this worktree. Do not rebuild the worktree while preparing
an unrelated revision's asset imports. The preparer checks exact anchors and refuses double instrumentation.

Run the existing floor fixture, with a fresh directory for every run:

```sh
python3 -B tools/dev/vent_residual_run.py --project=build/fu2a-observed/project \
  --output=build/fu2a-floor -- -p 24682 -n 2 -w 0 -l 30 -s '--match-seed=23' -- \
  --scenario=vent-contact --vent-contact-axis=mixed --tune.FixedSeed=23 \
  --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 \
  --vent-residual-dir="$PWD/build/fu2a-floor/traces"
```

FU2b's historical experiment preparer operates only on a fresh observed pre-FU2b parent (`3c07559`) under `build/`;
it rejects an already corrected runtime before modifying it:

```sh
python3 -B tools/dev/vent_correction_experiment.py --project=build/fu2a-observed/project
```

It records the patch and source hashes, adds scratch-only tuners and enables `FU2B_EXPERIMENT` in that
project. The ordinary game excludes the experimental implementation at compilation. Select candidates independently
with `--vent-experiment-acceleration`, `--vent-experiment-floor`, `--vent-experiment-age` or
`--vent-experiment-cadence`. Omitting all switches keeps the original equations (contact reporting remains enabled).
Use the same observed parent for comparisons; neither observation nor these experiments establish performance.
`--vent-experiment-probe` selects read-only native hull queries instead of contact-report observations. Jolt's
contact-report switch also controls manifold reduction, so do not copy observation's reporting policy into production.

`BodyPredictionTests --correction-proof` is an opt-in behavioral oracle, expected to fail on the parent. It aligns
the observer with an analytic accelerating COM and checks current velocity, rejected sequence isolation and reliable
rest anchoring. `BodyCorrectionTests` independently proves zero-motion hull queries detect a rotated floor overlap
without changing the body's transform or contact-reporting settings. `BodyCorrectionBenchTests --correction-bench`
measures 60,000 complete Match callbacks after 20,000 warm-up callbacks, with eight remote vents (four free-flight,
four floor), 15 Hz publications and 150 ms sample transit. Source poses and velocities are sampled 150 ms behind
the observer's analytic pose; assertions require displaced samples so the existing geometry queries are exercised.
`--correction-bench-aligned` retains the earlier diagnostic with coincident source/observer poses despite the transit.
The fixture resets poses without advancing native physics;
its allocation/mean/p95/p99 results do not replace native convergence or rendered observations. Copy this same probe
into the isolated parent and alternate at least three parent/candidate pairs with machine load recorded.

The wrapper records the exact command, exit status, `uptime` and Godot processes before, every five seconds during,
and after the run. It preserves failures and propagates the launcher's unchanged verdict. Omit `--project` and the
observation flag for an uninstrumented parent run. The historical P5 workload uses `-n 4 -w 0 -l 90`, server
`--match-seed=25`, and `--melee-start --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3` as client arguments.
Use `-w 1` for an invisible rendered client; capture with `--capture-dir="$PWD/build/fu2a-shots"`.
Inside the agent sandbox, process visibility is restricted to that agent; absence of other worktrees' processes
does not establish an idle machine. The load log labels this scope, and `uptime` remains machine-wide.

A successful observed run writes `traces/peer-*-match-*.jsonl`. Verify these exist before classifying it as
instrumented. The JSONL retains publication poses **after** the real sequence increment, incoming states before
admission (including rejected states), canonical authority, each vent correction decision and its reason, and
all bodies at the checker's exact sampling boundaries (including world liveness, pause and simulation time). Rests are recorded after their reliable send and sequence increment. It records monotonic sample/receipt clocks, ownership,
epoch, sequence, pending claim, pose/quaternion, mass-center offset, linear/angular velocities, targets, and native
contact collider/normal/impulse, and the actual observer force-suppression flag. Contacts refer to the latest available native physics state, not a prospective
collision proof. Reporting is capped at eight contacts per tank, enabled only in an attached observer. Each vent's
post-correction velocity is also recorded. Writer lifetime is one match; replacement, completion and tree exit
close it and reset its configuration marker. The first measurement records the effective correction/vent tuners and
the actual authored convex-hull points for offline support checks. Measurement boundaries flush it; a killed process can lose its last buffered records.

```sh
python3 -B tools/dev/vent_residual_report.py build/fu2a-floor/bot*.log \
  --traces=build/fu2a-floor/traces --output=build/fu2a-floor/alignment.json
```

This diagnostic first calls the unchanged checker. Its verdict is authoritative; the sidecar cannot turn a failure
into a pass. It writes the report and returns nonzero for a failed verdict or invalid coverage. It records the final common observed grid, both raw sample endpoints, interpolation span/weight,
independent final/completion times and every final pair distance. With traces it also extracts a two-second window
around the worst final body (`--body` and `--window` override that selection). It also reports authored hull minimum world Y at each peer's nearest correction decision when configuration data
is available. On vent-contact's y=0 clear floor this exposes impossible predicted support; it is not a general
collision or Jolt margin query. Full traces remain untouched. Gzipped
JSONL is also accepted. The checker compares node origins; use the recorded mass centers and quaternions to
separate translational drift, rotational offset and linear interpolation error. The `time` field uses the same raw Stopwatch clock as the checker; `network`, `sampled` and `updated` use each
process's own network clock, so compute ages in that domain. An incoming future state is recorded again when
`OnAuthority` replays it: the retained `network` receipt time distinguishes that replay from a new arrival. Physics frame counters establish order within one process only.

Observation adds serialization, allocation, contact-report storage and disk work. It does not establish performance
or deterministic replay, and fixed world/fault seeds do not fix native trajectories. Retain uninstrumented controls
and report other-worktree load. Do not change correction until the failing case separates target prediction,
post-correction native contact response, transport age, ownership and checker alignment.


The separate `ballistic` scenario isolates acceleration: no contacts, damping, rotation or jet thrust. It uses
native motion and the real puncture/authority path with a zero fixture impulse direction. Damping uses replace mode
so world damping cannot contaminate the control. It does not reset poses
or velocities after initialization, stop movement at the deadline, or change NetTest's results/completion rules.
A high initial altitude keeps the entire ordinary 20-second match and results interval clear of the floor.
`--ballistic-gravity=0` is the constant-velocity control; `1` enables ordinary engine gravity. Both require
`--tune.VentThrust=0`. Native assertions reject missing authority, accidental contacts/spin and a gravity selection
that did not take effect. The tank checker's verdict is separate; a diagnostic run can fail convergence after all
fixture assertions pass. These are fixture overrides, not changed game tuners or rules.
`--ballistic-vertical-speed=300` starts an upward arc when gravity is enabled, and a fast linear stream when it
is disabled. The arc approaches its apex near the ordinary final observation, separating acceleration from the
large wall-clock/physics-step alignment error possible at high speed. No velocity is reset at the apex.

```sh
# Run separately for GRAVITY=0 and GRAVITY=1, with fresh directories.
GRAVITY=1
python3 -B tools/dev/vent_residual_run.py --project=build/fu2a-observed/project \
  --output="build/fu2a-ballistic-$GRAVITY" -- -p 24682 -n 2 -w 0 -l 20 -s '--match-seed=23' -- \
  --scenario=ballistic --ballistic-gravity="$GRAVITY" --tune.VentThrust=0 \
  --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3 \
  --vent-residual-dir="$PWD/build/fu2a-ballistic-$GRAVITY/traces"
```
