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


## Picker and composition

Before this step `git merge dev` reported already up to date at `4a468ce`, followed by `make setup`. No settings,
main-menu or pause-menu implementation was changed. Main only supplies the saved hat when composing offline worlds;
Match assembly/lobby refresh supplies the full accepted record. New mutable state is actor-owned appearance/attachment
state and picker state; replacement and tree exit retire instances/tint copies before the skeleton. The immutable
catalog and default-rig validation cache live for the process. No gameplay state moved; no simulation timer, RNG
consumer or authority path was added. Old asset path dependencies were retired in the pure migration.

`make assets-check` now includes 26 Python cases and a native syntax/property load of actual scaffold output in a
temporary copied tree. The fixture relocates item addresses and reuses the already-imported original cooler prop;
it is not evidence of a populated exported catalog. It is deleted even on failure. Source tests include ID ordering
across authored/third-party items, mandatory third-party provenance, warnings with measured numbers, native import
hints (without rejecting ordinary names like mesh-color), resource/import UID collisions, rollback, orphan/escape/duplicate failures and literal
shell metacharacters passed through the real Make target. No scaffold was run against the shipped asset tree.

The headless `menu_test --scenario=input --classic-lobby --port=24682` and the rendered version (`--fixed-fps 60
--resolution 1280x720 --capture-dir=$PWD/build/al-menu-input`) pass 180 assertions. Tests inject actual keyboard,
mouse and gamepad events; check selection, None, apply/persistence/cancel, color preserving all other slots, empty
catalog hiding the control, unsupported-ID preservation, and offline human hat/float-color composition. Existing
look/jump/reload/melee, viewport, menu and focus checks still run. The earlier fixture used untransformed mouse
coordinates and assumed an Escape close path that the picker never had; those assertions failed. The corrected
fixture uses the existing window-coordinate click helper and real input to Cancel. No production input workaround
was added. Reviewed hat-picker-selected.png shows the tinted in-memory box in the live preview.

Network commands (each with a fresh absolute `-o` under build/, `TMPDIR=$PWD/build/al-tmp`):

```sh
tools/dev/net_bots.sh -n 2 -l 12 -p 24782 -t clean -s '--start-score=8' -c 12ABEF,F6F6F6 \
  -o "$PWD/build/al-net-appearance-score" -- --scenario=appearance --hat=missing_hat
tools/dev/net_bots.sh -n 2 -w 1 -l 12 -p 24782 -t clean -s '--start-score=8' -c 12ABEF,F6F6F6 \
  -o "$PWD/build/al-net-appearance-rendered" -- --scenario=appearance --hat=missing_hat \
  --capture-dir="$PWD/build/al-net-appearance-rendered/shots"
tools/dev/net_bots.sh -n 4 -l 90 -p 24782 -o "$PWD/build/al-net-fault4" -- --hat=missing_hat \
  --melee-start --net-lag=120 --net-jitter=30 --net-loss=3 --net-seed=2
```

Both clean runs pass record/body/drop/results assertions and score audits (16 initial - 2 spills = 14 banked).
Headless: 441 grids / 13,148 pairs; rendered: 440 grids / 13,118 pairs, both zero disagreements. Reviewed
Bot1_appearance_tags_glow.png shows cyan/white bare-head fallback bodies and corresponding dropped-tank glow.
An earlier run omitted the fixture's required starting bank and failed its missing-drop assertion; that log is
retained as al-net-appearance. The correct fixture precondition was restored, without changing gameplay rules.

The 90-second faulted soak passes: 1,999 grids / 178,222 body pairs, zero disagreements, 0.005/0.139 m final
rest/moving peaks. Transient fault episodes peak at 5.837 m and last at most 0.50 s; no zero-correction claim.
Audit: 36 map + 49 drops - 62 spills = 23 banked; all four clients agree. All five logs contain zero engine,
script or unhandled-exception diagnostics. This is functional/network evidence, not a performance measurement.

The picker/composition checkpoint independently passes `make lint` (Debug and ExportRelease, zero warnings/errors;
105 process statics, 31 launch reads, no architecture failures) and the full `make test` (753 unit tests / 81 suites,
behavior scenes, generator and separate-process hashes). The final validator refinements additionally pass
`make assets-check` with 26 cases. World hashes remain exactly the four baseline hashes; all 60 segment plans agree.
