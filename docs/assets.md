# Asset library contract

Runtime art is separated by provenance: `assets/authored` contains owner exports, `assets/generated` contains this
repository's procedural outputs, and `assets/third_party` retains upstream identity and licensing even for locally
processed derivatives. [The owner guide](../assets/README.md) gives the five-step hat workflow. Original Blender
sources, raw capture archives and intermediate output stay outside `res://`; no generator is needed to build/play.
Shaders remain source code in `shaders/`, and the root icon/tuning configuration stay at their existing paths.

`tools/assets/inventory.json` is the file/provenance/license allowlist, including unused kit reserves. Do not treat
an unused model as permission to delete it. Source acquisition revisions, local palette edits and the root icon's
exact copied revision still have inherited provenance gaps; record them honestly. [Animation sources](animation-sources.md)
records source hashes and retarget modifications; [CREDITS](../CREDITS.md) and bundled Kenney notices retain attribution.
Authored items inherit project authorship/all-rights-reserved terms from `assets/library.tres`. This is not a public
license grant. Third-party metadata cannot inherit those defaults.

## Identity and discovery

IDs are permanent lowercase ASCII `[a-z0-9][a-z0-9_-]{0,63}`, never a path, UID, label or list index. Never reuse or
rename a released ID; reserve retired identities in the inventory. `AppearanceCodec` retains its broader existing
wire grammar unchanged. Null Hat means None; null/unsupported ModelOrSkin means the code-named `mannequin` default.
Unknown IDs remain in the record for persistence/forwarding and render safe defaults. No asset transfer or arbitrary
user-path loader exists in production.

A future hat has one folder containing `model.glb`, `item.tres` and optional PNG dependencies. `HatDefinition` holds
ID/display name, optional authored provenance overrides and a typed PackedScene dependency. Third-party items must
provide author, license and source; license identifiers resolve through the inventory's license registry.
`make hat` scaffolds existing art, preserves overrides/resource UIDs, sorts registration and rolls back on errors.
It never authors a model. No sample hat/character or character manifest is supplied.

`ResourceLoader` loads the explicit `HatLibrary` in `assets/library.tres`; production never scans directories.
`HatCatalog` snapshots definitions into immutable ordered items. The shared catalog and scene resources are borrowed
on Godot's main thread for process lifetime; actors never dispose or mutate them. Tests inject catalogs of unsaved
Dev primitives without a process-global registration override.

## Hat geometry and attachment

Use Blender 5.1/glTF 2.0 GLB, meters, applied unit scale, static meshes and standard opaque/cutout PBR. Blender Z up/-Y
forward exports to model +Y up/+Z forward. Actor model rotation already converts that to actor -Z forward; do not
add another turn. Full export settings and file names are in the owner guide. Normals and ORM are data; albedo is
sRGB. No skin, animations, cameras, lights, collision, scripts or custom extensions in a hat.

The `head` socket is Head's global rest origin with model-aligned axes at rest. The bone-to-socket transform is
`HeadRest.Inverse × Transform(identity basis, HeadRest.origin)`. Hats are authored in the untouched reference
mannequin's model space, fitted on its head. The fixed authoring frame is identity axes at `(0, 1.568700552, 0.0050996924)` meters in imported Godot model space. It stays frozen
when adding a character; it is not measured from each actor. Attachment applies
`final Head pose × target bone-to-socket × inverse(fixed authoring frame) × imported root transform`.
The scaffold/import never rebakes this conversion. Attach-time conversion keeps the owner's GLB intact and gives
registered and raw-file previews one path; a future rig changes only the target socket. These frames are
rig geometry, not per-hat fit/effect tuners. A `BoneAttachment3D` with pose override disabled follows final animation,
physical bones, capture and get-up without another scheduler, animation modifier or simulation timer.

Each actor owns its attachment, hat subtree and `Tint_Player` material copies, releasing them on replacement/exit.
Shared materials stay immutable. Other hat materials retain their authored appearance. Hats join actor render layers
and mesh-detail bookkeeping. Hats have no collision, mass, hitboxes, blast registration, authority or gameplay RNG.
Changing color preserves all cosmetic slots; changing a hat preserves the existing body/rig and offline float color.

Network assembly and live lobby updates apply the full accepted AppearanceRecord. The existing codec/settings/wire
slots persist and replicate it, including frozen match profiles. AppearancePicker uses the same actor path and shows
a None/hat control only with a nonempty catalog; Cancel preserves the accepted record. Unknown saved slots survive
color edits. Lobby bots may receive explicit hats; no random assignment is made. Lab bots stay bare; offline humans
inherit the saved hat while preserving current body colors. Spectators see the same actor, lobby/stage use Match
actors, and 2D roster/results cards stay 2D. Raw model inspection tools intentionally remain bare.

## Current character binding and future characters

The existing mannequin remains the default code entry with no character item manifest. Its canonical binding is
65 joints at unit scale, Y-up/+Z-facing T-pose, with the same hierarchy/rest positions/axes as the shipped mannequin.
Required names include root/pelvis, spine_01..03, neck_01, case-sensitive Head; both clavicle/upperarm/lowerarm/hand,
thigh/calf/foot/ball chains; and index/middle/pinky/ring/thumb segments 01..03 with _l/_r suffixes. Keep leaf bones too.
Names alone do not establish compatibility: ragdoll capsules run along local +Y, hinges use local X, and RifleRig
uses absolute finger rests, arm lengths and calibrated wrist/shoulder/eye landmarks.

Imported base clips include Idle, Walk, Jog_Fwd, Sprint, Jump_Start, Jump_Land, Pistol_Idle, Hit_Chest and LayToIdle;
retain the other packaged clips. The separate directional library has Walk_Back/Walk_Left/Walk_Right. Preserve
in-place travel, stride touchdown/length calibration, get-up offset/timing and track paths. UAL1/UAL2 share a rest rig;
100STYLE retargeting transfers/calibrates rest frames and pelvis travel, not arbitrary humanoid retargeting.
[Locomotion](locomotion.md) and [rifle stance](motion.md) remain authoritative.

The existing controller capsule and 16 physical-bone ragdoll remain unchanged. Rifle sockets belong to the rifle
(Socket_Foregrip/Muzzle/Butt/Eye/MagWell); hands use hand_l/hand_r, and the eye is a tuned Head landmark. Current
mannequin surfaces containing Joint receive neutral joint material; its other surfaces receive body color/rim.
A future textured character must explicitly identify tintable surfaces instead of inheriting that blanket override.

The first owner character triggers architecture V1: review its canonical rig or new explicit binding, retargeting,
material tint, proportions, IK contacts and native ragdoll/get-up. Do not introduce a general rig framework or change
physics to make a new mesh fit. New gameplay/effect adjustments remain Tuning tuners and receive release
classification. The inherited 12/46 pending animation visual checks are not closed by this library work.

## Validation, preview and export

`make assets-check` runs the source inventory/manifest/GLB checks, their temporary-copy negative fixtures and native
resource validation. It runs through core tests and thus `make check`/CI using stock Python and Godot. It rejects
missing/duplicate/retired IDs, malformed references, path traversal/symlinks, unregistered/orphan files, missing or
unknown third-party license metadata and unsupported scene content. The inventory explicitly preserves old reserves
and known provenance gaps; new items receive no blanket orphan exemption. Budget limits live only in
`tools/assets/budgets.json` and warn with actual triangle/surface counts, texture dimensions and decoded RGBA+mip
estimates; they do not reject otherwise valid owner art or promise native performance.

`make assets-preview` imports/builds, then renders None and all hats (or a supplied GLB in memory) from five angles,
in motion and ragdolled/getting up, with screenshots/report under a worktree-local capture directory. The Dev
attachment tests assert final transforms and lifecycle coverage, per-instance tint, replacement/retirement,
unknown-ID fallback and unchanged collision/RNG state using an unsaved primitive. Test input intent is not proof of
device delivery; menu input tests additionally use Input.ParseInputEvent.

Keep all_resources export: C# world loaders use dynamic paths. Dev code/scenes and source/tool records stay excluded.
Packaged checks inspect native assemblies/PCKs, resolve resource .remap/.import targets and execute the untouched
Linux artifact outside the source project. The release smoke loads the shipped catalog and default bindings and
asserts the expected IDs; all three target inventories are required. Attribution texts are bundled beside the game.
The empty catalog proves native catalog/default/fallback loading, not a future populated item's serialized closure;
the first owner hat must pass the same packaged smoke. Linux execution and Windows/macOS inventories do not prove
native Windows/macOS behavior.

The migration preserved original model/texture/library bytes and import UIDs/parameters, updating paths and importer
cache targets only. World model keys/order, RNG, collision, packets and tuners remain unchanged. Hats use existing
Appearance slots with older-client fallback; Protocol.Version stays unchanged. Shared construction, physical rig,
message-shape or tuner-schema changes require separate minor-release review. [Verification](evidence/asset-library.md)
records commands, captures, limitations and inherited proof.
