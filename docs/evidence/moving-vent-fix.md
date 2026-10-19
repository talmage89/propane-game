# Moving-vent correction experiments (FU2b)

**Status:** FU2b candidate verification complete: controlled/native cohorts and the final full sweep pass.
Two earlier deep-sweep failures are retained below with unresolved causes and clean paired diagnostic runs.

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

## Authored mean-force candidate

The authored-force switch plus moving-only 100 ms response passes all six 240/30/3 controls:
final moving 0.117 / 0 / 0.230 / 0.244 / 0.314 / 0.365 m. The empty final-moving case is retained as such.
Three faulted terminal arcs pass at 0.024 / 0.123 / 0.029 m; clean arcs pass at 0.049 / 0.040 / 0.001 m.
Two 28 rad/s arcs pass at 0.018 / 0.016 m. Faulted/clean floor fixtures pass at 0.273 / 0.053 m.
All native fixture assertions and score audits pass, with zero engine errors or unobserved grids.
These observed runs preserve the original owner contact policy. The ordinary sixteen-case experiment matrix
is recorded separately from the eventual uninstrumented acceptance matrix.

The production form retains the five bounded tuners, existing 0.3 s lead and 0.5 s silence limits, and existing
rest/gather/intact response timing. No floor clamp, longer lead, higher send rate, full-vector high-spin estimate,
measured-axial estimate or clock filter is retained. A read-only `PropaneTank` vent-age accessor supports the
existing thrust ramp; this is the only production edit outside body correction and tuning. The temporary contact
limit and borrowed native gravity view share authority/rebind/retirement boundaries; the native body owns the view.
The new timing tuners start at 0.025 s: unlike the rejected 0.02 endpoint, that float value passes shared range
validation. Thirteen always-on prediction assertions and the native component/replacement oracle pass before
uninstrumented acceptance. Fifteen observation/preparer assertions pass, including atomic refusal on promoted source.

The complete authored-force ordinary matrix passes **16/16**, maximum final moving **0.289 m**.
Fresh uninstrumented acceptance, paired cost and final repository gates remain separate obligations.


## Final-model reference refresh

After a warning-free rebuild, `check_equivalence.py --refresh --production-revision=8580fbe` passes the complete
wire/schema/world/publication workload and all five Lab scenarios. Independent semantic review against the original
parent fixtures again proves byte-identical world, segment and original 48-envelope publication fixtures.
Removing exactly the five new tuner metadata entries recovers the complete original schema/UI/shared membership
and every Lab field/window/summary/tick count. Only StartMatch/client and MatchStart/server differ among 81 wire
fixtures, solely their tuner dictionaries. [Reference review](moving-vent-fix/reference-review.json) retains the
asserted inventory. This supersedes the three-tuner intermediate metadata refresh.


## Final-code controlled comparisons and cost

Fresh schema-4 observations preserve owner contact policy. Three parent terminal arcs fail at
0.715 / 0.760 / 0.796 m, while the candidate passes at 0.051 / 0.071 / 0.094 m.
Candidate clean arcs pass at 0.003 / 0.086 / 0.015 m, within FU2a's 0.117254 m maximum.
Two additional 28 rad/s parent arcs fail at 0.673 / 0.725 m; the candidate passes at 0.013 / 0.108 m.
All six candidate faulted/clean floor controls pass. Both candidate 240 ms controls pass (0.214 m and an empty
final-moving set); the parent fails the first at 0.833 m. Both builds pass canonical refusal, future-epoch and
body-stream scenarios. All three 300 m/s straight-line timing diagnostics still fail on each build; these are
retained inherited failures, rather than evidence for low-speed correction. Selected final/rejected commands and results are in the
[run table](moving-vent-fix/runs.csv); the [complete native inventory](moving-vent-fix/all-runs.csv) retains all 453 attempts, including checker failures and the excluded interrupted invocation.

The uninstrumented repeat cohort has **0/10 candidate and 0/10 parent failures**. Five complete alternating pairs
per shard use opposite within-pair order across the two shards. Each run records machine-wide uptime before,
every five seconds and afterward. The exact two-sided Clopper–Pearson 95% interval is **[0, 0.308497107819]**
for each build (`1 - 0.025^(1/10)` at the upper bound). These seeded native runs are conditional counts, not an
IID population estimate or statistical proof of a reduced failure rate. [Intervals and run names](moving-vent-fix/rifle-intervals.json).

[Source hashes](moving-vent-fix/source-hashes.json) assert all 249 non-Dev C# files in each uninstrumented build
against `3c07559` and `8580fbe`, with no observation hooks. The identical benchmark's SHA-256 is
`5308912065265c85c413714864de451dc7ddab75b049b8a315a66e2784b999e7`.
Six alternating cost pairs (three each at 0/28 rad/s, reverse pair order on the second pair) use eight vents,
four free and four geometrically overlapping the floor, 20,000 warm-up and 60,000 measured whole Match callbacks,
and 80,008 publications including warm-up. All publications exercise displaced-sample geometry. Native physics
does not advance inside the controlled timed loop; the native runs separately exercise integration and contact.

[Cost rows](moving-vent-fix/cost.csv) retain allocation, mean/p95/p99 and load before/after each run:
managed allocation is **72,605,472 → 39,962,016 bytes**, equal across all pairs of each build. At zero spin,
parent/candidate mean ranges are 32.702–33.483 / 27.598–28.032 µs; at 28 rad/s,
34.325–34.981 / 30.664–31.205 µs. All six candidate p95/p99 values improve. No owned compilation or deep
sweep overlaps these pairs. Earlier aligned-source diagnostics and observed-build allocation measurements are
not substituted for this final aged-source workload; their limitations remain recorded above.


## Uninstrumented matrix and native crowd checks

The final uninstrumented matrix passes **16/16**, maximum final moving 0.414 m, with the original seeds
24–27 crossed with fault seeds 1–4 at 120/30/3. Three original runs complete before an external 143 termination;
the interrupted fourth is excluded, and thirteen fresh-directory retakes complete the predeclared matrix.
The independent observed authored-force matrix above remains a separate 16/16 cohort.

All sixteen clean/faulted, 4/8-bot Bank/King parent/candidate runs pass, with complete matching audits, zero
engine/protocol errors and no unobserved grids. Each uses the existing launcher, one rendered client,
960×540 and disabled vsync; each retains uptime snapshots. Native trajectories and workload counts can differ
between builds. These are integration/convergence checks, not a controlled frame-time differential.

| Bots/mode/profile | Parent rest/moving peak (m) | Candidate rest/moving peak (m) |
| --- | --- | --- |
| 4/bank/clean | 0.130/2.969 | 0.148/0.818 |
| 4/bank/faults | 0.160/4.304 | 0.125/4.691 |
| 4/king/clean | 0.113/1.170 | 0.201/0.804 |
| 4/king/faults | 0.025/4.456 | 0.155/5.687 |
| 8/bank/clean | 0.174/2.292 | 0.169/1.666 |
| 8/bank/faults | 0.162/4.607 | 0.189/7.366 |
| 8/king/clean | 0.157/2.323 | 0.190/2.482 |
| 8/king/faults | 0.241/9.041 | 0.162/4.550 |


## Trajectory comparison and rendered review

The four final `puncture-race` runs pass their native assertions, common checker and score audits.
`python3 -B tools/dev/compare_vents.py build/CASE/bot*.log` uses the unchanged default tolerance:

| Profile/build | Pairs | Median / p95 / max (m) |
| --- | --- | --- |
| clean parent | 279 | 0.053 / 0.092 / 0.119 |
| clean candidate | 279 | 0.058 / 0.107 / 0.139 |
| faulted parent | 280 | 0.092 / 0.177 / 0.239 |
| faulted candidate | 280 | 0.100 / 0.182 / 0.253 |

The candidate numbers are slightly higher in this pair; no trajectory improvement is claimed. This measures
within-run client divergence, not equality of physical paths between builds. Faulted canonical winners differ
(parent owner 2, candidate owner 3), with opposite hole sides; clean parameters also have small native float
rounding differences. Every client within each run agrees on its complete canonical parameters.

Reviewed final images: `build/fu2b-v3-network-4-bank-clean-candidate/shots/Bot1_060.0.png` shows the rendered
suburb, player/HUD and explosion smoke. Both
`build/fu2b-v3-trajectories-race-{clean,faults}-candidate/shots/Bot1_race_600.png` show an airborne rotating vent
with its jet/flame plume and intact scene/HUD. The faulted image is the remote owner's tank on Bot1.
Stills verify rendering; numerical trajectories and native assertions provide the motion proof.

`make check` completes successfully on the final production/harness code: warning-free Debug and ExportRelease,
681 unit assertions, all behavior/real-device input, bounded generator and separate-process world/segment checks,
and gate self-tests including 51 compiled / 16 source architecture negatives. Deliberately malformed-packet and
failed-capture unit fixtures retain their expected diagnostics. An earlier gate and matrix scheduler received
external exit 143 without a code diagnostic; the gate retake passes, and the interrupted matrix run is excluded.
`make test-equivalence` also passes all five Lab scenarios and the wire/schema/world/publication checks after the
reviewed metadata refresh. The final deep sweep is recorded separately after completion.

Completed diagnostic traces were losslessly compressed: 882 files, 82,031,455,632 original bytes to
17,209,517,968 gzip bytes. No native verdict, record or command was dropped. There are no large recordings.


## First deep-sweep failure and diagnosis

The first `make -j4 test-full` exits **2**, because lifetime shard 3 logs
`System.InvalidOperationException: Handle is not initialized` in Godot's
`ScriptManagerBridge.SwapGCHandleForType`, reached through `ResourceLoader.Load<Shader>` at
`ExplosionEffect._Ready:137` while loading `fx_shockwave.gdshader`. The lifecycle assertions still finish stable
and print PASS; the unchanged clean-engine-log gate correctly rejects the error. The failed invocation is retained
in `build/fu2b-v3-full-failure/`. It is not a passing full sweep.

ExplosionEffect, Warmup, LabSession, SessionFactory and lifetime scenario source are identical to the parent.
Three valid alternating optimized-Debug parent/candidate pairs of lifetime shard 3 (ten cycles each) are all clean,
with their optimized-build guards and lifecycle/resource assertions passing. An initial parent probe was rejected
by that guard because an incremental build retained ordinary Debug; it is excluded, and an explicit
`--no-incremental -p:Optimize=true` rebuild precedes all valid parent samples. One extra candidate probe also passes.
[Diagnosis rows](moving-vent-fix/lifetime-retakes.json) record the six valid samples. The original GC-handle exception
has **not** been reproduced on the parent, so its cause remains unresolved; it is not declared inherited.
No FX, engine, lifetime, rule or checker change is made to hide it. The complete unchanged deep gate is retried.


## Second deep-sweep failure and diagnosis

The second complete attempt also exits **2**, this time in Dissolve seed 12:
`a caught runner falls (y=1.1, ragdolled=False)`. The first full attempt passed that shard. This failure is
preserved in `build/fu2b-v3-full-failure-2/`; no lifetime or Dissolve assertion is weakened. The H2 deadline fix
and final fall assertion are unchanged on this branch. Three alternating optimized-Debug parent/candidate pairs
of the exact 300-second seed-12/tracking case all pass with clean engine logs and all fall/score/stream/resource
assertions. [Dissolve diagnosis rows](moving-vent-fix/dissolve-retakes.json) retain commands, loads and outcomes.
The first failure has not been reproduced on the parent; its cause is also unresolved, rather than declared
inherited. No gameplay, FX, engine or other package's harness is changed. A third complete unchanged full sweep
is run to finish the still-open all-shard acceptance obligation; both earlier failures remain in this record.

## Scope, ownership and compatibility

Production changes are limited to `BodySync`, five tuners in `Tuning.70.Match`, and the read-only simulation-age
accessor in `PropaneTank`. No mutable owner is extracted, no runtime type/file is added, and no dependency is
removed or added across a forbidden edge. The architecture baseline remains empty. The two new source-site
approvals are Dev-only Stopwatch measurement sites, with no gameplay clock.

Per-entry acceleration history resets across authority, rest, prediction, stale-history and replacement
boundaries. The borrowed native gravity view is dropped before authority/rebind/retirement; the native body
owns and destroys it. Registry query scratch is disposed on clearing or final body removal. Temporary observer
contact reporting restores its original limit. Owners retain their contact policy, jet, flutter, spin limit,
physics clock and ordinary gameplay. Network sampling/publication deadlines retain the monotonic clock;
the vent-age accessor reads the existing physics clock. No RNG call, traversal order, callback boundary or scene
processing order is added. Reliable grouping and numerical correction behavior deliberately change as classified.

Other owners' files touched: `PropaneTank` supplies the accessor only; `AuthorityTests` retains explicit 0.25 s
mixed-disposable coverage; `PublicationReferenceTests` pins the original interval and strengthens assertions that
both disposable and required-send failures occur. The separate default-policy test covers the new interval.
The FX, Dissolve and lifetime sources involved in the two full-gate failures remain untouched.

This is a **minor release**: shared correction, publication reliability and five tuner entries change.
No message changes shape. `Protocol.Version` remains **3**. Linux runtime/rendering is verified; Windows and
Apple Silicon macOS runtime execution is not performed by this worktree.


## Final deep-sweep completion

The third unchanged `make -j4 test-full` exits **0** (`build/fu2b-v3-test-full-third.txt`) at production/harness
head `3764ba5`, after the two failed attempts documented above. No production, harness, rule or checker code
changes between these attempts. All four generator and lifetime shards, navigation **700/700**, deep piles,
menu/range probes and all Lab shards pass. Coverage checks report **550 cases**, including **514 dial fixtures**,
exactly once across 25 Lab shards. The final ordinary-Debug bot gates pass: rifle final moving **0.155 m**,
two-match duel **0.164 m**, and loading-readiness **0 m**; every score audit completes with no unobserved grids.
The rifle gate is one extra successful candidate run, separate from the predeclared ten-repeat cohort.

The two earlier full failures remain material verification history: neither the GC-handle exception nor the
standing caught runner has been reproduced in the three valid paired standalone checks. Their causes are
unresolved. A successful full retry does not establish that those other-package cases are free of intermittent
failures. This branch makes no change to conceal them. Final changes after this tested code head are evidence,
runbook and index text only. `check_links.py` and staged whitespace checks cover those final records.
