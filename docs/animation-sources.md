# Animation sources

The character plays polished public clips as authored. A light rifle stance (`RifleRig`) layers the arms, the aim
twist and pitch, and the hands over them; nothing generates or overrides steps.

## Quaternius Universal Animation Library (CC0)

The mannequin and its 22 packaged clips come from the free **Standard** editions of
[Quaternius UAL1](https://quaternius.com/packs/universalanimationlibrary.html) and
[UAL2](https://quaternius.com/packs/universalanimationlibrary2.html), dedicated to the public domain under
[CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/). `assets/character/mannequin.glb` is unchanged; the source
archives are not needed to build or play.

| Archive | SHA-256 |
| --- | --- |
| UAL1 Standard, 15,904,933 bytes | `cc73fc4e495b82958207316596317a3f40b9fa38065bde1027937452da537724` |
| UAL2 Standard, 18,735,003 bytes | `4008ea208a604773a2b2177d965f0f5d3195498b5bf838c3f5785d68e95f2a68` |

Used as authored: `Idle`, the forward `Walk`, `Jog_Fwd` and `Sprint` (the jog also plays in reverse for running
backward), `Jump_Start` and `Jump_Land`, `Hit_Chest`, `LayToIdle` (get-up) and `Pistol_Idle` (the arm and finger
base under the rifle stance). Neither free library has backward, sideways or rifle locomotion.

| Packaged cycle | Seconds | Left touchdown (fraction) | Ground per cycle | Played at |
| --- | ---: | ---: | ---: | --- |
| `Walk` | 1.367 | 0.962 | 1.33 m | walk and aimed creep (1.65× at 1.6 m/s) |
| `Jog_Fwd` | 0.967 | 0.046 | 5.3–5.5 m | 6 m/s run (about 1.1×) |
| `Sprint` | 0.700 | 0.992 | 6.3 m | 9.5 m/s sprint (about 1.05×) |

Touchdowns and ground per cycle are measured from the clips' own feet. Every locomotion clip is sampled at one
shared stride phase whose zero is the left foot's touchdown, so blends never restart a foot.

## The 100STYLE Dataset - Ian Mason (CC BY 4.0)

Backpedal and sidesteps come from **The 100STYLE Dataset - Ian Mason**:
[author's page](https://www.ianxmason.com/100style/), [Zenodo record 8127870](https://zenodo.org/records/8127870),
licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) (credit in `CREDITS.md`). Only the Neutral style
is used. The source is `100STYLE.zip` (1,468,515,354 bytes); the BVHs are read from it unaltered:

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `Neutral/Neutral_ID.bvh` (idle, posture calibration) | 1,465,227 | `71aead2e3246bdb47a5b839cb3e60e0574e9a1a046c071cc147e2922a35f259f` |
| `Neutral/Neutral_BW.bvh` (backward walk) | 6,072,424 | `cfa225ff3a29a38d11ecd1b72fa0d917ca6c3dc3b24b3d09a4cb5c28ee9dc5b9` |
| `Neutral/Neutral_SW.bvh` (sideways walk) | 4,373,576 | `07225ded6df11c4eb97a7b42e6121a9e985ae7e8d0f56f9ca971429f9c507ad9` |

`tools/animation/retarget_100style.py` (Python 3 and numpy) writes `assets/character/directional.tres`, a Godot
`AnimationLibrary` the character adds beside the packaged clips:

```sh
python3 -B tools/animation/retarget_100style.py <directory holding the Neutral_*.bvh files>
```

| Clip | Source cycles | Captured ground per cycle | Captured speed |
| --- | ---: | ---: | ---: |
| `Walk_Back` | 6 | 0.79 m | 0.64 m/s |
| `Walk_Left` | 3 | 0.60 m | 0.46 m/s |
| `Walk_Right` | 6 | 0.63 m | 0.54 m/s |

The retarget keeps the capture's motion:

- Both skeletons are Y-up T-poses facing +Z, so global bone rotations transfer between rest frames directly. The
  pelvis, spine (`Chest`, `Chest2`, `Chest4`), neck, head and legs are written; the rifle stance owns the arms.
- The trunk and head are calibrated to the performer's standing posture in the idle capture, so their normal head
  carriage (about 25° forward of the BVH's straight bones) maps onto the mannequin's own rest posture instead of
  bowing its head.
- Straight cycles from one left touchdown to the next are found in the capture (travel within 16° of the wanted
  direction, heading drift under 14°), turned to travel exactly back, left or right, and averaged. Each cycle's
  small start/end difference is spread over it so the loop closes. Both sidesteps are captured; nothing is
  mirrored.
- Travel is removed (physics moves the character) but the pelvis keeps its captured bob and sway, scaled by leg
  length (0.953).

Nothing is exaggerated or re-solved: no stride scaling, height scaling or foot IK. The captures are slower than
the 1.6 m/s aimed creep, so `BackStrideLength` (1.0 m) and `SideStrideLength` (0.85 m) are set a little longer than
the captured steps: the cadence stays natural and the feet slide slightly at full creep speed. The 100STYLE runs are
slow jogs (about 1 m/s); they are not used, because no playback rate turns them into a natural 6 m/s run.

## Authored in code

The rifle stances (carry, hip ready, shouldered, sprint port, reload) are poses of the rifle in a chest frame, set by
`Tuning` tuners; both hands reach the grips by two-bone IK. The rifle-butt strike is a keyframe track in
`src/Player/MeleeStrike.cs`, keyed to the gameplay's windup, active and recovery phases.
