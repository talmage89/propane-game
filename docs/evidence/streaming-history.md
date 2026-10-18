# W1 streaming history verification

**Status:** Storage, lifetime, feed differential, sampled visual checks and fast/reference gates verified on Linux.
This record covers that focused proof and the measurement protocol. The submission report records the subsequent
final-head deep gate and remaining monitored performance pairs; their logs remain under `build/w1/`. Current contracts: [architecture](../architecture.md) and
[streamed content history](../world-services.md#streamed-content-history).

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

The runner waits for five quiet seconds before each measured run and checks other worktrees' Godot, Blender,
game, make and dotnet build/format/run/test processes every 250 ms while it runs. Overlapped samples are retained
and excluded. No scheduling reservation was confirmed; the evidence is monitored quiet intervals rather than a
hardware-isolated benchmark. The first clean headless pair measured 1,206/1,192 callbacks (parent/candidate),
83,105,600/83,050,680 bytes, mean 2.301227/2.293226 ms, p95 3.3779/3.3522 ms and p99 11.8117/11.7178 ms.
Both versions used exactly 166,400 bytes over the additional 1,200 settled callbacks (138.67 bytes/tick).
A fresh complete manifest of accepted pairs is accumulated at `build/w1/pairs-monitored/manifest.json`; the
submission report gives the completed table and interpretation. Failed overlap trials remain alongside it.

Wall-time slicing means callback counts and work per callback can differ. Report bytes/tick and whole-callback
mean/p95/p99 with actual tick counts, and normalize total bytes/time by the fixed 96 measured segment lifetimes.
Do not mistake a lower tick count or larger average slice for different generated content or a helper-only speedup.

## Gates, retained failures and limits

- `make setup`, `make build`: warning-free.
- Parent `SegmentFeedTests` (2 cases), `LabScenarioTests` (7 cases): pass. No previous Lab assertion/log/capture/option
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
- The first performance attempt produced one clean headless pair, then the monitor rejected the second parent
  sample because another worktree started a dotnet build mid-run. The native probe itself passed. All attempt logs
  remain in `build/w1/pairs/` and `build/w1/pairs.log`; only monitored quiet samples supply the final paired table. A second attempt also rejected an overlapped
  candidate sample (`build/w1/pairs-quiet2/`); the monitored runner retains accepted legs and retries overlap-only
  failures. It was deliberately interrupted/restored while waiting so the deep gate could run before resuming it.
- Final `make lint`: pass (`build/w1/lint-final.log`), including the three-case feed suite's source; both build
  configurations warning-free, dependency/source gates unchanged and all 67 Markdown documents linked.
- `make -j4 test-full` is executed after this evidence commit on the final head. Its complete outcome is recorded
  in the submission report and `build/w1/test-full-final.log`; this focused record does not pre-claim its result.

Linux only. W1 does not resolve the architecture ledger's inherited network convergence/capacity, navigation,
causal-history, independent-stream, platform or continuous-animation obligations.
