# Crowded effect profiling

The Lab measurement uses 180 idle warm-up frames and 600 sampled real frames at 1920×1080, Forward+, seed 23,
and baked tuning. The trigger is included in the first wall sample. Opt-in `EffectProfile` timers are inclusive;
`Blast` includes the vents created by chain hits. These timers do not alter simulation decisions.

Before optimization, a 128-tank ordinary blast measured wall p50/p95/worst 116.3/152.1/236.1 ms and GPU p95
150.8 ms; physics p95 was 4.6 ms. Diagnostic exclusions identified lit smoke as the dominant persistent cost:
hiding smoke reduced GPU p95 to 18.4 ms; hiding all particles reduced it to 15.5 ms. Those first exclusion runs
traversed the tree every frame and are GPU evidence only, not valid CPU/GC measurements. The harness now applies
exclusions through NodeAdded instead. Omitting lights also saves substantial smoke lighting work. Vents already
have no shadows; explosions cap simultaneous shadow flashes at three.

Cold trigger timers measured about 86 ms vent construction, 46 ms explosion construction, 30 ms first scorch
texture construction and 8 ms debris construction. Blast's inclusive 68 ms includes newly punctured vents.
The primed 128-tank chain also showed a 1,330 ms wall spike, 140 ms debris construction in one frame and 565 ms
inclusive blast work. Lab sessions do not run the existing game/match effect warm-up. The repository has no audio
playback path, so these runs have zero audio voices. Jolt's active-body/contact Performance monitors return zero
here; that is unavailable telemetry, not evidence of zero contacts. Physics time remains measurable.

Diagnostics are not shipped presentation changes. Use `lab_test --scenario=measure --count=128 --phase=blast`
with `--metrics=<absolute path>` for raw CSV, or `--profile-hide=smoke|particles|lights|shadows|decals` for a
controlled rendering exclusion. Only newly created effect nodes are excluded; the sun is retained. GPU, wall,
render CPU, process, physics, object counts, compilation monitors, allocations and collection counts are printed.
`--scenario=effects --count=1|8|128 --capture-dir=<absolute path>` captures a single vent and primed chains.
Use `--fixed-fps 60` for those visual comparisons; omit it for throughput measurements.

## Implementation

Particle draw meshes and growth/shrink curves are shared immutable resources. Vent and explosion process
materials cache only the most recent scale; a tuning edit builds a new resource without mutating emitters already
in flight. CPU-generated scorch/hole/glow textures are built at session assembly. Lab starts the same effect
warm-up as the game, without adding physics bodies, and the hidden flash retires with its warm-up root.

Smoke discards only exactly zero-opacity fragments, including completely depth-faded or lifetime-faded fragments,
before noise work and Forward+ lighting. Particle counts, lifetimes, colors, trajectories, smoke density, light
energy and every existing gameplay/effect dial stay unchanged.

Above `VentLightMergeThreshold` (40), at most `VentLightClusterCount` (8) proxy lights combine nearby vents at
energy-weighted centers. Seeds begin near the camera and then spread across the sources. A source farther than
`VentLightMergeRadius` (2 m) from every seed keeps its individual light: this is a soft budget, not permission
to relocate isolated vents' light across the world. Unmerged energy plus proxy energy equals the original total;
flash lights and their existing three-shadow limit are unaffected. At 40 vents or fewer, every original vent
light remains unchanged. All three new presentation dials enter match tuning snapshots (minor-release scope);
no network message changes and no `Protocol.Version` change.

Blast query paging preserves engine hit order, duplicate shapes and caller exclusions. It uses a managed RID
hash set on full pages instead of repeated native linear searches, and skips exclusion work on the final short
page because no more query is needed. Queries still visit every body and apply the same impulses and chain rules.
The dense-query harness asserts 1,100 bodies across multiple pages, caller exclusions and short-page hit order.
