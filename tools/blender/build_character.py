"""Merge the Quaternius Universal Animation Libraries 1 and 2 onto one mannequin for Godot.

Run from the project root:
  blender -b --factory-startup --python tools/blender/build_character.py -- <UAL2 .glb> <UAL1 Unreal .fbx> assets/character/mannequin.glb

Both libraries share the same Unreal-style skeleton and rest pose, so UAL1 actions bind to the UAL2
rig by bone name with no retargeting.
"""
import bpy, sys

argv = sys.argv[sys.argv.index("--") + 1:]
UAL2_GLB, UAL1_FBX, OUT = argv[0], argv[1], argv[2]

KEEP_UAL1 = [
    "Idle_Loop", "Walk_Loop", "Jog_Fwd_Loop", "Sprint_Loop", "Jump_Start", "Jump_Loop", "Jump_Land",
    "Pistol_Idle_Loop", "Pistol_Aim_Neutral", "Pistol_Aim_Up", "Pistol_Aim_Down", "Pistol_Shoot",
    "Hit_Chest", "Hit_Head", "Crouch_Idle_Loop", "Crouch_Fwd_Loop", "Roll", "Death01",
]
KEEP_UAL2 = ["LayToIdle", "Hit_Knockback", "Idle_FoldArms_Loop", "NinjaJump_Land"]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=UAL2_GLB)
rig = next(o for o in bpy.data.objects if o.type == "ARMATURE")
rig.name = "Mannequin"
base_objects = set(bpy.data.objects)
for a in bpy.data.actions:
    a.name = a.name.split("|")[-1]
ual2_actions = {a.name: a for a in bpy.data.actions}

bpy.ops.import_scene.fbx(filepath=UAL1_FBX)
fbx_objects = [o for o in bpy.data.objects if o not in base_objects]
ual1_actions = {}
for a in bpy.data.actions:
    if a.name not in ual2_actions.values() and a not in ual2_actions.values():
        ual1_actions[a.name.split("|")[-1]] = a

keep = []
for name in KEEP_UAL1:
    a = ual1_actions[name]
    a.name = name
    keep.append(a)
for name in KEEP_UAL2:
    keep.append(ual2_actions[name])

for ob in fbx_objects:
    bpy.data.objects.remove(ob, do_unlink=True)

# Bind every kept action to the rig through its own NLA track so the exporter emits each one.
rig.animation_data_create()
rig.animation_data.action = None
for tr in list(rig.animation_data.nla_tracks):
    rig.animation_data.nla_tracks.remove(tr)
for a in keep:
    a.use_fake_user = True
    track = rig.animation_data.nla_tracks.new()
    track.name = a.name
    strip = track.strips.new(a.name, int(a.frame_range[0]), a)
    if hasattr(strip, "action_slot") and a.slots:
        strip.action_slot = a.slots[0]
    track.mute = True
for a in list(bpy.data.actions):
    if a not in keep:
        bpy.data.actions.remove(a)

bpy.ops.object.select_all(action="DESELECT")
for ob in bpy.data.objects:
    ob.select_set(True)
bpy.ops.export_scene.gltf(filepath=OUT, export_format="GLB", use_selection=True, export_yup=True,
                          export_animations=True, export_animation_mode="NLA_TRACKS", export_force_sampling=True,
                          export_optimize_animation_size=True, export_def_bones=False, export_skins=True)
print("EXPORTED", OUT, [a.name for a in keep])
