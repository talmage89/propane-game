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
