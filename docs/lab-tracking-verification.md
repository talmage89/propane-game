# Lab tank tracking verification

Recorded October 16, 2026 for refactor package P2. **Acceptance is incomplete: rendered paired performance needs a coordinated quiet window.** This is evidence; [Lab](lab.md) is the current contract.

## Revisions and environment

Baseline production: `b3a21e9`, the integrated P0 parent. The baseline scratch project received the same
`LabTest.Dissolve.cs` instrumentation and `LabCreditTests.cs` as the candidate, then its own `make setup` and build.
No release references were regenerated. The immediate production parent is also the production in `07b22b9`,
which adds opt-in observations only. Candidate production: `ad31958` plus the managed-detachment correction `ac85cc2`.

Linux x86_64, kernel 7.2.9-1-cachyos; Godot 4.7.2 (`ed1daf0bf`), .NET SDK 10.0.112, normal Debug;
Intel Core i5-10400F (12 logical CPUs), GTX 1660. Rendered runs use Vulkan Forward+ at 960×540 through the invisible
gamescope backend. `DISABLE_GAMESCOPE_WSI=1` disables only the optional WSI layer for the test process, permitting
`--disable-vsync`; the compositor still isolates rendering. No system or driver configuration changed.

Raw logs, commands, environment metadata, CSVs and comparison traces remain under `build/rfp2-evidence/` in the
implementation checkout. Reviewed captures remain in `build/rfp2-bot-validated/` and `build/rfp2-pace-validated/`.

## Correctness and collection

- `make setup`, `make build`, and `make check`: warning-free final build; 435 unit tests and all gate partitions and infrastructure
  self-tests pass. The gate includes session/lifecycle suites, real-device input, forced formations and separate-process
  world hashes. New unit assertions keep the existing suites' order by running their native fixtures afterward.
- `python3 -B tools/dev/check_equivalence.py --baseline="$BASELINE" --output="$PWD/build/rfp2-reference"`:
  all frozen wire/schema/world/publication references pass. All five Lab traces match both v0.4.0 and the parent
  exactly: Bank and King seeded idle workloads, spill/chain, Short Fuse replacement and controlled Dissolve retirement.
- `godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/unit_tests.tscn -- --suite=LabCreditTests`:
  both runners produce identical parent/candidate command transcripts for last-shooter credit, initiating-chain credit,
  a real spill and re-banked drop, and a puncture followed by an uncredited detonation. Each ends with map=2, drops=1,
  spills=1, banked=2 and no shooter/drop references.
- The same unit command with `--suite=TankTrackingTests` exercises every detonate/retire/shutdown order, duplicate
  tracking, reverse draining through removal/re-registration, cleanup failure, captured state and freed native bodies.
- `godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/lab_test.tscn -- --scenario=tracking`:
  detonate/retire/reset/repeated-exit permutations, visual dissolution preserving registrations, shooter/drop cleanup,
  world invalidation before draining and draining before runner stop. All 16 wrappers in a retired segment collect
  before exit, and all 31 remaining wrappers collect on reset, in each case while the Lab stays alive.
- `python3 -B tools/dev/check_lab.py --full --only=lifetime`: eleven scenario/environment pairs, each with two
  warm-ups and ten checked entry/reset/exit cycles (110 checked cycles); stable node/object/orphan counts throughout.

Long-run proof command (run once normally and once with `--driver=pace`):

```sh
DISABLE_GAMESCOPE_WSI=1 godot-mono --fixed-fps 60 --disable-vsync --resolution 960x540 --path . \
  res://scenes/dev/lab_test.tscn -- --scenario=dissolve --seconds=300 --first-seed=11 \
  --census-every=2 --tracking-proof --assert-tracking --capture-dir="$PWD/build/rfp2-proof" \
  --shots=30,150,300 --camera=overview
```

The bot reaches 1,430 m, detonates 189 tanks, builds/retires 26/24 segments and scores 5,344. Its tracking count equals
active content at every census; reported counts range from 8 to 55 while historical supply reaches 320. At the live
session's collection checkpoint, 312 retired wrappers are eligible and **zero remain reachable**.
The pacer reaches 1,799 m, builds/retires 32/30 segments and scores 1,799. Tracking matches content at every census;
reported counts range from 12 to 65 while historical supply reaches 374. **Zero of 366 retired wrappers remain reachable.**
The parent pacer, run headless with the identical observation code and no candidate-only assertion, reaches the same
1,799 m and 32/30 segments but retains **all 366** retired wrappers while alive.

A second headless bot proof (`--first-seed=12`, otherwise the same long-run assertions) builds/retires 21/19 segments,
reaches 1,118 m and detonates 183 tanks; all 253 retired wrappers collect. Every long run retains the original
node/body/tree bounds, score, chain, catch, fall and result-menu assertions. Collection excludes wrappers still
legitimately held by active or partly retiring segments' tank arrays.

All eight seed-11 captures (30, 150, 300 seconds and end, bot and pacer) were reviewed: the street remains stocked and
materializes ahead, bot blasts/debris remain visible, the pacer traverses intact content, and both end on the paused
result panel with the expected score. These are visual checks, not a feel or cross-platform equivalence proof.

## Whole-session measurements

Six accepted alternating pairs are complete: three headless bot pairs and three headless pacer pairs. Each side
uses the identical final `LabTest.Dissolve.cs` (SHA256
`9a433a3fd0794c72aac70c09a7c23c711f7de27372ca0a4b185dba74a20b03f9`), normal Debug, seed 11,
`--fixed-fps 60 --disable-vsync --seconds=300 --census-every=2 --tracking-metrics --replay-random
--tune.HitstopDuration=0`, adding `--driver=pace` for the pacer. Neither equivalence nor weak-reference observers are
active in these measurements. Whole-tick time includes callbacks and the native tail up to the next physics/process
signal; it excludes rendering and is an upper bound rather than Jolt's internal solver timer. Histograms use 0.01 ms
bins, capped at 200 ms; observed percentiles are below that cap.

The table shows the median of each statistic over the three accepted runs, parent → candidate.

| Headless workload | Whole tick mean / p95 / p99, ms | Frame wall p95 / p99, ms | Callback bytes/tick | Allocated after assembly, MiB |
| --- | --- | --- | --- | --- |
| Bot | 0.4014 / 0.63 / 1.62 → 0.4135 / 0.65 / 2.47 | 2.3 / 6.3 → 2.4 / 6.5 | 3926.6 → 3963.4 | 227.41 → 229.00 |
| Pacer | 0.3048 / 0.37 / 1.11 → 0.3041 / 0.37 / 1.36 | 1.2 / 5.2 → 1.2 / 5.2 | 4402.2 → 4374.2 | 236.05 → 235.05 |

All accepted pacer runs have 37,963 measured ticks, 1,799 m, zero detonations and 32/30 segments built/retired.
There is no repeatable mean/p95 increase in this control; one candidate timing outlier remains included. Its per-run
whole-tick means are 0.3722, 0.3036, 0.3041 ms versus 0.3043, 0.3048, 0.3061 ms in the parent. Tail statistics vary;
these data do not establish strict performance equivalence by themselves.

The live bot does not execute the same physical event sequence across processes. One baseline was legally caught
early (19,489 ticks, 97 tanks, 11/8 segments); the other parents have 37,973 ticks, 168–181 tanks and 22–25 built
segments. Candidates have 37,811–37,963 ticks, 178–179 tanks and 23–25 built segments. The early run is retained in the
statistics, not censored. In particular, total allocations cannot be compared without these workload counts, and
higher bot-tail statistics must not be dismissed as a proven non-regression.

Other worktrees repeatedly started engine jobs during measurement. Every overlapped pair was retained and excluded
from acceptance, including an otherwise quiet partner. Fourteen earlier diagnostic runs and twenty-seven later completed
runs remain in `paired-final/` and `paired-quiet/`; the latter contains the twelve accepted headless runs. No completed
run failed its scene assertions. Three complete rendered bot pairs were attempted, but each overlapped external engine
activity; no rendered pair is accepted. A fourth already-contended parent was stopped when the launcher was paused.
The strict launcher waits for a sustained 60-second engine-free interval, watches for foreign engine activity during
each run and resumes accepted pairs without overwriting prior attempts.

**Remaining acceptance:** three uncontended rendered bot pairs and three rendered pacer pairs, plus a conclusion about
allocation and whole-tick/frame mean/p95/p99 from the full data. Do not treat this package as performance-accepted yet.
The existing baseline project is intentionally retained for that work. In a reserved window, resume with
`python3 -B -u build/rfp2-evidence/measure.py`; `python3 -B build/rfp2-evidence/summarize.py` writes the accepted-run
inventory and per-run values. The commands and raw summaries are in each cohort's `results.json`.
No production tracking code runs each physics tick; registration and removal occur at tank lifecycle boundaries.
This structural fact and the allocation control are useful evidence, but do not replace the outstanding rendered checks.

## Retained failures and limits

Early fixture compilation omitted the required tank spawn owner; using the existing tank factory corrected it.
Launches after that failed build saw a stale assembly and did not discover the new suites; none is claimed as a pass.
Initial format checks found whitespace and missing braces in new tests; corrected before the clean gate.
A new captured-state test deliberately ran against the first helper's native-validity guard and failed (9 tests,
1 failure): the freed wrapper still held managed event callbacks. Removing that guard detaches the C# events even
after native freeing; the same test then passed. This changed subscription cleanup only.
Two loaded gate attempts hit existing navigation allocation assertions (one Easy fallback measured 5,072 bytes;
an isolated candidate probe measured 7,880; another gate hit the cached-route assertion). No navigation code or
assertion changed. Five alternating isolated runs per revision then passed all 30 navigation tests, and a parent core
run with the new credit fixture passed. New native fixtures now run after every existing suite, preserving their
previous setup/cache environment; the final gate passes. The original failure logs are retained; their precise runtime
allocation source was not established.

An initial rendered diagnostic fell back to VSync despite `--disable-vsync`; it was stopped and excluded from timing.
A pacer render was interrupted at 150 seconds when gamescope's reaper reported its parent killed; its cause was not
established. The complete rendered proofs use the process-local WSI override and passed with clean Godot logs.
Neither failed/incomplete render is used as performance evidence.

This package removes subscription roots only. Forgotten-index history, timing lists, navigation chunks and the global
reveal window remain unchanged. It changes no shared tuners, tank rules, message bytes or world construction;
`Protocol.Version` remains 3. Native streaming completion is wall-time-sensitive; use P0's controlled trace for exact
retirement equivalence and the live workloads for statistical/lifetime evidence. Windows/macOS execution was not run.
