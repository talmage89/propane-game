# Rifle motion review

Movement package 4 presents the existing movement controller. Speeds, facing rules, ammo timing, spread, tank
behavior and blast responses are unchanged. New shared presentation tuners require a minor release; messages and
`Protocol.Version` are unchanged.

## Presentation

`RifleRig` reads the same `LocomotionPose` phase and gait weights as the clips and feet. A full-stride sine moves
the carry sideways and rolls the rifle about its stock; a double-frequency cosine supplies vertical and fore-aft
motion. The torso rolls with the stride. Sprint scales the motion up, walking scales it down, and aiming suppresses
it. The aimed pelvis also suppresses the authored walking twist. Neither gait transitions nor shots restart the
stride. Remote characters use displayed snapshot travel, just like their feet.

Both local fire and the existing remote Shot event add momentum to an exact underdamped `RecoilSpring`. The torso
absorbs most of the rearward travel and some pitch; the remaining compression and muzzle lift pivot about the stock.
The shouldered pose fits the head to the moving eye socket. Bursts preserve the spring's displacement and momentum,
then settle to zero. Camera recoil also returns completely, including its transient sideways kick, without changing
the player's input yaw or pitch. Shot rays and spread still use the existing camera path.

`PoseBlend` runs before contacts, bounds authored animation changes, and observes the displayed pose at the end of
the modifier stack. Get-up starts from that displayed pose. Physical ragdoll bones remain authoritative; digits,
which have no physical bodies, still blend their release. The rifle caches its handoff in the chest frame. The
support hand's handoff reference follows that chest rather than staying behind in skeleton space.

The rig interpolates a coherent upper pose and rifle placement, then solves both wrists. If a candidate exceeds
`PoseAngularSpeed`, it reduces the candidate and solves again (at most 12 attempts). Hands are never detached by
clamping arm rotations after IK. The aimed eye anchors the final placement before the reach projection. Finger curls
also obey the angular tuner. This can delay presentation during abrupt look changes; effective aim and fire remain
immediate. No per-frame pose arrays are allocated after bone resolution.

Earlier contact release (`FootContactRadius`) prevents the trailing lateral sprint leg from exhausting reach while
waiting behind the planted leading foot. Air and contact recovery bound lower-pose changes while contacts are
unsettled; settled ground contacts retain their exact analytic solves. Snapshot reload progress has its own bounded
visual clock, including the final hand return if a snapshot skips the end. Local reload progress is unchanged.
An exceptional grounded solve above `FootPoseRecoverySpeed` also enters bounded recovery and withdraws its contact
promise until the displayed joints converge. This covers late support and stance snapshots without sliding a
reported planted contact.

## Reproduce

Run `make build` first. All rendered sequences advance at 60 frames/s, with 120 Hz physics. The `motion` scenario
covers analog walk, default run, sprint, creep, nine-shot hip and aimed bursts, walking reload, and a controlled throw
followed by get-up into a run. It captures every rendered frame in the review windows, rather than sampling shots
between kicks.

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=motion --assert-motion --assert-grip
godot-mono --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=motion --view=side --assert-motion --capture-dir=/tmp/wpmove4-side
godot-mono --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=motion --view=player --assert-motion --capture-dir=/tmp/wpmove4-player
```

`--capture-group=walk|run|sprint|creep|hip|aim|reload|getup` narrows the capture window; `recoil` captures both bursts.
For an A/B fixture, use the motion scene/probe harness on package 3 (`cf3adce`), without changing player code.
The harness files are `src/Dev/PlayerRange.cs` and `src/Dev/MotionProbe.cs` with their `.uid` files; rebuild the
fixture before running it. Use `--motion-metrics` on the reference because it intentionally fails the new bounds.
The old recoil comparison also overrides `RifleRecoilBack=0.045` and `RifleRecoilLiftDegrees=5` on that fixture.
Do not use those overrides on the new spring implementation as a substitute for the old recoil algorithm.

```sh
python3 -B tools/dev/review_motion.py /tmp/wpmove4-before-side /tmp/wpmove4-after-side /tmp/wpmove4-review-side --old-recoil=/tmp/wpmove4-old45-side
python3 -B tools/dev/review_motion.py /tmp/wpmove4-before-player /tmp/wpmove4-after-player /tmp/wpmove4-review-player --old-recoil=/tmp/wpmove4-old45-player
tools/dev/net_bots.sh -o /tmp/wpmove4-net -n 2 -w 0 -l 18 -- --scenario=movement --matches=2 --assert-feet --assert-grip --assert-motion --net-lag=120 --net-jitter=30 --net-loss=3
python3 -B tools/dev/check_scores.py /tmp/wpmove4-net/server.log /tmp/wpmove4-net/bot1.log /tmp/wpmove4-net/bot2.log
```

The network sequence extends the gait/jump proof with hip and aimed bursts and a walking reload. Each match gets
fresh remote probes; recoil and reload coverage are required when `--assert-motion` is enabled.
For rendered remote screenshots, use `-w 1 -l 35 --view=rifleright --capture-dir=DIR` with the same assertion
and transport flags. Keep `-w 0 -l 18 --matches=2` for the headless reuse/second-match proof.

## Acceptance

`make test` asserts final wrist contact and sampled hand penetration, a maximum angular rate of 1500 degrees/s
(25 degrees at 60 Hz), and creep lateral muzzle range no greater than 12 mm over a measured steady interval.
The motion scenario also requires burst coverage and camera return within 0.02 degrees after both bursts.
Unit tests check spring recovery at 30/60/120 Hz, burst accumulation, bounded response and lifecycle reset.

The rotation metric covers every local bone after all modifiers. Spawn and explicit staging teleports are excluded;
ordinary snapshot corrections remain measured. Physical ragdoll samples are reported separately because limiting
those poses would change the physical blast response. Digit release remains subject to the bound even during a
ragdoll. Variable-frame-rate network runs report both raw frame changes and angular rate; a frame longer than
1/60 s can contain more than 25 degrees while meeting the equivalent rate bound. Rendered screenshots can stall
GPU readback. The local comparison uses fixed 60 Hz stepping; a network screenshot run uses real time and reports
the rate as well as the raw frame maximum.

## Review results

The reference is package 3 (`cf3adce`) with the motion capture harness only. Baseline failures came from abrupt
look/stance changes in the neck and arm IK, trigger curls, get-up/reacquisition of the carry, and the trailing
lateral sprint leg exhausting reach. Late remote reload phases added another wrist/finger handoff; late support
and stance snapshots exposed a grounded foot-solve change even after landing recovery. These now use bounded
presentation, coherent contact solving, or exceptional contact recovery. Staging teleports are excluded in the
final probe rather than treated as normal movement.

| Measure at 60 Hz | Package 3 | Package 4 |
| --- | ---: | ---: |
| Eight-direction gait matrix, largest bone change | 39.914 degrees | 24.522 degrees |
| Full-pitch grip sweep, largest bone change | 133.292 degrees | 20.220 degrees |
| Correction matrix, largest bone change | — | 20.779 degrees |
| Movement / feet-events / motion, largest bone change | — | 19.994 / 20.009 / 19.987 degrees |
| Steady creep, body-relative lateral muzzle range | 31.123 mm | 6.380 mm |
| Full-pitch sweep, maximum wrist residual | — | 0.262 mm |
| Full-pitch sweep, sampled penetration | — | 0.000 mm |
| Full-pitch sweep, aimed eye/socket distance | — | 0.002 mm |
| Motion sequence, remaining camera rotation after bursts | — | 0.0047 degrees |

Physical ragdoll rotations remain above 25 degrees: the final motion throw reached about 103 degrees on the
right forearm, 91 on the right upper arm and 85 on the right hand. Left arms, legs, clavicles and the head also
had physical samples above 25; the probe lists every offending bone. Those samples come from physical collision,
joint projection and flail. Clamping the displayed physical body would separate it from its collider and change
the blast response, so those are reported exceptions. Digit release and every get-up frame still obey the bound.

Both side and existing third-person player views contain 966 before and 966 after frames: 60 for each gait,
108 for each burst, 150 for reload and 360 for throw/get-up/run. The old 45 mm/5-degree fixture adds 216 burst
frames per view. All 4,296 PNGs were decoded and checked, and both views have eight comparison GIFs and eight
contact sheets. GIF delays cycle 20/20/10 ms because GIF timing has 10 ms precision; each sequence preserves
its 60 Hz frame count and total duration.

The side and player-camera sequences were reviewed across the strides, kicks, magazine return and get-up.
The chosen carry adds a small lateral arc and roll, strongest in sprint, rather than letting the shoulders and
rifle bob independently. Walking remains subtle; aimed creep has much less body twist. Compared with 25 mm/2
degrees, the new recoil visibly compresses the shoulder and lifts the muzzle; compared with the old 45 mm/5
degrees, the aimed head follows the stock. Both hands stay on their solved destinations. Reload returns through
the chest frame, and get-up reaches carry progressively before the run. Random physical flail makes the falls
different between runs; the fall itself is not an A/B animation claim.

The artifacts are retained locally under ignored `build/motion-proof/`: `review-side/`, `review-player/`, original
before/after/old-recoil PNG directories, and remote review frames. They are generated evidence, not release assets.
Pillow is needed only for `review_motion.py`; `make test` has no new external tool requirement.

The final two-bot headless run passed two matches with 120 ms lag, 30 ms jitter and 3% loss. Its remote probe
measured 23.260 and 20.122 degrees per frame, 72 and 70 recoil frames, wrist residuals at most 0.264 mm and no
sampled penetration. A rendered screenshot run passed the same transport/probe flags and all seven remote
state captures; those close-ups retain both grips through creep, sprint, flight and restored landing aim.
GPU readback made its longest raw change 53.771 degrees across a long frame, with maximum rate 1211.10 degrees/s
(20.19 degrees per 60 Hz interval). Both bots agreed with the server's score conservation audits in all matches.

Earlier recording attempts exposed a 30.428-degree remote foot solve, which led to the exceptional recovery
guard, and also failed state coverage when loading/capture stalls skipped the short observation windows.
The passing rendered proof uses state screenshots rather than claiming a smooth 60 Hz multiplayer recording.
Local comparisons still render every fixed 60 Hz frame. A full temporary filesystem interrupted one local
capture attempt; those frames were regenerated and all final PNGs and GIF frames checked.


## Rifle-butt strike (R6)

The resolver supplies melee progress to the rifle rig, locally and in snapshots. Windup turns the rifle across the
body; the active arc brings the stock forward, with torso twist and forward lean. Recovery blends back to the
current carry/ready pose. The normal coherent upper-pose solve retains both wrist targets and projects the rifle
into both arms' reach. Melee has a separate angular response tuner (1200 degrees/s by default) beneath the existing
1500 degrees/s final-pose test bound. It skips the extra carry smoothing so that the stock turn follows contact.

A canceled reload needs one presentation exception: its support hand may still be returning from the magazine or
bolt during windup. Early reloads retrace their clearance path; late reloads finish the hand return. Ammo cancellation
is immediate and transfers no rounds. The rig bounds the visual reload phase together with the candidate upper pose;
`Rifle.PreviewReloadContact` samples the curved clearance path without magazine-drop side effects. Only the accepted
phase advances the rifle visual. Interpolating wrist frames instead cuts through the receiver; snapping reload to
zero exceeds the angular bound. The grip probe requires the support hand back at the foregrip for active contact
and recovery, including ten early/late cancellation fixtures across both clips.

`player_range --scenario=melee` strikes from carry, aim, sprint and reload and captures every 60 Hz frame around each
strike. Use `--view=side` or `--view=player` with `--assert-grip --assert-motion`. The headless deeper sweep is
`--scenario=melee-reloads` (in `make test-full`). Existing grip, movement, motion and get-up regression suites still
apply to the shared solver. Network reproduction and the wire changes are in [multiplayer](multiplayer.md#melee-r6-unreleased-protocol-2).
