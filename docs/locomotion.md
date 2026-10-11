# Locomotion review

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

Run `make build` before every scene. Use the same commands on the baseline and the changed branch:

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --capture-dir=/tmp/wpmove3-metrics
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --view=side --capture-dir=/tmp/wpmove3-side
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --view=player --capture-dir=/tmp/wpmove3-player
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/player_range.tscn -- --scenario=feet-events --view=side --capture-dir=/tmp/wpmove3-events
```

`feet` supplies controlled snapshots to the same remote character used in a match: three gaits, eight directions
in 45-degree increments (0 = forward, 90 = right, 180 = back). Each three-second episode accelerates, runs, then
stops. Fixing body yaw isolates direction from the controller's existing facing limits. This deliberately covers
snapshot travel directions that local sprint intent would not select. It does not alter local sprint rules.

`feet-events` uses the real local controller: creep starts/stops/backpedal/reversal, aimed turns both ways,
default/sprint/creep transitions, moving and stationary jumps, held air aim, and stepping down over a slab, curb
and slope. The slab is created at the terrain stage so it does not interfere with earlier movement. Teleporting to
the terrain stage is excluded from the angular measurement.

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
