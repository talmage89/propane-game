"""Bake neutral 100STYLE locomotion onto the Quaternius rest skeleton.

BVH translation is centimeters, Z is up after Blender import. Root heading/travel is removed;
rotations are transferred in rest frames, then ankle targets are solved with the mannequin's actual
segment lengths. Only pelvis, spine, head and legs are baked: rifle arms remain runtime contacts.
"""
import math
import statistics

import bpy
from mathutils import Matrix, Quaternion, Vector

MAP = {"pelvis": "Hips", "spine_01": "Chest", "spine_02": "Chest2", "spine_03": "Chest4",
       "neck_01": "Neck", "Head": "Head", "thigh_l": "LeftHip", "calf_l": "LeftKnee",
       "foot_l": "LeftAnkle", "ball_l": "LeftToe", "thigh_r": "RightHip", "calf_r": "RightKnee",
       "foot_r": "RightAnkle", "ball_r": "RightToe"}
# Selected complete cycles with straight travel and a small endpoint pose difference (inclusive BVH frames).
CYCLES = {"Walk_Back_Loop": ("BW", 1121, 1193, 1), "Walk_Left_Loop": ("SW", 603, 672, 1),
          "Jog_Back_Loop": ("BR", 546, 589, 2), "Jog_Left_Loop": ("SR", 1330, 1366, 2)}


def bind(rig, action):
    rig.animation_data_create()
    rig.animation_data.action = action
    if action:
        if not action.slots:
            action.slots.new("OBJECT", rig.name)
        rig.animation_data.action_slot = action.slots[0]


def update():
    bpy.context.view_layer.update()


def reset(rig):
    for bone in rig.pose.bones:
        bone.matrix_basis = Matrix.Identity(4)
    update()


def orient(rig, name, rotation):
    bone = rig.pose.bones[name]
    matrix = rotation.to_matrix().to_4x4()
    matrix.translation = bone.matrix.translation
    bone.matrix = matrix
    update()


def leg_ik(rig, side, ankle, pole, foot_rotation):
    names = [f'thigh_{side}', f'calf_{side}', f'foot_{side}']
    upper, lower, foot = [rig.pose.bones[n] for n in names]
    a, b, c = [v.matrix.translation.copy() for v in (upper, lower, foot)]
    l1, l2 = ((b - a).length, (c - b).length)
    d = ankle - a
    length = min(max(d.length, abs(l1 - l2) + 1e-05), l1 + l2 - 1e-05)
    direction = d.normalized()
    along = (l1 * l1 - l2 * l2 + length * length) / (2 * length)
    height = math.sqrt(max(0, l1 * l1 - along * along))
    bend = pole - a
    bend = (bend - direction * bend.dot(direction)).normalized()
    forward = Vector((0, -1, 0))
    forward = (forward - direction * forward.dot(direction)).normalized()
    # A near-straight mocap knee has an ill-conditioned pole. Favor the mannequin's sagittal
    # hinge plane while retaining the donor's small lateral variation; contacts and swing endpoints
    # remain exact. Independent axial counter-twists cannot be interpolated without losing the ankle.
    bend = (forward * 0.9 + bend * 0.1).normalized()
    knee = a + direction * along + bend * height
    # Keep each knee outside the hip midline on the exact IK circle, rather than merely offsetting a pole.
    if (knee.x if side=='l' else -knee.x)<.08:
        sign = 1 if side == 'l' else -1
        outer = Vector((sign, 0, 0))
        projected = outer - direction * outer.dot(direction)
        available = projected.length * height
        outer = projected.normalized()
        cosine = min(1, max(-1, (0.08 - sign * (a + direction * along).x) / max(available, 1e-06)))
        tangent = bend - outer * bend.dot(outer)
        if tangent.length < 1e-06:
            tangent = direction.cross(outer)
        knee = a + direction * along + (outer * cosine + tangent.normalized() * math.sqrt(max(0, 1 - cosine * cosine))) * height
    # Use one knee plane for both segments. Transporting each captured bone's axial twist independently
    # can make adjacent keys counter-rotate by 180 degrees while their ankle endpoints agree; interpolation
    # then pulls a planted ankle below the floor. Rest-frame orientation keeps the FK chain continuous.
    normal = direction.cross(knee - a - direction * (knee - a).dot(direction)).normalized()
    def frame(axis, plane):
        y = axis.normalized()
        x = (plane - y * plane.dot(y)).normalized()
        return Matrix((x, y, x.cross(y))).transposed()
    rest_a, rest_b, rest_c = [rig.data.bones[n].head_local for n in names]
    rest_plane = Vector((-1, 0, 0))
    upper_rotation = frame(knee - a, normal) @ frame(rest_b - rest_a, rest_plane).inverted() @ upper.bone.matrix_local.to_3x3()
    orient(rig, names[0], upper_rotation.to_quaternion())
    b = lower.matrix.translation.copy()
    c = foot.matrix.translation.copy()
    lower_rotation = frame(a + direction * length - b, normal) @ frame(rest_c - rest_b, rest_plane).inverted() @ lower.bone.matrix_local.to_3x3()
    orient(rig, names[1], lower_rotation.to_quaternion())
    orient(rig, names[2], foot_rotation)


def key(rig, frame, names):
    for name in names:
        bone = rig.pose.bones[name]
        bone.rotation_mode = 'QUATERNION'
        bone.keyframe_insert("rotation_quaternion", frame=frame, group=name)
        bone.keyframe_insert("location", frame=frame, group=name)


def linear(action):
    for layer in action.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                for curve in bag.fcurves:
                    for point in curve.keyframe_points:
                        point.interpolation = 'LINEAR'


def directional_actions(rig, root, references):
    output = []
    for name, (kind, start, end, stride_scale) in CYCLES.items():
        bpy.ops.import_anim.bvh(filepath=str(root / f"Neutral_{kind}.bvh"), global_scale=.01,
                                axis_forward="-Z", axis_up="Y", frame_start=1, use_fps_scale=False, update_scene_fps=False)
        donor = bpy.context.object
        source = donor.animation_data.action
        print("SOURCE", kind, source.frame_range[:], bpy.context.scene.render.fps)
        frames = []
        for frame in range(start, end+1):
            bpy.context.scene.frame_set(frame)
            frames.append({n: donor.pose.bones[n].matrix.copy() for n in MAP.values()})
        forward = frames[0]['Hips'].to_3x3() @ donor.data.bones['Hips'].matrix_local.to_3x3().inverted() @ Vector((0, -1, 0))
        heading = math.atan2(forward.x, -forward.y)
        heading_angle = heading
        heading = Quaternion((0, 0, 1), -heading)
        first, last = (frames[0], frames[-1])
        def frame_heading(f):
            front = f['Hips'].to_3x3() @ donor.data.bones['Hips'].matrix_local.to_3x3().inverted() @ Vector((0, -1, 0))
            yaw = math.atan2(front.x, -front.y)
            residual = (yaw - heading_angle + math.pi) % (2 * math.pi) - math.pi
            return Quaternion((0, 0, 1), -yaw + min(max(residual, -math.radians(5)), math.radians(5)))
        def delta(f,name):
            return (frame_heading(f).to_matrix() @ f[name].to_3x3() @ donor.data.bones[name].matrix_local.to_3x3().inverted()).to_quaternion()
        rotation_drift = {n: delta(first, n).inverted() @ delta(last, n) for n in MAP.values()}
        relative_drift = {n: frame_heading(last) @ (last[n].translation - last['Hips'].translation) - frame_heading(first) @ (first[n].translation - first['Hips'].translation) for n in MAP.values()}
        vertical_drift = {n: last[n].translation.z - first[n].translation.z for n in MAP.values()}
        for i, f in enumerate(frames):
            f['CyclePhase'] = i / (len(frames) - 1)
        # Run source SR was captured traveling toward the performer's right. Mirror after the common retarget.
        mirror_source = kind == 'SR'
        scale = ((rig.data.bones['calf_l'].head_local-rig.data.bones['thigh_l'].head_local).length +
                 (rig.data.bones['foot_l'].head_local-rig.data.bones['calf_l'].head_local).length) / .869580
        mean_height = statistics.mean((f['Hips'].translation.z for f in frames))
        root_travel = heading @ (frames[-1]['Hips'].translation - frames[0]['Hips'].translation)
        stride = (root_travel.y if kind in ('BW', 'BR') else abs(root_travel.x)) * scale * stride_scale
        floor = min((f[n].translation.z for f in frames for n in ('LeftAnkle', 'RightAnkle')))
        is_run = kind in ('BR', 'SR')
        # Align each cycle's left touchdown with the forward library's phase zero.
        count = len(frames) - 1
        heights = [f['LeftAnkle'].translation.z for f in frames[:-1]]
        threshold = min(heights) + 0.025
        touchdown = next((i for i in range(count) if heights[i] <= threshold < heights[(i - 1) % count]), min(range(count), key=heights.__getitem__))
        frames = frames[touchdown:count] + frames[:touchdown]
        frames.append(frames[0])
        for mirrored in ([False, True] if kind in ('SW','SR') else [False]):
            action_name = name.replace('Left', 'Right') if mirrored else name
            action = bpy.data.actions.new(action_name)
            bind(rig, action)
            for index, f in enumerate(frames):
                bpy.context.scene.frame_set(index)
                reset(rig)
                # Remove the captured path's turn, retaining a bounded pelvis sway about the aim.
                heading_now = frame_heading(f)
                cycle_phase = f['CyclePhase']
                # Sideways reflection exchanges limbs as well as rotation handedness.
                reflection = Matrix.Diagonal((-1, 1, 1)) if mirrored != mirror_source else Matrix.Identity(3)
                for target, source_name in MAP.items():
                    source_name = source_name.replace('Left', 'Swap').replace('Right', 'Left').replace('Swap', 'Right') if mirrored != mirror_source else source_name
                    rotation = delta(f, source_name) @ Quaternion().slerp(rotation_drift[source_name].inverted(), cycle_phase)
                    rotation = reflection @ rotation.to_matrix() @ reflection
                    orient(rig, target, (rotation @ rig.data.bones[target].matrix_local.to_3x3()).to_quaternion())
                    if target == 'pelvis':
                        b = rig.pose.bones[target]
                        m = b.matrix.copy()
                        m.translation = rig.data.bones[target].head_local.copy()
                        m.translation.z += (f['Hips'].translation.z - vertical_drift['Hips'] * cycle_phase - mean_height) * scale * (1.75 if is_run else 1) - (0.08 if is_run else 0)
                        b.matrix = m
                        update()
                pelvis = rig.pose.bones['pelvis'].matrix.translation.copy()
                for side in ('l','r'):
                    source_side = 'Left' if side == 'l' else 'Right'
                    if mirrored != mirror_source:
                        source_side = 'Right' if side == 'l' else 'Left'
                    relative = (heading_now @ (f[source_side + 'Ankle'].translation - f['Hips'].translation) - relative_drift[source_side + 'Ankle'] * cycle_phase) * scale
                    relative = reflection @ relative
                    # Expand travel while preserving the hip width, anatomical leg lengths and knee plane.
                    travel_axis = 1 if kind in ('BW', 'BR') else 0
                    center = 0 if travel_axis == 1 else (0.1 if side == 'l' else -0.1) * scale
                    relative[travel_axis] = center + (relative[travel_axis] - center) * stride_scale
                    ankle = pelvis + relative
                    ankle.x = max(ankle.x, 0.1) if side == 'l' else min(ankle.x, -0.1)
                    ankle.z = 0.104 + (f[source_side + 'Ankle'].translation.z - vertical_drift[source_side + 'Ankle'] * cycle_phase - floor) * scale * (2.5 if not is_run else 3)
                    foot = rig.pose.bones['foot_' + side]
                    knee_relative = (heading_now @ (f[source_side + 'Knee'].translation - f['Hips'].translation) - relative_drift[source_side + 'Knee'] * cycle_phase) * scale
                    pole = pelvis + reflection @ knee_relative
                    pole.x = max(pole.x, 0.12) if side == 'l' else min(pole.x, -0.12)
                    if is_run:
                        a, b, c = [rig.pose.bones[n + '_' + side].matrix.translation for n in ('thigh', 'calf', 'foot')]
                        reach = (a - b).length + (b - c).length - 0.01
                        allowed = math.sqrt(max(0, reach * reach - (a.z - ankle.z) ** 2))
                        horizontal = Vector((ankle.x - a.x, ankle.y - a.y, 0))
                        if horizontal.length>allowed:
                            horizontal = horizontal.normalized() * allowed
                            ankle.x = a.x + horizontal.x
                            ankle.y = a.y + horizontal.y
                    leg_ik(rig, side, ankle, pole, foot.matrix.to_quaternion())
                key(rig, index, MAP)
            linear(action)
            travel = Vector((0, -(0.93 if not is_run else 3.6), 0)) if kind in ('BW', 'BR') else Vector((-(0.69 if not is_run else 2.6), 0, 0))
            if mirrored:
                travel.x *= -1
            action = bake_contacts(rig, action, travel, retime='run' if is_run else 'walk', reference=None if is_run else references['walk'])
            output.append(action)
            print('DIRECTIONAL',action_name,'frames',end-start,'stride',stride)
        bpy.data.objects.remove(donor, do_unlink=True)
        bpy.data.actions.remove(source)
    bind(rig, None)
    return output


def bake_contacts(rig, source, travel, retime=False, reference=None, normalize_phase=False):
    """Repair the shipped sprint's slow ground skate by baking constant travel during authored stance.

    Retain its vertical arc, contact timing, pelvis, spine and arm curves. This is asset authoring, not a
    runtime step generator. Cyclic smooth corrections on lift-off/touchdown return to the original swing.
    """
    bind(rig, source)
    start, end = [int(v) for v in source.frame_range]
    poses = []
    for frame in range(start, end+1):
        bpy.context.scene.frame_set(frame)
        poses.append({b.name:b.matrix_basis.copy() for b in rig.pose.bones})
    # Normalize the final mannequin's contact phase AFTER mirroring, which exchanges the two feet.
    if retime or normalize_phase:
        heights = []
        for frame in range(start,end):
            bpy.context.scene.frame_set(frame)
            heights.append(rig.pose.bones['foot_l'].matrix.translation.z)
        touchdown = min(range(end - start), key=heights.__getitem__)
        poses = poses[touchdown:-1] + poses[:touchdown]
        poses.append(poses[0])
    # Shorten the mocap run's support intervals without compressing its swing poses. At our fixed
    # 6 m/s speed the original sidestep's short stride otherwise needs five cycles each second.
    count = end - start
    phase_map = [(0, 0), (1, 1)]
    if retime=='run':
        heights = {}
        for side in ('l','r'):
            heights[side] = []
            for pose in poses[:-1]:
                for bone in rig.pose.bones:
                    bone.matrix_basis = pose[bone.name]
                update()
                heights[side].append(rig.pose.bones['foot_'+side].matrix.translation.z)
        def contact_interval(values, center):
            floor = min(values) + 0.03
            lo = hi = center
            while values[(lo - 1) % count] <= floor and center - lo < count // 2:
                lo -= 1
            while values[(hi + 1) % count] <= floor and hi - center < count // 2:
                hi += 1
            # Include the interpolated threshold crossings. Rounding a boundary to the next BVH key
            # leaves part of stance in the stretched swing, making low feet travel instead of plant.
            before, at_lo = (values[(lo - 1) % count], values[lo % count])
            at_hi, after = (values[hi % count], values[(hi + 1) % count])
            return ((lo-(floor-at_lo)/max(before-at_lo,1e-6))/count,
                    (hi+(floor-at_hi)/max(after-at_hi,1e-6))/count)
        left_lo, left_hi = contact_interval(heights['l'], 0)
        right_center = min(range(count), key=heights['r'].__getitem__)
        right_lo, right_hi = contact_interval(heights['r'], right_center)
        phase_map = [(0, 0), (0.5, right_center / count), (1, 1)]
    elif retime=='walk':
        right = []
        for pose in poses[:-1]:
            for bone in rig.pose.bones:
                bone.matrix_basis = pose[bone.name]
            update()
            right.append(rig.pose.bones['foot_r'].matrix.translation.z)
        phase_map = [(0, 0), (0.5, min(range(count), key=right.__getitem__) / count), (1, 1)]
    assert all((a[1] < b[1] for a, b in zip(phase_map, phase_map[1:]))), (source.name, phase_map)
    def sample_phase(phase):
        for (a,x),(b,y) in zip(phase_map,phase_map[1:]):
            if phase <= b:
                return x + (y - x) * (phase - a) / (b - a)
        return 1
    # A normalized cycle is sampled densely for Godot's 30 Hz importer; runtime cadence is independent
    # of this two-second authoring duration. Reconstruct matrices, never interpolate joint positions.
    def interpolate(phase):
        phase = phase % 1 * count
        a = min(int(phase), count - 1)
        return {name: poses[a][name].lerp(poses[a + 1][name], phase - a) for name in poses[0]}
    sampled = []
    captured_feet = {'l': [], 'r': []}
    for i in range(121):
        pose = interpolate(sample_phase(i / 120))
        if retime=='run':
            # Retiming the entire skeleton compressed the OTHER leg's swing during each stance.
            # Each leg gets its own short support interval and a full, uninterrupted swing interval.
            for side,center,lo,hi,target_center in [('l',0,left_lo,left_hi,0),
                                                  ('r',right_center/count,right_lo,right_hi,.5)]:
                peak = max(range(count), key=heights[side].__getitem__) / count
                while peak <= hi:
                    peak += 1
                phase = (i / 120 - target_center) % 1
                if phase <= 0.1:
                    source_phase = center + (hi - center) * phase / 0.1
                elif phase>=.9: source_phase=center+1+(lo-center)*(1-phase)/.1
                elif phase<=.5: source_phase=hi+(peak-hi)*(phase-.1)/.4
                else: source_phase=peak+(1+lo-peak)*(phase-.5)/.4
                leg_pose = interpolate(source_phase)
                for bone in rig.pose.bones:
                    bone.matrix_basis = leg_pose[bone.name]
                update()
                captured_feet[side].append(rig.pose.bones['foot_'+side].matrix.translation.copy())
                for bone in ('thigh_', 'calf_', 'foot_', 'ball_'):
                    pose[bone + side] = leg_pose[bone + side]
        sampled.append(pose)
    poses = sampled
    count = 120
    feet = {}
    pelvis_positions = []
    for pose in poses:
        for bone in rig.pose.bones:
            bone.matrix_basis = pose[bone.name]
        update()
        pelvis_positions.append(rig.pose.bones['pelvis'].matrix.translation.copy())
    pelvis_center = sum(pelvis_positions, Vector()) / len(pelvis_positions)
    for side in ('l','r'):
        rows = captured_feet[side] if retime == 'run' else []
        if not rows:
            for pose in poses:
                for bone in rig.pose.bones:
                    bone.matrix_basis = pose[bone.name]
                update()
                rows.append(rig.pose.bones['foot_'+side].matrix.translation.copy())
        floor = min((p.z for p in rows))
        contact = [i for i, p in enumerate(rows[:-1]) if p.z <= floor + 0.03]
        # Unwrap each stance across the seam; the left/right contact clusters are half a cycle apart.
        center = min(contact, key=lambda i: rows[i].z)
        phase = [((i - center + count / 2) % count - count / 2) / count for i in range(count + 1)]
        # Follow the entire contiguous authored support interval. A half-cycle window truncates
        # backpedal's long touchdown, leaving its ankle at ground height while XY still swings inward.
        active = [center]
        for sign in (-1,1):
            for distance in range(1,count//2):
                index = (center + sign * distance) % count
                if rows[index].z > floor + 0.03:
                    break
                active.append(index)
        # Center the planted arc under the pelvis: the original sprint plants too far forward.
        midpoint = (min((phase[i] for i in active)) + max((phase[i] for i in active))) / 2
        width = (0.3 if retime == 'run' else 0.24) if abs(travel.x) > 0 else 0.14
        anchor = Vector((width if side == 'l' else -width, pelvis_center.y, 0))
        correction = []
        lo, hi = (min((phase[i] for i in active)), max((phase[i] for i in active)))
        if retime == 'run':
            print('RETIMED_FEET', source.name, side, 'floor', floor, 'contact', lo, hi, 'heights', [round(p.z, 3) for p in rows[::5]], flush=True)
        for i,p in enumerate(rows):
            distance = max(lo - phase[i], phase[i] - hi, 0)
            weight = max(0, 1 - distance / 0.12)
            weight = weight * weight * (3 - 2 * weight)
            target = anchor + travel * (min(max(phase[i], lo), hi) - midpoint)
            clearance = min(1, distance / 0.02)
            clearance = clearance * clearance * (3 - 2 * clearance)
            lift = max(0, 0.134 + 0.01 * clearance - (p.z + 0.104 - floor)) if distance > 0 else 0
            correction.append(Vector(((target.x-p.x)*weight if retime else 0,(target.y-p.y)*weight,.104-floor+lift)))
        feet[side] = correction
    root_deltas = [0.0] * len(poses)
    if reference:
        # Directional mocap has asymmetric weight shifts. Use the forward authored clip's vertical
        # pelvis layer at the common contact phase, so opposite sidesteps don't cancel when blended.
        bind(rig, reference)
        first, last = reference.frame_range
        wave = []
        for i in range(len(poses)):
            frame = first + (last - first) * i / count
            bpy.context.scene.frame_set(int(frame),subframe=frame-int(frame))
            wave.append(rig.pose.bones['pelvis'].matrix.translation.z)
        mean = statistics.mean(wave)
        root_deltas = [wave[i] - mean - (pelvis_positions[i].z - pelvis_center.z) for i in range(len(poses))]
    replacement = bpy.data.actions.new(source.name + '_contacts')
    bind(rig,replacement)
    drop = 0
    for i, pose in enumerate(poses):
        for bone in rig.pose.bones:
            bone.matrix_basis = pose[bone.name]
        update()
        for side in ('l','r'):
            a, b, c = [rig.pose.bones[n + '_' + side].matrix.translation for n in ('thigh', 'calf', 'foot')]
            target = (captured_feet[side][i] if retime == 'run' else c) + feet[side][i]
            reach = (a - b).length + (b - c).length - 0.01
            if target.z > .134:
                # Swing XY can shorten to anatomical reach, but its captured height must be reachable
                # even directly below the hip. Otherwise a high pelvis makes the IK hinge degenerate.
                drop = max(drop, a.z + root_deltas[i] - target.z - reach + 0.01)
                continue
            target = target.copy()
            target.z = 0.104
            horizontal = Vector((target.x - a.x, target.y - a.y, 0)).length_squared
            vertical = math.sqrt(max(0, reach * reach - horizontal))
            drop = max(drop, a.z + root_deltas[i] - target.z - vertical)
    print('CONTACT_REACH',source.name,'pelvis-drop',drop,'pelvis',pelvis_center[:],'warp',phase_map,flush=True)
    for i, pose in enumerate(poses):
        bpy.context.scene.frame_set(i)
        for bone in rig.pose.bones:
            bone.matrix_basis = pose[bone.name]
        update()
        pelvis = rig.pose.bones['pelvis']
        matrix = pelvis.matrix.copy()
        matrix.translation.z += root_deltas[i] - drop
        pelvis.matrix = matrix
        update()
        for side in ('l','r'):
            foot = rig.pose.bones['foot_' + side]
            ankle = (captured_feet[side][i] if retime == 'run' else foot.matrix.translation + Vector((0, 0, drop - root_deltas[i]))) + feet[side][i]
            # Expanded running strides must stay within the mannequin's anatomical reach. Preserve the
            # authored swing height and shorten the horizontal excursion; a reach clamp must never turn
            # a low swinging ankle into a shared vertical hop when the pelvis rises.
            if retime and ankle.z > .134:
                a, b, c = [rig.pose.bones[n + '_' + side].matrix.translation for n in ('thigh', 'calf', 'foot')]
                reach = (a - b).length + (b - c).length - 0.01
                allowed = math.sqrt(max(0, reach * reach - (a.z - ankle.z) ** 2))
                horizontal = Vector((ankle.x - a.x, ankle.y - a.y, 0))
                if horizontal.length>allowed:
                    horizontal = horizontal.normalized() * allowed
                    ankle.x = a.x + horizontal.x
                    ankle.y = a.y + horizontal.y
            leg_ik(rig,side,ankle,rig.pose.bones['calf_'+side].matrix.translation,foot.matrix.to_quaternion())
        key(rig,i,[b.name for b in rig.pose.bones])
    # Keep the original action identity so callers and the exported name stay stable.
    bind(rig,None)
    name = source.name
    source.name = name + '_unrepaired'
    replacement.name = name
    # The caller replaces its reference; swapping the action contents isn't supported by the slotted API.
    return replacement


def repair_contacts(rig, source, stride):
    return bake_contacts(rig, source, Vector((0, stride, 0)), normalize_phase=True)


def diagonal_actions(rig, actions):
    """Bake endpoint-compatible diagonal poses instead of blending incompatible IK joint rotations.

    Contact paths and captured swing heights blend in mannequin space at the shared touchdown phase.
    The leg solve reconstructs those paths with the original segment lengths. Runtime can interpolate
    toward the neighboring directions without asking terrain IK to manufacture a diagonal stride.
    """
    def samples(action):
        bind(rig, action)
        start, end = action.frame_range
        rows = []
        for i in range(121):
            frame = start + (end - start) * i / 120
            bpy.context.scene.frame_set(int(frame), subframe=frame-int(frame))
            rows.append(({b.name: b.matrix_basis.copy() for b in rig.pose.bones},
                         {b.name: b.matrix.copy() for b in rig.pose.bones}))
        return rows
    output = []
    for gait, forward, strides in [('Walk', 'Walk_Loop', (1.34,.93,.69)),
                                   ('Jog', 'Jog_Fwd_Loop', (5,3.6,2.6))]:
        for longitudinal, long_name, stride in [('Front',forward,strides[0]),
                                               ('Back',gait+'_Back_Loop',strides[1])]:
            long_rows = samples(actions[long_name])
            for lateral in ('Left','Right'):
                side_rows = samples(actions[gait + '_' + lateral + '_Loop'])
                amount = stride / (stride + strides[2])
                heights = [a[1]['pelvis'].translation.z*(1-amount)+b[1]['pelvis'].translation.z*amount
                           for a,b in zip(long_rows,side_rows)]
                spans = [max(r[1]['pelvis'].translation.z for r in rows)-min(r[1]['pelvis'].translation.z for r in rows)
                         for rows in (long_rows,side_rows)]
                span = spans[0] * (1 - amount) + spans[1] * amount
                mean = statistics.mean(heights)
                gain = span / max(max(heights) - min(heights), 1e-06)
                action = bpy.data.actions.new(gait + '_' + longitudinal + '_' + lateral + '_Loop')
                bind(rig, action)
                for i, (a,b) in enumerate(zip(long_rows,side_rows)):
                    bpy.context.scene.frame_set(i)
                    for bone in rig.pose.bones:
                        bone.matrix_basis = a[0][bone.name].lerp(b[0][bone.name], amount)
                    update()
                    pelvis = rig.pose.bones['pelvis']
                    matrix = pelvis.matrix.copy()
                    matrix.translation.z = mean + (heights[i] - mean) * gain
                    pelvis.matrix = matrix
                    update()
                    for side in ('l','r'):
                        foot = 'foot_' + side
                        ankle = a[1][foot].translation.lerp(b[1][foot].translation, amount)
                        knee = a[1]['calf_' + side].translation.lerp(b[1]['calf_' + side].translation, amount)
                        rotation = a[1][foot].to_quaternion().slerp(b[1][foot].to_quaternion(), amount)
                        leg_ik(rig,side,ankle,knee,rotation)
                    key(rig,i,[bone.name for bone in rig.pose.bones])
                linear(action)
                output.append(action)
    bind(rig,None)
    return output


def turn_actions(rig, source):
    """Author quarter-turn steps from the neutral sidestep's opposite stance/swing poses.

    The clip owns lift, knee flexion and weight shift. Its support arc counter-rotates a quarter turn;
    runtime only samples this baked cycle against observed body rotation.
    """
    bind(rig,source)
    poses = []
    start, end = source.frame_range
    for i in range(121):
        frame = start + (end - start) * i / 120
        bpy.context.scene.frame_set(int(frame),subframe=frame-int(frame))
        poses.append({b.name:b.matrix_basis.copy() for b in rig.pose.bones})
    output = []
    for name,sign in [('Turn_Left_Loop',1),('Turn_Right_Loop',-1)]:
        action = bpy.data.actions.new(name)
        bind(rig,action)
        for i,pose in enumerate(poses):
            bpy.context.scene.frame_set(i)
            for bone in rig.pose.bones:
                bone.matrix_basis = pose[bone.name]
            update()
            for side,contact in [('l',0),('r',.5)]:
                phase = (i / 120 - contact + 0.5) % 1 - 0.5
                fraction = max(0, min(1, (abs(phase) - 0.18) / 0.32))
                return_weight = 1 - fraction * fraction * (3 - 2 * fraction)
                rotation = Quaternion((0, 0, 1), -sign * math.pi / 2 * phase * return_weight)
                foot = rig.pose.bones['foot_' + side]
                ankle = foot.matrix.translation.copy()
                horizontal = rotation @ Vector((0.2 if side == 'l' else -0.2, 0, 0))
                ankle.x = horizontal.x
                ankle.y = rig.pose.bones['pelvis'].matrix.translation.y + horizontal.y
                pole = rig.pose.bones['calf_' + side].matrix.translation.copy()
                pole.x = max(pole.x, 0.12) if side == 'l' else min(pole.x, -0.12)
                leg_ik(rig,side,ankle,pole,rotation @ foot.matrix.to_quaternion())
            key(rig,i,[b.name for b in rig.pose.bones])
        linear(action)
        output.append(action)
    bind(rig,None)
    return output
