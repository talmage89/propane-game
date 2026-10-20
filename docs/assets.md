# Asset library (draft proposal)

Phase 1, 2026-10-20. Inventory baseline: `4a468ce` (`dev`, Propane 0.5.0, protocol 3).
This is a proposal for lead/owner review, not an implemented contract. No assets move in phase 1.
The owner's hats and characters are the only new cosmetic art: neither phase creates a sample hat or character
model, texture, mesh, scene, or item manifest. Dev tests may construct an unsaved primitive in code only.

## Recommendation

Separate runtime art by provenance, and keep cosmetic identity independent of paths. Use imported GLBs directly,
Godot Resource item definitions for future owner hats, one explicit shipped hat list, and one actor-owned head
attachment. Preserve all existing model bytes, import settings, world keys/order, rig behavior and wire bytes.
Ship this plumbing with zero hats. Keep the current mannequin as the default; defer interchangeable rigs until
the first owner character provides a real second case.

The binding constraints are [vision](../design/VISION.md), [architecture](architecture.md),
[appearance](multiplayer.md#appearance), [animation sources](animation-sources.md), [locomotion](locomotion.md),
[rifle stance](motion.md) and [artifact verification](runbooks/release-artifacts.md).

## Layout and identity

Proposed paths below are illustrative destinations, not new files in this commit. The hats directory initially
contains only its instructions; there are no item folders or item definitions until the owner supplies one.

```text
assets/
  README.md                       map, ownership, author/check/preview commands, shipping rules
  library.tres                    HatLibrary Resource, Hats = [] initially; explicit shipped list
  authored/                      owner-authored runtime exports
    hats/                        future: <stable-id>/model.glb, item.tres, textures if needed
    tank/                        current tank-project exports and textures
    rifle/                       current rifle-project exports and textures
  generated/                     reproducible runtime outputs from this repository's scripts
    props/                       current procedural props
  third_party/                   upstream identity, license and attribution retained
    kenney/{cars,furniture,nature,suburban}/
    quaternius/ual/               current merged mannequin.glb (derived from UAL1/UAL2)
    100style/neutral/             current directional.tres (retargeted derivative)
```

Third-party derivation stays under `third_party`, even when a local script produces it: running a generator does
not transfer authorship or remove a license. Each family records its upstream inputs and local processing in
`assets/README.md` and the existing source/credits documentation. Preserve Kenney's `Textures` case and original
file names. Keep the three suburban palette derivatives alongside their source atlas, labeled as locally modified
with currently incomplete provenance. Do not silently represent them as unmodified upstream art.

Use one folder per independently selectable future cosmetic; its `model.glb`, textures and `item.tres` travel
together. Do not split existing cohesive kits into hundreds of item folders. Existing export names and case stay
unchanged during migration; new owner paths use lowercase ASCII and underscores. Scripts remain in `tools/`;
shaders remain code in `shaders/`. `.blend`, raw capture archives, large source textures and intermediate exports
stay outside `res://` in the owner's source project or the shared asset-source store, with source records there.
Do not auto-import `.blend` or require Blender/numpy to build, validate in CI, or play.

An appearance ID is a permanent identity, not a directory, UID, label, array index or resource path. New IDs use
lowercase `[a-z0-9][a-z0-9_-]{0,63}`, a narrower convention inside `AppearanceCodec`'s existing 1–64 ASCII
letters/digits/underscore/hyphen contract. Do not narrow the codec or reject old valid mixed-case IDs. Compare
ordinally; reject duplicate and case-colliding authored IDs. Reserve `mannequin` as the default character identity;
null model means that default, and null hat means none. Never put `none` on the wire as a sentinel. Never rename
or reuse a released ID; a retired ID remains reserved in a small reviewed retirement ledger, with safe fallback.
Changing a display name or physical path does not change identity.

For a future hat, `item.tres` is a typed `HatDefinition : Resource` with `Id`, `DisplayName`, `Author`, `License`,
`Source`, and `Scene : PackedScene`. License is an SPDX identifier where one exists, otherwise a documented
`LicenseRef-...` resolved to supplied terms. Source is a public upstream URL/revision or a repository-relative
generator/source-project label and revision/hash; never a personal filesystem path. Author and license must not be
guessed. An owner-source label can be public while the actual source store stays private. No executable script,
collision, arbitrary loader path, entitlement or download behavior belongs in an item. Player-color tint is an
explicit material-slot convention, not an arbitrary manifest shader.

Godot Resources give the owner inspector editing, typed scene dependencies and native remapping. A separate JSON
runtime loader or one wrapper scene per static hat adds no value here. The list resource contains typed hat
references, not path strings. New characters eventually use the same identity/provenance fields, but **AL creates
no character item manifest**, including for the existing mannequin. Its existing load becomes a named default
catalog entry in code, with provenance here and in CREDITS. Converting it to an item resource must wait until the
hard rule changes. Do not generalize Outfit/GunSkin/BulletSkin as part of AL.

### Discovery and export proof

Production loads exactly `res://assets/library.tres` with `ResourceLoader`, then resolves the list by ID. An empty
list is valid. Sort picker entries by ID for deterministic presentation; world model traversal and RNG remain
unchanged. Source validation scans the authored directory to reject an item missing from the shipped list and a
list entry with missing dependencies. Production does not use `DirAccess`, raw file existence, glTF parsing or
directory iteration to find cosmetics. Imported filenames and binary resource remaps differ in exports;
[ResourceLoader](https://docs.godotengine.org/en/stable/classes/class_resourceloader.html) supplies resource loading
and dependency APIs. The existing artifact checker already resolves the main scene's `.remap` to its packed target.

Keep `all_resources` for all three presets. Do not combine migration with deleting unused kit art or switching to
selected-scene export: C# dynamic model paths need explicit care. Add exclusions for the asset instructions/source
records if needed, and explicitly package attribution/license texts outside the PCK; text notices are not reliably
included just because a neighboring GLB is a resource. Preserve Dev exclusions and `ExportRelease` source removal.

Phase 2 acceptance requires extending the artifact inventory to require the list and its remap target, the default
mannequin, directional library and every listed hat's dependency closure, and to reject obsolete asset paths and
any Dev fixture. Extend `ReleaseSmoke.ReleaseTest` to load the actual list, report/assert IDs/count against the
source validator's expected inventory, instantiate listed scenes, and exercise null/unknown selection. Run
`tools/release/build.sh godot-mono` (or the documented unsigned macOS verification option) and
`python3 -B tools/release/check_artifacts.py --output build/al-artifacts` plus its rendered mode in a new directory.
Execute the unpacked Linux game outside the project; keep all three PCK inventories and screenshots. Do not count
source loading, an editor export log, or a raw PCK filename as runtime proof. Windows/macOS inventory is not native
execution proof.

With zero hats, this proves the empty exported catalog, default model, resource type loading and fallback, not a
nonempty hat's serialized dependency closure. In-memory Dev tests prove populated selection/attachment separately;
the first owner hat must pass the same packaged smoke before shipping. No synthetic cosmetic file is exported to
simulate that evidence. These are phase 2 requirements; the proposed resource loader has not been executed yet.

## Hats from Blender to every actor

### Proposed authoring contract

Use Blender 5.1 and glTF 2.0 binary (`.glb`), consistent with the existing tools; Godot
[recommends glTF](https://docs.godotengine.org/en/stable/tutorials/assets_pipeline/importing_3d_scenes/available_formats.html).
Author against the existing mannequin as a read-only reference, never export it with the hat.

| Concern | Proposed contract |
| --- | --- |
| Units and scale | Metric, 1 Blender unit = 1 meter. Apply object rotation/scale before export; no negative/nonuniform scale, runtime rescale or import root-scale correction. Mesh-bearing transforms must be finite. |
| Frame | Blender Z up, -Y forward; export +Y up, yielding model +Y up/+Z forward, like the current mannequin. `PlayerCharacter` already rotates its model 180 degrees to Godot actor forward (-Z); the attachment inherits that once. |
| Origin/socket | Hat root origin = `head` socket, **not the crown or ground**. For the current rig the socket is at the `Head` bone's global rest origin, with axes aligned to model space in rest. Store the bone-to-socket basis in code, derived from that rest transform, so the rest bone's small pitch is not an authoring correction. Runtime applies live Head pose × this constant frame. |
| Placement | Import the current reference at unit scale/T-pose, align the hat on its head, then export only the hat in socket space. `Head` rest origin measured by `character_lab --info` is approximately (0, 1.569, 0.005) meters in model space; derive the exact transform, do not type those rounded coordinates into production. Preview will print the frame for the owner. |
| Export | Selected hat objects only; GLB; +Y Up; normals and tangents; applied mesh modifiers; materials exported; no cameras/lights, animations, armature/skin, collision suffixes or embedded scripts. No Draco/custom extensions required. One identity root, intentional child transforms baked consistently. |
| Materials | Standard glTF metallic/roughness PBR. Opaque or alpha-cutout initially; avoid alpha blend and shader extensions. Base color is sRGB, normals/ORM are data; ORM is R occlusion/G roughness/B metallic. Use factors for flat colors; embedded PNG textures preferred. Optional external textures stay under that item's folder and are dependencies. |
| Tint | Authored materials remain as supplied unless named `Tint_Player`; that slot multiplies its base color by the accepted player color using an instance-owned material copy. Never mutate a shared imported material. Confirm whether the owner wants tint before enabling it for their first hat. |
| Initial budget | Proposal: at most 5,000 rendered triangles, 2 material surfaces/draws, 3 unique textures, each at most 1024×1024; at most 16 MiB decoded RGBA including mip levels in total. Count glTF indices/vertices, not Blender polygon counts or compressed GLB size. Treat these as reviewable authoring limits, not a measured performance guarantee. |
| Import | Keep unit root scale, normals/tangents, generated mesh LODs/shadow meshes, named materials; disable animation import for hats, enable texture mipmaps/appropriate platform VRAM compression. Preserve the generated `.import` UID/settings in git. |
| Names | Future item folder is its stable ID; `model.glb`, `item.tres`, descriptive lowercase texture names when external. No example item or template manifest is generated by AL. |

The head socket is rig geometry metadata. No per-hat fit sliders, launch forces, timers or hidden effect constants
are introduced. Any future adjustable effect/fit behavior must use `Tuning` and receive its own compatibility
classification. Proposed budgets are validator policy, not runtime gameplay/effect tuners.

### Runtime binding and lifetime

Add native catalog/binding code under `src/Assets` in **Simulation**, with an explicit namespace mapping in
`tools/architecture/modules.json`. Foundation keeps only appearance values/codec. Simulation, Presentation,
Composition and Dev can already reference Simulation; Wire/Server must never load the native catalog. This is a
module mapping for a new source directory, not a new dependency exemption. Keep `GameAssets`' tank/prop API and
immutable cache behavior intact; no universal asset framework.

The library is a main-thread, process-lifetime borrowed immutable resource, consistent with current GameAssets
caches. Record its cache/state owner in architecture inventories. Tests inject an immutable test catalog directly;
no process-global registration overlay is needed. Each `PlayerCharacter` owns its resolved selection, one
`BoneAttachment3D` bound to `Head` with pose override disabled, the optional instantiated hat subtree, and any tint
copies. Free the old subtree on selection replacement; native actor destruction frees attachment/instances. Never
dispose shared PackedScenes/materials from an actor. Construction failure must clean up detached instances too.

[BoneAttachment3D](https://docs.godotengine.org/en/stable/classes/class_boneattachment3d.html) follows skeleton updates.
Verify its final transform after animation, PhysicalBoneSimulator, PoseBlend, RifleRig and RifleHandFollow;
do not add another animation modifier or reorder existing callbacks. Its child socket frame handles the rest-basis
correction. It follows the same final head in ordinary motion, aim, hits, native ragdoll, capture and get-up.
Distant/hidden actors retain the same held pose cadence; hats join `RenderLayers.Actors` and mesh-detail bias
bookkeeping even when changed after `_Ready`. Hats have no bodies, colliders, hitboxes, mass, blast registration or
authority. No simulation RNG draw, timer, network event or polling `_Process` is required for attachment.

Give the character one full-record appearance application path usable before `_Ready` and on live lobby profile
updates. Resolve null/unknown/unavailable Hat to no hat; resolve unsupported ModelOrSkin to the mannequin; keep all
original IDs in the record for saving/forwarding. A missing optional resource is diagnosed once and falls back;
malformed shipped resources fail validation/build. Missing default rig resources remain a hard setup error.
Retain BodyColor compatibility for existing code/fixtures, and make record/color updates agree without discarding
unimplemented slots. Avoid rebuilding the model/rig for a hat or color change.

| Display/integration | Current code and proposed change |
| --- | --- |
| Local/remote network actors; owned bots; arrivals | `Match.Assembly.AddActor` currently passes only profile color. Pass the complete accepted appearance to the common character path before session attachment. `Match.Lobby.UpdateLobbyActor` currently compares only color; apply changed records there. Keep roster callbacks and native order. |
| Roaming lobby and countdown/stage | They use the same Match actors, including classic lobby staging; no second portrait rig. StageScreen's cards, lobby/browser/roster and results are 2D color/name UI. Keep those 2D; do not invent hat thumbnails. |
| Single player and Lab human | `Game.Assembly` and `LabSession` currently create default-orange actors without saved cosmetics. Propose applying the saved **hat slot only**, preserving their existing float body colors without a byte-color round trip; composition passes the saved selection through the existing borrowed settings boundary. This is an owner-facing choice to approve. |
| Offline Lab bots | Keep current palette and no automatic hats. The shared actor plumbing supports an explicit hat if later requested; no random cosmetic assignment. |
| Lobby bot editor | It already edits an AppearanceRecord through AppearancePicker and UpdateActor. If bot hats are approved, use the same selector; existing bots start with none. No server-side cosmetic resolver. |
| AppearancePicker | Pass `Selected` rather than only its color to its PlayerCharacter preview; selection changes use the same application path. Add a None + ID-sorted hat control only when the catalog has selectable hats. Unknown saved IDs stay retained with fallback until the user explicitly chooses None/another hat. Cancel changes nothing. |
| Raw asset tools | CharacterLab, WorldLab's scale dummy, AnimationClipReview's direct-model branch intentionally inspect raw art; keep them bare. `player_range`, animation review with a real PlayerCharacter, and the new asset preview exercise the common attachment. |
| Other views | Spectator cameras see the existing actor; no clone is needed. Main's menu backdrop is a tank/vent scene without a character. Results and roster cards do not create character models. |

Selection uses `Selected with { Hat = id }`, preserving color and all other slots. `MenuProfile.Save`,
`PlayerSettings.Appearance` and `AppearanceCodec` already persist the entire record. `NetClient.SetProfile`, Hello,
LobbyState, MatchStart, AddActor/UpdateActor and standings already carry it, including the frozen match profiles.
Do not change a packet, ID bound, admission rule, field order or codec version. Color edits must keep the hat and
unknown slots. Bot edits must preserve the whole appearance. A peer without the asset renders bare mannequin while
retaining the ID. No asset transfer, mod search path or cosmetic entitlement system is added.

UI work waits for phase 2 to bring in the settings package's `dev`; leave main/pause menus and PlayerSettings alone
in phase 1. Extend real device-event tests for selection, cancel/apply, keyboard/gamepad focus and empty catalog,
including `menu_test --scenario=input --classic-lobby`; intent injection alone is insufficient.

## Characters later: the current contract is specific

The mannequin GLB contains 65 skin joints, 22 packaged clips and `M_Main`/`M_Joints` materials. Godot removes `_Loop`
from imported loop clip names. `CharacterAnimator` also loads the three 1-second directional cycles. The reference
faces +Z in a Y-up T-pose at unit scale; the controller creates a 1.8 m capsule with radius 0.32 m independently of
the mesh. Those are existing values, not new choices to change in AL. The present get-up also assumes the existing
pelvis offset and clip timing. An arbitrary humanoid skeleton or merely matching names is not sufficient.

| Subsystem | Required binding for a compatible first character |
| --- | --- |
| Skeleton/rest | One Skeleton3D with the same hierarchy, rest positions, rotations and bone axes as the current mannequin, including root/pelvis, `spine_01..03`, `neck_01`, case-sensitive `Head`, both clavicle/upperarm/lowerarm/hand chains, thigh/calf/foot/ball chains and fingers. Skins bind correctly; no root motion, hidden scale or negative transforms. Existing ragdoll capsules run along local +Y and hinges assume local X flexion. |
| Fingers | `index`, `middle`, `pinky`, `ring`, `thumb`, segments `01..03`, suffix `_l`/`_r`; preserve local rest flexion axes and hand proportions. Existing leaf bones remain in the canonical 65-joint rig. RifleRig caches absolute rest rotations, not a generic humanoid mapping. |
| Clips | Imported names required now: Idle, Walk, Jog_Fwd, Sprint, Jump_Start, Jump_Land, Pistol_Idle, Hit_Chest, LayToIdle; preserve the other packaged clips. Directional library: Walk_Back, Walk_Left, Walk_Right. All tracks must target the imported skeleton path; preserve stride touchdown/calibration, durations and in-place travel. |
| Retargeting | UAL1/UAL2 currently share names/rest; local build merges them without retargeting. 100STYLE transfers global rest-frame rotations, calibrates posture and scales pelvis translation by leg length; it writes only trunk/head/legs. A different rig requires a new explicit map/rest calibration and grip/get-up review; relabeling bones or Godot humanoid retarget alone does not establish compatibility. |
| Sockets/IK | `head` socket uses Head binding above. Hands use actual `hand_l`/`hand_r` bone frames; eye position is a tuned Head landmark. Rifle sockets belong to the separate rifle: Socket_Foregrip, Socket_Muzzle, Socket_Butt, Socket_Eye, Socket_MagWell. Arm lengths, elbow axes, wrist/finger frames and shoulder/eye landmarks must keep the rifle-contact assertions valid. |
| Physics and recovery | Preserve capsule, existing 16 ragdoll bodies/joints/masses/radii, bone-length derivation, launch order, captured pose and get-up blending. Skin fit must be reviewed against these shapes; changing proportions cannot silently change physical behavior. |
| Materials | Existing StyleMannequin replaces every surface: names containing Joint get the neutral joint material, everything else the accepted body color/rim. A future character must explicitly mark player-color surfaces (proposed `Tint_Player`) and preserve other authored PBR materials. Do not apply the mannequin override indiscriminately to an owner's textured skin. |

Build now: validate the existing default rig once, resolve/copy a head socket frame once, name the default character
in code, provide common appearance application and hat attachment, document the binding and safe unsupported-model
fallback. Validator errors should name missing bones/clips/socket/material contracts before gameplay initialization.
Do not extract RifleRig, Ragdoll or PoseBlend, alter their algorithms, build a generic socket/retarget framework,
add character selection UI, or create a character resource in AL. No moved simulation state or owner retirement.

When the first owner character arrives: decide whether it uses the canonical rig or needs a reviewed binding;
implement its real manifest and selection only then, confirm material tint and proportions, validate all animation
and physical contracts (architecture V1), and do multi-view grip/motion/ragdoll/recovery acceptance. Skeleton/rest
changes that affect shared physical behavior need separate minor-release classification. Existing animation visual
acceptance is incomplete (12/46 checks pending per architecture); AL cannot claim to close it.

## Owner tooling and tests to build in phase 2

Proposed commands, **not available yet**:

```sh
make assets-check
make assets-preview CAPTURE_DIR="$PWD/build/al-preview"
make assets-preview ASSET="assets/authored/hats/<owner-id>/model.glb" CAPTURE_DIR="$PWD/build/al-preview-file"
```

`assets-check` depends on `build`, uses Python 3 standard library for file/provenance/GLB/PNG inspection and a
headless Dev scene for typed resource/native validation; add it to `make check` and matching stock Ubuntu CI.
No Blender, numpy, Pillow, jq or ripgrep in the gate. Keep quick catalog/fallback/attachment assertions in `make test`.
Wire gate self-tests so a failing assets check fails check/CI, and demonstrate negative coverage before relying
on the validator. New scripts/scenes retain required UIDs; Dev fixtures must stay absent in ExportRelease/PCKs.

Validate required metadata/types, exact codec-compatible IDs, uniqueness/case collisions/retirement, scene references
and dependency containment, author/license terms and attribution coverage, no source art or unregistered item,
triangle/surface/texture budgets, finite bounds/transforms, no skin/animation/collision/scripts in a hat subtree,
and the default rig's bones/rest/clips. Measure budgets from actual GLB accessors and PNG headers, then verify native
mesh surfaces/import dependencies. Reject unsupported topology/image formats/extensions rather than reporting
success without counting them. No external texture URI may escape the item directory; GLB embedded images count
toward the same limits. Report size/count metrics, missing provenance and all errors with paths/IDs; exit nonzero.

An orphan means an unexplained file, not merely a model absent from today's random selection. Account for every
file under assets, including texture dependencies, notices, import sidecars, source records, the explicit catalog,
and documented raw-kit preview/reserve content. Preserve the currently unused kit files in the inventory allowlist;
do not delete them in migration or make an unconditional catch-all exemption for new unregistered hats. Require
matching `.import` UIDs/settings and no duplicated UID; do not treat `.godot/imported` as author-owned source.
Use in-memory invalid definitions/GLB-header fixtures to prove missing licenses, invalid/duplicate IDs, path escape,
over-budget content and orphan detection. Do not save sample cosmetic manifests/scenes for tests.

`assets-preview` builds first, runs invisibly through `godot-mono` at a fixed resolution/FPS, and creates an isolated
WorldContext with the real PlayerCharacter, rig and physics. Iterate None and every listed hat, or import the given
worktree-local GLB in Dev memory via GLTFDocument without saving a PackedScene/manifest. Capture front/back/left/right
and top-oblique views, plus moving walk/jog/creep/sprint, aim/reload/melee and a physical throw/get-up. With no hats,
render the base actor from the same views and print/assert `0 hats`; no error and no fake item. Reuse CaptureDirector
failure handling and write a small machine-readable report of IDs, measured socket transforms, scenarios and image
paths. Deterministic capture clocks are render/fixed simulation time; production gains no new timer. Preview roots,
raw-import temporary resources and tint copies are disposed with the preview world.

Assert head-to-hat transform equality after final skeleton sampling through all states; replace/remove/unknown
selections, two actors with different hats/colors, early actor retirement and repeated picker creation/teardown;
and unchanged collider counts, shapes, shot queries, RNG consumption and lifecycle callbacks. Construct the only
placeholder mesh and PackedScene in `src/Dev` code, unsaved, passed through an injected catalog. Give tests explicit
coverage assertions and nonzero exits. Review screenshots; a transform assertion alone cannot establish clearance.
No claim that an empty catalog validates a future owner's silhouette, materials or nonempty exported hat closure.

## Migration and acceptance sequence

1. Bring phase 2 up to the lead's then-current `dev` (including settings). Capture paired baselines **there** before
   implementation: character front/side, player stances/grip/motion/ragdoll, world kit families, seeded suburb and tank
   vent/blast, appearance picker and roaming/classic stage. Retain raw metrics and image paths under `build/al-*`.
   Phase 1 screenshots establish the current subjects only; they are not a post-merge differential baseline.
2. Prove harness/validator negative assertions in a separate commit. Define the new `src/Assets` Simulation mapping,
   native cache lifetime and reviewed process-state entries; do not regenerate ratchets or reference fixtures to pass.
3. Land file/reference migration separately from new hat behavior. Move every existing asset as mapped below,
   **with its `.import`**, preserving UID and all import parameters; preserve existing `.tres` text. Update
   source_file/dest_files/remap cache paths deliberately through reimport. `.godot` path hashes change; UIDs do not.
   Keep only necessary reviewed `.import` path changes, no editor churn, texture recompression/settings changes or
   regenerated geometry. The hard rule permits relocating existing character art, not creating another character.
4. Update every direct path in the reader map, dynamic Catalog prefixes, raw Dev loaders, script CLI headers and
   retarget defaults (`--mannequin`/`--output`). Keep scripts' caller-supplied output argument behavior. Export_tank
   does not copy textures: document the three 4K maps/flipbook copy from the tank project's source separately.
   Update README/development/animation-source paths, CREDITS, artifact requirements and any release-impact path
   classification. Historical dated evidence/immutable fixtures keep their old paths and provenance.
5. `make setup` must pass after moves; compare model/texture hashes and source-to-new UID/parameter maps. Assert
   every resource loads, embedded external texture URIs still resolve, skeleton/rest/clip inventory and Catalog
   measurements/order are identical, and old active load paths are absent. Run `make build`, `make test`, `make check`.
   Separate-process world hashes and frozen wire/tuner/plan references must remain unchanged; never regenerate
   fixtures to make a path move pass. If an oracle includes a changed resource path, report it and add an explicit
   geometry/semantic comparison; do not claim byte identity where it is false.
6. Compare paired deterministic 1280×720 captures using the same seed/camera/time/effect seed; review pixel diffs
   and both images. Require unchanged still art/materials/scales; use finite/contact/pose and lifecycle assertions
   plus multi-frame review for ragdolls (native timing may differ slightly). Exercise every kit, not just normal
   random selections. Preserve golden-hour rendering and existing color contrast. Record inherited visual defects.
7. Land catalog/default binding/attachment and then picker/composition integration in logical production commits,
   each with relevant validation. No new tuners or shared simulation changes are needed for the proposed defaults.
   Run real-device menu tests and clean/faulted two-peer runs via `tools/dev/net_bots.sh` in **new** output
   directories, checking accepted/frozen/live-lobby appearances and missing-ID fallback. Source peers may use the
   in-memory Dev fixture; no cosmetic is written to assets. Empty shipping catalogs must also complete ordinary
   roaming/classic match, bot edit, departure and return without a leak.
8. Package all three targets; run headless/rendered artifact checks above. Copy all four Kenney License.txt files
   and required Quaternius/100STYLE license/attribution terms into the external notices bundle, preserving author,
   source and retarget-modification credit. Obtain any missing legal text only from approved primary sources with
   checksum provenance; no network fetch in ordinary make test. Retain existing Godot notices. No publication.

| Current prefix/file | Proposed destination (relative to repository) |
| --- | --- |
| `assets/tank/` (all files) | `assets/authored/tank/` |
| `assets/rifle/` (all files) | `assets/authored/rifle/` |
| `assets/props/` (all files) | `assets/generated/props/` |
| `assets/kenney/` (all files) | `assets/third_party/kenney/` |
| `assets/character/mannequin.glb` and its `.import` | `assets/third_party/quaternius/ual/mannequin.glb` and its `.import` |
| `assets/character/directional.tres` | `assets/third_party/100style/neutral/directional.tres` |
| `shaders/`, `icon.svg`, `tuning.tres` | Keep their current locations. |

## Decisions for review

| Decision | Proposed default; alternative/implication |
| --- | --- |
| Does a hat fly off? | Stay attached through ragdoll/get-up. Flying loose hats require separate visual lifetime/physics/RNG decisions and testing; no hidden implementation in AL. |
| Do bots wear hats? | Allow explicitly selected hats in the existing lobby bot appearance editor; no automatic/random assignment; offline Lab bots remain bare. Restricting hats to humans is also straightforward at the selector boundary. |
| Offline human cosmetics | Honor the saved hat in single player/Lab, preserving their current body colors. Alternatively limit first use to network/picker actors; call out the differing contexts in owner instructions. |
| Budget and materials | Start with 5k triangles, 2 surfaces, 3×1K textures/16 MiB decoded, opaque/cutout; `Tint_Player` opt-in. These limits need owner approval before hard enforcement on their art; measure a populated 16-actor scene before claiming performance. |
| Author/terms/source labels | Owner supplies public author name, use/redistribution terms for owner art, source-project labels/revisions, and missing palette provenance. Repo has no general LICENSE; “Project's own” is ownership credit, not a grant we can invent. |
| Character scope | Canonical mannequin rig only now; no new character manifest/asset. First real owner character triggers V1 binding/retarget/material review and its own estimate. |

Estimated phase 2: 5–7 focused engineering days for validator/harness, mechanical migration, empty catalog and
default binding, hat attachment/picker integration, packaged/network/visual proof and docs. Allow 1–2 additional
days for independent review/fixes and settings-package integration. This excludes art creation, first-character
retargeting, flying hats and native Windows/macOS execution. No deadline or approval by elapsed time is implied.

Compatibility: this phase is documentation only. Proposed hat IDs use the existing slot and safe older-client
fallback; no Protocol.Version/AppearanceCodec change. A pure move must preserve world model keys, order, geometry,
shapes, material/texture values and RNG. New/removed tuners, changed physical character binding, or changed shared
construction would require a separately classified minor release; stop and report if any becomes necessary.

## Inventory at the phase 1 baseline

There are **259 files** under assets: **115 GLBs, 12 PNGs, 127 `.import` sidecars, one AnimationLibrary and four
License.txt files**. Disk inventory equals git inventory, with no extra untracked files. No hats exist. The tables
below enumerate every primary file and its matching sidecar explicitly by the `+ .import` rule: a row marked
`+ .import` covers both the displayed filename and that full filename with `.import` appended in the same folder.
Every such sidecar is Godot-generated import metadata, authored import settings plus stable UID in git, source of
truth beside that primary file; it maps source_file to `.godot/imported` cache targets, not an independent art
license. No asset is loaded by UID in checked-in code/scenes; UIDs here are identities to preserve during moves.
No `.tscn` directly references assets; scene nodes/scripts construct them. No `.blend`/audio/font asset exists here.

### Provenance keys (apply to every file in the corresponding rows)

| Key | What / author / license | Source of truth and limits |
| --- | --- | --- |
| Q | Quaternius UAL1/UAL2 Standard mannequin + 22 clips, CC0-1.0 | `tools/blender/build_character.py` merges UAL2 GLB and UAL1 Unreal FBX. Official source archives/hashes in animation-sources.md; archives not in repo. Derived GLB checked in; no owner-made character. |
| N | Ian Mason 100STYLE Neutral back/side retarget, CC-BY-4.0 | `tools/animation/retarget_100style.py` + current mannequin rest + three Neutral BVHs; exact BVH hashes and changes in animation-sources.md. Large source archive not in repo. |
| KC | Kenney Car Kit 3.1, CC0-1.0 | Upstream kit; checked-in subset, local License.txt identifies version. No archive hash/import acquisition script recorded. |
| KF | Kenney Furniture Kit 2.0, CC0-1.0 | Same upstream/subset limitation, local License.txt. |
| KN | Kenney Nature Kit 2.1, CC0-1.0 | Same upstream/subset limitation, local License.txt. |
| KS | Kenney City Kit Suburban 2.0, CC0-1.0 | Same upstream/subset limitation, local License.txt; GLBs reference the adjacent atlas. |
| KV | Three alternative suburban atlas PNGs, derivatives beside Kenney CC0 art | CREDITS/runtime identify house palettes; first commit contains them. No author, source recipe or revision recorded for the edits. Treat checked-in PNGs as current truth; provenance needs owner confirmation. Do not infer a new license grant. |
| P | Procedural suburb props, project's own (no declared public license) | `tools/blender/build_props.py` is the reproducible source; factor materials, no external textures. |
| R | Owner rifle-project PD4A1/rifle magazine and 2K textures, project's own (no declared public license) | Separate rifle repo's export/*.glb and textures/2k; `tools/blender/import_rifle.py` strips embedded textures/copies maps. Source commit not recorded. |
| T | Owner propane-tank-project tank/debris/maps/flipbook, project's own (no declared public license) | Separate propane-tank repo's Unreal FBX/texture exports; `tools/blender/export_tank.py` converts FBX/hulls only. Texture-copy recipe/source commit absent; checked-in PNGs are build inputs. |

Source revisions/licenses not present in the checkout are **unknown**, not verified upstream equivalence. AL phase 1
downloads no asset archives and runs no source generator. The present release bundles CREDITS and Godot notices, not an
audited full asset-license bundle. Reorganizing files cannot repair missing provenance by itself.

Import settings are authored behavior too: rifle animations use 60 fps and retain immutable tracks (the short
firing cycle is lost at 30 fps), the mannequin currently uses 30 fps with immutable-track removal, and the fireball
atlas currently has mipmaps disabled and lossless compression. Preserve those differences; new-hat defaults do
not authorize normalizing existing imports. Preserve GLB external atlas URIs and material/node names used by code.

### Reader keys and complete path construction map

All paths below are current baseline paths. Line numbers name load/key construction points; the per-file rows
select which family calls apply. Import sidecar `source_file`/`dest_files`/`path` references apply to each imported
file in addition to these runtime readers. GLB JSON texture dependencies are listed separately for atlases.

| Key | Every direct asset reader / dynamic path construction point |
| --- | --- |
| MAN | `src/Player/PlayerCharacter.cs:209`; `src/Dev/CharacterLab.cs:23`; `src/Dev/WorldLab.cs:62`; `src/Dev/AnimationClipReview.cs:57`. |
| DIR | `src/Player/CharacterAnimator.cs:19,76`. |
| TN | `src/Core/GameAssets.cs:16` (tank), `:17` (debris), `:197–199` (maps); `src/Dev/TankGeometry.cs:18` (tank). |
| FB | `src/Fx/ExplosionEffect.cs:60,115`. |
| RF | `src/Player/Rifle.cs:18` (model), `:361,365,367` (ORM, base color, normal); `src/Player/DroppedMagazine.cs:28` (magazine). |
| H | `src/World/Catalog.cs:38,47–48`: buildings a–u in that order. `src/Dev/WorldLab.cs:25` all buildings; `:35–36` b/e in composite. |
| PAL | `src/World/Catalog.cs:51–58`: colormap, variation-a/b/c in that order. |
| C | `src/World/Catalog.cs:39,62` via `GameCatalog.Resolve:38`; `PlanModels.CarModels:8–11` (weighted sedan/suv duplicates preserved), PickupModel truck `:13`; `PlanCatalogSnapshot.Keys:14`. |
| F | `src/World/Catalog.cs:41,68` via `GameCatalog.Resolve:40`; `PlanCatalogSnapshot.Keys:17–18`; `SuburbFurnishing.cs:174,184,229`. |
| NATURE | `src/World/Catalog.cs:40,64,66` via `GameCatalog.Resolve:39`; `PlanModels.TreeModels:29–33`, BushModels `:35`; `SuburbFurnishing.cs:149,501,536`; `Suburb.Warm:236–238`. Flowers in PlanModels `:37` are declared but not selected by current furnishing. |
| PROP | `src/World/Catalog.cs:42,70` via `GameCatalog.Resolve:37`; `PlanCatalogSnapshot.Keys:16–17`; `SuburbFurnishing.cs:120,132,167,191,197,212,221,238,434,444,478`; fences in `Suburb.cs:241–242,527–529`. |
| WLY | `src/Dev/WorldLab.cs:26` yard set; `:41` fence in composite. Catalog.Fence `src/World/Catalog.cs:60` constructs fence.glb but has no current caller. |
| WLC | `src/Dev/WorldLab.cs:27` all cars; `:37–38` sedan/truck in composite. |
| WLN | `src/Dev/WorldLab.cs:28` nature subset; `:43–44` tree_oak/tree_default in composite. |
| WLF | `src/Dev/WorldLab.cs:30` furniture subset; `:45–47` chair/table/trashcan in composite. |
| WLP | `src/Dev/WorldLab.cs:29` props except fences (production handles those). |
| reserve | No active code/scene model load found; still an imported shipping resource under all_resources. Keep in migration; explicit future cleanup only. |

Plan-key consumers also include `SuburbFurnishing.AddProp`/`GameCatalog.Resolve`, `Suburb.Warm`/`BuildProp`,
`src/Dev/MenuLifetimeTest.cs:84–89` (C/F/PROP/NATURE), `BotNavigationCourse.cs:99,103` (wheelie_bin/sedan),
and `src/Dev/UnitTests/ContractPlacementTests.cs:84` (model-key classification). The placement tests use the same
catalog prefixes, not other resource paths. TankLedger/ClientWorldReplica/ServerRun and generator tests classify `car:`/`prop:` keys but do not
load another asset path. Preserve those plan keys. Generic `GameAssets.Instantiate` at `GameAssets.cs:59` loads
the paths above, not additional assets. No UID-based art reference was found outside import metadata.


### `assets/character/`

| File(s) | What / provenance | Import UID | Readers |
| --- | --- | --- | --- |
| `directional.tres` | Three directional animation cycles; N | `—` | DIR |
| `mannequin.glb` + `.import` | Skinned mannequin + clips; Q | `uid://1mu6hy7505hm` | MAN |

### `assets/kenney/cars/`

| File(s) | What / provenance | Import UID | Readers |
| --- | --- | --- | --- |
| `License.txt` | Upstream CC0 notice; KC | `—` | Human attribution; not a ResourceLoader dependency |
| `delivery.glb` + `.import` | delivery vehicle; KC | `uid://drdadjrmrkf8b` | WLC |
| `hatchback-sports.glb` + `.import` | hatchback-sports vehicle; KC | `uid://db325c3kn1rua` | C, WLC |
| `police.glb` + `.import` | police vehicle; KC | `uid://4lxpo0ukyrct` | WLC |
| `sedan-sports.glb` + `.import` | sedan-sports vehicle; KC | `uid://hi8m86mo84hv` | C, WLC |
| `sedan.glb` + `.import` | sedan vehicle; KC | `uid://bet8wdjamyp0a` | C, WLC |
| `suv-luxury.glb` + `.import` | suv-luxury vehicle; KC | `uid://dga0q1yu1pnhf` | C, WLC |
| `suv.glb` + `.import` | suv vehicle; KC | `uid://gbvso3ayebtq` | C, WLC |
| `taxi.glb` + `.import` | taxi vehicle; KC | `uid://c2o6ahikraw5o` | WLC |
| `truck-flat.glb` + `.import` | truck-flat vehicle; KC | `uid://be45c7716v8cv` | WLC |
| `truck.glb` + `.import` | truck vehicle; KC | `uid://do3kievretc5p` | C, WLC |
| `van.glb` + `.import` | van vehicle; KC | `uid://en6vsjlo1j4` | C, WLC |

### `assets/kenney/cars/Textures/`

| File(s) | What / provenance | Import UID | Readers |
| --- | --- | --- | --- |
| `colormap.png` + `.import` | 512×512 color atlas; KC | `uid://b56wyy2hh26i5` | All 11 car GLBs: JSON images[0].uri |

### `assets/kenney/furniture/`

| File(s) | What / provenance | Import UID | Readers |
| --- | --- | --- | --- |
| `License.txt` | Upstream CC0 notice; KF | `—` | Human attribution; not a ResourceLoader dependency |
| `bench.glb` + `.import` | bench prop; KF | `uid://c0cvt70hao0yh` | WLF |
| `benchCushion.glb` + `.import` | benchCushion prop; KF | `uid://bx6v0pc8fc6sc` | reserve |
| `cardboardBoxClosed.glb` + `.import` | cardboardBoxClosed prop; KF | `uid://cchwd07l4yt5o` | WLF |
| `cardboardBoxOpen.glb` + `.import` | cardboardBoxOpen prop; KF | `uid://vu8p3jaaf2y` | reserve |
| `chair.glb` + `.import` | chair prop; KF | `uid://bpp3mx3u4ug5j` | F, WLF |
| `chairCushion.glb` + `.import` | chairCushion prop; KF | `uid://c5oyolngw7gpg` | F, WLF |
| `loungeChair.glb` + `.import` | loungeChair prop; KF | `uid://8cw5px8dobij` | F, WLF |
| `loungeChairRelax.glb` + `.import` | loungeChairRelax prop; KF | `uid://be65mel1p58pe` | F, WLF |
| `plantSmall1.glb` + `.import` | plantSmall1 prop; KF | `uid://bqjy7ngi7eg6s` | reserve |
| `plantSmall2.glb` + `.import` | plantSmall2 prop; KF | `uid://dug7j23u8ietw` | reserve |
| `plantSmall3.glb` + `.import` | plantSmall3 prop; KF | `uid://ck87tv3j5soqn` | reserve |
| `pottedPlant.glb` + `.import` | pottedPlant prop; KF | `uid://dyda08s2ckck5` | WLF |
| `sideTable.glb` + `.import` | sideTable prop; KF | `uid://bndco40bnekd0` | WLF |
| `table.glb` + `.import` | table prop; KF | `uid://ddda15c2467wx` | F, WLF |
| `tableRound.glb` + `.import` | tableRound prop; KF | `uid://dabjqfsn3rc0q` | F, WLF |
| `trashcan.glb` + `.import` | trashcan prop; KF | `uid://b5vbjs0awsrkd` | WLF |

### `assets/kenney/nature/`

| File(s) | What / provenance | Import UID | Readers |
| --- | --- | --- | --- |
| `License.txt` | Upstream CC0 notice; KN | `—` | Human attribution; not a ResourceLoader dependency |
| `flower_purpleA.glb` + `.import` | flower_purpleA vegetation/prop; KN | `uid://dud1uodhh5yf5` | reserve; PlanModels.FlowerModels declaration only |
| `flower_purpleB.glb` + `.import` | flower_purpleB vegetation/prop; KN | `uid://duvlr3wolgcxl` | reserve; PlanModels.FlowerModels declaration only |
| `flower_redA.glb` + `.import` | flower_redA vegetation/prop; KN | `uid://c58b3jfypf6gr` | WLN; PlanModels.FlowerModels declaration only |
| `flower_redB.glb` + `.import` | flower_redB vegetation/prop; KN | `uid://djoeb02i15iky` | reserve; PlanModels.FlowerModels declaration only |
| `flower_yellowA.glb` + `.import` | flower_yellowA vegetation/prop; KN | `uid://bxt55ybacm17q` | reserve; PlanModels.FlowerModels declaration only |
| `flower_yellowB.glb` + `.import` | flower_yellowB vegetation/prop; KN | `uid://cc4gxkcle725x` | reserve; PlanModels.FlowerModels declaration only |
| `grass.glb` + `.import` | grass vegetation/prop; KN | `uid://cpx71te40mlig` | reserve |
| `grass_large.glb` + `.import` | grass_large vegetation/prop; KN | `uid://dtnxbj5lucj6q` | reserve |
| `log.glb` + `.import` | log vegetation/prop; KN | `uid://woe6wthak2ff` | reserve |
| `plant_bush.glb` + `.import` | plant_bush vegetation/prop; KN | `uid://c7gpkle84vwy6` | NATURE, WLN |
| `plant_bushDetailed.glb` + `.import` | plant_bushDetailed vegetation/prop; KN | `uid://bp6ev1u0b5r7p` | NATURE, WLN |
| `plant_bushLarge.glb` + `.import` | plant_bushLarge vegetation/prop; KN | `uid://kvfehfx8fo1l` | NATURE, WLN |
| `plant_bushSmall.glb` + `.import` | plant_bushSmall vegetation/prop; KN | `uid://ka46a7648i7t` | NATURE |
| `plant_bushTriangle.glb` + `.import` | plant_bushTriangle vegetation/prop; KN | `uid://cmjpiid3tgt8c` | NATURE |
| `rock_smallA.glb` + `.import` | rock_smallA vegetation/prop; KN | `uid://bwax3mbys21nk` | WLN |
| `rock_smallB.glb` + `.import` | rock_smallB vegetation/prop; KN | `uid://ut52ay65oqoj` | reserve |
| `rock_smallC.glb` + `.import` | rock_smallC vegetation/prop; KN | `uid://oyy5dbwlo6d` | reserve |
| `stump_round.glb` + `.import` | stump_round vegetation/prop; KN | `uid://tf4cnqt82rwn` | reserve |
| `tree_cone.glb` + `.import` | tree_cone vegetation/prop; KN | `uid://s5ep0mnh5jsg` | WLN |
| `tree_default.glb` + `.import` | tree_default vegetation/prop; KN | `uid://ipxad3p6bgc3` | NATURE, WLN |
| `tree_default_dark.glb` + `.import` | tree_default_dark vegetation/prop; KN | `uid://caesrbsvqmmgv` | NATURE |
| `tree_detailed.glb` + `.import` | tree_detailed vegetation/prop; KN | `uid://b8qw8gaiej7ow` | NATURE, WLN |
| `tree_detailed_fall.glb` + `.import` | tree_detailed_fall vegetation/prop; KN | `uid://dmnv70g3tmhpx` | reserve |
| `tree_fat.glb` + `.import` | tree_fat vegetation/prop; KN | `uid://dskxr3c16v58y` | NATURE, WLN |
| `tree_oak.glb` + `.import` | tree_oak vegetation/prop; KN | `uid://jx1svn0i1pnq` | NATURE, WLN |
| `tree_oak_dark.glb` + `.import` | tree_oak_dark vegetation/prop; KN | `uid://kq5uh1mhnf6s` | NATURE |
| `tree_simple.glb` + `.import` | tree_simple vegetation/prop; KN | `uid://drmarwqgvyxky` | NATURE, WLN |
| `tree_small.glb` + `.import` | tree_small vegetation/prop; KN | `uid://bkmavr4p6466l` | WLN |
| `tree_tall.glb` + `.import` | tree_tall vegetation/prop; KN | `uid://j2vnwk87eeqt` | NATURE, WLN |

### `assets/kenney/suburban/`

| File(s) | What / provenance | Import UID | Readers |
| --- | --- | --- | --- |
| `License.txt` | Upstream CC0 notice; KS | `—` | Human attribution; not a ResourceLoader dependency |
| `building-type-a.glb` + `.import` | House a; KS | `uid://bj24khqrury0n` | H |
| `building-type-b.glb` + `.import` | House b; KS | `uid://crcqpmliyeklk` | H |
| `building-type-c.glb` + `.import` | House c; KS | `uid://csct3bptwtcqq` | H |
| `building-type-d.glb` + `.import` | House d; KS | `uid://dqnjgaty3a313` | H |
| `building-type-e.glb` + `.import` | House e; KS | `uid://cx15tyrx2twv4` | H |
| `building-type-f.glb` + `.import` | House f; KS | `uid://bt0uhsjt6qqvd` | H |
| `building-type-g.glb` + `.import` | House g; KS | `uid://wxb2i2vy8fb0` | H |
| `building-type-h.glb` + `.import` | House h; KS | `uid://fsnto8anv2dw` | H |
| `building-type-i.glb` + `.import` | House i; KS | `uid://c20en7dr6pfwt` | H |
| `building-type-j.glb` + `.import` | House j; KS | `uid://p2rxpggkuejd` | H |
| `building-type-k.glb` + `.import` | House k; KS | `uid://chq1uykwj7moj` | H |
| `building-type-l.glb` + `.import` | House l; KS | `uid://c3wjtj0kr5muc` | H |
| `building-type-m.glb` + `.import` | House m; KS | `uid://cofqm3k0x0m63` | H |
| `building-type-n.glb` + `.import` | House n; KS | `uid://y3lv7j0bwcir` | H |
| `building-type-o.glb` + `.import` | House o; KS | `uid://0krgex1louxf` | H |
| `building-type-p.glb` + `.import` | House p; KS | `uid://dvlxxutsygf2m` | H |
| `building-type-q.glb` + `.import` | House q; KS | `uid://ettyhlx2p82w` | H |
| `building-type-r.glb` + `.import` | House r; KS | `uid://b0q44p3vgoryo` | H |
| `building-type-s.glb` + `.import` | House s; KS | `uid://c7krn4mhstagt` | H |
| `building-type-t.glb` + `.import` | House t; KS | `uid://8q3858ydrme6` | H |
| `building-type-u.glb` + `.import` | House u; KS | `uid://dq420kwvsvoyh` | H |
| `driveway-long.glb` + `.import` | driveway-long yard part; KS | `uid://co5eomb6h6kpx` | WLY |
| `driveway-short.glb` + `.import` | driveway-short yard part; KS | `uid://bk0nbp2ldrwnp` | reserve |
| `fence-1x2.glb` + `.import` | fence-1x2 yard part; KS | `uid://qtold04lowge` | reserve |
| `fence-1x3.glb` + `.import` | fence-1x3 yard part; KS | `uid://dxr4jh22bgrau` | WLY |
| `fence-1x4.glb` + `.import` | fence-1x4 yard part; KS | `uid://c6ja7t3y7mla8` | reserve |
| `fence-2x2.glb` + `.import` | fence-2x2 yard part; KS | `uid://e6bdw62xr8a2` | reserve |
| `fence-2x3.glb` + `.import` | fence-2x3 yard part; KS | `uid://sinsnicx30k5` | reserve |
| `fence-3x2.glb` + `.import` | fence-3x2 yard part; KS | `uid://dpihb1tn1uvkt` | reserve |
| `fence-3x3.glb` + `.import` | fence-3x3 yard part; KS | `uid://c7f01mpvyb78x` | reserve |
| `fence-low.glb` + `.import` | fence-low yard part; KS | `uid://cp2ulpixh0iia` | WLY |
| `fence.glb` + `.import` | fence yard part; KS | `uid://crxg4e45g6dk7` | WLY |
| `path-long.glb` + `.import` | path-long yard part; KS | `uid://xv3ku5p0gfjp` | WLY |
| `path-short.glb` + `.import` | path-short yard part; KS | `uid://ey1a1ecc5eer` | reserve |
| `path-stones-long.glb` + `.import` | path-stones-long yard part; KS | `uid://cnf2tgdmn68re` | reserve |
| `path-stones-messy.glb` + `.import` | path-stones-messy yard part; KS | `uid://boc2bm7sbs38l` | reserve |
| `path-stones-short.glb` + `.import` | path-stones-short yard part; KS | `uid://gd7akeeg1e5f` | reserve |
| `planter.glb` + `.import` | planter yard part; KS | `uid://fu6211xy4cvr` | WLY |
| `tree-large.glb` + `.import` | tree-large yard part; KS | `uid://dcl8y7tr2pmcu` | WLY |
| `tree-small.glb` + `.import` | tree-small yard part; KS | `uid://b65it3v3wr3ue` | WLY |

### `assets/kenney/suburban/Textures/`

| File(s) | What / provenance | Import UID | Readers |
| --- | --- | --- | --- |
| `colormap.png` + `.import` | 512×512 color atlas; KS | `uid://l3b0ixk7cfsu` | PAL; all 40 suburban GLBs: JSON images[0].uri |
| `variation-a.png` + `.import` | 512×512 alternate house atlas; KV | `uid://dadplbrfanmmn` | PAL |
| `variation-b.png` + `.import` | 512×512 alternate house atlas; KV | `uid://dio52d1gcx6dg` | PAL |
| `variation-c.png` + `.import` | 512×512 alternate house atlas; KV | `uid://3px85sja6djb` | PAL |

### `assets/props/`

| File(s) | What / provenance | Import UID | Readers |
| --- | --- | --- | --- |
| `cooler.glb` + `.import` | cooler; P | `uid://crw6j5u04vcx` | PROP, WLP |
| `fence_picket.glb` + `.import` | fence picket; P | `uid://cdy3o015qt06p` | PROP |
| `fence_post.glb` + `.import` | fence post; P | `uid://cve3adtwt4b5x` | PROP |
| `fence_privacy.glb` + `.import` | fence privacy; P | `uid://ddmam0j4p2jcm` | PROP |
| `fire_hydrant.glb` + `.import` | fire hydrant; P | `uid://lfqq4lab3o48` | PROP, WLP |
| `gas_grill.glb` + `.import` | gas grill; P | `uid://bk5yfofoydt4v` | PROP, WLP |
| `kiddie_pool.glb` + `.import` | kiddie pool; P | `uid://0vnv25pf2t4q` | PROP, WLP |
| `mailbox.glb` + `.import` | mailbox; P | `uid://udltpt4owfkx` | PROP, WLP |
| `patio_heater.glb` + `.import` | patio heater; P | `uid://c2cgcojsxd553` | PROP, WLP |
| `patio_umbrella.glb` + `.import` | patio umbrella; P | `uid://bkt0xqtjlokvb` | PROP, WLP |
| `shed.glb` + `.import` | shed; P | `uid://b5omc6t8et8lh` | PROP, WLP |
| `stop_sign.glb` + `.import` | stop sign; P | `uid://ux4o1861pjab` | PROP, WLP |
| `street_lamp.glb` + `.import` | street lamp; P | `uid://di4by43oxuc3y` | PROP, WLP |
| `wheelie_bin.glb` + `.import` | wheelie bin; P | `uid://cui0t52tc1dai` | PROP, WLP |

### `assets/rifle/`

| File(s) | What / provenance | Import UID | Readers |
| --- | --- | --- | --- |
| `T_Rifle_BaseColor.png` + `.import` | 2K BaseColor map; R | `uid://dtsfoby811lii` | RF |
| `T_Rifle_Normal.png` + `.import` | 2K Normal map; R | `uid://bcq257f10h7nc` | RF |
| `T_Rifle_ORM.png` + `.import` | 2K ORM map; R | `uid://dvg7tkhej1sui` | RF |
| `magazine.glb` + `.import` | Dropped magazine mesh; R | `uid://cxdbnivujwfy7` | RF |
| `rifle.glb` + `.import` | Skinned rifle + sockets/clips; R | `uid://btcyx7dtx0cub` | RF |

### `assets/tank/`

| File(s) | What / provenance | Import UID | Readers |
| --- | --- | --- | --- |
| `T_Fireball_SubUV_8x8.png` + `.import` | 1280×2048 fireball atlas; T | `uid://bm6ksdqkhkrdb` | FB |
| `T_PropaneTank_BaseColor.png` + `.import` | 4K BaseColor map; T | `uid://d7jihnwyiee1` | TN |
| `T_PropaneTank_Normal.png` + `.import` | 4K Normal map; T | `uid://dafv2co8xelaw` | TN |
| `T_PropaneTank_ORM.png` + `.import` | 4K ORM map; T | `uid://chjfrax57y8i3` | TN |
| `debris.glb` + `.import` | 25 shell fragments + hulls; T | `uid://b1axdwposawgm` | TN |
| `tank.glb` + `.import` | Tank and collision mesh; T | `uid://d28h20gmsouxg` | TN |

### Loaded assets outside `assets/`

All following shader sources are project-authored code; no separate public license is declared.
Each row includes the same-named `.uid` sidecar (21 sources + 21 sidecars), whose identity is shown.
Their source of truth is the checked-in shader itself, with all direct load/include references listed.
These remain in place; they are imported/shipped resources under all_resources.

| Shader under `shaders/` (+ `.uid`) | UID | All path readers/includes |
| --- | --- | --- |
| `appearance_rim.gdshader` | `uid://hiks5nc8r1rf` | `src/Player/PlayerCharacter.Presentation.cs:170` |
| `debris_interior.gdshader` | `uid://cm1m4h6jkfla2` | `src/Core/GameAssets.cs:51` |
| `dissolve_front.gdshader` | `uid://b80keluc6w1rb` | `src/Lab/DissolveArena.cs:86` |
| `fx_common.gdshaderinc` | `uid://jq5emlxpjk08` | `shaders/fx_fire.gdshader:6`, `shaders/fx_flipbook.gdshader:5`, `shaders/fx_glow.gdshader:6`, `shaders/fx_smoke.gdshader:5` |
| `fx_fire.gdshader` | `uid://v70eoqpfvn46` | `src/Fx/FxLibrary.cs:41`, `src/Fx/FxLibrary.cs:47` |
| `fx_flipbook.gdshader` | `uid://d1eo00gksjik4` | `src/Fx/ExplosionEffect.cs:116` |
| `fx_glow.gdshader` | `uid://cldoank6jhug4` | `src/Fx/ExplosionEffect.cs:104`, `src/Player/Rifle.cs:153`, `src/Tank/VentJet.cs:63` |
| `fx_ground_ring.gdshader` | `uid://dygucrugq8a5n` | `src/Fx/ExplosionEffect.cs:147` |
| `fx_jet.gdshader` | `uid://bh4mh8b8m0j6x` | `src/Tank/VentJet.cs:75` |
| `fx_shockwave.gdshader` | `uid://pheirxpmreyn` | `src/Fx/ExplosionEffect.cs:137` |
| `fx_smoke.gdshader` | `uid://duep3mkuhh5vk` | `src/Dev/LabScenarios/MeasureScenario.cs:64`, `src/Fx/FxLibrary.cs:58`, `src/Fx/FxLibrary.cs:64`, `src/Fx/FxLibrary.cs:73` |
| `fx_spark.gdshader` | `uid://b5aj1wj3g8inb` | `src/Fx/FxLibrary.cs:81` |
| `fx_tracer.gdshader` | `uid://44umosd5stv2` | `src/Fx/Tracer.cs:21` |
| `ground.gdshader` | `uid://bihuk07mujv0x` | `src/World/Suburb.cs:843` |
| `pickup.gdshader` | `uid://cyj4ji4vo260e` | `src/World/AmmoPickup.cs:39` |
| `pickup_disc.gdshader` | `uid://b1blxa3cnqr8i` | `src/World/AmmoPickup.cs:53` |
| `reveal.gdshaderinc` | `uid://cjspq5oyl2nks` | `shaders/debris_interior.gdshader:4`, `shaders/ground.gdshader:7`, `shaders/pickup.gdshader:5`, `shaders/pickup_disc.gdshader:5`, `shaders/tank.gdshader:5`, `shaders/world_lit.gdshader:5` |
| `tank.gdshader` | `uid://bqfvaqyrw863e` | `src/Core/GameAssets.cs:196` |
| `void_floor.gdshader` | `uid://bvq48owua7qlf` | `src/World/VoidEnvironment.cs:114` |
| `void_sky.gdshader` | `uid://cq7cesxnjmott` | `src/World/VoidEnvironment.cs:43` |
| `world_lit.gdshader` | `uid://x50atkr704yp` | `src/Dev/ShaderCapacityTests.cs:18`, `src/World/WorldMaterials.cs:16` |

`icon.svg` + `icon.svg.import` (`uid://cy0rjf4cref8f`): checked-in Godot-face project icon. Its appearance suggests
the default project template, not owner-authored art; the copied revision and precise icon-license mapping are
not recorded. Godot's [project creator](https://raw.githubusercontent.com/godotengine/godot/4.7.2-stable/editor/project_manager/project_dialog.cpp)
writes a default SVG; its [copyright inventory](https://raw.githubusercontent.com/godotengine/godot/4.7.2-stable/COPYRIGHT.txt)
distinguishes MIT engine source from CC-BY-4.0 logo art, and the [press kit](https://godotengine.org/press/) identifies
the logo license. Exact source matching/attribution remains an inventory follow-up; do not label this project's
own. Referenced by `project.godot:18` and the Windows `export_presets.cfg` application/icon setting. Keep both;
replacing project branding is an owner decision outside AL.

`tuning.tres`: checked-in Tuning Resource configuration, script dependency `src/Core/Tuning.cs`;
loaded by `src/Core/Tuning.cs` through ProjectPath/FilePath and saved editor/user overrides. It is runtime
configuration, not art; preserve its bytes, schema/order and current path. `.godot` imported resources are cache
products, not additional authoring sources. `build_id.txt` is release-generated metadata read by BuildInfo,
not art. UI uses `UiStyle.BoldFont` SystemFont name fallback and Godot defaults, with no bundled font file or
font redistribution. FxLibrary creates noise, particle meshes, curve, scorch, bullet-hole and soft-dot textures
in memory; world/actor/pickup UI geometry and materials are also code-generated, not missing asset files.
Their source is the owning C# code; AL does not move their state or change constants.

Dev `AnimationClipReview` optionally imports a user-specified external GLB via GLTFDocument, and
animation tooling reads approved external UAL/BVH sources. Those are command inputs, not checked-in or shipped
asset paths. No other current disk-backed art loader was found in the source/scene/resource search. Docs media
are excluded by `docs/.gdignore`; their complete inventory is documentation rather than runtime art.

`scenes/main.tscn` is the shipped composition scene (project-owned code, no separate license), referenced by
`project.godot` and constructed from `src/Main.cs`; it contains no art resource or scene UID. The ShutdownGuard
autoload is a C# script. Other `.tscn` files under `scenes/dev` are script-only harness entry points, documented
in development.md and excluded from release. They are executable test composition, not a second art library.

## Phase 1 verification record

Only this draft is changed. No model, mesh, texture, scene, manifest, gameplay, wire, tuner, menu or settings edit;
no moved state, retired owner or removed dependency. No baseline performance claim is made.

All commands ran in the assigned worktree. Scratch/logs/captures are ignored worktree-local `build/` files.
`TMPDIR="$PWD/build/al-tmp"` was supplied to the test/render commands. The inspected baseline is unchanged code.

| Verification | Result |
| --- | --- |
| `make setup` | Passed asset import, Debug build (0 warnings/errors), strict post-build import. |
| `make build` | Passed, 0 warnings, 0 errors. Log: `build/al-phase1/make-build.log`. |
| `make test` | Passed, 753 tests / 81 discovered unit suites / 0 failures, behavior and real-device input probes, bounded generator/forced formations; fresh-process agreement for all four world formations and five runs × twelve segment plans. Log: `build/al-phase1/make-test.log`. |
| `python3 -B tools/dev/check_links.py` and `git diff --cached --check` | Passed; all relative Markdown links resolve and no whitespace errors. |
| Python standard-library inventory assertions | On-disk/tracked inventory agrees; draft rows cover all 259 files exactly once (including sidecars); all 127 import UIDs match; named source-file references exist. Scratch inventory: `build/al-phase1/inventory.json`. GLB JSON/PNG headers inspected without reimporting or running generators. |
| `godot-mono --fixed-fps 60 --resolution 1280x720 res://scenes/dev/character_lab.tscn -- --info --anims=Idle,Walk@0.4 --capture-dir="$PWD/build/al-character-front"` | Passed; two captures. Reviewed `00_Idle_0.00.png`: raw mannequin/materials, bare head; native log supplies rest bone/socket measurements. This raw preview intentionally lacks gameplay material overrides/rifle. |
| `godot-mono --fixed-fps 60 --resolution 1280x720 res://scenes/dev/world_lab.tscn -- --set=composite --capture-dir="$PWD/build/al-world-composite"` | Passed. Reviewed `composite_7.5.png`: houses, cars, atlas colors, kit trees/furniture, tank and raw mannequin. Fence occludes the dummy in this existing view; it is not evidence of character clearance. |
| `godot-mono --fixed-fps 60 --resolution 1280x720 res://scenes/dev/player_range.tscn -- --scenario=ragdoll --view=side --assert-grip --assert-motion --capture-dir="$PWD/build/al-player-ragdoll"` | Passed; grip 365 frames, maximum wrist residual 0.008132 m, penetration 0; motion 595 frames. Reviewed sampled `rag_00`, `rag_04`, `rag_12`, `rag_19`: throw, inversion, get-up and recovered stance with rifle/body materials. The existing camera crops portions of the tumble; these samples do not prove continuous visual acceptance. |

Proposed validator, catalog, preview and export integration are not implemented or verified in phase 1. No AL
export, new hat, populated shipping catalog, before/after migration, native Windows/macOS or performance proof
is claimed. Their acceptance steps above remain work for phase 2 and the first owner-supplied item respectively.
