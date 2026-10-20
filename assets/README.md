# Assets

Only runtime exports live here. Keep Blender projects, raw captures and intermediate files in the owner's source
project or asset-source store outside `res://`. A checkout builds and plays without Blender.

| Folder | Who edits it |
| --- | --- |
| `authored/` | The owner's exports. `tank/` and `rifle/` come from their separate Blender projects. New hats go in `hats/<id>/`; future characters will go in `characters/<id>/` after their rig is reviewed. |
| `generated/props/` | Output from `tools/blender/build_props.py`; edit the script and regenerate. |
| `third_party/` | Kenney kits, the Quaternius default mannequin, and 100STYLE directional retargets. Keep their original names, license notices and attribution. These are not owner-authored art. |
| `library.tres` | Explicit list of shipped hats and the project's default author/terms. Empty is valid; there are no bundled hats. |

`tools/assets/inventory.json` accounts for the existing files, provenance, licenses and retained kit reserves.
[The contract](../docs/assets.md) covers IDs, character bindings, validation and export.

## Add a hat in five steps

1. Choose a permanent lowercase ID: 1–64 letters/digits/underscores/hyphens, starting with a letter or digit.
   Never rename or reuse an ID after release. Make `assets/authored/hats/<id>/`.
2. In Blender 5.1, use Metric units, **1 unit = 1 meter**. Reference the existing mannequin at unit scale; do not
   export it. Align the hat to the head socket: origin at the Head bone's rest origin (not the crown), with axes
   aligned to the mannequin's model axes in rest. Blender Z is up and -Y is forward. The preview prints the socket
   frame; the imported reference is approximately (0, 1.569, 0.005) meters at that origin. After fitting, clear parenting (Keep Transform), then move the
   whole hat so that socket origin is at world (0, 0, 0); set object origins there. Export only the hat, with
   zero object location/rotation and unit scale. Keeping the reference head height in the exported transform
   would apply that offset twice.
3. Apply rotation/scale and mesh modifiers. Select only the hat, then export **glTF 2.0 → GLB**, **Selected Objects**,
   **+Y Up**, **Normals**, **Tangents**, **Materials → Export**, with **Animations**, **Cameras** and **Lights** off.
   Turn off Draco/mesh compression and material extensions. Use PNG source images (Automatic image export);
   JPEG/WebP images are outside the initial contract.
   Do not export an armature, skin, collision or special glTF extensions. Avoid Godot import hints such as `-col`, `_rigid`,
   `$col`, `-vehicle` or `-noimp` on objects/meshes. Use standard metallic/roughness PBR,
   opaque or alpha-cutout. Prefer embedded PNG textures; external PNGs must remain inside the item's folder.
   Base color is sRGB; normal/ORM are data. Name a material `Tint_Player` only if it should multiply by player color.
   Write `model.glb` in the item folder.
4. Register it (replace the example ID/name with your own):

   ```sh
   make hat ID=your_id NAME="Your display name"
   # Or copy an existing self-contained GLB from a supplied path:
   make hat ID=your_id NAME="Your display name" MODEL="path/to/model.glb"
   ```

   This writes `item.tres`, registers it once in ID order, and validates. Repeating the command updates the name
   while preserving metadata overrides. It never replaces an existing model with another file. Authored items
   inherit the author and all-rights-reserved terms in `library.tres`; optional `AUTHOR`, `LICENSE` and `SOURCE`
   overrides are supported. Third-party items require explicit author/license/source and an inventory license entry.
5. Preview and inspect the PNGs/report, then commit the GLB, item, library, texture dependencies and generated
   `.import` metadata together:

   ```sh
   make assets-preview CAPTURE_DIR="$PWD/build/my-hat-preview"
   # Inspect a given worktree-local file without registering it:
   make assets-preview ASSET="assets/authored/hats/your_id/model.glb" CAPTURE_DIR="$PWD/build/my-file-preview"
   make assets-check
   ```

The preview imports/builds first, then shows None and every hat from five angles and during movement, aim, reload,
melee, ragdoll and get-up. With an empty library it shows the base character. Review clearance yourself; numbers do
not judge fit. `build/` is ignored. The preview creates no hat or character asset.

Recommended triangle/surface/texture budgets only **warn with measurements**. Change them in
`tools/assets/budgets.json`. Missing/duplicate IDs, bad references, path escapes, unknown license identifiers,
unlicensed third-party entries and unregistered files **fail** validation and `make check`.

For now hats stay attached through throws/get-up and have no collision. Lobby bots may wear a chosen hat; Lab bots
stay bare. Offline humans use their saved hat with their current body color. The first new character needs the rig,
material, IK and ragdoll review in the contract; a humanoid GLB is not automatically interchangeable.
