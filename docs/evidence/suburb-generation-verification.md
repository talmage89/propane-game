**Status:** Verified B10 extraction on Linux. Current contract: [suburb generation](../suburb-generation.md).

# Suburb generator stage extraction

2026-10-17, Linux x86-64, Godot 4.7.2 .NET, .NET 10 SDK. Baseline: `4b4d14b`. Branch:
`wp/rf-b10-generator-stages`. Layout commit: `17f7177`; furnishing commit: `24e17c1`; supply candidate follows it.

## Ownership

| Surface | Lines | Owns |
| --- | ---: | --- |
| `SuburbGenerator.cs` | 131 | 52-line coordinator plus the unchanged catalog/settings declarations; plan creation, ordered calls and existing yaw API. |
| `SuburbLayout` | 338 | Road templates, lot packing, cropping, road areas and explicit-input geometry queries. |
| `SuburbFurnishing` | 588 | Houses, paving, yard/street props, parkland, occupied areas, obstacles, story sites, fence segments and openings. |
| `SuburbSupply` | 285 | Tank candidates/formations, player starts, ammo starts and their selection scratch. |

The master RNG is the same object passed to all three owners. Lot RNG creation remains inside lot furnishing.
Stage state retains the generator's original lifetime. Supply borrows furnishing's outputs; only furnishing writes
them. The original concrete fence list preserves struct enumeration in repeated clearance queries. Supply still
checks raw segments, before gate cuts. Multiplayer additions remain last. Runtime `Suburb` and streaming sources
are untouched. No interfaces, processing nodes, scheduling or asset recipe abstractions were added.

## Parent plan bytes

Scratch instrumentation copies the immediate parent's generator and extracted owners with `git show`, compiles
those copies under a separate namespace, and compares their `PlanCodec.Encode` results with the candidate. Parent
static imports resolve to the parent namespace too. The unchanged settings, catalog, plan types, pile builder and
codec are shared. A mismatch asserts and exits nonzero with its seed/settings and first differing byte.

Each extraction checks 4,000 complete plans: 100 seed values, eight settings variants and five weight sets. Seeds
are 1–80 and `100000003 * k` for `k = -10..9`. Settings are default single player; 2/4/8/16/24/32-player matches;
and a 110 m strip with 30 tanks. Weight sets are defaults plus forced Rows, Square, Triangular and Cluster.

| Candidate | Immediate parent | Exact plans | Encoded bytes compared | Result |
| --- | --- | ---: | ---: | --- |
| Layout | `4b4d14b` | 4,000 | 56,330,189 | PASS |
| Furnishing | `17f7177` | 4,000 | 56,330,189 | PASS |
| Supply | `24e17c1` | 4,000 | 56,330,189 | PASS |

The layout probe runs as one optimized Debug process; later probes partition the same ordered case inventory into
four independent 1,000-plan processes, asserting each shard's completion/count. These are scratch probes, outside
CI. Files and logs are retained under `build/rfb10/`: `prepare_parent.py`, `B10PlanProbe.cs`, `Scratch.targets`,
`run_shards.py`, `layout-bytes-optimized.log`, and the furnishing/supply shard logs. The temporary build targets
are absent from ordinary builds. A text check also verifies all 31 moved block-bodied methods against the initial
parent, normalizing only the explicit geometry-query arguments; coordinator/stage entry points are reviewed separately.

## Gates and references

Layout and furnishing each pass `make setup`, warning-free builds/lint, `make check` and `make test-equivalence`.
The latter includes `--platform-references`: four exact saved release plans, saved-plan construction hashes, 60
exact segment hashes, wire/schema/publication fixtures and all five seeded Lab traces. References were not refreshed.
`make check` includes `make test`, bounded forced formations, the generator sweep, four separate-process world
hashes, 60 separate-process segment hashes, the real device-input path and the gate self-tests.

Supply also passes `make test-equivalence`, including all exact platform references and five Lab traces.
The final deep sweep passes:

```sh
make -j4 test-full-generator-1 test-full-generator-2 test-full-generator-3 \
  test-full-generator-4 test-full-generator-coverage
```

All four shards report `ALL PASSED`. The independent coverage checker accounts for exactly 1,044 inventory entries:
944 suburb layouts (200 single-player, 200 two-player, 400 larger matches, 144 forced-formation layouts) and 100
segment runs of 12 plans each. All 1,200 segments pass with no rerolls. The final sweep uses a clean optimized Debug
assembly without scratch parent/probe classes. Log: `build/rfb10/supply-deep-final.log`.
Final `make check`: PASS, including warning-free compilation, `make test` and all infrastructure self-tests.
Only deliberate negative-test diagnostics appeared; the earlier incidental Jolt warning did not recur. Log:
`build/rfb10/supply-check.log`. All three stage commits therefore pass the full gate and exact release references.

## Rendered comparisons

`suburb_preview.tscn`, `godot-mono --resolution 1280x720 --fixed-fps 60`, effects seed 101:
seed 7/default single player, seed 23/forced Square single player, and seed 41/eight-player match. Captures use
absolute worktree paths under `build/rfb10/{parent,layout,furnishing,supply}-<seed>`. Each set contains overhead,
aerial, spawn, backyard, front and tank views. The parent was captured before editing. `captures.py` asserts exact
PNG file bytes. Layout and furnishing each match all 18 parent images. Representative parent overhead/backyard
views and candidate overhead/backyard views were inspected: roads, slabs, lots, houses, props and fences agree.
Supply also matches all 18 PNGs. Final seed 7/41 overheads and seed 23 backyard were inspected and agree with the parent.

## Generation measurements

The original baseline and final candidate run in one process with identical instrumentation and settings: default
single player, seed 23, 30 tanks, shared catalog; 50 paired warm-ups, then three alternating pairs of 50 complete
generations. Timing includes constructor and `Generate`, excludes encoding/building, and records per-generation
mean/p95/p99. Allocation is main-thread `GC.GetAllocatedBytesForCurrentThread`; timer buffers are allocated before
measurement. These measurements cover synchronous plan generation; runtime tick code is unchanged.

Builds use `--no-incremental`, log `DebuggableAttribute.IsJITOptimizerDisabled`, and assert the intended configuration.
Optimized Debug uses `DOTNET_TieredCompilation=0` to stabilize adaptive optimization; ordinary Debug retains its
default runtime settings. Confirmed pairs (`parent` / `candidate`, milliseconds):

| Configuration/pair | Mean | p95 | p99 | Allocated bytes/plan |
| --- | --- | --- | --- | --- |
| Optimized, 1 | 39.579 / 39.801 | 40.989 / 41.073 | 41.110 / 41.195 | 10,368,622 / 10,368,790 |
| Optimized, 2 | 39.557 / 39.732 | 40.883 / 41.034 | 41.031 / 41.209 | 10,368,622 / 10,368,790 |
| Optimized, 3 | 39.638 / 39.718 | 40.818 / 41.065 | 40.879 / 41.256 | 10,368,622 / 10,368,790 |
| Debug, 1 | 46.671 / 46.894 | 48.132 / 48.410 | 48.571 / 48.602 | 14,454,630 / 14,454,798 |
| Debug, 2 | 46.682 / 46.787 | 48.172 / 48.512 | 48.439 / 48.632 | 14,454,630 / 14,454,798 |
| Debug, 3 | 46.790 / 46.921 | 48.362 / 48.455 | 48.494 / 48.677 | 14,454,630 / 14,454,798 |

The fixed increase is 168 bytes per generated plan: original coordinator object 104 bytes versus final coordinator
and three owners totaling 272 bytes on this runtime. Mean generation cost is within 0.6% in these pairs. There is no
new per-query allocation growth in the confirmed configurations and no new per-tick path. Logs and instrumentation:
`controlled_bench.py`, `bench-optimized-eager.log`, `bench-normal-debug-default.log`, and `B10PlanProbe.cs` under
`build/rfb10/`.

Additional eager runs reproduce the small fixed owner cost. Supplemental eager Debug had a candidate p99 outlier
of 63.845 ms in pair 2 (parent 49.445 ms); other p99s were about 49–50 ms. This is retained in
`bench-normal-debug-eager.log`. The measurements make no claim about runtime physics or rendering throughput.

## Retained failures and limits

- The initial link check rejected the new, unstaged contract document. Staging it resolved the refusal; the full gate passed.
- An extraction-script offset error left a trailing source fragment during furnishing's first setup. The build failed;
  the script/edit was corrected before any furnishing proof. The failed setup log is retained alongside the clean build.
- Layout's gate emitted one Jolt job-capacity warning during body correction while other heavy work was active.
  The isolated rerun passed without warnings, with unchanged drift/impulse numbers. Furnishing's gate did not repeat it.
  The malformed-Score warning and capture-save errors in core tests are deliberate negative cases.
- The first normal Debug scratch probe was intentionally superseded by the complete optimized Debug comparison;
  its incomplete log is retained and is not counted as a pass. Ordinary gates and release references use normal Debug.
- Default-tiered optimized measurements varied by process: an initial candidate allocation increase of about
  942 KB/plan, then +168 bytes in a diagnostic process, then about 820 KB fewer candidate bytes on repetition.
  The deep sweep was interrupted while this was investigated; its incomplete log is retained and superseded by
  the final sweep. No production change was made to chase those measurements. Confirmed configuration logs above
  separate the fixed owner cost from adaptive-runtime variation. A concrete-list diagnostic variant was not adopted.
- A scratch benchmark logger initially needed `System.Environment` qualified to avoid a Godot type-name collision;
  that failed diagnostic build log is retained. The configuration comparisons were rebuilt from scratch afterward.
- Verification is on Linux. These checks do not establish cross-platform or cross-CPU bit identity.

No gameplay/effect values, tuner classification, plan format or message shapes changed. `Protocol.Version` stays 3.
