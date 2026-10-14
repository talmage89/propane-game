# Rifle stance

`RifleRig` is the only layer over the locomotion clips. It runs after the animation and the get-up blend
(`PoseBlend`), never decides anything, and costs a handful of bone rotations and two two-bone solves per frame. Shots,
their muzzle obstruction and hits come from the logical aim only (`PlayerCharacter.LogicalMuzzle` and the shared `ShotMuzzle*` tuners; see
[Logical muzzle](recoil.md#logical-muzzle)), so no pose can change a hit. The rifle and hands it shows are presentation.

## Layers

1. **Torso.** The shown aim follows the logical aim at up to `RifleAimTurnSpeed` (720 degrees/s), so a mouse flick
   swings the body over a few frames; logical aim and shots never wait for it. The spine turns toward that aim, bladed right so the left shoulder comes forward to the foregrip
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
   bone's frame comes from the bend plane (the mannequin's elbow hinges about its local X), so the arms never flip as
   a strike crosses the body; the wrist takes the hand's twist. If a stance holds a grip a little beyond
   its arm, the rifle slides toward that shoulder by the shortfall. Fingers take absolute curls per grip.

Hip fire raises the rifle from the carry over a few frames (the character's `ReadyRaiseSpeed` and the rig's
`RifleStanceBlendSpeed`): a fast, visible raise. The shot itself is never delayed; its tracer starts at the rendered
muzzle.

The visual aim direction and applied torso yaw both turn at `RifleAimTurnSpeed`. Near the body's rear direction,
the signed aim angle wraps from +180 to -180 degrees: even a tiny aim change can switch the desired torso between
its twist limits. Bounding the applied yaw prevents that switch from snapping the spine and head after a get-up.
It does not limit logical aim or modify the authored clips and final IK poses.

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
frame; the strike peaks near 45 and is continuous, while the IK flips it caught jumped 90 to 170). It skips simulated
ragdoll limbs and the root bone, and measures the pelvis in world space, because the model turning to the get-up
heading re-expresses the same pose. The get-up fade keeps each bone on one interpolation path, so a limb never flips
where the shortest arc to the captured pose changes sides. It also keeps the logical
recoil climb checks; aimed creep allows 8 cm of lateral muzzle sway (the walk clip's shoulders sway about 6.6 cm). Judge the stance from rendered clips ([animation review](animation-review.md)).

`rifle_reach_tests` also crosses the aim behind a stationary body and checks every final bone against the same
60-degree frame bound. This deterministically reproduces the intermittent ragdoll head snap; the physical get-up
heading determines whether the ordinary ragdoll fixture crosses that wrap.

## Actor detail

Actor presentation caches rifle clip names/lengths and immutable finger rest rotations, and reuses camera-shape and aim-ray
query parameters. Query geometry, exclusions and cadence are unchanged. Native query result arrays/dictionaries
still allocate; this does not claim an allocation-free physics path.

The **Actor Detail** tuners sample a camera-free actor's animation tree and skeleton modifiers at 30 Hz beyond
30 m, 15 Hz beyond 80 m, or 10 Hz outside the camera frustum. A 3 m outward hysteresis prevents boundary chatter;
entering the near distance always restores full updates immediately. The local viewed character, actors without
a camera viewing their world, active hit reactions (including their fade) and all ragdoll/capture/get-up states
keep the engine's original idle callbacks. A hit catches up the held tree before queuing the new reaction, so
earlier elapsed time cannot consume the new one-shot.
`ActorPoseLod=false` restores full pose updates and the imported mesh LOD bias everywhere. Rates/distances remain
live tuners, including during matches; they affect presentation, not movement, intent, recoil aim, shot queries
or network snapshots.

Stride, jump, turning travel and reload presentation clocks still advance each frame. Throttled pose samples
receive actual accumulated time; actors stagger their sample phases, and a hitch samples one current pose instead
of replaying missed poses. The top-level rifle follows its last final pose in the moving visual frame between
samples, so it stays in the held skinned hands. Muzzle flashes and logical aim still update normally. Distant
limbs step between samples. Their mannequin and rifle use `ActorMeshLodBias` (default 0.1) times the imported
bias to choose coarser generated mesh LODs; 1 retains the imported bias. Near meshes, shadows, animations and IK
retain their current fidelity. A throw cancels the cadence immediately, and native ragdoll recovery remains at
full rate.

`actor_detail_tests` exercises near, mid-distance, far, hidden, near restoration, a local view looking through an
observer camera, and far ragdoll/get-up. It checks actual native modifier counts, movement, aim/support, sampled
contacts and rifle contacts on frames that hold the previous skinned pose. It also checks near/distant/owning
mesh bias, live mesh-bias changes, full detail during recovery/hit reactions and distant restoration after a hit.
Its reload uses the existing rule:
reload suppresses aim and allows the ordinary movement gait. The unit suite checks distance hysteresis, live rate
changes, time conservation, staggered clocks and zero clock allocations. Both run in `make test`.

Before a throttled actor starts a ragdoll, its tree samples the current authored pose with its pending animation
time. This restores the same initial collider geometry as a full-rate actor, without advancing locomotion clocks
again or delaying the throw. `ragdoll_pose_tests` compares every first physical-bone transform against a full-rate
control while the other actor renders at 1 Hz, during walking, a hit reaction, aim, reload and melee. The fixture
asserts that the held trees really use manual sampling and each weapon action really is active before the throw.
The bounds are 1 mm and 0.1 degree. It fails before the refresh (29.4 cm), or without the full-rate hit transition (65.0 cm/116 degrees),
and runs in `make test`.
