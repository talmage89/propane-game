# Locomotion review

This is package 3's historical proof. [Package 4](motion.md) adds rifle motion and replaces its 40-degree
rotation allowance with 25 degrees at 60 Hz, including snapshot and lifecycle handoffs. The final contact
release radius is now 0.3 m. Correction episodes last 3.5 seconds; the proxy samples settled poses from
2.1 through 3.25 seconds to retain contact coverage after bounded pose recovery. Ordinary episodes still last 3 seconds.

Movement package 3 presents the existing controller; movement speeds, facing rules, aim ownership and rifle fit
remain the controller's decisions. Review with `player_range` at 60 rendered frames per second.

## Source inventory

`tools/blender/build_character.py` takes a UAL2 GLB and a UAL1 Unreal FBX as positional arguments. No original
Universal Animation Library files were available on this machine. The script selects the following clips from
Quaternius's CC0 libraries; these are also the complete packaged GLB inventory (Godot strips `_Loop`):

- UAL1: `Idle_Loop`, `Walk_Loop`, `Jog_Fwd_Loop`, `Sprint_Loop`, `Jump_Start`, `Jump_Loop`, `Jump_Land`,
  `Pistol_Idle_Loop`, `Pistol_Aim_Neutral`, `Pistol_Aim_Up`, `Pistol_Aim_Down`, `Pistol_Shoot`, `Hit_Chest`,
  `Hit_Head`, `Crouch_Idle_Loop`, `Crouch_Fwd_Loop`, `Roll`, `Death01`.
- UAL2: `LayToIdle`, `Hit_Knockback`, `Idle_FoldArms_Loop`, `NinjaJump_Land`.

The packaged asset has no directional, rifle locomotion or turn clips. `Jump_Start` includes anticipation, push,
then tucked and extended air poses; `Jump_Loop` is a subtle held air pose; `Jump_Land` includes compression and
recovery. The unavailable libraries' omitted clips cannot be enumerated or assessed. No download or asset rebuild
was performed; the directional and contact layer is procedural.

## Reproduce the measurements

Run `make build` before every scene. The baseline fixture uses the measurement harness from `1fae6a2` on unchanged
`b18cb08` player code.
Its capture schedule, terrain stages, jump timing probe and review cameras were brought up to the final
harness without adding any presentation code. Use the same scene commands for both; append `--assert-feet`
on the changed branch to enforce acceptance bounds:

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --capture-dir=/tmp/wpmove3-metrics
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --feet-corrections --assert-feet --capture-dir=/tmp/wpmove3-corrections
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet-events --assert-feet --assert-grip --capture-dir=/tmp/wpmove3-events-metrics
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --view=side --capture-dir=/tmp/wpmove3-side
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --view=player --capture-dir=/tmp/wpmove3-player
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/player_range.tscn -- --scenario=feet-events --view=side --capture-dir=/tmp/wpmove3-events
```

`feet` supplies controlled snapshots to the same remote character used in a match: three gaits, eight directions
in 45-degree increments (0 = forward, 90 = right, 180 = back). Each three-second episode accelerates, runs, then
stops. Fixing body yaw isolates direction from the controller's existing facing limits. This deliberately covers
snapshot travel directions that local sprint intent would not select. It does not alter local sprint rules.

`feet-events` uses the real local controller: creep starts/stops/backpedal/reversal, an aimed look reversal with
standing turn steps, default/sprint/creep transitions, moving and stationary jumps, held air aim, and stepping down
over a slab, curb
and slope, independent support at a slab/curb boundary, and backpedaling uphill. The existing controller stops
at the uphill curb: this checks slope support and settling, not curb ascent. The slab is created at the terrain
stage so it does not interfere with earlier movement. Stage teleports are excluded from angular measurements.

The final skeleton modifier writes `feet.csv` and measures the maximum local per-bone angular change across every
frame (spawn and scripted teleport excluded). For each settled gait/direction it also measures consecutive ankle
positions within 3 cm of that episode's minimum height. That near-floor classification is an independent contact
proxy, not a collision proof: its tail includes initial lift-off and final swing frames. Compare its median along
with the actual saved-contact residuals.

Baseline `b18cb08`, with the new measurement harness, near-floor median drift in m/s:

| Body-relative angle | Default | Sprint | Creep |
| --- | ---: | ---: | ---: |
| 0 | 0.671 | 8.771 | 0.093 |
| 45 | 4.401 | 9.002 | 1.214 |
| 90 | 8.114 | 9.532 | 2.212 |
| 135 | 4.401 | 8.904 | 1.209 |
| 180 | 0.622 | 9.021 | 0.090 |
| 225 | 4.409 | 9.166 | 1.215 |
| 270 | 8.103 | 9.531 | 2.188 |
| 315 | 4.405 | 9.097 | 1.205 |

Baseline matrix maximum angular change: 37.875 degrees (`calf_r`). These controlled yaw/direction measurements
are intentionally stricter than the earlier report's controller-facing strafe measurement.

## Presentation layer

`LocomotionPose` retains one distance-driven stride phase across gait changes, idle transitions and jump flight.
`CharacterAnimator` seeks the walk, jog and sprint clips to that normalized phase and blends their weights; the
pistol torso mask and rifle solver remain intact. Physics observations latch takeoff and impact before render can
skip a one-tick support state. Accepted `Jumping` also latches a rebound when no grounded tick was observed.
Rising, apex and falling sample `Jump_Start` by vertical velocity. Landing blends
compression into a continuing gait rather than owning movement. Walk/jog and sprint weights follow actual
speed through acceleration and stopping, with the resolved creep gait retaining its walk presentation.

`FootPlacement` runs after ragdoll simulation and before the rifle rig. The feet swing toward travel-relative
placements, retain world positions and orientations during contact, and lift before exhausting reach. Starts,
reversals and changed stride lengths can request a corrective step. The entire swing and touchdown keep the
ankles separated; a moving swing start cannot pass the stance ankle. Standing turns and stop settles move one
foot at a time. Downward static-ground probes align soles to slopes; bounded pelvis correction keeps the leg
solves reachable. All new feel values are tuners in the Locomotion group. Teleports, air and ragdoll clear contacts.
Remote phase and destinations
use displayed displacement, so interpolation stalls do not keep running the feet against a stationary body.
Corrections beyond `FootResetDistance` release old contacts and blend the cached lower pose into a new solve.
When interpolated height leads the discrete support flag beyond pelvis reach, contacts release and the lower
pose holds until support catches up. Authored air translation is removed; physics owns the character root.
Temporary native ground queries and
result dictionaries are disposed immediately after each sample.

This adds shared tuners and therefore requires a minor release. Messages and `Protocol.Version` are unchanged.

## Acceptance proof

The changed matrix has 0.0000 m/s median near-floor drift in all 24 cases. The worst saved-contact residual is
0.000004 m and consecutive contact drift is 0.000229 m/s. The matrix's maximum angular change is 39.914 degrees;
the local event sequence falls from 80.499 to 35.464 degrees. The extra terrain-stage teleports are excluded at the
reset frame; their following torso settle remains measured. Slab/curb ankle heights differ by exactly 0.120000 m,
versus 0.000086 m on the baseline. Combined terrain smoothing and reach correction stay within `FootPelvisLimit`.
Standing half-turns produce eight observed lift samples per foot, versus zero on the baseline. The event test
requires both feet to lift above half the tuned walk lift; a torso-only turn cannot pass.

Takeoff's first air blend is on the accepted physics tick for both jumps. Landing is two 120 Hz physics ticks
(one 60 Hz render frame) after actual post-move contact. On the baseline, the moving landing never appears;
stationary landing takes the same two ticks. Assertions cover phase retention, live creep tuning, missed one-tick
remote takeoff, repeated jumping snapshots, rebounds that skip observed grounding, all six support poses,
impact scaling, immediate movement and lifecycle reset.

`make test` includes the matrix and event assertions, with grip checks throughout the terrain and turn sequence.
Contact residual must be at most 3 mm, saved-contact drift
at most 0.02 m/s, near-floor median at most 0.05 m/s, and angular rate at most 2400 degrees/s (40 degrees at 60 Hz).
The worst sprint lateral sample is close to this limit; this is an acceptance bound on the final pose, not a claim
that fast procedural steps have no angular acceleration.
The probe measures the final ankle after every modifier, rather than just trusting the saved target. Per-case
coverage requirements prevent a solver from passing by declaring no contacts. `feet.csv` retains frame-level
values, support poses, reach error and pelvis correction; `jump-timing.csv` compares physics and pose ticks.

Ground support currently covers static world/floor geometry. Moving tanks/platforms are not foot supports.
The ankle/sole model aligns to a sampled normal; it does not model separate heel/toe articulation. The sprint's
flight interval is intentional; no-contact samples are not presented as planted-foot measurements. Cross-platform
exports and authoring replacement clips were not part of the Linux verification.

The correction matrix (`--feet-corrections`) adds 0.8 m planar corrections in both directions and a 0.4 m height
change while the grounded snapshot flag remains set. It measures contact residuals throughout, while the
independent near-floor proxy samples only the settled interval after correction (2.1–2.7 seconds). All 24 cases
pass; maximum angular change is 35.605 degrees. Minimum ankle separation is 119.999 mm normally and once
corrections settle, and 95.198 mm during pose recovery. Separation remains positive through recovery. Tests
require the full tuned separation when settled and forbid crossed ankles during recovery.

The initial network harness relocation to its void range spans multiple interpolated frames. Explicit local
teleports and relocation samples over 10 m are excluded from angular statistics. The network probe attaches
one second into the script, after the controlled relocation replaces position, yaw and aim target. The early
relocation can otherwise produce a torso jump unrelated to the measured movement sequence. Ordinary 0.8 m
corrections remain measured throughout the correction matrix. Variable-frame-rate network runs report angular
rate as well as raw angular change; synchronous PNG readback can stall a rendered frame, while fixed-fps scene
measurements remain at 60 Hz.

## Rendered and network review

Capture the matrix with both `--view=side` and `--view=player`, and the event sequence with both views as well.
The player view uses `CameraRig`, including the game's projection and shoulder offset. Rendered time advances at
60 frames/s, with 120 Hz physics. The matrix saves 25 frames per case at 15 samples/s (600 images/view), while
the local event sequence saves 300 frames/view at 12 samples/s. This spans multiple creep, jog and sprint strides.

`tools/dev/review_locomotion.py` (Pillow) pairs captures into sheets and GIFs. Run it once per view and scenario:

```sh
python3 -B tools/dev/review_locomotion.py /tmp/wpmove3-before-final-side /tmp/wpmove3-final-side /tmp/wpmove3-review --view side
python3 -B tools/dev/review_locomotion.py /tmp/wpmove3-before-player-camera /tmp/wpmove3-final-player-camera /tmp/wpmove3-review --view player
python3 -B tools/dev/review_locomotion.py /tmp/wpmove3-before-events-final-side /tmp/wpmove3-final-events-side /tmp/wpmove3-review --scenario feet-events --view side
python3 -B tools/dev/review_locomotion.py /tmp/wpmove3-before-events-player /tmp/wpmove3-final-events-player /tmp/wpmove3-review --scenario feet-events --view player
```

Review `Creep_090` and `Creep_180`, frames 00/03/06/09, for separated lateral steps and backward placement;
`event_024`/`026` for stop settling; `091`/`094` for right/left standing turn lifts; `108`–`134` for gait transitions;
`140`/`143`/`146`/`150`/`154` for takeoff, rise, apex, fall and moving landing with aim held; `175`–`189`
for the stationary jump; `195`–`230` for slab/curb/slope descent; and `244` for independent slab/curb support.
Matrix sheets use gait-specific frame spacing to cover a stride (creep 00/03/06/09, default 00/01/03/05,
sprint 00/02/04/06). The GIFs retain all 25 frames and the world grid rather than tracking individual feet.

The remote check uses two bots and two matches on the same connections, with latency, jitter and loss:

```sh
tools/dev/net_bots.sh -o /tmp/wpmove3-net-live -n 2 -w 1 -l 14 -- --scenario=movement --matches=2 --assert-feet --assert-grip --net-lag=120 --net-jitter=30 --net-loss=3 --record=/tmp/wpmove3-net-live.mp4 --record-fps=60
python3 -B tools/dev/check_scores.py /tmp/wpmove3-net-live/server.log /tmp/wpmove3-net-live/bot1.log /tmp/wpmove3-net-live/bot2.log
ffprobe -v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate,nb_frames,duration -of json /tmp/wpmove3-net-live.mp4
```

Both matches pass feet, grip and remote gait/support assertions. Maximum saved-contact residual is 1 micrometer,
drift is 0.000039 m/s, and maximum per-frame pose changes are 29.666 and 29.233 degrees. Rifle wrist error is at
most 15 micrometers. Both bots agree with both server score audits. The asynchronous recording contains
61.93 seconds at 60 fps; 3524 of 3717 submitted frames are distinct rendered frames (the rest repeat when rendering
misses a recording deadline). Synchronous PNG captures were also reviewed, but their readback stalls make raw
per-frame angular changes incomparable to a fixed 60 Hz matrix.

The final headless network run repeats the same assertions without recording:

```sh
tools/dev/net_bots.sh -o /tmp/wpmove3-net-headless-final -n 2 -w 0 -l 14 -- --scenario=movement --matches=2 --assert-feet --assert-grip --net-lag=120 --net-jitter=30 --net-loss=3
python3 -B tools/dev/check_scores.py /tmp/wpmove3-net-headless-final/server.log /tmp/wpmove3-net-headless-final/bot1.log /tmp/wpmove3-net-headless-final/bot2.log
```

Both matches pass, with maximum pose changes 28.812 and 28.854 degrees, and both server audits agree. This run
includes the final accepted-rebound logic and query disposal. The rendered recording preceded those two changes;
the final side/player event reruns cover ordinary jumps and query shutdown on the complete code.

`make setup`, `make build`, and `make test` also passed independently on the baseline fixture and on the first
implementation commit. Final `make check` covers formatting, a warnings-as-errors build (zero warnings/errors),
84 unit tests, 746 movement assertions, 112 menu assertions, the grip/ragdoll suites, all three foot scenarios,
100 generator seeds, 9 platform checks and matching
world hashes in separate processes (108 fences). Earlier runs exposed crossing swings, unreachable contacts
with interpolated support, and abrupt pose recovery; these led to the fixes above. A later network repeat caught
the controlled relocation's torso discontinuity, so its movement probe now starts after staging. The rendered
turn review revealed that the first look sweep stayed below the foot threshold; half-turns and explicit two-foot
lift assertions now exercise the actual turn step. One rendered shutdown reported outstanding native ray-query
bindings; the foot layer now disposes its temporary queries/results explicitly. Both final rendered event reruns
exit successfully with no error, fatal or leaked-binding messages. A temporary-space
failure during verification was cleared by removing obsolete task captures and repeating the full check.

All 3600 final before/after PNGs were checked with Pillow's `Image.verify()`; all 14 generated GIFs have the
expected frame count (25 matrix, 29 turn, 24 jump and 36 terrain frames per view). All 28 side/player comparison
sheets and two recording contact sheets were inspected. Side frames show sole contact and independent slab
support; the wider player-camera projection shows direction changes and jump silhouette. Some fast swings leave
the player-camera viewport; the side view supplies their sole review. Accepted rebounds without an observed
grounded tick are covered by pure-state assertions, rather than a rendered controller rebound fixture.
