# Rifle contact

[Package 4](motion.md) extends this fit with stride sway, shoulder-absorbed spring recoil and bounded
pose handoffs. The calibration results below are package 2's historical measurements.

The rifle rig places a rigid rifle before solving the arms. Its wrist offsets and orientations are fitted to
the mannequin palms, the traced pistol grip and the vertical foregrip. A 25 mm reach margin leaves both elbows
bent: translation projects the two wrist targets into the intersection of the arms' reachable spheres. IK uses
the actual targets and reports unreachable targets or residuals above 3 mm instead of silently shortening the
reach.

The upper-body animation filter includes the three spine bones so run and sprint cannot swing the chest away
from the contacts. The rig distributes one world-space torso rotation across those bones, including camera
pitch. Aim, ready and sprint have a separate visual blend in the rig: logical aim can clear immediately on
release, jump, sprint or reload without snapping the rifle or shoulders. Lower-body gait selection, speed
and clip timing are unchanged.

Carry and sprint keep a dipped, inboard muzzle; ready points from the hip. Aim seats the butt at the right
shoulder pocket, raises the clavicle and fits the neck/head to the authored `Socket_Eye`. The fit rotates
bones without stretching them. The get-up handoff keeps the firing wrist attached and projects the gradually
returning support wrist into reach after blending the rifle. The right-eye landmark is calibrated to the
mannequin head, rather than its origin. The shoulder and head absorb recoil while both wrists follow the
moving rifle and the aimed eye fit stays on its socket.

Each finger has three absolute curls relative to its authored rest frame. The firing index rests along the
receiver and curls toward the trigger during recoil. Reload uses separate magazine curls and follows the
magazine's orientation. The hand opens away from the magazine and receiver before moving back; its thumb stays
wrapped until clear, then closes around the foregrip. Empty reload also approaches the bolt catch from
outside. The source rifle mesh, sockets, clips and their event times are unchanged. The ragdoll hand follower
reads the same live firing-wrist calibration as the rig.

Placement, recoil, wrist calibration, eye landmark, finger curls and reload clearance are `Tuning` values in
**Rifle Contact** and **Rifle Motion**. These values join the existing shared tuning snapshot: the next release must be minor. No
messages changed, and `Protocol.Version` remains 2.

## Regression and rendered review

After `make build`, run:

```sh
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- \
  --scenario=grip --grip-sweep --assert-grip --capture-dir=/tmp/grip
```

`make test` includes this assertion run and a ragdoll/get-up contact run. The basic 15-second scenario covers
carry, ready, aim, empty and tactical reload, run, sprint, aimed strafe and both camera pitch limits (-75 and
+45 degrees). `--grip-sweep` extends it to 39 seconds, with pitch changes in each hold, recoil bursts at both
limits, a reload while sprinting, and reloads at both pitch limits. The assertions require both reloads, every
gait and hold, recoil and both pitch limits to have occurred. The extended run additionally requires both
pitch limits in every hold and a 25 mm reach margin (0.5 mm tolerance).

`GripProbe` runs after the other skeleton modifiers. It writes `grip.csv` with time, gait, camera pitch,
stance blends, recoil, both final wrist position/rotation residuals, maximum sampled hand penetration and
aimed eye socket distance. Thresholds are 3 mm per wrist, 0.1 degree per wrist, 0.5 mm sampled penetration and
35 mm eye distance including recoil (3 mm with settled aim). Per-frame stance blend changes are bounded by
`RifleStanceBlendSpeed`. Nonfinite values, missing samples or missing scenario coverage also fail with exit 1.
Wrist residuals are measured during the get-up handoff too;
penetration and eye checks resume when the hold is fully applied.

The penetration check skins 1,487 actual palm/finger surface vertices (every fourth qualifying mesh vertex)
and measures their depth inside conservative solid cores of the receiver, handguard, foregrip, traced pistol
grip and moving magazine. The cores avoid hollow rails and trigger space; the pistol profile is inset 4 mm.
This is not exhaustive triangle collision, and passing it does not prove zero mesh interpenetration. The
mannequin has large stylized hands; rendered review remains necessary when changing the curls or rifle
geometry.

Repeat with rendering, `--grip-metrics` and each `--view=rifleleft`, `rifleright`, `side` and `player`. Review
`aim`, `fire`, `run`, `sprint`, both `*_swap` frames, `tactical_seat`, `bolt`, `pitch_down`, `pitch_up` and
the extended `*_down/up` frames. The player view is the game's existing third-person camera.

For the same rig on a remote player, run:

```sh
tools/dev/net_bots.sh -n 2 -l 45 -w 1 -- --scenario=movement --assert-grip \
  --view=rifleright --capture-dir=/tmp/grip-remote
```

The host probes its remote character during creep, sprint, jumping and landing through real snapshots. Its
`remote_*.png` frames and `grip.csv` show that rendered copy; the remote probe does not claim reload coverage.

## Calibration results (2026-10-10)

The complete sweep was run against the pre-placement rig from `37e20f6` with the same final diagnostic eye
landmark and surface probe, and then against the fitted rig. Each rendered view measured 2,340 modifier
frames. The old rig failed the contact assertions; the fitted rig passed. Maxima across the four rendered
views:

| Measure | Before | After |
| --- | ---: | ---: |
| Firing wrist position residual | 0.008 mm | 0.008 mm |
| Support wrist position residual | 413.098 mm | 0.008 mm |
| Wrist rotation residual (either hand) | 0.0001 degrees | 0.0001 degrees |
| Sampled hand penetration | 14.000 mm | 0.000 mm |
| Eye to socket, including recoil | 305.381 mm | 30.418 mm |
| Eye to socket, settled aim | 305.381 mm | 0.004 mm |

Rotation residual measures whether the solver reaches its chosen wrist orientation, not whether that
orientation looks correct. The wrist frames and curls were therefore also reviewed in both rifle views. The
before/after `aim` and `fire` frames show the raised pocket and cheek line; `run` and `sprint` show the
support hand on the foregrip; `tactical_swap`, `tactical_seat` and `bolt` show magazine contact and outside
receiver clearance. `pitch_down/up` and `aim_down/up_fire` show the pitch fit with both contacts retained. All
29 captures in each of the four views were reviewed as matching before/after pairs.

The two-bot movement run passed remote contact and movement assertions: 531 sampled frames, eye offset at most
0.015 mm, zero sampled penetration, wrist residuals at most 0.004 mm and rotation residuals below 0.0001 degree.
Ragdoll/get-up retained the firing-hand attachment and the returning support wrist reached its blended target
within 0.001 mm. These checks cover the defaults and scripted paths, not every combination of live tuning
overrides.
