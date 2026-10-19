# Moving-vent correction experiments (FU2b)

**Status:** In progress. Harness assertions are established on the parent; no production correction is accepted yet.

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
