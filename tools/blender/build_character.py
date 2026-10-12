"""Rebuild the mannequin from the recorded Quaternius and 100STYLE sources.

Run from the project root (Blender 5.2):
  blender -b --factory-startup --python-exit-code 1 --python tools/blender/build_character.py
Optional arguments after --: --sources ../assets-src --output assets/character/mannequin.glb
The shared source archive's SOURCES.md records URLs, licenses and checksums. Physics owns root travel.
"""
import argparse
import hashlib
import sys
from pathlib import Path

import bpy
from mathutils import Vector

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
from retarget_locomotion import bake_contacts, diagonal_actions, directional_actions, repair_contacts, turn_actions

UAL1_PATH = 'UAL1-Standard/Universal Animation Library[Standard]/Unreal-Godot/UAL1_Standard.glb'
UAL2_PATH = 'UAL2-Standard/Universal Animation Library 2[Standard]/Unreal-Godot/UAL2_Standard.glb'
SOURCE_HASHES = {
    UAL1_PATH: "69591853d817488edaa8fd9bf8fc1d821eaeaf789f8627b3cd23b41c4ed67997",
    UAL2_PATH: "8cee20ab1bc55130092447e810e26df22dd2803eccc54f52137a7d54d7ab88a8",
    "100style/Neutral_BW.bvh": "cfa225ff3a29a38d11ecd1b72fa0d917ca6c3dc3b24b3d09a4cb5c28ee9dc5b9",
    "100style/Neutral_SW.bvh": "07225ded6df11c4eb97a7b42e6121a9e985ae7e8d0f56f9ca971429f9c507ad9",
    "100style/Neutral_BR.bvh": "5661951058a5ec50e02a6708a5f9d3ccd09ff768051584cac9611aee8402ea13",
    "100style/Neutral_SR.bvh": "804046d4d29109515fa3f6268f85873bab1dc3d08b87dabbe644eddbf00aec71",
}

KEEP_UAL1 = [
    "Idle_Loop", "Walk_Loop", "Jog_Fwd_Loop", "Sprint_Loop", "Jump_Start", "Jump_Loop", "Jump_Land",
    "Pistol_Idle_Loop", "Pistol_Aim_Neutral", "Pistol_Aim_Up", "Pistol_Aim_Down", "Pistol_Shoot",
    "Hit_Chest", "Hit_Head", "Crouch_Idle_Loop", "Crouch_Fwd_Loop", "Roll", "Death01",
]
# Preserve the shipped one-shot timing across the Standard archive's later resampling.
UAL2_DURATION = {'LayToIdle': 1.2, 'Hit_Knockback': 2 / 3, 'Idle_FoldArms_Loop': 2, 'NinjaJump_Land': 1}


def import_library(path):
    objects = set(bpy.data.objects)
    actions = set(bpy.data.actions)
    bpy.ops.import_scene.gltf(filepath=str(path))
    new_objects = set(bpy.data.objects) - objects
    rig = next((o for o in new_objects if o.type == 'ARMATURE'))
    actions = {a.name.split('|')[-1].split('.')[0]: a for a in set(bpy.data.actions) - actions}
    return (rig, new_objects, actions)


def curves(action):
    return [f for layer in action.layers for strip in layer.strips for bag in strip.channelbags for f in bag.fcurves]


def main():
    project = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sources", type=Path, default=project.parent / "assets-src")
    parser.add_argument("--output", type=Path, default=project / "assets/character/mannequin.glb")
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
    source = args.sources.resolve()
    for name, checksum in SOURCE_HASHES.items():
        actual = hashlib.sha256((source / name).read_bytes()).hexdigest()
        if actual != checksum:
            raise ValueError(f'Source checksum mismatch: {name}: {actual}')
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.render.fps = 60
    rig, _, ual2 = import_library(source / UAL2_PATH)
    donor, objects, ual1 = import_library(source / UAL1_PATH)
    for bone in rig.data.bones:
        rest = donor.data.bones[bone.name].matrix_local
        assert max((abs(rest[i][j] - bone.matrix_local[i][j]) for i in range(4) for j in range(4))) < 1e-05, bone.name
    keep = [ual1[name] for name in KEEP_UAL1] + [ual2[name] for name in UAL2_DURATION]
    for name, seconds in UAL2_DURATION.items():
        a = ual2[name]
        start, end = a.frame_range
        for f in curves(a):
            for k in f.keyframe_points:
                for p in (k.co, k.handle_left, k.handle_right):
                    p.x = (p.x - start) * seconds * 60 / (end - start)
    for ob in objects:
        bpy.data.objects.remove(ob, do_unlink=True)
    rig.name = 'Mannequin'
    rig.animation_data_create()
    rig.animation_data.action = None
    for track in list(rig.animation_data.nla_tracks):
        rig.animation_data.nla_tracks.remove(track)
    sprint = bake_contacts(rig, ual1['Sprint_Loop'], Vector((0, 4.2, 0)), retime='run')
    keep[keep.index(ual1['Sprint_Loop'])] = sprint
    jog = repair_contacts(rig, ual1['Jog_Fwd_Loop'], 5)
    keep[keep.index(ual1['Jog_Fwd_Loop'])] = jog
    walk = repair_contacts(rig, ual1['Walk_Loop'], 1.34)
    keep[keep.index(ual1['Walk_Loop'])] = walk
    keep += directional_actions(rig, source / '100style', {'walk': walk, 'run': jog})
    keep += diagonal_actions(rig, {a.name: a for a in keep})
    keep += turn_actions(rig, next((a for a in keep if a.name == 'Walk_Left_Loop')))
    rig.animation_data.action = None
    for action in keep:
        action.name = action.name.split('|')[-1].split('.')[0]
        action.use_fake_user = True
        track = rig.animation_data.nla_tracks.new()
        track.name = action.name
        strip = track.strips.new(action.name, int(action.frame_range[0]), action)
        strip.action_slot = action.slots[0]
        track.mute = True
    for action in list(bpy.data.actions):
        if action not in keep:
            bpy.data.actions.remove(action)
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.gltf(filepath=str(args.output.resolve()), export_format="GLB", use_selection=True,
                             export_yup=True, export_animations=True, export_animation_mode="NLA_TRACKS",
                             export_force_sampling=True, export_optimize_animation_size=True,
                             export_def_bones=False, export_skins=True)
    print("EXPORTED", args.output, [a.name for a in keep])


if __name__ == "__main__":
    main()
