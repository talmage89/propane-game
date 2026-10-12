# Authored locomotion

The controller owns travel, acceleration, gait eligibility, jumping and melee timing. Animation presents those
rules. The mannequin rebuild and licenses are documented in [animation sources](animation-sources.md); the
34-case controller comparison is documented in [animation review](animation-review.md).

`CharacterAnimator` blends forward, backward, sideways and baked diagonal walk/jog clips on one retained stride
phase. Speed controls cadence; walk-to-jog weights change through acceleration. Aimed creep remains 1.6 m/s.
Non-sprinting movement faces logical aim, so backward travel backpedals and sideways travel strafes. Sprint
continues to face travel and remains forward-only under the existing eligibility rules. Standing ready turns keep
the controller's existing facing threshold and use authored quarter-turn derivatives.

`LocomotionPose` smooths direction changes without resetting the clock. A diagonal's cycle travel is the harmonic
combination of its neighboring strides. Mirrored clips share contact phase; diagonals reconstruct ankle and knee
paths offline instead of blending joint rotations that would arc a planted foot away from the ground. Forward
walk/jog and sprint retain their library poses with edited in-place stance paths. Physics supplies all world travel.

The pistol layer covers arms and fingers, preserving authored pelvis, spine, clavicle and head motion. The rifle
adds bounded upper-body aim and contact corrections. Source mocap arm swing does not compete with the grips.

## Terrain and planting

`FootPlacement` applies small corrections after animation. It never schedules a step, makes a swing arc or chooses
new step destinations. A near-floor ankle can plant once the authored support path slows against world travel;
height alone would mistake late swing for stance. A contact fixes its world anchor until the clip lifts. Exhausting
the planar correction or losing anatomical reach releases that contact until the next authored lift. The fading
correction cannot pull a sole through its support surface.

Each foot probes static world/floor geometry. Terrain offsets and a bounded downward pelvis adjustment preserve
independent support on slopes, slabs and curbs. The authored knee supplies the IK pole, with an outward clearance
constraint. Exact small-angle rotations keep the final ankle on its saved contact. A pose that cannot reach its
anchor at the bounded joint rate releases its contact promise before recovery; it is never reported as planted
while clamped away from the anchor.

Remote cadence follows displayed displacement, including interpolation stalls. Teleports clear contacts and
presentation history. Air and ragdoll release support; physics owns flight height. Physics observations latch
jump takeoff and landing, including snapshots that skip a support tick. Existing jump/get-up timing is retained.

Starts and stops currently blend idle with the continuing gait. Separate start/stop clips have not been selected.
Moving platforms and tanks are not foot supports; the downward queries cover static world and floor geometry.

## Verification

Run `make build` before scenes. Use absolute worktree capture paths:

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --assert-feet --assert-motion --capture-dir=$PWD/build/feet
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --feet-corrections --assert-feet --assert-motion --capture-dir=$PWD/build/feet-corrections
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/player_range.tscn -- --scenario=feet-events --view=side --assert-feet --assert-grip --assert-motion --capture-dir=$PWD/build/feet-events
```

The matrix covers three gaits and eight directions, including remote corrections. Sprint fixtures turn body and
logical aim toward travel; they exercise the same forward sprint clip in each world direction. Each episode
accelerates, runs and stops. Correction episodes also introduce snapshot discontinuities.

The final-pose probe measures near-floor world ankle drift independently of the IK's planted flags. Median drift
must be at most 0.05 m/s in every settled episode; saved-contact residual must be at most 3 mm and consecutive
saved-contact drift at most 0.02 m/s. Coverage requirements prevent passing by withholding all contacts. The
maximum local joint rate remains 1500 degrees/s (25 degrees at 60 Hz). Signed ankle separation stays positive,
and settled separation must meet the tuned minimum within 3 mm.

`feet-events` uses the local controller for creep starts/stops, backpedal, reversals, standing turns, gait changes,
jumps and separate support on a slab/curb boundary. It requires both feet to lift during turns, all jump phases,
takeoff/landing timing and a 0.12 m support-height difference. Stage teleports alone are excluded from continuity
measurements. `feet.csv` records frame-level poses, correction state, contact flags and the actual drift window;
`jump-timing.csv` compares physics and pose ticks.

`make test` includes these probes and the strict seven-case animation gate. `make test-full` additionally runs the
full 34-case gate and deeper lifecycle, melee and generation sweeps. Rendered review remains necessary: proxies
and continuity bounds do not establish a natural silhouette or exact mesh collision freedom.

Presentation tuners are shared match data and require a minor release. Message shapes and `Protocol.Version`
are unchanged by the animation redo. Collider shapes, movement and combat rules remain unchanged.
