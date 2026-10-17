# Movement verification

**Status:** Dated record (2026-10-10). Still accurate: `CreepSpeed` is 1.6 m/s in code and in the baked tuning. The
rule is stated in [locomotion.md](../locomotion.md).

`CreepSpeed` defaults to **1.6 m/s** in code and the baked tuning. The walk clip's native speed, 0.98 m/s, is a
calibration value, not a gameplay constraint: playback follows actual planar speed divided by 0.98. The default
gait remains 6 m/s and sprint remains 9.5 m/s.

The October 2026 comparison rendered two settled strides from the side and player camera at each speed, using
`player_range.tscn --scenario=creep` with `--fixed-fps 60`. Each run captures 25 frames (12 intervals per stride)
and writes `stride.csv` with the final world-space positions of both feet and toes, after skeleton modifiers.

| Creep speed (m/s) | Walk playback | Stride period (s) | Near-floor drift median / p75 (m/s) | Sample pairs |
| --- | --- | --- | --- | --- |
| 0.98 | 1.000 | 1.367 | 0.011 / 0.105 | 290 |
| 1.6 | 1.633 | 0.837 | 0.010 / 0.169 | 173 |
| 2.2 | 2.245 | 0.609 | 0.017 / 0.222 | 124 |
| 2.8 | 2.857 | 0.478 | 0.013 / 0.225 | 95 |

Both views produced the same measurements. For each bone, a near-floor pair has both endpoints within 3 cm of
that bone's lowest sampled world height. Drift is horizontal world displacement divided by elapsed time.
This follows the discovery's near-ground foot/toe method; it is a proxy for planting, not a collision contact test.
These numbers cover forward travel on flat ground, not strafing, slopes or gait transitions.

At 1.6 m/s the upright, shouldered walk stays readable while a meter at settled speed takes 0.625 s instead of
1.02 s at 0.98. That gives aimed tank-bank movement more room for dodging and adjusting around a pile. The faster
settings have little median sliding too, but their 3.3 and 4.2 footfalls per second make the walk look hurried.
The 1.6 default favors movement without requiring that cadence; human duel testing should decide any later
adjustment. `CreepSpeed` remains the owner's live dial.

To repeat the comparison, run this command for each speed and for `--view=side` and `--view=player`:

```sh
godot-mono --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- \
  --scenario=creep --view=side --tune.CreepSpeed=1.6 --capture-dir=/tmp/creep-1.6-side
```

The headless `movement_tests.tscn` suite checks resolver transitions through pure intent and mapped gamepad events,
actual input dispatch and pause/focus notifications, snapshot interpolation, all four facade speeds and walk
playback ratios, and ragdoll/get-up after released aim or a sprinting reload. Failures exit with status 1.
