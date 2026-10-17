# Locomotion

The character plays polished public clips as authored ([sources](animation-sources.md)), the way the first release
did. Nothing plants, generates or overrides steps; a light rifle stance ([rifle stance](motion.md)) layers the arms,
aim and hands over the legs. Movement speeds, acceleration, sprint, aim, creep and jump rules belong to the
controller and are unchanged by presentation: aimed creep is 1.6 m/s (`CreepSpeed`), the run 6 m/s (`RunSpeed`) and the
sprint 9.5 m/s (`SprintSpeed`). The walk clip's native speed (0.98 m/s) is only a calibration value, not a limit: the
stride clock below follows the distance travelled, so the clip plays faster or slower with the actual speed.

## Clips on one stride clock

`LocomotionPose` keeps one stride phase. It advances by the distance travelled over the current blend's ground per
cycle, and only while supported, so jumps hold it. Every locomotion clip is one cycle whose zero is the left foot's
touchdown (`CharacterAnimator` offsets the packaged clips by their measured touchdowns), and the tree seeks every
clip to that phase. Blending two gaits or two directions therefore never restarts or doubles a step.

| Family | Clips | Ground per cycle (tuner) |
| --- | --- | --- |
| Walk (walk, aimed creep) | `Walk`; 100STYLE `Walk_Back`, `Walk_Left`, `Walk_Right` | `WalkStrideLength` 1.33 m, `BackStrideLength` 1.0 m, `SideStrideLength` 0.85 m |
| Jog (6 m/s run) | `Jog_Fwd`, played in reverse to run backward | `JogStrideLength` 5.5 m |
| Sprint (forward only) | `Sprint` | `SprintStrideLength` 6.3 m |

The walk family's weight falls with speed toward the jog (`1 - smoothstep(0, RunSpeed, speed)`); the resolved creep
gait keeps the walk family. Sprint fades in with speed while the resolved gait is sprint. `Moving` blends from the
authored `Idle` over `AnimationIdleSpeed` and `LocomotionSettleTime`, so starts and stops ease between idle and the
continuing cycle. `GaitBlendTime` paces the gait weights.

## Direction relative to the body

Travel is measured in the body's frame (local players use their velocity, remote copies their displayed
displacement, so an interpolation stall stops the feet as well; a displacement faster than 1.5 times the sprint
speed is a snapshot correction, and the replicated velocity stands in for it). The heading of travel turns smoothly; it is an
angle, so even a straight reversal sweeps through a side direction. Diamond weights then pick neighbors: the
sidestep's share is `|x| / (|x| + |y|)`, so a diagonal is half sidestep and half forward walk or backpedal. The
front/back and left/right pairs change over only within a few degrees of the axis where their share is near zero,
so no clip pops in. The jog family has only forward and reverse; its reverse share crosses over around the side.

Which way the body faces decides which clip plays (`PlayerCharacter.UpdateFacing`):

- **Relaxed** (no aim, no recent shot): the body turns to face travel and plays the forward cycles, as the first
  release did.
- **Ready** (aiming or `ReadyHoldTime` after a shot), walking or creeping: the body faces the aim, and the walk family
  steps in any direction: backpedal, sidesteps and diagonals.
- **Ready, running**: the legs turn toward travel by up to `CombatSpineTwistDegrees` (moving backward past
  `BackpedalAngleDegrees`, they face the aim and run backward) and the torso twists back to the aim. The turn grows with
  speed from `CreepSpeed` to `RunSpeed`, so the change from the walk family is gradual. The 100STYLE runs are slow
  jogs whose steps cannot be stretched to 6 m/s, so the run keeps the packaged jog.
- **Ready, standing**: the body turns to the aim once the twist exceeds `CombatTurnDegrees`, in short bursts while
  the look keeps turning. The turn steps: its yaw rate, smoothed over `GaitBlendTime` into one turn, times
  `TurnStepRadius` and capped at the creep speed, drives the sidestep in the turn's direction.

Facing is presentation; it is replicated with the snapshot, so remote copies choose the same clips.

## Jumps and landings

Physics observations latch takeoff and impact before render can skip a one-tick support state; an accepted
`Jumping` also latches a rebound when no grounded tick was observed. `Jump_Start` is sampled by vertical velocity
through takeoff (`TakeoffClipStart`, `TakeoffClipSpeed`), rising, apex and falling (`Jump*ClipTime`,
`JumpSampleBlendSpeed`). Landing blends `Jump_Land`'s compression into the continuing gait, scaled by impact
(`LandingSettleTime`, `LandingFullImpactSpeed`, `LandingClipTime`). The air blend uses `AirPoseRaiseSpeed` and
`AirPoseLowerSpeed`.

## Cost

Per character and frame: one `AnimationTree` with nine time-seeked clips, `RifleRig`'s handful of bone rotations
and two two-bone solves, and finger curls. No physics queries, no per-bone queries and no per-frame allocations
(tree parameter names are cached `StringName`s).

## Verification

`make test` runs the unit suite (phase across gaits and stops, directional weights and their crossovers, jump
latching and landing), the movement suite, and the `feet`, `feet-events` and network movement probes. Those probes
assert finite poses, no snaps (40 degrees in one 60 Hz frame), jump-phase coverage, at least six near-floor sample
pairs per episode (the run touches down briefly) and feet that lift 1.5 cm in standing turns (a captured sidestep
lifts its trailing foot only about 3 cm). They report near-floor foot drift and ankle separation without asserting
them: the `feet` matrix runs every gait in every direction with the body held facing forward, which play never does
at a run, so its sideways runs slide. Judge the motion from rendered
clips ([animation review](runbooks/animation-review.md)).
