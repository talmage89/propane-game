# Moving-vent residual investigation (FU2a)

**Status:** Investigation only. An unchanged parent reproduces the ordinary terminal residual. Observed controls
identify several contributors, but do not establish one cause for every historical failure. Correction and the
P5/FU2 acceptance obligations remain unchanged and open.

2026-10-18, Linux x86-64, Godot 4.7.2 .NET/Jolt. Runtime reference:
`674152bf5b63147b1c48228cea16de08a5194756` (immediate parent `dev`, v0.4.2). This record adds findings to
[corrections](network-corrections.md), [P5](lab-tuner-sampling.md),
[participant integration](participant-integration.md) and [King crowd](king-crowd.md); it does not reinterpret their
unavailable raw traces. The procedure and clock definitions are in the [runbook](../runbooks/moving-vent-residual.md).

## Scope and provenance

No production source, correction threshold, tuner default, wire byte, RNG call, callback order or existing fixture
ending changed. The uninstrumented archive was checked byte-for-byte against all 210 parent non-Dev C# files
(`build/fu2a-pristine/runtime-check.json`). The ordinary checkout likewise has no non-Dev source delta. The immutable
release references were not regenerated.

The source-build observer inserts calls into **scratch** BodySync, BodyPublisher, Match and NetTest. Removing its
injected calls recovers the original tokens/order (asserted by the harness test). Manifests and patches alongside
these projects retain exact hashes, including the actual tuning resource and observer. Runtime hooks are absent
from ordinary runs; both new types belong to Dev. ExportRelease excludes Dev entirely. This is not a performance
measurement: serialization, contact reporting, allocation and disk traffic can affect native scheduling.

The separate ballistic Dev scenario uses the canonical puncture/authority path, with zero impulse direction,
zero jet thrust, collision layers/mask off, zero damping in **replace** mode, no spin and native gravity selected by
argument. Assertions reject contamination. It changes neither an existing fixture nor the common ending/checker.
Its optional initial vertical velocity is set once; the arc never stops or resets at its apex.

## Original workload and frequency

The workload is four clients, ordinary 90-second Bank game, melee start, 120 ms lag / 30 ms jitter / 3% loss.
Fixed seeds identify construction/fault inputs, not deterministic native trajectories. The unchanged checker
self-tests and limits remain authoritative. Client-log ordinals below are not real network peer IDs.

* Uninstrumented, predeclared seeds 24, 25, 26, 27 with fault seed 2: **1/4 fails**. Finals are
  0.319110, 0.000198, **0.557242**, 0 m. The two earlier seed-25 controls pass (0 and 0.253997 m).
* Fully observed fixed seed-25/net-2 repeats: **0/6 fails**, maximum final 0.449188 m.
* Fully observed predeclared seed matrix: seeds 24–27 crossed with fault seeds 1–4, **0/16 fails**, maximum
  final 0.379514 m.
* After the untouched seed-26 failure, six observed seed-26/net-2 repeats: **0/6 fails**, maximum final
  0.410089 m. This is an adaptive cohort, not part of the predeclared matrix.

The exact two-sided 95% binomial interval for the untouched predeclared 1/4 is approximately **0.63–80.59%**.
For the observed matrix's 0/16 it is **0–20.59%**; descriptively pooling the 28 observed original-workload runs
would give **0–12.34%**, but their seeds/selection and observer timing differ. These are conditional run counts,
not an IID estimate of all player matches. Instrumented passes do not erase the parent failure or prove that
observation is neutral.

In `fu2a-pristine-26`, body `2030000e` ends at 0.527 m (logs 1/2) and 0.557 m (2/4). Real owner 3 (Bot2), epoch 4,
is stable for roughly the last ten seconds; no claim is pending. Near the final grid, observer 1 reports sample
age 0.223 s / receipt quiet 0.069 s, speed 7.192 m/s and spin 29.288 rad/s; observer 4 reports age 0.305 s /
quiet 0.123 s, speed 7.554 m/s, spin 30.197 rad/s. Both report `barrier=none`. Legacy traces contain no snap,
stale or ownership transition for that body in the last two seconds. Fresh receipt is compatible with an old
sample. These legacy records do **not** contain per-tick quaternions, contact impulses or every blend decision;
the exact mechanism of this original-profile failure remains underdetermined.

## What the checker actually compares

The final comparison is the last common **observed 50 ms grid**, not either client's independent `tank-final`
or completion pose. It interpolates node origins between each client's bracketing `tank-check` records, retaining
lifecycle/motion labels. Completion establishes coverage, not a substituted endpoint. The sidecar calls the
unchanged checker and records its exact brackets/weights. A synthetic asynchronous fast-linear test fails loudly
if this interpretation changes; incomplete streams remain failures.

For the untouched failure, the grid is `23214.45`. Log 2 brackets `23214.439234`–`23214.513148`, weight
0.14565576, interpolated origin `(32.598776, .425448, -16.825120)`. Log 4 brackets
`23214.436346`–`23214.510781`, weight 0.1834352, origin `(32.958544, .818539, -16.988107)`.
Thus the 0.557 m is an aligned checker result, not a subtraction of independent final rows. The existing
50 ms interpolation and differing native simulation phase can still contribute; legacy data cannot quantify
that contribution exactly.

Same-machine raw Stopwatch timestamps can be compared across processes. `network`, `sampled` and `updated` are
process-local monotonic network times: age is `network - sampled`, freshness is `network - updated`. Comparing
`sampled` directly to another process's raw clock is invalid. Native physics catch-up steps can execute almost
back-to-back while advancing simulation by separate fixed deltas. Reinterpolating dense mixed callback records
against wall time is an alternative diagnostic, not independent continuous-physics ground truth. In the
selected observed finals and completions, every recorded world is alive and unpaused. Their simulation clocks
have different per-world origins and are not substituted for the common raw timestamp.

## Contact and rotational evidence

The existing `vent-contact` floor fixture passes all six valid observed 120/30/3 repeats, plus the untouched,
clean and rendered controls. That does not mean its trajectory always stays within the **final-only** 0.50 m
limit: one observed transient reaches 0.653966 m at grid `21266.0`, body `10000007`.

The retained [case rows and authored hull](moving-vent-residual/floor-transient.json) establish this sequence:

1. Owner 2, epoch 2, is stable; observer 3 uses publication sequence 173. Actual publication age is 0.315338 s,
   estimated age 0.323988 s, receipt quiet 0.150335 s; lead is capped at 0.300 s. This is a blend, not stale fallback.
2. Source angular speed is high. The owner experiences contact on 17 of 37 recorded ticks between publication
   and the observer decision. Its current velocity differs substantially from the source velocity.
3. Extrapolating the **actual 114-point authored convex hull** about its COM puts its bottom at **−0.148071 m**
   through the clear floor (`y=0`). The received sample, observer and owner bottoms are respectively
   +0.049565, +0.006762 and +0.068558 m. This is a target-feasibility failure, not a prospective Jolt query.
4. The observer is in VoidFloor contact. The commanded vertical velocity is −1.254531 m/s; the next recorded
   native state is +0.567595 m/s (change +1.822126 m/s), with angular-velocity change magnitude 3.74876 rad/s.
   The supplied steering is being changed by native contact response.
5. At that observer decision, extrapolated target COM is 0.63757 m from the interpolated owner COM; the
   observer's own COM is 0.61661 m away, while its distance to its target is only 0.18834 m. Following the target
   more aggressively alone would not solve the incorrect target.

Alternative dense-record interpolation gives node/COM disagreements of 0.644256/0.606754 m, compared with the
checker's 0.653966 m. Rotation of an offset origin matters, but is not the entire discrepancy in this transient.
Contact counts are capped at eight and describe the latest native state; they do not prove every solver event.
This is a **transient passing run**, not proof that the same mechanism caused the historical terminal failures.

## Older-sample diagnostic control

An explicitly different four-client profile (240/30/3, otherwise seed 25/net 2, 90 seconds) fails once in two
runs: `fu2a-age240-fault-01`, final 0.532465 and 0.504 m on bodies `20200008` and `2020000a`. The second run
passes at 0.241879 m. Neither is pooled with the original 120 ms cohort.

The first failing body has stable owner 5/epoch 2, no pending claim, active ordinary velocity blends, and fresh
receipts (quiet 0.166–0.201 s). All observers retain sequence 427; actual publication age is 0.493–0.521 s,
estimated 0.466–0.501 s, while lead stays capped at 0.300 s. Their steering errors are at most 0.112 m, although
the owner is elsewhere and its velocity has changed through SlabBody contact. Native reinterpolation gives
0.530286 m at node origin and 0.484102 m at COM. Thus rotational origin displacement can change which side of
0.50 m this measurement falls on. The second body's corresponding values are 0.503158/0.266597 m: rotation is
material there too. The extrapolated hull for the first body is **above** the floor; the specific impossible-floor
target from the transient cannot be assigned to this final failure.

The full trace audit matches 105,662 before/after blends to `targetLinear + error / blend`, with maximum float
component difference 2.8e−6 m/s and no missing post-correction record. That establishes ordinary blend execution;
it is not evidence that its target was correct. Full traces retain publications, receipts including rejections,
epoch/owner transitions, branch decisions and native responses for both failing bodies.

## Native acceleration and timing controls

The preliminary no-contact/no-spin free-fall pairs (zero damping, zero initial vertical velocity, same 120/30/3
profile) pass all three no-gravity controls (finals 0.050062, 0.021429, 0.014337 m) and fail all three gravity
runs (0.845702, 2.382254, 4.984706 m). At roughly 294 m/s, differing native catch-up/measurement phases can
move the result by meters, so those failures alone do not isolate acceleration.

An upward native arc starting at 300 m/s reduces terminal speed to about 6 m/s without changing the ending.
The initial valid arc (`fu2a-arc-1-arc`) fails at **0.650662 m**; the corresponding clean arc passes at
0.093218 m. Dense recorded-pose interpolation gives **0.652919 m at both node and COM**. In its final window,
owner 2/epoch 2 stays stable, there is no pending claim, no contact and zero angular velocity. This establishes a
terminal residual that does not require rotational offset, contact or an authority transition.

At the observer decision nearest the final grid (`24850.448099538`), quiet is only **0.015674 s** and sample
age is **0.186452 s** (matching publication's actual raw age **0.192700 s**). Lead is not capped. Source vertical
velocity is **7.567446 m/s**; steering error is −0.5286 m and the supplied velocity is **5.454165 m/s**. Native
motion then changes it to **5.372415 m/s**, exactly the ordinary gravity decrement for one 120 Hz step
(−0.081750 m/s). The target COM is only 0.13035 m from the interpolated owner COM, while the observer COM is
0.65348 m away. There is no obstruction or snap; ordinary velocity blending is executing.

This is the expected lag of a constant-velocity predictor and old-velocity feed-forward under acceleration.
For constant acceleration `a`, sample age `h`, and blend time `B`, ignoring discrete-step and clock-estimate
terms, the position bias relative to the owner is approximately `-a * (h*B + h*h/2)`. Gravity, `h≈0.19 s` and
`B=.25 s` give about **0.64 m**. The pose-extrapolation error alone is smaller; replacing only the predicted
position would leave the old feed-forward velocity term. This model explains the controlled low-speed arc;
it does not assert a constant acceleration model during spinning floor contact.

The subsequent three predeclared triplets repeat the discrimination (world seed 23/fault seed 2 throughout):

| Initial vertical speed / native mode | 120/30/3 finals (m), three repeats | Clean arc finals (m) |
| --- | --- | --- |
| 300 m/s, gravity off (fast linear control) | **2.915224 / 1.635450 / 3.759937**, all fail | — |
| 300 m/s, gravity on (low-speed terminal arc) | **0.614831 / 0.590137 / 0.660572**, all fail | **0.090887 / 0.117254 / 0.078844**, all pass |

All nine runs pass the native fixture assertions, exit their Godot processes normally, agree with the server
score audit, and have zero engine/protocol errors and unobserved pair grids. The low-speed arc is a repeatable
failing **parent correction** workload with full traces, not an ordinary-play failure-rate estimate.
The selected [arc publication, receipt, decisions and native response](moving-vent-residual/arc-terminal.json)
retain the first valid arc's complete numerical decomposition.

In the first fast-linear final, estimated sample age is .136356 s and raw publication age .143493 s. Their
7.137 ms difference alone corresponds to 2.141 m at 300 m/s. These timestamps also include the difference
between packet/pose sampling and observer hook execution, so this is not an exact measurement of clock-offset
error alone. Dense event-row interpolation differs from the checker's final by about .8 m. This is evidence
for timing sensitivity under high speed, not proof that the checker manufactured the entire failure. At the
slow arc's terminal speed these millisecond/phase errors cannot explain its roughly .6 m measured residual.

The prototypes are retained and excluded from this discrimination: the first ballistic probe used a normal
shot impulse and correctly failed the no-spin assertion; the next had zero body damping in combine mode and
therefore still inherited world damping. The first fast-linear arc control incorrectly asserted that an
observer's corrected velocity must equal the owner's constant velocity; it failed loudly and is incomplete.
The final fixture checks native velocity selection on the owner and gravity mode on every copy, retaining all
contact/spin/authority assertions and the unchanged convergence checker. Its guard against invalid arguments
was added afterward; the final-source observed arc recheck (`fu2a-arc-current`) still fails at **0.573094 m**,
with normal native exits, passing fixture assertions and matching audit.

## Discrimination and smallest supported follow-up

| Candidate | Evidence and remaining limit |
| --- | --- |
| Transport silence / stale fallback | Ruled out at the observed floor transient, age-240 final and low-speed arc final: fresh receipts, stable targets, ordinary blends, no stale branch. Receipt freshness does not bound publication age. |
| Publication age / capped lead | Directly present in the age-240 failure: roughly .5 s old publication, .3 s lead. At 120 ms the low-speed arc fails without reaching the cap. Increasing the cap alone cannot resolve both cases and extends prediction through unknown contact. |
| Acceleration | Sufficient for the controlled low-speed no-contact/no-spin final. Old velocity enters both extrapolated pose and steering feed-forward; the measured lag agrees with their combined acceleration bias. It has not been established as the sole cause of the ordinary seed-26 failure. |
| Rotation / contact | Direct contact response and an infeasible extrapolated hull are established in a transient; rotational node offset materially affects the age-240 final. Neither is necessary for the low-speed arc failure. Contact/spin in the historical failures remains an inference. |
| Ownership / epoch | Stable in each selected final/transient window; no pending claim. Not necessary for these residuals. This does not audit every historical transition. |
| Checker / clock alignment | The checker uses a shared observed grid. Its native phase/interpolation contribution is small in the low-speed arc diagnostic but substantial at very high speed; the fast linear control exposes timing sensitivity without acceleration. The original legacy failure cannot be decomposed exactly. |
| Shared-machine load | One untouched failure occurs under high load; other passes overlap equal/higher load. The cohorts are too small/confounded for a causal or statistically useful load correlation. |

For **FU2b (minor release)**, the smallest correction experiment supported by the isolated acceleration evidence
is a paired acceleration term in **both** target COM prediction and feed-forward linear velocity, limited to
motion where that acceleration model is valid. First prototype it in Dev and compare against the untouched
parent under identical observation. Do not apply a constant-gravity model indiscriminately through contact.
The floor case separately supports testing feasibility of the **extrapolated hull** against contact geometry;
the current COM ray to the received sample does not establish that feasibility. No evidence here establishes
that either experiment alone fixes the original workload. No production candidate or shipping fix is delivered.

FU2b must retain the existing .50 m limit measured at node origins and deadlines, with clean/faulted stationary/moving/venting coverage,
free flight with acceleration on/off, slow final arc and fast constant motion, rolling/floor/edge/obstruction
contact, high spin and COM offsets, age below/above the .3 s cap, silence beyond .5 s, rejected sequences and
epoch handoff/refusal/replay. Include the seed-26 original-profile failure, 240 ms final, floor transient and
unchanged P5 full deep gate. Preserve scores, lifecycle, authoritative rest and canonical ownership. Re-run
original-profile native repeats with full failure retention and both checker and physical-timeline diagnostics;
require an observed original-profile failure before claiming its mechanism is closed. Any wire-shape change
would need its own protocol bump; the minimal experiment above does not require one.

## Reproduction, load and retained artifacts

[Run inventory](moving-vent-residual/runs.csv) records each completed valid analysis, normalized exact native
command, cohort, native/launcher/checker status, score audit count, unobserved grids, engine errors, alignment
hash and machine load range/mean. `$PWD` in those commands replaces this checkout's absolute path; quoted
occurrences expand in the shell. Reproduce in fresh output/project directories. Prepare the untouched control
with `python3 -B tools/dev/server_equivalence.py --revision=674152b --label=fu2a-pristine-repro`, or use the
runbook's observer preparer with that same pinned revision. The preparer copies the native launcher/checkers
into the isolated project. The first two seed-25 controls predate the command manifest; their exact workload is
the original profile above, port 24682, with before/after load files. All subsequent commands are captured.

All raw native logs, command/status/load files, `alignment.json`, complete JSONL traces (lossless `.gz` where
compressed), manifests and scratch source patches remain under `build/fu2a-*`. The selected cases committed
beside this report contain source hashes and raw publication/receipt/decision/native rows; the
[artifact inventory](moving-vent-residual/artifacts.csv) hashes the retained failed-case files. The full trace
files are intentionally local artifacts, not truncated to the committed excerpts. Helper scripts for the
predeclared matrix, untouched series, focused repeats and control triplets remain in `build/fu2a-matrix.py`,
`fu2a-pristine-series.py`, `fu2a-focused.py`, `fu2a-ballistic-final-series.py` and `fu2a-arc-final-series.py`.
To regenerate each alignment, run `tools/dev/vent_residual_report.py build/RUN/bot*.log --traces=build/RUN/traces
--body=BODY --window=5 --output=build/RUN/alignment.json` (omit traces for the untouched controls, use
`--profile=clean` for clean runs). A failed verdict still writes its report and exits 1. Early sidecar versions
returned 0 after writing a failed verdict; the native launcher's failure was always preserved. The final CLI
and its new negative test enforce nonzero exit too.

No quiet window was requested or awaited. The original observed cohorts span machine one-minute loads
**10.21–43.01**; the untouched predeclared series spans **14.75–41.69**. The failing untouched run's mean load
is **36.89**, compared with **23.17 / 33.55 / 26.85** for the three passes. Observed passes also occur at similar
or greater load. A single untouched failure cannot establish correlation; observation, seed selection,
concurrent gates/experiments and native scheduling confound it. The process snapshots before the namespace
change include other worktrees' Godot commands. After the sandbox gained a private process namespace, they
necessarily show only this agent's processes; the new wrapper labels this limitation. `uptime` remains
machine-wide. Absence of visible other agents after that boundary is not evidence of an idle machine.

The first sandbox move interrupted `fu2a-soak-observed-06`; its partial traces remain and it is excluded. A
fresh `06r` replaces it in the six-run fixed cohort. An early floor run was launched before the preparer's
second build and had no observer; another prototype used incompatible raw/network trace clocks. Neither is
counted as an observed control. Early gravity probes with nonzero impulse/world damping, and the incomplete
fast-linear assertion failure, remain excluded scaffolds. A four-client rendered run completed the audits
and checker (final .311 m) but its launcher exited 2 during an external shim quoting failure; it is retained as
a failed launcher and excluded from success counts. Subsequent rendered floor and race runs exit 0.
One scratch-log redirect briefly targeted the workspace parent; it was immediately moved into this worktree's
`build/`, leaving no file there. No other checkout's source was changed.

Rendered native commands use `net_bots.sh -w 1`, which supplies `--resolution 960x540 --disable-vsync` through
the required invisible `godot-mono` shim, with absolute capture directories. Reviewed captures:

* `build/fu2a-race-v3-rendered/shots/Bot1_race_600.png`: tilted venting tank with visible jet/flame on the street;
  native puncture-race checks pass, final .221678 m. `compare_vents.py` also passes 279 trajectory pairs,
  median .109 m, p95 .213 m, maximum .340 m.
* `build/fu2a-rendered-fault/shots/Bot1_060.0.png`: ordinary four-client suburb fight, loose tanks and effects;
  this is the failed-shim launcher above, not an accepted full run.
* `build/fu2a-floor-v3-rendered/shots/Bot1_030.0.png`: floor/players render, but the test vents are outside the
  view; it is not visual proof of the selected floor contact. Native fixture and checker pass, final .225150 m.

The 16-actor King faulted control (four remote clients plus three lobby bots per client, supply 4) passes,
final .220 m, 2,717,408 body-pair grids and 238 banked; all four clients agree with the complete server score audit. The existing body-stream faulted control passes,
but deliberately becomes stationary before completion and therefore is not evidence for a final moving case.
These controls use the normal native scenarios and ending conditions. No complete new P5 `test-full` run,
performance/allocations comparison, Windows/macOS execution, cross-machine clock proof or historical raw-trace
reconstruction is claimed.

## Verification and compatibility

`make setup`/`make build`, the full `make check`, unchanged checker self-tests and frozen `make test-equivalence`
are the reproducible repository gates; their final results are recorded below. The new Python tests cover exact
insertion-only scratch hooks, refused double instrumentation, asynchronous grids, incomplete logs, failed
verdict/CLI exit, data immutability, clock-domain selection, compressed traces, actual hull support and wrapper
failure/fresh-directory behavior. Native fixture assertions validate the physical controls separately from the
convergence verdict. No production state moved or retired, dependency edge changed, baseline exception added,
or immutable reference changed. New mutable lifetime is confined to the match writer and connection-owned Dev
scenario, both explicitly retired/disposed. The shared docs index, architecture ledger and scenario registry
are updated; no other package's implementation is edited.

Players share exactly the existing messages, construction, tanks, tuners and rules. **Protocol.Version remains
3**. Scratch observation projects deliberately depend on Dev and must not be exported. Ordinary source and
ExportRelease have no observer hooks; compiled release architecture checks confirm Dev is absent.

Obsolete compiled source-build copies were removed after the runs. Their manifests, insertion patches and
observer/fixture source snapshots remain alongside the retained data; the parent is permanently pinned above.
The current prepared project and the legacy clock-observer project remain available locally for review.

Retained-data validation passes **264 size/SHA256 checks** and verifies **28 complete failed-run peer traces**
by reading their gzip streams through the `measurement-completion` marker. All 69 alignment hashes, cohort
commands and original-workload identities were checked against the retained native data.

Final gate results (all exit 0):

| Command | Result and retained log |
| --- | --- |
| `make setup` | Import, Debug compile, strict compiled import; 0 warnings/errors (`build/fu2a-setup-final.log`). |
| `make check` | Complete serial gate and infrastructure self-tests pass (`build/fu2a-check-complete.log`); this preceded the last fixture guard and CLI exit test. |
| `make -j4 check` | Final complete gate: 545 unit tests, 0 failures; real-device menu input and all behavior/generator/world suites pass; 10 new diagnostic and 25 unchanged checker tests pass. Debug/ExportRelease: 0 build warnings/errors, 2159/1019 types, 79 unchanged reviewed exceptions, 0 architecture failures; 86 approved source sites, 0 failures. Gate negatives pass (`build/fu2a-check-deliverable.log`). |
| `make test-equivalence` | Six Python self-tests; frozen wire/schema/world/publication pass; Bank, King, spill-chain, Short Fuse and Dissolve Lab traces all match (`build/fu2a-equivalence-final.log`, `build/rfp0-equivalence-11`). References untouched. |
| `python3 -B tools/dev/vent_residual_tests.py` and `python3 -B tools/dev/compare_tanks_tests.py` | 10 and 25 tests pass independently as well as inside the final gate. |
| `python3 -B tools/dev/compare_vents.py build/fu2a-race-v3-rendered/bot*.log` | Pass, 279 trajectory pairs, median/p95/max .109/.213/.340 m (`build/fu2a-race-v3-rendered/vents-check.log`). |

The first security cutover interrupted an earlier gate; its log is incomplete. A resumed attempt was invalidated
by overlapping a Debug rebuild with a native scene and was stopped. Neither is claimed as a pass; both complete
serial/final invocations above supersede them. The damping-enum compile typo was corrected before these passing
builds. These are harness/setup failures, not evidence for a production correction mechanism.
