# W1 streaming history verification

**Status:** Storage, lifetime, feed differential, visual checks, shared-load paired performance and fast/reference/deep
gates verified on Linux. The completed performance table below supersedes the earlier quiet-window attempts.
Current contracts: [architecture](../architecture.md) and [streamed content history](../world-services.md#streamed-content-history).

## Scope and versions

Production parent: `620b817`. Identical observation harness: `7169456` (parent production plus the new tests/probe).
Candidate production: `cf3ad9c`; additional real-worker gap/boundary coverage: `1ca8a90`. No new runtime type/file, module edge, dependency exception or runtime source-site exception
was introduced. The two added clock approvals belong to the Dev probe. Existing arena/feed types remain Composition
under `tools/architecture/modules.json`; no baseline edge became stale. Harness and production changes are separate.
No other package's production files were touched; the shared architecture contract, Lab documentation and harness
registry/inventories were updated for W1.

The feed uses a `long` exclusive nonnegative prefix watermark and a sparse forgotten set. This is preferable to an
arbitrary retention limit: every forgotten request stays rejected, regardless of request/forget order or worker
completion. Sequential retirement does not allocate the set's backing storage. Gaps retain exact identity until the
prefix reaches them; storage follows peak outstanding out-of-order forgets, and closing a large gap can consume that
many set removals in one call. This is deliberately not a constant bound for arbitrary sparse callers.

Raw arena histories are explicitly requested by `CollectRawTimings`; ordinary lists have zero capacity. Retention
lasts until arena collection, or a caller stops recording and eventually releases the arena. The diagnostic caller
bounds the recording duration. `LastStepMilliseconds` remains one scalar. No world construction, RNG consumption,
retirement budget/order, rule, score, tuner, message or protocol version changed. Workers still generate plain plans
from the same captured settings/catalog/seed and never publish a completion into the feed.

## Container inventory (parent → candidate)

All unchanged backing containers may retain their **peak** capacity after removal; none shrinks its backing array
on every retirement. Bounds below refer to the fixed settings/catalog and caller workload, not arbitrary live tuner
changes or unlimited request demand.

| Owner/container | Before → after; retention boundary or excluded history |
| --- | --- |
| Arena `buildSlices`, `retireMilliseconds` | All run slices → empty/zero capacity in ordinary play; complete ordered evidence only on opt-in. Arena lifetime. |
| Feed `forgotten` | All forgotten integers → only out-of-order indices beyond a scalar prefix. Capacity follows peak unresolved sparse population; ordinary ordered stream stays zero. Feed lifetime, including post-dispose request semantics. |
| Feed `plans` and worker captures/results | Unchanged: requested, unforgotten plans/tasks; Forget removes the handle, Dispose clears handles. In-flight tasks finish and release their captured feed/results without re-insertion. Arbitrary request demand has no fixed cap. Arena requests one ahead. |
| Feed catalog snapshot; segment plans and their lot/road/fence/prop/tank/ammo arrays | Snapshot has fixed catalog vocabulary. Plan arrays follow the generated per-segment settings and the retained task/live/pending/retiring windows. No plan history was added. |
| Source `segments`, `pending` and staged `steps` enumerator | Unchanged: live window, one staged build, one finite plan's piece traversal (disposed on completion). Live list releases retired entries; assembly/build order unchanged. |
| Source `retiring` | Unchanged: content removed from live window but not yet drained by the retirement budget. Bound is outstanding retirement workload; caller overload is not silently evicted. Controlled run never queues a second retirement before draining the first. |
| Each `RunSegment`: `Tanks`, `Pickups`, floor shape; Suburb tank list/plan and native subtree | Unchanged: one content lifetime, including staged retirement. Collected after source queue drops the segment. Shared borrowed models/materials survive; the duplicated per-segment floor shape does not. |
| Arena `dissolved`, `pickups`; Lab `tanks`; runner tracking registrations, `directShots`, `drops` | Unchanged: live content/tanks; arena retirement forgets all tank subscriptions/captured state and removes pickups before native drain. Visual dissolution alone retains tracking. |
| Runner roster/scores/brains/spectating/shots/activity; session/world views and lifecycle callbacks | Unchanged: actor/session assembly population, not one entry per segment. Dissolve is solo. |
| BotServices tanks/venting/indices/pile buckets, navigation hazards and route/search scratch | Unchanged: refreshed current-tick populations and configured search/route budgets. Actor collider cache follows actor setup (one actor here); actor-removal repair is L1, not W1. |
| NavigationGrid layer `Chunks` | Visited-space cache remains; **explicitly out of scope**. Changing eviction/query behavior needs its own correctness contract. No bot navigation is driven by the controlled probe. |
| Dissolve rules `open`/`closed` chains; ledger tombstones and causal history | Active chain window and run-owned chain report/score history remain unchanged; **causal/report history is out of scope**. No ledger is pruned. |
| World reveal geometry, effect origins, receivers, clocks/deferred work | Unchanged: live geometry/effects/source callbacks; content retirement unregisters/frees/cancels them. Fixed application/session stop callbacks are not added per segment. Effects also expire through their existing clocks. |
| World decals/magazines | Unchanged existing caps: 96 decals, 24 dropped magazines, plus existing lifetime/content cleanup. |
| MeshKit `SurfaceTool` scratch and generated geometry | Unchanged: finite per-build plan geometry; the builder is temporary, generated meshes follow content, and RefCounted scratch follows existing GC cleanup. Not a process cache. |
| Catalog/GameAssets/model hull/trimesh/material caches | Unchanged borrowed immutable asset vocabulary, warmed before streaming; fixed catalog and scale/material combinations for this workload. Not a retired-segment log. |
| Probe timing arrays, census and transcript/evidence buffers | Dev-only requested diagnostic evidence, bounded by requested run length; never ordinary play. |
| Global `reveal_window` | One independently advancing stream per process remains the W2 limitation; W1 does not scope shader state. |

## Long controlled stream and native collection

Commands (all logs/captures under the worktree's ignored `build/`):

```sh
make setup
make build
godot-mono --path . --fixed-fps 60 --disable-vsync --resolution 1280x720 \
  res://scenes/dev/lab_test.tscn -- --scenario=streaming-history --segments=512 \
  --stream-transcript="$PWD/build/w1/parent-stream.txt" --capture-dir="$PWD/build/w1-parent-long-shots"
# Repeat against the candidate with --assert-bounded and candidate output paths.
```

The controlled actor samples advance the real rules' window. All arena begin/request, staged-build, floor/sweep,
forget and retirement work runs through `_PhysicsProcess`. Workers are made ready before timing; native physics and
bot navigation remain paused. Every 64 retirements, forced collection asserts that every observed retired segment,
suburb, floor-shape and tank wrapper is unreachable, and that suburb/floor-shape/tank native IDs are invalid while
the session remains alive. Both runs passed all eight checkpoints. This is 512 retirements/515 builds over 30,761
sampled meters, versus the ordinary fast scenario's three retirements; it is not 512 segments of native bot play.

| Retired | Parent forgotten entries/capacity | Parent build samples/capacity | Parent retire samples/capacity | Candidate entries + both sample counts/capacities |
| ---: | ---: | ---: | ---: | --- |
| 0 | 0/0 | 0/0 | 0/0 | all 0/0 |
| 64 | 64/89 | 688/1024 | 376/512 | all 0/0 |
| 128 | 128/197 | 1305/2048 | 731/1024 | all 0/0 |
| 192 | 192/197 | 2334/4096 | 1207/2048 | all 0/0 |
| 256 | 256/431 | 3230/4096 | 1645/2048 | all 0/0 |
| 320 | 320/431 | 4201/8192 | 2115/4096 | all 0/0 |
| 384 | 384/431 | 4878/8192 | 2485/4096 | all 0/0 |
| 448 | 448/919 | 5484/8192 | 2839/4096 | all 0/0 |
| 512 | 512/919 | 6057/8192 | 3168/4096 | all 0/0 |

At **every checkpoint on both versions**: live segments 3, retiring 0, plan handles 4/capacity 7, pickups 6,
direct shots/drop captures 0/0, receivers 1, effects 4, timers 3, native resources 636. Per-point remaining counts:

| Retired | Segment nodes/bodies (both) | Reveal registrations (both) | Runner tracking (both) | Lab tanks parent/candidate | Dissolved parent/candidate |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 2518/542 | 687 | 47 | 47/47 | 0/0 |
| 64 | 2396/497 | 677 | 12 | 12/12 | 0/0 |
| 128 | 2375/506 | 647 | 42 | 42/42 | 0/0 |
| 192 | 2649/555 | 744 | 12 | 12/12 | 0/0 |
| 256 | 2336/488 | 649 | 12 | 12/12 | 0/0 |
| 320 | 2451/513 | 688 | 12 | 9/9 | 3/3 |
| 384 | 2413/506 | 674 | 12 | 12/12 | 0/0 |
| 448 | 2444/516 | 665 | 42 | 42/42 | 0/0 |
| 512 | 2376/498 | 663 | 12 | 12/8 | 0/4 |

The last sweep difference is expected: time-budgeted native slicing uses wall time, so total callback counts and
sweep phase need not match. No request decision or generated segment changed. Counts are checkpoint observations,
not a universal maximum for arbitrary segment settings. The controlled source has three live segments, one staged
build and at most one retiring segment; ordinary production backlog remains throughput-dependent.

## Feed differential and diagnostics

```sh
godot-mono --headless --path . res://scenes/dev/unit_tests.tscn -- \
  --suite=SegmentFeedTests --feed-transcript="$PWD/build/w1/parent-feed.txt"
# Repeat on candidate, then:
cmp build/w1/parent-feed.txt build/w1/candidate-feed.txt
cmp build/w1/parent-stream.txt build/w1/candidate-stream.txt
```

All three unit cases pass on both versions. The added real-worker gap case covers indices 1/3/5 after forgetting
4/2/0; the candidate also tests a synthetic prefix at `int.MaxValue`, proving the exclusive boundary cannot overflow. The 25,018-line transcript covers an independent old HashSet admission model,
10,000 indices, gaps closed out of order, duplicate/negative/extreme forgets, re-request after forget, unfinished and
completed plans, real generated plan identity on repeated request/Wait, late completed/canceled workers, caller-visible
worker failure, forgotten Wait failure and disposal. The 1,024-line native stream transcript records every awaited
plan's index/seed/tank/jackpot fields plus retired-index re-request rejection and retained plan keys. Both `cmp` calls
pass byte for byte. The frozen plan/construction suite separately protects full generated content.

`streaming-history --segments=64 --raw` passes on parent and candidate. Every callback asserts exactly one appended
build sample iff construction was pending at entry, and exactly one retirement sample iff retirement ran. All samples
are finite and nonnegative. The candidate recorded 670 build/332 retirement samples in the retained run; the parent
recorded 644/311 in its boundary-check run. Exact duration values and slice counts are inherently wall-time-dependent;
this proves unchanged sample boundaries, units, ordering and shape, not identical stopwatch numbers between runs.

The only raw-history consumer is `DissolveScenario.Report`, reached by `lab_test --scenario=dissolve`,
`tools/dev/check_lab.py` (including its full bot/pacer sessions), and the corresponding make gates. The harness now
explicitly requests raw evidence before streaming. Existing `segment build steps/avg/max`, `retire avg/max`, frame
and CSV fields are unchanged. The diagnostic `streaming-history --raw` assertions supplement those report consumers.

## Rendered review

Reviewed `stream_before.png` and `stream_after.png` from both 512-retirement runs, 1280×720. The initial window shows
the road, yards, houses, tanks and cars with bright clipped reveal edges. After 512 retirements the matching late
segment still has a clean bounded street window, complete geometry/materials and the same reveal edge treatment.
No accumulated retired scenery is visible. Both corresponding PNG files are byte-identical:

- Before SHA256: `270d0636f886a7195a538cf24cb53c6126af041ff6da7d1919023cdf80397262`.
- After SHA256: `751a770d4918ffae0ffddeea8dd2f3fc14b46d5ca4d5b86e97e382e5e7996a4b`.

This is sampled reveal/native-content evidence, not continuous motion or input/feel acceptance. The first exploratory
camera was mistakenly attached in the simulation viewport; those captures were discarded as proof, the harness was
fixed before its commit, and the four reviewed captures above use the application viewport. Gamescope reports its
normal teardown messages and a V-Sync-disabled fallback warning; no engine errors occurred in the passing probes.

## Paired performance protocol

The required comparison alternates parent/candidate for three pairs in each of headless and rendered modes. The identical probe warms 32 segments, measures the whole arena
callback for 96 further segment builds/retirements in each 128-segment run, and reports actual callback counts,
allocation bytes and mean/p95/p99. An additional 1,200 settled callbacks checks steady allocation. Both long runs
report 166,400 bytes over those 1,200 callbacks; this is existing allocation, not a zero-allocation claim.
The measurement excludes worker generation/awaiting, probe assertions/census/collection and native engine physics.
No frame-time/capacity claim is inferred from it. These are managed current-thread callback bytes; native resource
retention is checked separately above. Parent and candidate run the same probe, seed/settings and warm-up, using
`--segments=128 --fixed-fps 60 --disable-vsync --resolution 1280x720` (with `--headless` for headless cases).

The lead explicitly accepted measurements under shared load; no CPU/GPU reservation was available. The final runner
uses strictly alternating parent/candidate legs in this worktree only, three retained pairs per mode. Both versions
were built warning-free once with the identical final harness; their complete Debug binary directories were cached
under `build/w1/shared-load-cached/`. Between legs the runner copies the chosen binary set into this worktree's normal
Godot output directory, checks its DLL SHA256, and launches `godot-mono` with identical flags. Sources are restored to
the candidate before measurements; no compilation runs in this worktree during a sample. The final restoration build
reproduced the candidate DLL hash. Caches were removed after the measurements; hashes/build logs remain:

- Parent DLL SHA256: `7bad5fe9dc1dc7dd4d3c26c0ff120b783d894250c28b70d9320b9760a3cbef37`.
- Candidate DLL SHA256: `2d0620ccd5ac5b8b878b105a763cde6d03a5c96b26f9c1c6144b2637dd2807b6`.

Every leg records literal `uptime` and `pgrep -af godot` output immediately before and after the native process.
The process monitor samples `/proc` every 200 ms: any dotnet build/format driver rejects the pair; MSBuild, csc and
Roslyn compiler processes, including the SDK's native `VBCSCompiler` executable, reject it when consuming more than
25% of one CPU over an interval. Resident idle compiler servers do not reject it. Admission waits for one second
without compiler activity, **not** for native/GPU quiet. Any overlap during either leg discards the **entire pair**;
its replacement starts again with parent, then candidate. Timing or allocation results never cause exclusions.

The final monitor completed 12 pair attempts: six retained, six discarded and retaken. All 24 native legs passed
the probe. Retained samples span 2026-10-18 07:50:53–07:56:55 UTC. Around retained headless legs, one-minute load
averages were 18.39–37.82 with 8–19 confirmed native Godot processes from other worktrees; rendered legs had
22.10–32.07 load and 10–17 such processes. These are **shared-load comparisons**, not isolated frame benchmarks.
The process count includes only actual native engines whose command names another worktree, excluding wrappers,
our engine and shell commands that merely contain the word Godot. Complete unfiltered `pgrep` output is retained.
These samples completed before the subsequent sandbox change to private process namespaces; later `pgrep` calls
can see only this sandbox, while `uptime` remains machine-wide. The saved pre-change snapshots are the evidence
for other-worktree activity; no process visibility is inferred from the new namespace.

`build/w1/shared-load-final/manifest.json` identifies all retained logs; `attempts.json` identifies every complete
attempt, and each leg has a `*-context.json`. `observed-compilers.json` proves the corrected monitor saw native
Roslyn activity. `summary.csv`, `table.md` and `assessment.txt` contain parsed counts, allocations, tails, normalized
segment work and the paired assessment; the scratch runner/parser are `build/w1/run_shared_final.py` and
`build/w1/summarize_shared_final.py`. Both arena-history and forgotten counts also independently distinguish the
parent/candidate binaries at the final census. The final candidate source/harness is unchanged from the deep gate.

Each leg built 131 and retired 128 segments; the measured portion covers the same 96 post-warm-up segment lifetimes.
Values below are parent/candidate. Every leg additionally used exactly **166,400 bytes / 1,200 settled ticks =
138.6667 bytes/tick**. There is no new steady allocation.

| Mode/pair | Ticks P/C | Bytes/segment P/C | Bytes/tick P/C | Mean ms P/C | p95 ms P/C | p99 ms P/C | ms/segment P/C |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| headless-1 | 1936/2203 | 866883.33/866073.67 | 42985.95/37740.84 | 3.0042/3.3390 | 4.9733/5.6960 | 17.0551/20.5463 | 60.5846/76.6233 |
| headless-2 | 2146/1976 | 867255.92/865746.58 | 38796.16/42060.56 | 3.2653/2.9983 | 5.5490/4.8018 | 18.3397/16.9273 | 72.9934/61.7148 |
| headless-3 | 1795/1726 | 866549.67/865472.33 | 46344.72/48137.51 | 2.8298/2.7372 | 4.1238/4.0775 | 16.1709/13.8993 | 52.9107/49.2122 |
| rendered-1 | 2038/2000 | 867127.00/865753.17 | 40846.02/41556.15 | 3.1747/3.1617 | 5.5148/5.4579 | 18.2157/18.2016 | 67.3963/65.8686 |
| rendered-2 | 1931/2050 | 866888.00/865895.33 | 43097.49/40549.24 | 3.0516/3.2149 | 5.0692/5.6083 | 17.2397/18.9927 | 61.3814/68.6515 |
| rendered-3 | 2015/2120 | 867007.83/866038.17 | 41306.58/39216.82 | 3.2268/3.4224 | 5.5953/6.1076 | 18.5424/21.3467 | 67.7284/75.5774 |

Candidate allocation per segment is lower in every pair, by 809.67–1,509.33 bytes. Bytes per active tick can rise
when fewer slices do the same segment work; allocation per segment and settled bytes/tick are the acceptance metrics.
Whole-callback mean/p95/p99 and normalized total time per segment have signed paired differences in both directions.
Compare the largest absolute paired difference with the larger observed within-version range across the three pairs:

| Mode | Mean: paired max / run range ms | p95: paired max / run range ms | p99: paired max / run range ms | ms/segment: paired max / run range |
| --- | ---: | ---: | ---: | ---: |
| Headless | 0.3348 / 0.6018 | 0.7472 / 1.6185 | 3.4912 / 6.6470 | 16.0388 / 27.4111 |
| Rendered | 0.1956 / 0.2607 | 0.5391 / 0.6497 | 2.8043 / 3.1451 | 7.8490 / 9.7088 |

All paired timing differences fall inside the observed run spread. This supports no repeatable regression under
this load; it does not establish a speedup or a hardware-isolated upper bound. No production or harness source was
changed in response to these measurements.

Wall-time slicing means callback counts and work per callback can differ. Report bytes/tick and whole-callback
mean/p95/p99 with actual tick counts, and normalize total bytes/time by the fixed 96 measured segment lifetimes.
Do not mistake a lower tick count or larger average slice for different generated content or a helper-only speedup.

## Gates, retained failures and limits

- `make setup`, `make build`: warning-free.
- Parent and candidate `SegmentFeedTests` (3 cases), `LabScenarioTests` (7 cases): pass. No previous Lab assertion/log/capture/option
  expression was removed; independent registry coverage includes the new opt-in selection.
- `streaming-history --segments=64 --assert-bounded` against the parent: expected exit 1, “incidental history grew”.
  This proves the new bound assertion detects the original defect; it is not a candidate failure.
- First candidate `make check`: stopped in formatting with IDE0011 (braces on the new `else if`); corrected in the
  production commit. Failed log retained at `build/w1/check.log`; final result recorded below.
- Exploratory `make test` overlapped a later rebuild: its second fresh world-hash process could not resolve the
  ShutdownGuard/TestRunner C# classes during assembly replacement and stalled. That process was terminated; the
  failed gate/logs are retained in `build/w1/harness-test.log` and its printed `build/tmp/wptest-world-*` directory.
  This is a verification orchestration failure; final gates run without concurrent rebuilds.
- `make check`: pass (`build/w1/check-final.log`), warning-free Debug and ExportRelease, 78 existing dependency
  exceptions unchanged, 82 approved source sites, all fast suites/world hashes, gate self-tests including 51 compiled
  architecture negatives and 16 source negatives. An additional real-worker gap/upper-bound case was then verified
  against both runtime versions; final lint/deep results below cover that final harness source.
- `make test-equivalence`: pass (`build/w1/equivalence.log`): frozen wire/schema/world/publication plus Bank, King,
  spill-chain, Short Fuse and controlled Dissolve retirement traces. No reference was regenerated.
- Earlier quiet-only runners were interrupted/restored while waiting for other worktrees; their logs remain in
  `build/w1/pairs*`. They do not supply the final table. The first shared-load runner discarded four complete pairs
  with compiler overlap (`build/w1/shared-load/`), then was stopped at a child-free boundary to cache the builds.
  The cached runner completed 21 pairs, including four provisional accepts, before inspection found its compiler
  filter covered the managed DLL form but missed the SDK's native `VBCSCompiler` executable. **Its entire set was
  excluded**, the runner stopped at a child-free boundary, and the corrected monitor reran both modes from scratch.
  No provisional timing result supplies acceptance evidence. A missing scratch output directory caused an earlier
  cached-runner initialization failure; no sample ran, sources were restored, and the failure log remains at
  `build/w1/shared-load-cached-init-failure.log`. Final accepted/excluded pairs follow the complete monitor above.
- Final `make lint`: pass (`build/w1/lint-final.log`), including the three-case feed suite's source; both build
  configurations warning-free, dependency/source gates unchanged and all 67 Markdown documents linked.
- `make -j4 test-full`: pass, exit 0 (`build/w1/test-full-final.log`). All final runtime/harness sources were tested;
  a docs-only MeshKit wording correction amended the evidence commit while it ran (both head IDs are retained in
  `build/w1/test-full-head.txt`). No runtime/harness change followed. Deep generator/navigation/dials/lifecycle/input/
  player/Lab suites passed, including full Dissolve seeds 11/12 and the 300-second pacer. The pacer built 32/retired
  30 segments, freed 366 of 374 historically observed tanks, retained no retired tanks in the surviving session,
  and reported 425 raw build samples. The seed-12 bot built 23/retired 20, freed 290 of 307 historical tanks and
  retained no retired tanks. Diagnostic report fields remained populated. All final network audits passed: four-bot
  melee faults, two-bot blocked-duel faults and loading-readiness faults, with zero engine errors/disagreements.
- The process-namespace sandbox restart terminated the first post-performance `make check` during
  `test-behavior`; the incomplete log is retained at `build/w1/check-post-performance-interrupted.log`. It had
  passed its earlier stages, with one Jolt job-capacity warning in the passing body-correction case. The complete
  command was restarted in the new sandbox and **passed, exit 0** (`build/w1/check-post-performance.log` and
  `.exit`). Debug and ExportRelease builds had zero compiler warnings; body correction passed without the Jolt
  warning. All fast suites, real-device input checks, generation/world hashes and gate self-tests passed, including
  51 compiled architecture negatives and 16 source negatives. No runtime or harness source changed for this rerun.

Linux only. W1 does not resolve the architecture ledger's inherited network convergence/capacity, navigation,
causal-history, independent-stream, platform or continuous-animation obligations.
