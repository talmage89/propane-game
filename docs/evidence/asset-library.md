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

`make assets-preview ASSET=assets/generated/props/cooler.glb CAPTURE_DIR=$PWD/build/al-preview-file` also passes.
Reviewed file_preview_view_0.png shows that existing prop at the head socket, loaded in memory without registration.


## Picker and composition

Before this step `git merge dev` reported already up to date at `4a468ce`, followed by `make setup`. No settings,
main-menu or pause-menu implementation was changed. Main only supplies the saved hat when composing offline worlds;
Match assembly/lobby refresh supplies the full accepted record. New mutable state is actor-owned appearance/attachment
state and picker state; replacement and tree exit retire instances/tint copies before the skeleton. The immutable
catalog and default-rig validation cache live for the process. No gameplay state moved; no simulation timer, RNG
consumer or authority path was added. Old asset path dependencies were retired in the pure migration.

`make assets-check` now includes 27 Python cases and a native syntax/property load of actual scaffold output in a
temporary copied tree. The fixture relocates item addresses and reuses the already-imported original cooler prop;
it is not evidence of a populated exported catalog. It is deleted even on failure. Source tests include ID ordering
across authored/third-party items, mandatory third-party provenance, warnings with measured numbers, native import
hints (without rejecting ordinary names like mesh-color), resource/import UID collisions, stale external UID/path pairs, rollback, orphan/escape/duplicate failures and literal
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


## Packaged proof and final gate

`tools/release/build.sh godot-mono --unsigned-macos` succeeds for Linux x86_64, Windows x86_64 and macOS arm64.
It uses existing installed templates/tooling, not a downloaded executable. The repository's downloader verifies
both pinned Godot notice SHA-512 checksums; four original Kenney license notices are included beside CREDITS.
The macOS ZIP is explicitly UNSIGNED verification output and must never be published. Nothing was pushed.

```sh
python3 -B tools/release/check_artifacts.py --output "$PWD/build/al-artifacts"
python3 -B tools/release/check_artifacts.py --rendered --output "$PWD/build/al-artifacts-rendered"
TMPDIR="$PWD/build/al-tmp" python3 -B tools/architecture/tests.py
TMPDIR="$PWD/build/al-tmp" python3 -B tools/release/artifact_tests.py
TMPDIR="$PWD/build/al-tmp" python3 -B tools/dev/release_tests.py
```

Both artifact commands pass all three real PCK/assembly inventories (1,120 types / 546 resources, zero failures),
Linux ordinary/classic menu startup, server binding and release smoke from extracted directories without a source
project. Both report `[assets export] PASS ids= roots=5`: zero hats, actual typed resource load, default rig and
packed dependency closure, safe missing-ID resolution. Dev types/resources are absent; old asset paths fail.
Reviewed rendered 01_main_menu.png and 02_single_player.png show the ordinary menu and orange mannequin in the
populated suburb. Windows/macOS inventory is not native execution/signing/input proof. A future populated hat's
exported closure remains unproven until the owner supplies real art and runs this gate.

Artifact checker negatives pass missing asset root, broken imported remap target and obsolete asset path probes,
as well as existing Dev/smoke/entry/settings/truncation probes (plus 55 compiled-edge architecture negatives and 17 source
negatives). All five artifact wrapper tests pass, including error/timeout/missing marker and interrupted
child retirement. The 20 release-script tests pass, including source-validation failure before any import/export
and bundling the extra license notices. Initial packaging test fixtures lacked the newly required validator and
notices; their nine failures were fixed by modeling that boundary and adding the independent preflight-negative
assertion, not by weakening packaging. The final lint's first attempt found one missing brace in the new native
fixture; it was corrected. Earlier failed evidence is retained.

No gameplay rule, tuner/schema, message shape, construction key/order, simulation clock or RNG contract changes.
The actor's new native BoneAttachment follows final poses with override disabled; it does not reorder existing modifiers or
simulation callbacks. Protocol.Version remains 3. Raw original payloads, all import UIDs and parameter
blocks were independently compared again after preview/export: 132/132 and 127/127 unchanged.

Owner defaults (approved; deliberately easy to revise with explicit design review): hats stay through ragdoll/get-up;
lobby bots use only an explicitly chosen hat; Lab bots stay bare; offline humans use their saved hat with current
body colors; Tint_Player is opt-in. No owner decision blocks this delivery. The first character still needs the
rig/material/IK/ragdoll review, and inherited provenance gaps and pending animation visual checks remain open.

## Clean import and final detail-list correction

A new `.godot` cache was created after moving the prior ignored cache inside build/. `make setup` passes both the
initial and strict post-compile import, with zero build warnings/errors. Native typed-resource loading therefore
does not depend on the development cache. The prior cache is removed after verification.

Final review caught the extra hat traversal in detail bookkeeping: hats are already descendants of Model. A new
Dev assertion inspects the detail list's instance identities; before the fix it exits 1 with "mesh detail bookkeeping
duplicates the hat" (detail-list-red.log). Removing the redundant traversal fixes it; failure retirement also stops
fixture callbacks immediately. The rendered rerun passes all 660 final-pose frames (495 active / 99 ragdoll / 66
get-up), distant replacement and near restoration, unique detail entries, tint, collision and RNG checks. Reviewed
al-attachment-final/attachment_320.png and attachment_600.png show retained head fit while thrown and recovered.
Transform tolerances and the original movement/lifecycle assertions remain unchanged. This fixes AL's bookkeeping;
no gameplay state, physics rule or existing simulation callback order changes.

After the clean import and completed actor plumbing, the original rendered player_range command was repeated into
build/al-final-stances. Idle, aimed, run, sprint and aimed-strafe PNGs remain byte-identical to the original dev
baseline (5/8). The three firing frames differ, as in the pure migration comparison; reviewed aimed-fire samples
retain the same body/rig/rifle/materials with dynamic pose/effect samples. This is not a continuous-motion pixel claim.

The complete final `TMPDIR=$PWD/build/al-tmp make check` after clean setup and both corrections passes
(final-fresh-check.log): warning-free Debug/ExportRelease builds, 105 statics / 32 launch reads, 27 asset cases,
129 native resources with zero hats plus temporary scaffold syntax proof, 753 unit tests / 81 suites, 180 real-device
input assertions, all behavior/generator scenes, four unchanged baseline world hashes, 60 agreeing segment plans,
and the gate's compiled/source/artifact failure-propagation tests. No deep sweep or native performance claim is made.
The final review archives are rebuilt from the committed runtime code and rechecked in fresh al-artifacts-final
and al-artifacts-final-rendered directories; extracted duplicate payloads and the prior cache are then removed,
retaining archive digests, logs and screenshots.


## Review fixes after personal settings merge

Starting point `2d0e4d1`; `make setup` passed before edits. The author now imports the reference mannequin into
Blender, fits the hat in place and exports only the hat without repositioning it. Its frozen model-space head frame
has identity axes and origin `(0, 1.568700552, 0.0050996924)` meters. HeadAttachment applies the inverse once between
the actor's target socket and the untouched imported root transform. Neither scaffold nor import rebakes art.
This keeps one registered/raw-file preview path and lets a future target socket differ without changing the
original authoring reference. No new art, item manifest or shipped catalog entry was created.

The Dev box is now authored at model height `(0, 1.788700552, 0.0050996924)`, independently of production's reference
property. The new oracle fails against the original attachment (`al-review-authoring-red.log`, exit 1); its root
position contains the missing reference offset. The final rendered `asset_tests` passes 660 frames: 471 active,
123 ragdoll, 66 get-up. Root position/basis and visible mesh position retain the 0.0001 tolerance. A separate native
Skeleton3D has a Head at `(0.1, 2.1, -0.2)` with different rest axes and binds the same model-space primitive through
HeadAttachment. Its mesh position/basis must match its own final socket every frame. Temporarily replacing the fixed
reference with each actor's rest origin fails this assertion (`al-review-alternate-red.log`, exit 1); restoring the
fixed frame passes. The earlier algebraic projection was replaced because it did not exercise a second native
binding. This proves the attachment target contract, not a complete future character rig/IK/ragdoll. Reviewed
`al-review-attachment-native/attachment_030.png`, `_320.png` and `_600.png` show two
independently tinted primitives fitted standing, thrown and recovered. Collision, RNG, replacement, detail and
retirement assertions still pass.

Runtime HatCatalog now validates each item independently, reports its ID/reason once, and keeps only valid entries.
The shipped snapshot is still cached, so actors do not repeat diagnostics. The strict native validation factory
throws instead; assets-check uses it. Missing library/defaults remain hard setup errors. An in-memory alpha-blend
fixture asserts one exact diagnostic, mixed-catalog survival, bare actor fallback without erasing the ID, subsequent
valid attachment and strict rejection. The default diagnostic run (`asset_tests --scenario=invalid-log`) exits zero
and a Python assertion finds exactly one expected ERROR line naming `dev_probe` and the material rule. That is a
deliberate negative-test diagnostic, not an unexpected engine error.

`python3 -B tools/assets/tests.py` passes 28 cases. The new temporary alpha-blend mutation of existing prop data
fails both source validation and the real `make hat ASSET_ROOT=...` command, preserving library bytes and rolling
back the attempted item. No fixture remains. Menu device tests exercise both an actually empty catalog and one
recovered from an invalid-only item, hiding the choice and preserving unsupported saved IDs.


Review verification commands (captures are rendered, invisible; all subsequent commands set
`TMPDIR=$PWD/build/al-tmp`):

```sh
make build
python3 -B tools/assets/tests.py
make test-equivalence
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/asset_tests.tscn -- --capture-dir="$PWD/build/al-review-attachment-native"
godot-mono --headless --path . res://scenes/dev/asset_tests.tscn -- --scenario=invalid-log
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/menu_test.tscn -- --scenario=input --classic-lobby --port=24682 --capture-dir="$PWD/build/al-review-menu-input"
godot-mono --fixed-fps 60 --resolution 1280x720 --path . res://scenes/dev/menu_test.tscn -- --scenario=settings --classic-lobby --port=24682 --capture-dir="$PWD/build/al-review-menu-settings-classic"
make assets-preview CAPTURE_DIR="$PWD/build/al-review-preview"
make assets-check
tools/dev/net_bots.sh -n 4 -l 90 -p 24782 -o "$PWD/build/al-review-net4" -- --hat=missing_hat --melee-start --net-lag=120 --net-jitter=30 --net-loss=3 --net-seed=2
make check
```

`make test-equivalence` passes its six self-tests, frozen release wire/schema/world/publication checks and all five
Lab traces (bank, king, spill-chain, shortfuse, dissolve). SHA-256 comparisons before/after confirm all 12 reference
files remain byte-identical; no refresh command was run. Evidence is in `al-review-equivalence.log` and
`rfp0-equivalence-12/`.

Rendered input passes 183 assertions, zero failures; rendered settings passes 119 checks, including the native
window/display checks omitted headlessly. The first rendered settings command accidentally omitted the documented
`--classic-lobby` flag and timed out after 96 checks waiting for a classic lobby screen while the game entered the
lobby world. That failed log/captures remain in `al-review-menu-settings`; the documented command above passes.
No production or fixture workaround was made. Reviewed input `hat-picker-selected.png` shows the primitive fitted
in the appearance preview, and `single_02_after_turn.png` shows the bare-head offline fallback after real mouse
look. Reviewed settings `single_camera.png` / classic `match_camera.png` show readable controls over the paused
single-player/match context.

The empty catalog preview passes with exactly 15 None-only PNGs/report entries (five angles plus ten motion
samples). Reviewed `al-review-preview/none_view_0.png` and `none_motion_9.0.png` show the base mannequin standing
and thrown, without a hat. `make assets-check` passes 28 source/tool cases, 129 native resources, zero hats and the
temporary scaffold resource-load proof.

The 90-second four-bot lag/jitter/loss soak passes all checks: 1,999 tank grids, 180,801 body pairs, no unobserved
pair grids or disagreements; final rest/moving peaks 0.010/0.205 m. Transient rest/moving peaks are 0.154/8.182 m,
971 above-tolerance episodes, longest 0.95 s; this is not a zero-correction or performance claim. Score audit:
35 map + 43 drops - 57 spills = 21 banked; all four clients agree. An explicit scan of all five logs finds zero
engine/script/unhandled-exception diagnostics.


The final `TMPDIR=$PWD/build/al-tmp make check` after the stronger native target test passes
(`al-review-check.log`): warning-free Debug/ExportRelease builds; 106 approved mutable statics / 32 launch reads,
zero architecture failures; 28 asset cases / 129 native resources / empty shipped library; 758 tests / 82 suites;
660 headless native attachment frames (476 active / 118 ragdoll / 66 get-up); 183 real-device input assertions and
112 headless settings checks; all behavior/generator scenes, four unchanged world hashes and 60 agreeing segment
plans; all gate self-tests, including 55 compiled and 17 source architecture negatives. The earlier complete gate
also passed (`al-review-check-initial.log`), before the extra native-target assertion replaced the projection.
A final Git comparison against `2d0e4d1` confirms every asset payload/import file is unchanged (only the owner README
changed under assets/); frozen-reference SHA-256 hashes remain identical. No import churn or new UID files exist.

Production retains the same cached catalog owner and actor-owned attachment retirement; no state moved, no existing
callback/clock order changed, and no simulation dependency was removed or added. No shared message, construction,
tank, tuner or protocol change; Protocol.Version remains 3. The accepted owner defaults remain unchanged.
This review did not rebuild release archives or execute Windows/macOS natively; prior empty-catalog artifact
verification remains above. A real owner's first hat/populated export and the first character's V1 binding review
remain future acceptance work. No blocking issue remains, and no performance claim is made.
