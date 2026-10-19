# Moving-vent correction experiments (FU2b)

**Status:** In progress. The initial 0.125 s production candidate failed the final native matrix; it is not accepted.
Further effective-value experiments and final verification remain open.

2026-10-19, Linux x86-64, Godot 4.7.2 .NET/Jolt. Immediate parent: `3c07559` (C2).
The [FU2a investigation](moving-vent-residual.md) and [observation runbook](../runbooks/moving-vent-residual.md)
define the workload, independent fixture assertions and unchanged final-grid checker. Thresholds and fixture endings
remain unchanged. Fixed seeds do not fix native trajectories.

## Harness proof before production

- `make setup`: clean import/compile/strict import, zero warnings/errors.
- `python3 -B tools/dev/vent_residual_tests.py`: all 11 assertions pass, including refused checkout mutation and
  double experiment insertion. The original 10 observation/checker assertions also passed before adding experiments.
- `NetScenarioTests`: all eight registry/completion assertions pass on the unchanged parent.
- `BodyPredictionTests --correction-proof`: the parent fails the analytic accelerating-COM assertion as intended:
  commanded `(4, 1.2000008, 0)` versus current `(4, 0, 0)`. This oracle remains opt-in until production is corrected.
- Native `body_correction_tests`: existing force suppression, stale/predicted/granted-owner restoration, spinning
  custom/automatic COM and settled-contact assertions pass. The new read-only hull query detects a rotated floor
  overlap and reports 0.13655542 m upward recovery without changing pose or contact reporting.

`build/fu2b-before-arc` reproduces the faulted 300 m/s upward arc on the immediate parent: final 0.704 m, 601
comparison grids, 16,824 body-pair grids, zero unobserved grids, complete matching score audit and normal native
exits. The experiment advancing both predicted COM and feed-forward velocity finishes at 0.073 m; its clean control
finishes at 0.034 m. Selecting native hull probes instead of reported contacts gives 0.056/0.030 m respectively.
These are observed diagnostic runs, not performance samples or ordinary-match failure-rate estimates.

The first `make test` attempt collided with an experiment on the worktree's UDP port. The second rejected an empty
opt-in suite registration, correctly exposing a harness setup error. Both logs are retained; the suite now always
registers its opt-in test, matching the existing benchmark convention. Neither attempt is a passing gate.
The third complete `make test` passes: 668 unit tests, all behavior (including real-device menu input), generator
and separate-process world/segment checks. `build/fu2b-harness-gate3.txt` retains the complete invocation. The final
harness build is warning-free. The native `--correction-proof` contact oracle also fails on the parent as intended
when no contacts are reported; the unflagged native suite remains green.

## Experiment boundaries

`tools/dev/vent_correction_experiment.py` injects only into scratch builds and records its source delta. All four
candidates have separate Dev switches: bounded acceleration in both prediction terms, floor-hull support,
extended prediction lead and twice-as-frequent reliable checkpoints. Scratch-only tuners declare their bounds.
`VentCorrectionExperiment` is compiled only with the preparer's `FU2B_EXPERIMENT` define; ordinary Debug and
ExportRelease contain no experimental calls. Neither runtime source nor immutable references changed for this
harness commit.

The contact-report version is diagnostic only: [Jolt 4.7.2 source](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/jolt_physics/objects/jolt_body_3d.cpp)
shows `set_max_contacts_reported` also changes manifold reduction. Production must preserve owner contact policy.
The alternate native probe tests the existing body hull without enabling contact reporting or moving the body.

New mutable state is confined to scratch entry estimates and the existing match-owned trace writer. The cost probe
owns its fixture/native bodies for one test and retires them through `MatchClient`. Its measurements include complete
Match callbacks, publications and fixture updates; they do not advance Jolt. No new runtime dependency or baseline
exception is introduced. Final production choice, compatibility classification and complete verification follow here.

## Production candidate and reference refresh

`b953856` retains bounded low-spin free-flight acceleration in both prediction terms and shortens reliable
checkpoints from 0.25 to 0.125 network seconds without adding samples. Acceleration, floor support and extended lead
alone each fail 2/16 ordinary seed/fault cases; faster checkpoints pass 16/16. The floor and extended-lead changes
are dropped. Native hull queries are also dropped from production after their measured callback cost; eligible
observers instead temporarily report one native contact and restore their original setting at every exit boundary.
Owner settings are preserved. The unchanged obstruction ray reuses owned query scratch to reduce allocation/cost.
These are shared **minor-release** changes with three exported tuners; `Protocol.Version` remains 3.

The task explicitly permits a legitimate frozen-reference update in its own commit. Before refresh,
`make test-equivalence` fails three reference assertions, as expected. The complete explicit refresh command is
`python3 -B tools/dev/check_equivalence.py --refresh --production-revision=b953856 --output="$PWD/build/fu2b-reference-refresh"`.
The old fixtures remain under `build/fu2b-reference-parent`. Semantic comparison proves:

- All existing tuner entries, values, types, attributes, relative order, shared/local classification and UI entries
  are identical after removing `BodyCheckpointInterval`, `CorrectionMaxAcceleration`, `CorrectionAccelerationMaxSpin`.
- World and segment fixture bytes are unchanged. All five Lab fixtures are exactly identical after removing only
  the three new tuner metadata entries, including every window, summary and tick count.
- Only StartMatch/client and MatchStart/server wire fixtures change, carrying complete tuner dictionaries.
- Owner, epoch, sequence, pending/required state and awake state are identical at every publication step. Checkpoint
  lane/reliability/last-publication metadata and packet batching change: 48 to 53 envelopes. Common attempted body/rest
  rows and their sampling timestamps are identical, but the required-send-failure step attempts 19 fewer body rows:
  the new grouping reaches a reliable failure sooner and the existing disconnected-client guard suppresses later
  transport calls. The initial comparison incorrectly claimed identical complete attempted-row inventory before its
  assertion completed; that claim is withdrawn. The reference harness must retain explicit disposable-failure coverage.

`build/fu2b-reference-differences.json` retains the complete decoded differences. The manifest and provenance name
the reviewed production revision; no offline behavior change or threshold relaxation is hidden in the refresh.

## Retained rejected candidate and harness corrections

The unobserved `fu2b-final-matrix-26-1` rejects `b953856`: final moving drift **1.175 m**, complete audit, zero
engine errors or unobserved grids. The remaining v1 schedulers were stopped after their current native run so they
cannot be mistaken for completed acceptance cohorts. All completed and partial outputs remain under `build/`.

The first `fu2b-cadence05-*`, `fu2b-highspin-*` and `fu2b-preserved-short-*` experiments passed tuner overrides
only to the server. The creator's template replaced those values in StartMatch: recorded effective checkpoint
intervals remain **0.125 s**, and spin limits remain **6 rad/s**. Their names are misleading; they are explicitly
excluded from evidence for 0.05 s or 100 rad/s. This was caught by reading the new schema-4 configuration records.
Corrected experiments pass overrides to the clients and assert effective values before classification.

`--vent-residual-preserve-contacts` leaves ordinary owner physics unchanged. Validation of three completed runs
checks 149,353 / 162,050 / 90,595 owner rows, every reporting limit exactly zero; observer limits are zero or the
production correction's one-contact limit. The ordinary floor fixture still explicitly requests its eight contacts.
The first preserved prior run fails an inherited resting episode (2.35 s, peak 0.106 m), with final moving 0.182 m;
it is not a moving failure. No contact count with a zero reporting limit proves absence of native contact.

The publication reference harness now pins its historical 0.25 s checkpoint interval and asserts that both
disposable and required send failures actually occur. Against `build/fu2b-reference-parent`, the complete original
publication reference passes unchanged (one test, zero failures). A separate production publication test compares
0.25/0.05 s policies, requires sustained reliable motion at 0.05 s and proves identical per-body sample counts.
The eventual reference refresh will restore the original publication bytes rather than dropping failure coverage.

## Superseding reference review (876c22a)

The final reference workload pins its original 0.25 s cadence and asserts both disposable and reliable send
failures. The explicit refresh after rebuilding `876c22a` is retained in
`build/fu2b-reference-refresh-v2-verified.txt`; all five Lab scenarios and the wire/schema/world/publication pass.
`build/fu2b-reference-review.py` independently asserts byte-identical original world, segment and 48-envelope
publication fixtures. Removing exactly the three new tuner metadata entries makes the complete schema/UI/shared
snapshot and all five Lab documents identical to the original, including every window, summary and tick count.
Only StartMatch/client and MatchStart/server change among the 81 wire fixtures, solely their tuner dictionaries.
This supersedes the interim 53-envelope publication refresh, which did not preserve all negative-path coverage.
The earlier `fu2b-reference-refresh-v2` attempt was stopped after detecting an assembly built with the rejected
100 rad/s spin bound; its partial files and results are excluded. A warning-free rebuild precedes the verified refresh.

## Rejected 50 ms checkpoint-only candidate and settling experiment

`876c22a` retains the conservative acceleration spin bound and reserves every normal moving publication.
Its ordinary matrix/repeat results were healthy before scheduling was stopped, but the second early 240 ms
control fails at **0.566 m** (`fu2b-v2-age-early-2`), with native exits zero, complete matching audit and no
unobserved grid. The first passes at 0.214 m. The first report of the second result was sent before checking the
completed log and incorrectly stated a pass; it is withdrawn. Neither partial native cohort is final acceptance.
At the nearest final physical samples, the predicted node goal is 0.336 m from the owner while the observer is
farther away. This separates an additional correction residual from prediction error; it does not prove an
exact decomposition at the checker's interpolated grid. The lead is capped at 0.3 s, the sample is roughly
0.4 s old and its receipt remains fresh. Native high spin excludes acceleration on this body.

The separate Dev-only settling switch tests shorter vent steering (100/50 ms), leaving intact-body blending,
publication policy, acceleration bounds, lead limits and checker thresholds unchanged. Twelve preparer/diagnostic
assertions pass before running it, including refused checkout mutation and refused duplicate insertion. Every
client must assert the effective experimental blend value. Production is unchanged while these experiments run.

The analytic blend-scope oracle (`BodyPredictionTests --vent-blend-proof`) is proved red on the unchanged
250 ms controller: a one-meter moving-vent error commands 4 m/s instead of the proposed 10 m/s; intact and rest
checks pass. On the first Dev prototype, moving/intact checks pass but the rest check fails: a 0.4-second-old
rest anchors early. The experiment is narrowed to **moving** vents, preserving the original rest blend/deadline.
Both logs remain under `build/fu2b-vent-blend-oracle-{before,prototype}.txt`. This catches the scope regression
before any production blend change.

## Fast-spin prediction experiment

The 100 ms blend passes its sixteen ordinary experiments but fails the additional 240 ms case at 0.573 m
(`fu2b-blend100-extra-age-3`). Its nearest predicted node goal is already 0.584 m from the owner; gain alone cannot
close this residual. The last fitted acceleration is approximately `(-17.1, +0.58, -0.86)` m/s², while the delayed
body turns from upward to downward velocity. Applying that whole vector as constant acceleration is also invalid
for a rotating jet. The new Dev switch retains observed acceleration along the spin axis, uses native gravity
across it, and bounds the resulting vector by the existing acceleration limit. Native contact disables it.

With the original 250 ms response, six 240 ms experiments pass (final moving 0.239/0.371/0.328/0/0.248/0.291 m;
one has no final moving body). Two spinning arcs pass at 0.099/0.028 m. Ordinary matrix and final verification are
still open. The native component oracle fails on production at `(5, 1.2151974, 0)` versus `(5, 0.03799987, 0)`;
it passes on the experiment, proving transverse gravity and retained axial acceleration. The cost workload now
accepts a source spin so final measurements can exercise this path. Thirteen preparer assertions pass.

## Spin-component limits and authored-force follow-up

The complete spin-average matrix rejects that isolated model: **1/16 fails**, seed 27 / fault seed 3,
final 0.612 m. Its source spin is below the experimental 20 rad/s threshold; the predicted goal is about
0.22 m from the owner, while the observer is about 0.61 m away. The combined 100 ms response and spin average
still fails **1/6** 240 ms runs (0.512 m). There the predicted goal is already 0.535 m away. The source's fitted
axial component includes a recent contact impulse. All completed failures retain the original checker verdict.

Increasing normal tank publications to 30 Hz, with reliable checkpoints at 25 ms, does not solve this:
the original response fails 1/3 age controls (0.945 m), and the combined response/model fails 1/3 (0.586 m).
These overrides were verified on every client. Two earlier rate experiments requested 20 ms, which the shared
float range validator rejects at that endpoint; they never started and are excluded. No tank send-rate change
has entered production. Raw-clock publication/receipt matching in three failing bodies finds transit errors
of approximately -13 to +11 ms, rather than evidence of a large clock-estimation fault. No clock filter changed.

The next Dev-only switch replaces the fitted fast-spin axial component with the **existing** authored mean jet
force, retaining native gravity, the magnitude bound, contact exclusion and the separate moving-only response.
The scratch tank exposes its existing simulation vent age read-only for the authored thrust ramp; owner physics,
flutter and force application are unchanged. The native oracle uses a deliberately different measured axial
acceleration, checks the authored axial force independently, and replaces the native body with a zero-gravity
body while the old one remains alive. It fails on production and passes on the scratch model. Fourteen Python
preparer/observer assertions pass. Native acceptance is still open.

Cost caution: observed scratch projects retain observer hooks even when no trace is requested. Their extra
allocation is instrumentation overhead and is excluded from production cost comparisons. The clean combined
prototype measures 39,962,016 bytes versus 72,605,472 parent bytes across 60,000 whole client callbacks; three
alternating pairs retain their load records. This is a rejected prototype's cost, not final-head acceptance.
