# Recoil

A shot follows the logical aim, never the rifle's pose. Recoil moves that aim; the weapon and camera springs only add
a short presentation kick beside it. Damage, spread, bloom, fire rate, ammunition and the tank and blast rules do not
depend on any of it. The rifle and hands that a player sees are described in [rifle stance](motion.md); the dated
R19 design and its measurements are in the [recoil report](evidence/recoil.md).

## Climb

Each shot kicks the logical aim directly (`LogicalAim.Kick`). Pitch rises by `RecoilKickDegrees` (times
`AimRecoilScale` while aiming) and yaw moves by a uniform random amount up to `RecoilYawDegrees` in either direction,
on the same scale. Mouse, stick and bot look displacements change that same orientation. The pitch limits apply
before the settle budget is added, so a blocked kick cannot wind up a later downward pull, and setting the spawn
orientation resets the budget.

Only `RecoilReturnShare` of a pitch kick settles on its own, at `RecoilSettleSpeed` (exponential, per second). The
rest, and all sideways displacement, stay until look input pulls them back, so a long burst needs correction.

| Tuner | C# default | Baked game | Meaning |
| --- | ---: | ---: | --- |
| `RecoilKickDegrees` | 0.28 | 0.8 | Immediate logical pitch per hip shot |
| `RecoilReturnShare` | 0.125 | 0.125 | Share allowed to settle automatically |
| `RecoilSettleSpeed` | 1 | 1 | Exponential settle rate, per second |
| `RecoilYawDegrees` | 0.175 | 0.5 | Uniform persistent sideways range, either direction |
| `AimRecoilScale` | 0.6 | 0.6 | Share of the kick that remains while aiming |
| `CameraRecoilKickDegrees` | 0.35 | 0.35 | Separate camera spring impulse |
| `BotRecoilControlSpeed` | 2.8 | 2.8 | Local brain correction rate, per second |

With the baked values a hip shot keeps `0.8 * (1 - 0.125) = 0.7` degrees of climb. The baked column is `tuning.tres`.
The camera spring uses the rifle spring's frequency (`RifleRecoilFrequency`) and adds only a local camera rotation;
the rifle's own `RifleRecoil*` spring moves the shown stock and muzzle. Neither reaches the logical aim.
`BotRecoilControlSpeed` is a local brain dial: bots pull against the climb with `BotAim`, and it never enters the
match snapshot.

## Logical muzzle

The primary ray is the logical aim's chest-plane projection with a random cone sample, and its hit query. Something
between the muzzle and that endpoint still blocks the bullet, but the muzzle is where the logical aim holds the rifle
(`PlayerCharacter.LogicalMuzzle`): a height above the character's feet, an offset to the right of the aim and a
distance along the aim, separately for the hip and for aiming. These are shared gameplay tuners:

| Tuner | Default |
| --- | ---: |
| `ShotMuzzleHipHeight` | 1.26 m |
| `ShotMuzzleHipSide` | 0.13 m |
| `ShotMuzzleHipForward` | 0.65 m |
| `ShotMuzzleAimHeight` | 1.59 m |
| `ShotMuzzleAimSide` | 0.07 m |
| `ShotMuzzleAimForward` | 0.76 m |

The tracer, flash and shot report start at the visible muzzle, so the picture follows the rifle while the hit does
not: no pose can change a hit, and the rifle is never re-aimed or re-gripped for a shot.

## Checks

- `RecoilTests` (unit): a magazine's climb is the same at any render rate, mouse and stick correction is immediate
  with no clamp windup, the camera kick never changes the logical aim, and bursts settle without drift.
- `player_range --scenario=recoil --stance=aim|hip|creep` (and `--stance=hip --burst-start=0`) fires real full-auto
  magazines with no look input and compares the climb with the tuners; `make test-full` adds `--stance=air`.
  `--scenario=bot-recoil --magazines=2` runs the production bot aim against a capsule (ten magazines in `make test-full`).
- `menu_test --scenario=input` holds a real mouse burst and checks the retained climb and the mouse pull-down.
- `rifle_reach_tests` delivers remote shot echoes, reloads, strikes and the get-up in every order against snapshots and
  keeps both wrists on their grips. Shots never depend on that pose.
