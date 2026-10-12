# Animation source choices

Downloaded the free **Standard** editions of [Quaternius UAL1](https://quaternius.com/packs/universalanimationlibrary.html)
and [UAL2](https://quaternius.com/packs/universalanimationlibrary2.html), through their officially linked itch.io pages.
Both archives contain `License.txt` dedicating the files to [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/).
Commercial use, modification and redistribution in public source are permitted. No payment or account was used.

| Archive | SHA-256 |
| --- | --- |
| UAL1 Standard, 15,904,933 bytes | `cc73fc4e495b82958207316596317a3f40b9fa38065bde1027937452da537724` |
| UAL2 Standard, 18,735,003 bytes | `4008ea208a604773a2b2177d965f0f5d3195498b5bf838c3f5785d68e95f2a68` |

Each has 43 distinct clips plus root-motion variants, Unity FBX and Unreal/Godot GLB. UAL2 also has a female
mannequin, not an additional animation set. `animation_inventory.py` enumerates all 86 names, lengths, in-place
root displacement, root-motion displacement/span/turn, root-travel direction and locomotion/carry classifications. Non-locomotion actions
are not mislabeled as strafes because their root happens to move sideways. No forward/back/side/diagonal rifle
locomotion exists in these free archives. Marketing describes the larger libraries, not this actual free inventory.

The official catalog has two Universal Animation Libraries and Universal Base Characters. The latter contains
compatible character meshes, not a missing directional animation set. Older Toon Shooter/modular character packs
were not verified as same-skeleton sources and were not downloaded. No extra compatible free set covering these gaps was
found. Paid editions, Mixamo and account-based services were not downloaded.

| Candidate | Seconds | Proposed use / derivation |
| --- | ---: | --- |
| UAL1 `Idle_Loop` | 2.500 | Base upright idle with current hands; retain torso breathing. |
| UAL1 `Walk_Loop` | 1.333 | Best slow walk/creep timing reference. Reverse for an initial authored backpedal derivative; inspect heel/toe transfer before accepting. |
| UAL1 `Jog_Fwd_Loop` | 0.933 | Forward 6 m/s base. Retiming should use measured root-motion travel rather than an arbitrary shared normalized stride. |
| UAL1 `Sprint_Loop` | 0.667 | Forward sprint base; original full-body timing, smaller foot correction. |
| UAL2 `Walk_Carry_Loop` | 2.000 | Two-hand **box carry**, not rifle. Compare its chest/weight shift against normal walk; use lower-body/chest reference while keeping existing hand sockets. |
| UAL1 `Push_Loop` | 2.667 | Two-hand pushing reference; posture is too forceful for upright creep. |
| UAL1 `Punch_Cross`, `Punch_Jab` | 1.000 / 0.867 | Prefer Cross as the restrained torso/weight-shift donor; neither is a finished rifle-butt strike. |
| UAL2 `Melee_Hook`, `Melee_Hook_Rec` | 0.467 / 0.600 | Strong rotation, deep crouch and explicit recovery; too extreme as-is for a rifle-butt strike. |
| UAL2 `Sword_Regular_A`, `A_Rec`, `B`, `B_Rec`, `C` | 0.433 / 0.967 / 0.533 / 1.033 / 2.000 | Compare weight transfer and impact timing; remove sword-specific arm arcs for a rifle. |
| UAL2 `Sword_Regular_Combo`, `Sword_Heavy_Combo` | 3.000 / 4.333 | Longer melee reference only; unsuitable as-is for the existing short strike and recovery rules. |
| UAL2 `Shield_Dash`, `Sword_Dash` | 1.100 / 1.567 | Lunge/pivot weight-shift reference, not ordinary locomotion starts or strafes. |

Rendered evidence favors normal Walk over Carry: Carry leans backward and nods strongly while holding an imaginary box.
Choose normal Walk for upright creep; keep the current wrist/finger/socket solve. Carry walk is an alternative
reference to compare visually, not evidence of an authored rifle stance. Reverse Walk and Carry are explicitly
shown as derivatives. Mirroring swaps left/right limbs and local X, including rotation handedness; reverse
playback alone cannot create a side-step. Derive left/right and diagonal lower-body poses by offline key editing
from Walk/Jog, preserving opposite contacts, hip-facing aim and upright stance; mirror only after reviewing knee
poles and handed rifle carry. These derivatives must be rendered through the controller and pass the calibrated
gait/slide checks before becoming production assets.

Root-motion variants give an initial timing estimate: Walk travels 1.3 m/1.333 s (0.975 m/s), Jog 5 m/0.933 s
(5.357 m/s), Sprint 5.5 m/0.667 s (8.25 m/s), and Carry 1.3 m/2 s (0.65 m/s). At today's speeds, initial playback
multipliers are Walk 1.54 for 1.5 m/s and 1.64 for creep, Jog 1.12 and Sprint 1.15. Carry would need 2.46 times
playback for creep, another reason not to select it. These are source travel estimates, not guaranteed foot-contact
speeds; derive planted-foot velocity from the actual clip and verify sliding through the controller. Keep root
motion stripped; physics owns travel. Changes to contact timing belong in the authored asset, not foot IK.

Still missing: genuine rifle strafes/diagonals, a purpose-authored backpedal with correct weight transfer, directional
starts/stops/pivots, turn-in-place, rifle aim offsets, and a two-hand butt-strike trajectory. Keyframe these on the
existing rig or record our own reference and author the poses. This remains an asset-authoring task, not another
procedural gait. No license/account decision is needed for the downloaded sources; the owner chooses derivatives
from evidence and approves newly authored clips. Prefer Punch_Cross’s restrained torso drive for melee; the hook
and sword arcs are stronger but visibly over-rotate/crouch and require extensive reauthoring.

The current free archives differ from the older sources used to build the packaged mannequin: UAL2 LayToIdle is
1.533 s rather than the packaged 1.200 s, NinjaJump_Land is 1.267 s rather than 1.000 s, Hit_Knockback
is 0.833 s rather than 0.667 s, and Idle_FoldArms is 2.500 s rather than 2.000 s. Do not silently change
get-up/landing timing during a rebuild. The free UAL1 Unreal/Godot edition is GLB, whereas `build_character.py`
currently asks for an Unreal FBX; update that importer in the asset package, using the verified shared GLB rest
pose, rather than feeding its Unity FBX blindly. No importer or packaged asset is changed here.
