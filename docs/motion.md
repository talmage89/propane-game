# Rifle stance

`RifleRig` is the only layer over the locomotion clips. It runs after the animation and the get-up blend
(`PoseBlend`), never decides anything, and costs a handful of bone rotations and two two-bone solves per frame. Shots,
their muzzle obstruction and hits come from the logical aim only (`PlayerCharacter.LogicalMuzzle` and the shared `ShotMuzzle*` tuners; see
[Logical muzzle](recoil.md#logical-muzzle)), so no pose can change a hit. The rifle and hands it shows are presentation.

## Layers

1. **Torso.** The spine turns toward the aim, bladed right so the left shoulder comes forward to the foregrip
   (`RifleHipBladeDegrees`, `RifleAimBladeDegrees`), and pitches part of the way with it (`RifleHipPitchShare`,
   `RifleAimPitchShare`; less in sprint). One world rotation is spread over `spine_01..03` by the `RifleSpine*Share`
   tuners. A relaxed player looking far behind twists no further than `CombatSpineTwistDegrees`. The clip's own spine
   motion remains underneath.
2. **Head.** The neck takes `RifleNeckShare` of the head's turn toward the aim, and the head the rest
   (`RifleHipHeadFollow`, `RifleAimHeadFollow`), so the aim pitch is spread through the spine, neck and head.
3. **Rifle.** Authored stances, placed in a chest frame (the body's yaw plus the torso twist, and world up) from the
   right shoulder, and blended by state with eased weights (`RifleStanceBlendSpeed`). Blends pivot about the butt,
   so the stock stays put while the rifle swings up, as a person raises one:
   - *Carry*: low and relaxed, muzzle dipped and angled across the body (`RifleCarry*`), stock by the hip.
   - *Hip ready*: stock tucked under the arm against the ribs, muzzle on the aim point (`RifleHip*`).
   - *Shouldered*: butt in the pocket just inside the shoulder joint (`RifleShoulderInboard`, `RifleShoulderUp`,
     `RifleShoulderForward`), the sight line through the aim point. The head comes down to the stock
     (`RifleCheekWeld` of the way from its look to the sight, with `RifleCheekRollDegrees` of cant) rather than the
     rifle rising to the eye: the rifle sits at eye height and the stock never reaches through the shoulder or head.
   - *Sprint port*: muzzle down and inboard (`RifleSprint*`).
   - *Reload*: in front of the chest, canted, magazine well toward the left hand (`RifleReload*`); the left hand follows
     the rifle's reload clip to the magazine and bolt catch.
   - *Butt strike*: see below.
4. **Recoil.** An underdamped spring (`RifleRecoil*`) pushes the stock back and lifts the muzzle about the butt; the
   torso absorbs `RifleRecoilTorsoShare` of the lift.
5. **Hands.** Two-bone IK puts each wrist on its grip frame (`RifleRightWrist*`, `RifleLeftWrist*`, magazine and bolt
   frames) with elbows down, out and a little back (`RifleElbowOut`, `RifleElbowDown`, `RifleElbowBack`). Each arm
   bone's frame comes from the bend plane (the mannequin's elbow hinges about its local X), and the forearm takes half
   the hand's twist, so the arms never flip as a strike crosses the body. If a stance holds a grip a little beyond
   its arm, the rifle slides toward that shoulder by the shortfall. Fingers take absolute curls per grip.

Hip fire raises the rifle from the carry over a few frames (the character's `ReadyRaiseSpeed` and the rig's
`RifleStanceBlendSpeed`): a fast, visible raise. The shot itself is never delayed; its tracer starts at the rendered
muzzle.

## Butt strike

`MeleeStrike` is an authored keyframe track keyed to the gameplay phases (`MeleeWindup`, `MeleeActive`,
`MeleeRecovery`), so retiming the strike keeps its shape. Windup: the torso coils right and the stock is drawn back
by the right hip, the muzzle up and forward across the body. Active: the torso unwinds left and leans in; the rifle
pivots near the support hand, which pulls the barrel back past the left shoulder while the firing hand drives the
stock through a wide arc to chin height in front. Recovery: a short follow-through, then the rifle returns to the
hip. `MeleeTorsoTwistDegrees` and `MeleeTorsoLeanDegrees` scale the body's part. Hit volumes, timing and the lunge
are unchanged. A strike that cancels a reload returns the support hand at `MeleeReloadReturnSpeed`.

## Removed layers

The earlier procedural package is gone: world-space foot planting, stride sway of the rifle and torso, per-bone
angular rate limits on authored and solved poses, the raised clavicle, the neck/head fit to the eye socket with
reach projection, and per-shot barrel and grip re-fitting. They made motion rigid, delayed it, and coupled hits to
the pose. Their tuners were deleted.

## Checks

`--assert-grip` (grip, ragdoll, recoil, melee and network scenarios) keeps sanity bounds: wrists within 2 cm of their
grips, no hand 1 cm into the rifle's solid proxies, an aimed eye within 15 cm of the sight, finite values and stance
coverage. `--assert-motion` rejects non-finite bones and snaps (a limb turning 60 degrees, or a finger 90, in one 60 Hz
frame; the strike peaks near 45 and is continuous, while the IK flips it caught jumped 90 to 170) and keeps the logical
recoil climb checks; aimed creep allows 5 cm of lateral muzzle sway. Judge the stance from rendered clips ([animation review](animation-review.md)).
