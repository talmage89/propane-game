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
The old recoil comparison also overrides `RifleRecoilBack=0.045` and `RifleRecoilLiftDegrees=5` on that fixture.
Do not use those overrides on the new spring implementation as a substitute for the old recoil algorithm.

```sh
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
