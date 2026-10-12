# Rifle motion and body clearance

The rifle layer preserves the authored gait's pelvis, spine and head motion. A bounded aim offset adds pitch and
yaw; part of the captured chest yaw remains, rather than a pistol hold replacing the whole torso. This keeps
weight shift and head movement while bringing the shoulders toward logical aim. [Locomotion](locomotion.md)
describes clip cadence and small foot corrections; [animation review](animation-review.md) supplies the paired
release/dev/candidate comparison and immutable natural-motion calibration.

The stock sits near the shoulder pocket. A bounded neck lean brings the cheek toward it while the head keeps
its gaze. An exact optic weld is intentionally absent: it would translate the stock through the mannequin's broad
head. Carry, low ready, aimed hold and reload placement are tuners. The sprint carry keeps the support forearm
above the moving thighs. The original wrist frames, finger curls, curved magazine/bolt contacts and get-up handoff
remain the contact definitions.

## Constraint fit

`RifleClearance` derives ellipsoid proxies from the mannequin skin for pelvis, spine, head and thighs. It samples
the rounded stock core, upper/lower arms and tapered arm pairs. The shoulder pocket uses the proximal upper-arm
capsule. A wrist obstruction follows its own surface normal; it does not inherit an unrelated head correction.
Corrections measure each violated surface again, since searching for clearance of every surface along one normal
can have no solution and unnecessarily jump the whole correction limit.

The fit alternates skin clearance, shoulder pocket, wrist reach and elbow placement. Elbows stay on the anatomical
two-bone circle; only an obstructed preferred bend searches alternatives. Scapular protraction has a total bound
and a per-frame bound. Small-angle rotations retain wrist precision rather than treating nearby targets as equal.

The coherent upper pose, rifle, get-up handoff weight and reload contact clock are fitted together. Clamping individual arm rotations
away from a solved contact would detach the hands. Failed candidates reduce the whole pose change, with distinct
body and carrying-arm rates; the final motion probe still enforces its 1500 degrees/s ordinary-pose bound. The
previous elbow is a destination, while the candidate supplies the twist reference. Reload trials follow the curved
contact path, including its final return; ammo, eligibility and reload completion remain authoritative.
Get-up trials also retain their applied handoff progress when the pose is held. Clearance fades in with the rig,
so an initially physical hand can transfer its rifle without an immediate stock correction snapping the wrist.

[Recoil](recoil.md) owns persistent player-controlled climb and barrel alignment. The fit clears both reachable
grips before the authoritative muzzle ray. The rendered firing pose retains that exact world muzzle and carries
the shoulder frame through actor translation, facing and authored pelvis motion. The two arms follow the rigid
aligned rifle immediately, as R19 requires; body/head and digit bounds remain measured. This does not change the
recoil spring, camera, firing eligibility, damage or spread rules.

These volumes are regression proxies, not exact mesh collision shapes. The head/shoulder harness tolerates 5 mm
of proxy uncertainty; its shoulder upper bound is 25 mm. Skin-derived body and arm clearance are checked in the
gait, pitch, recoil, reload, terrain, melee and lifecycle suites. Rendered silhouettes remain part of acceptance.

## Reproduce

```sh
make build
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=grip --grip-sweep --assert-grip --assert-motion --capture-dir=$PWD/build/grip
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/player_range.tscn -- --scenario=motion --view=side --assert-motion --assert-grip --capture-dir=$PWD/build/motion-side
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/player_range.tscn -- --scenario=motion --view=player --assert-motion --assert-grip --capture-dir=$PWD/build/motion-player
```

`motion` covers analog walking, jogging, sprint, creep, bursts, walking reload and ragdoll/get-up. `grip-sweep`
adds both pitch limits in every hold and both reload types. The final modifier requires wrist position error at
most 3 mm, orientation error at most 0.1 degree, sampled hand penetration at most 0.5 mm and the tuned reach
margin within 0.5 mm. Coverage requirements catch a stalled reload or a pose that never reaches an aimed hold.
Body/arm and independent continuous tapered arm-pair gaps must be at least −5 mm; fully aimed gaze error must
be at most 30 degrees. `grip.csv` records frame-level contact and clearance values.

The motion probe measures local joint changes after all modifiers. Spawn and explicit staging teleports are
excluded; ordinary snapshot corrections remain measured. Physical ragdoll samples are separate, because
limiting those poses would change blast response. Firing body/head use `RifleShotPoseAngularSpeed`; immediate
aligned arm changes are reported separately under R19's existing exception. Digits retain their ordinary bound.
Variable-rate network runs measure angular rate as well as raw frame changes.

Network proof uses the production bots and remote poses:

```sh
tools/dev/net_bots.sh -o $PWD/build/motion-net -n 2 -w 0 -l 18 -- --scenario=movement --matches=2 --assert-feet --assert-grip --assert-motion --net-lag=120 --net-jitter=30 --net-loss=3
python3 -B tools/dev/check_scores.py build/motion-net/server.log build/motion-net/bot1.log build/motion-net/bot2.log
```

Use `-w 1 -l 35 --view=player --capture-dir=$PWD/build/motion-net-player` for rendered player-camera evidence.
Fixed-step review films are visual evidence; real-time recordings and frame times are affected by shared GPU load.
Presentation tuners are shared match data and require a minor release; this package changes no message shape or
`Protocol.Version`. The owner approves the look before landing.
