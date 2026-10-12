# Animation source choices

Downloaded the free **Standard** editions of [Quaternius UAL1](https://quaternius.com/packs/universalanimationlibrary.html)
and [UAL2](https://quaternius.com/packs/universalanimationlibrary2.html), through their officially linked itch.io pages.
Both archives contain `License.txt` dedicating the files to [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/).
Commercial use, modification and redistribution in public source are permitted. No payment or account was used.

| Archive | SHA-256 |
| --- | --- |
| UAL1 Standard, 15,904,933 bytes | `cc73fc4e495b82958207316596317a3f40b9fa38065bde1027937452da537724` |
| UAL2 Standard, 18,735,003 bytes | `4008ea208a604773a2b2177d965f0f5d3195498b5bf838c3f5785d68e95f2a68` |

Each Standard archive has 43 distinct clips plus root-motion variants. Neither supplies backward or sideways
locomotion, or a rifle stance. `animation_inventory.py` records their actual clip names, timing and root travel;
marketing for the larger paid libraries is not an inventory of these downloads. The normal UAL1 walk is the
forward/creep reference. UAL2's box-carry walk leans backward and nods more strongly, so it is not used as a rifle gait.

The Standard UAL2 clips have different durations from the shipped mannequin. Rebuilding retains `LayToIdle` at
1.2 s, `Hit_Knockback` at 2/3 s, `Idle_FoldArms` at 2 s and `NinjaJump_Land` at 1 s. The builder imports the recorded
Unreal/Godot GLBs, checks identical bone rests and keeps the original mannequin mesh and skin weights.

## Directional motion in the mannequin rebuild

The directional redo adds **The 100STYLE Dataset - Ian Mason**, [author downloads](https://www.ianxmason.com/100style/)
and [Zenodo record 8127870](https://zenodo.org/records/8127870), under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). This changes the previous CC0-only animation source policy.
The neutral style supplies real backward and sidestep weight transfer. The shared source archive records each
unaltered BVH's URL, byte count and SHA-256 in `100style/SOURCES.md` and the root `SOURCES.md`.

`build_character.py` reads that archive via `--sources`, verifies the six exact rebuild input checksums, and imports
the two recorded Standard GLBs rather than the old unrecorded FBX paths. It checks identical mannequin rest bones
between libraries and retains the shipped one-shot durations listed above. Run with Blender 5.2:

```sh
TMPDIR=$PWD/build/character-tmp blender -b --factory-startup --python-exit-code 1 --python tools/blender/build_character.py -- --sources ../assets-src
make setup
```

Create `build/character-tmp` first. The default source path is the shared `assets-src` beside this checkout. The script
exports only the selected libraries and derivatives; source archives and Blender are not needed to play or test.

| Output | Neutral 60 Hz BVH frames (inclusive) | Derivative |
| --- | --- | --- |
| `Walk_Back` | `Neutral_BW`, 1121–1193 | Strip captured path heading/root travel; retarget hip/spine and leg rest frames. |
| `Walk_Left`, `Walk_Right` | `Neutral_SW`, 603–672 | Retarget sideways gait; mirror limbs and quaternion handedness for the opposite side. |
| `Jog_Back` | `Neutral_BR`, 546–589 | Retarget backward running; retime support and flight for the unchanged 6 m/s rule. |
| `Jog_Left`, `Jog_Right` | `Neutral_SR`, 1330–1366 | The captured run goes right; exchange sides to produce the left derivative, then mirror. |
| `Turn_Left`, `Turn_Right` | Derived from the retargeted neutral sidestep | Bake opposite support arcs for a quarter turn; retain authored lift, flexion and weight shift. |

Mirrored cycles are aligned by the **target mannequin's** left contact, then right contact is placed at half a cycle.
The authored clips own stance, swing, pelvis and spine motion. Offline ankle correction uses anatomical leg lengths
and knee clearance; runtime foot IK only applies bounded terrain/contact corrections. Source arm swing is excluded
from the rifle layer. Two-second normalized derivative cycles retain enough samples through Godot's default importer;
playback tempo comes from travel and the shared stride tuners, not the baked authoring duration.

Forward Walk keeps the library pose. Forward Jog and Sprint retain their vertical arcs, torso motion and contacts,
with a baked constant-speed support path to remove skating. Movement, jump, melee and hit-volume rules are unchanged.

Four walk and four jog diagonals are baked from neighboring directional ankle and knee paths. Each sample is
reconstructed with rigid leg lengths rather than quaternion interpolation between feet, which would lift a planted
ankle. Cycles share left contact at zero and right contact at half a cycle. Source pelvis/spine motion remains;
the walk derivatives share the forward walk's vertical weight shift so opposite-side blends do not cancel it.

| Clip family | Forward cycle travel | Backward | Sideways |
| --- | ---: | ---: | ---: |
| Walk | 1.34 m | 0.93 m | 0.69 m |
| Jog | 5.00 m | 3.60 m | 2.60 m |
| Sprint | 4.20 m | Forward only | Forward only |

These distances match the runtime stride tuners. Running derivatives retime each leg independently around its
captured stance and swing apex. The source path is converted to an in-place constant-speed support path; physics
still supplies world travel. Anatomical reach and knee separation are enforced offline. The resulting locomotion
is edited motion, not unchanged mocap; rendered controller review remains necessary.

Starts and stops currently blend the authored idle and continuing gait. No separate start/stop clip is selected.
The turn derivatives are presentation only. Jump, landing, get-up and melee rules and hit volumes are unchanged.
