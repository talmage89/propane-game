# Asset library verification (AL, 2026-10-20)

Baseline `4a468ce`, proposal `cfabd60`; implementation on `wp/asset-library`. No art was authored.
Logs, metrics and captures are ignored worktree-local `build/al-*`; no other checkout was modified.

## Validator and pure migration checkpoints

`6270518` added the source inventory/validator/scaffold and gate negatives before migration. `make test` passed
(753 unit tests, 81 suites, no failures; four world formations and 60 segment plans agreed), as did the 20 Python
asset tests and `python3 -B tools/dev/lint_tests.py`. A negative fixture initially caught substring matching of
`source_file`; the final checker requires the exact field and rejects that fixture. Temporary asset-tree copies
reuse existing prop art and are deleted, with no retained cosmetic fixture.

`make setup` and the full `make test` were run after the mechanical migration. The original 132 primary files have
identical SHA-256 bytes; every one of the 127 import UIDs and parameter blocks is identical. Only source/remap/cache
paths changed. The initial scratch mover stopped on textures' platform-specific path fields; strict import and a
complete independent old/new comparison verified the finished map. No generator, geometry or texture setting changed.

Thirteen rendered scene commands ran before and after, all with `godot-mono --fixed-fps 60 --resolution 1280x720`
and absolute capture directories under `build/al-baseline` / `build/al-migration`:

- world_lab: houses, yard, cars, nature, props, furniture and composite sets;
- character_lab: `--info --anims=Idle,Walk@0.4`;
- player_range: stances and `--scenario=ragdoll --view=side --assert-grip --assert-motion`, effects seed 1;
- suburb_preview: seed 23;
- tank_range: vent and blast.

All seven kit views, two raw character views and six suburb views are byte-identical PNGs. Five stance frames are
also identical (20/70 frames overall). Effect/action frames differ: native ragdoll/contact timing, particle seeds
and follow-camera samples are not a pixel oracle. Reviewed baseline and migration ragdoll inversion/get-up,
aimed stance and blast samples show the same art/materials with those dynamic differences; the camera crops parts
of the existing tumble. The grip/motion assertions pass on both runs. This proves sampled visuals and unchanged
source geometry/material inputs, not continuous animation/feel or native performance. No performance claim is made.

## Native catalog and attachment

`make assets-check` loads 129 native resources including an empty typed HatLibrary. Scaffold/validator coverage now
includes literal display names through the real Make target, preserved resource UIDs, and native-wrapper rejection
of engine diagnostics or missing pass markers (23 Python cases). Budgets remain warnings.

The initial native attachment oracle incorrectly read the restored base pose outside a modifier callback and
failed. The corrected oracle captures inside the final RifleHandFollow modification signal and compares afterward;
the 0.1 mm/basis tolerance was not changed. HeadAttachment itself required no pose-order workaround.
`asset_tests.tscn` passes 660-frame local/remote final-head comparisons, motion, distant held poses, live replacement
and LOD restoration, ragdoll/get-up, independent tint/instances, unknown-ID fallback, collision-count/RNG conservation
and early retirement. A rendered run recorded 499 active, 95 ragdoll and 66 get-up frames. Reviewed attachment_030,
attachment_320 and attachment_600 show two independently tinted unsaved Dev boxes following standing, thrown and
recovered heads. These are in-memory primitives in src/Dev, not assets or item manifests.

`make assets-preview CAPTURE_DIR=.../build/al-preview-empty` passes with zero hats, None from five angles and ten
motion/recovery samples, plus preview.json. No owner art is supplied or generated. All original source payloads and
import identities remain unchanged.
