"""Retarget 100STYLE Neutral locomotion onto the packaged mannequin as a Godot AnimationLibrary.

    python3 -B tools/animation/retarget_100style.py <dir with Neutral_*.bvh> [--output assets/third_party/100style/neutral/directional.tres]

The source is The 100STYLE Dataset - Ian Mason (CC BY 4.0), Zenodo record 8127870; docs/animation-sources.md lists
the files and checksums. Needs Python 3 and numpy only.

Each output is one in-place gait cycle, normalized to 1 s, that starts on the left foot's touchdown, so all
locomotion clips share one phase. The motion is the capture's own: global bone rotations are transferred between
the two skeletons' rest frames (both are Y-up T-poses facing +Z), several straight cycles are averaged to remove
single-step noise, and each cycle's small start/end difference is spread over the cycle so it loops. Nothing is
exaggerated or re-solved: no stride scaling, no foot IK. Only the pelvis, spine, neck, head and legs are written;
the rifle stance owns the arms. The pelvis keeps its captured bob and sway; travel is removed (physics moves the
character) and each cycle's heading is turned so it travels exactly back, left or right.
"""
import argparse
import hashlib
import json
import pathlib
import struct
import sys

import numpy as np

ROOT = pathlib.Path(__file__).resolve().parents[2]
SAMPLES = 60
# Target bone <- source joint. Global rotations transfer, so skipped source joints (Chest3) are not lost.
MAP = {"pelvis": "Hips", "spine_01": "Chest", "spine_02": "Chest2", "spine_03": "Chest4", "neck_01": "Neck",
       "Head": "Head", "thigh_l": "LeftHip", "calf_l": "LeftKnee", "foot_l": "LeftAnkle", "ball_l": "LeftToe",
       "thigh_r": "RightHip", "calf_r": "RightKnee", "foot_r": "RightAnkle", "ball_r": "RightToe"}
# Output clip: source file, travel direction in the character frame (degrees; 0 forward, +90 left, 180 back).
CLIPS = {"Walk_Back": ("BW", 180.0), "Walk_Left": ("SW", 90.0), "Walk_Right": ("SW", -90.0)}
# The legs' rest frames already agree (straight, feet forward); posture differs only in the trunk and head.
CALIBRATED = ("Hips", "Chest", "Chest2", "Chest4", "Neck", "Head")
DIRECTION_TOLERANCE = 16.0
DRIFT_TOLERANCE = 14.0
MAX_CYCLES = 6


# ------------------------------------------------------------------ quaternions (x, y, z, w)

def qmul(a, b):
    ax, ay, az, aw = np.moveaxis(a, -1, 0)
    bx, by, bz, bw = np.moveaxis(b, -1, 0)
    return np.stack([aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
                     aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz], -1)


def qinv(a):
    return a * np.array([-1, -1, -1, 1.0])


def qrot(q, v):
    qv = np.concatenate([v, np.zeros(v.shape[:-1] + (1,))], -1)
    return qmul(qmul(q, qv), qinv(q))[..., :3]


def qaxis(axis, angle):
    axis = np.asarray(axis, float) / np.linalg.norm(axis)
    angle = np.asarray(angle, float)[..., None]
    return np.concatenate([axis * np.sin(angle / 2), np.cos(angle / 2)], -1)


def qnorm(q):
    return q / np.linalg.norm(q, axis=-1, keepdims=True)


def qslerp(a, b, t):
    d = np.sum(a * b, -1, keepdims=True)
    b = np.where(d < 0, -b, b)
    d = np.clip(np.abs(d), 0, 1)
    t = np.asarray(t, float)[..., None]
    theta = np.arccos(d)
    s = np.sin(theta)
    small = s < 1e-6
    wa = np.where(small, 1 - t, np.sin((1 - t) * theta) / np.where(small, 1, s))
    wb = np.where(small, t, np.sin(t * theta) / np.where(small, 1, s))
    return qnorm(wa * a + wb * b)


def qangle(a, b):
    return 2 * np.arccos(np.clip(np.abs(np.sum(a * b, -1)), 0, 1))


def qmean(qs):
    """Normalized hemisphere-aligned mean; accurate for the closely clustered samples averaged here."""
    ref = qs[0]
    aligned = np.where((np.sum(qs * ref, -1) < 0)[..., None], -qs, qs)
    return qnorm(aligned.mean(0))


# ------------------------------------------------------------------ sources

class Bvh:
    def __init__(self, path):
        tokens = pathlib.Path(path).read_text().split()
        self.names, self.parent, self.offset, self.channels = [], [], [], []
        stack, i, end_site = [], 0, False
        while tokens[i] != "MOTION":
            token = tokens[i]
            if token in ("ROOT", "JOINT"):
                self.names.append(tokens[i + 1])
                self.parent.append(stack[-1] if stack else -1)
                self.channels.append([])
                self.offset.append(np.zeros(3))
                current = len(self.names) - 1
                i += 2
            elif token == "End":
                end_site = True
                i += 2
            elif token == "{":
                stack.append(-1 if end_site else current)
                i += 1
            elif token == "}":
                if stack.pop() == -1:
                    end_site = False
                i += 1
            elif token == "OFFSET":
                if stack[-1] != -1:
                    self.offset[stack[-1]] = np.array([float(x) for x in tokens[i + 1:i + 4]])
                i += 4
            elif token == "CHANNELS":
                count = int(tokens[i + 1])
                self.channels[stack[-1]] = tokens[i + 2:i + 2 + count]
                i += 2 + count
            else:
                i += 1
        self.frames = int(tokens[i + 2])
        self.dt = float(tokens[i + 5])
        self.data = np.array(tokens[i + 6:], float).reshape(self.frames, -1)
        self.index = {name: k for k, name in enumerate(self.names)}

    def forward_kinematics(self):
        """Global rotations and positions (meters) for every frame."""
        count, joints = self.frames, len(self.names)
        rotation = np.zeros((count, joints, 4))
        position = np.zeros((count, joints, 3))
        column = 0
        for j in range(joints):
            local = np.tile([0, 0, 0, 1.0], (count, 1))
            offset = np.tile(self.offset[j], (count, 1))
            for channel in self.channels[j]:
                values = self.data[:, column]
                column += 1
                if channel.endswith("position"):
                    offset[:, "XYZ".index(channel[0])] = values
                else:
                    axis = np.zeros(3)
                    axis["XYZ".index(channel[0])] = 1
                    local = qmul(local, qaxis(axis, np.radians(values)))
            p = self.parent[j]
            if p < 0:
                rotation[:, j], position[:, j] = local, offset
            else:
                rotation[:, j] = qmul(rotation[:, p], local)
                position[:, j] = position[:, p] + qrot(rotation[:, p], offset)
        return rotation, position * 0.01


class Skeleton:
    """Rest pose of the packaged mannequin, in Godot's skeleton space (Y up, facing +Z)."""

    def __init__(self, path):
        data = pathlib.Path(path).read_bytes()
        length = struct.unpack("<I", data[12:16])[0]
        gltf = json.loads(data[20:20 + length])
        nodes = gltf["nodes"]
        joints = gltf["skins"][0]["joints"]
        parent = {c: i for i, n in enumerate(nodes) for c in n.get("children", [])}
        self.names = [nodes[j]["name"] for j in joints]
        self.parent = {nodes[j]["name"]: nodes[parent[j]]["name"] for j in joints if parent.get(j) in joints}
        self.local_rotation, self.local_position, self.rotation, self.position = {}, {}, {}, {}
        for j in joints:
            name = nodes[j]["name"]
            self.local_rotation[name] = np.array(nodes[j].get("rotation", [0, 0, 0, 1.0]))
            self.local_position[name] = np.array(nodes[j].get("translation", [0, 0, 0.0]))
        for name in self.names:
            p = self.parent.get(name)
            if p is None:
                self.rotation[name], self.position[name] = self.local_rotation[name], self.local_position[name]
            else:
                self.rotation[name] = qmul(self.rotation[p], self.local_rotation[name])
                self.position[name] = self.position[p] + qrot(self.rotation[p], self.local_position[name])

    def leg_length(self, side):
        return (np.linalg.norm(self.position[f"calf_{side}"] - self.position[f"thigh_{side}"]) +
                np.linalg.norm(self.position[f"foot_{side}"] - self.position[f"calf_{side}"]))


# ------------------------------------------------------------------ cycles

def heading(rotation):
    forward = qrot(rotation, np.array([0, 0, 1.0]))
    return np.unwrap(np.arctan2(forward[..., 0], forward[..., 2]))


def find_cycles(bvh, rotation, position, travel_degrees):
    """Left-touchdown to left-touchdown cycles traveling straight in the wanted body-relative direction."""
    hips, ankle = bvh.index["Hips"], bvh.index["LeftAnkle"]
    yaw = heading(rotation[:, hips])
    height = position[:, ankle, 1]
    floor = np.percentile(height, 2)
    touchdowns = [i for i in range(1, bvh.frames) if height[i] <= floor + 0.03 < height[i - 1]]
    found = []
    for start, end in zip(touchdowns, touchdowns[1:]):
        if not 30 <= end - start <= 110:
            continue
        mean_yaw = yaw[start:end + 1].mean()
        travel = position[end, hips] - position[start, hips]
        travel_yaw = np.arctan2(travel[0], travel[2])
        relative = np.degrees((travel_yaw - mean_yaw + np.pi) % (2 * np.pi) - np.pi)
        error = (relative - travel_degrees + 180) % 360 - 180
        drift = np.degrees(yaw[end] - yaw[start])
        if abs(error) <= DIRECTION_TOLERANCE and abs(drift) <= DRIFT_TOLERANCE and np.hypot(travel[0], travel[2]) > 0.3:
            found.append((start, end, abs(error) + abs(drift)))
    periods = np.array([e - s for s, e, _ in found])
    if len(found) == 0:
        raise SystemExit(f"no cycles travel at {travel_degrees} degrees")
    median = np.median(periods)
    found = [c for c in found if abs((c[1] - c[0]) / median - 1) < 0.15]
    found.sort(key=lambda c: c[2])
    return [(s, e) for s, e, _ in found[:MAX_CYCLES]]


def posture(path):
    """The performer's standing posture: mean heading-free global rotation of each calibrated joint in the idle
    capture. Their natural head carriage and pelvis tilt then map onto the mannequin's own rest posture."""
    bvh = Bvh(path)
    rotation, _ = bvh.forward_kinematics()
    unturn = qaxis([0, 1, 0], -heading(rotation[:, bvh.index["Hips"]]))
    return {source: qmean(qmul(unturn, rotation[:, bvh.index[source]])) for source in CALIBRATED}


def retarget_cycle(bvh, rotation, position, start, end, travel_degrees, skeleton, scale, calibration):
    """One captured cycle as target local rotations and pelvis position, resampled to SAMPLES phases."""
    hips = bvh.index["Hips"]
    frames = np.arange(start, end + 1)
    travel = position[end, hips] - position[start, hips]
    # Turn the whole cycle so it travels exactly in the wanted direction. Heading drift is a pelvis rotation
    # residual, spread over the cycle below with every other bone's.
    turn = np.radians(travel_degrees) - np.arctan2(travel[0], travel[2])
    phase = (frames - start) / (end - start)
    correction = qaxis([0, 1, 0], np.full(len(frames), turn))
    global_rotation = {}
    for target, source in MAP.items():
        source_rotation = rotation[frames, bvh.index[source]]
        if source in calibration:
            source_rotation = qmul(source_rotation, qinv(calibration[source]))
        global_rotation[target] = qmul(qmul(correction, source_rotation), skeleton.rotation[target])
    # In place: remove the straight travel line, keep the captured bob and sway, centered on the root.
    hips_position = position[frames, hips]
    line = hips_position[0] + (hips_position[-1] - hips_position[0]) * phase[:, None]
    in_place = qrot(correction, hips_position - line)
    in_place[:, 1] += line[:, 1].mean()
    in_place[:, [0, 2]] -= in_place[:, [0, 2]].mean(0)
    in_place *= scale
    # Hip joints sit at the source root; place the mannequin pelvis so its thigh midpoint lands there.
    thighs = (skeleton.position["thigh_l"] + skeleton.position["thigh_r"]) / 2
    pelvis_rotation_delta = qmul(global_rotation["pelvis"], qinv(skeleton.rotation["pelvis"]))
    pelvis_position = in_place + qrot(pelvis_rotation_delta, np.broadcast_to(skeleton.position["pelvis"] - thighs, in_place.shape))
    # Local rotations, then resample by phase.
    local = {}
    for target in MAP:
        parent = skeleton.parent[target]
        parent_rotation = global_rotation[parent] if parent in global_rotation else np.broadcast_to(skeleton.rotation[parent], global_rotation[target].shape)
        local[target] = qnorm(qmul(qinv(parent_rotation), global_rotation[target]))
    root = skeleton.parent["pelvis"]
    local_pelvis = qrot(np.broadcast_to(qinv(skeleton.rotation[root]), pelvis_position.shape[:-1] + (4,)),
                        pelvis_position - skeleton.position[root])
    samples = np.linspace(0, 1, SAMPLES + 1)
    out_rotation = {}
    for target, values in local.items():
        # Make the source cycle loop: remove the end/start difference progressively.
        residual = qmul(values[-1], qinv(values[0]))
        values = qmul(qslerp(np.tile([0, 0, 0, 1.0], (len(phase), 1)), np.tile(qinv(residual), (len(phase), 1)), phase), values)
        out_rotation[target] = resample(values, phase, samples)
    out_position = np.stack([np.interp(samples, phase, local_pelvis[:, k]) for k in range(3)], -1)
    stride = np.hypot(travel[0], travel[2]) * scale
    return out_rotation, out_position, stride, (end - start) * bvh.dt


def resample(values, phase, samples):
    out = np.zeros((len(samples), 4))
    for i, t in enumerate(samples):
        k = min(np.searchsorted(phase, t, side="right") - 1, len(phase) - 2)
        u = (t - phase[k]) / (phase[k + 1] - phase[k])
        out[i] = qslerp(values[k], values[k + 1], u)
    return out


def average(cycles):
    rotation = {}
    for target in MAP:
        stacked = np.stack([c[0][target] for c in cycles])
        rotation[target] = np.stack([qmean(stacked[:, i]) for i in range(stacked.shape[1])])
        rotation[target][-1] = rotation[target][0]
    position = np.mean([c[1] for c in cycles], 0)
    position[-1] = position[0]
    return rotation, position


# ------------------------------------------------------------------ output

def numbers(values):
    return ", ".join(f"{v:.6g}" for v in values)


def write_library(path, clips, skeleton_path):
    lines = ['[gd_resource type="AnimationLibrary" format=3]', ""]
    for index, (name, (rotation, position, _, _)) in enumerate(clips.items()):
        lines += [f'[sub_resource type="Animation" id="Animation_{index}"]', f'resource_name = "{name}"',
                  "length = 1.0", "loop_mode = 1"]
        track = 0
        times = np.linspace(0, 1, SAMPLES + 1)
        for bone, values in rotation.items():
            keys = [x for t, q in zip(times, values) for x in (t, 1, *q)]
            lines += track_lines(track, "rotation_3d", f"{skeleton_path}:{bone}", keys)
            track += 1
        keys = [x for t, p in zip(times, position) for x in (t, 1, *p)]
        lines += track_lines(track, "position_3d", f"{skeleton_path}:pelvis", keys)
        lines.append("")
    lines += ["[resource]", "_data = {"]
    lines += [f'&"{name}": SubResource("Animation_{index}"),' for index, name in enumerate(clips)]
    lines += ["}", ""]
    pathlib.Path(path).write_text("\n".join(lines))


def track_lines(track, kind, path, keys):
    prefix = f"tracks/{track}/"
    return [f'{prefix}type = "{kind}"', f"{prefix}imported = false", f"{prefix}enabled = true",
            f'{prefix}path = NodePath("{path}")', f"{prefix}interp = 1", f"{prefix}loop_wrap = true",
            f"{prefix}keys = PackedFloat32Array({numbers(keys)})"]


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("source", help="directory holding the Neutral_*.bvh files")
    parser.add_argument("--output", default=str(ROOT / "assets/third_party/100style/neutral/directional.tres"))
    parser.add_argument("--mannequin", default=str(ROOT / "assets/third_party/quaternius/ual/mannequin.glb"))
    args = parser.parse_args()
    skeleton = Skeleton(args.mannequin)
    clips = {}
    loaded = {}
    idle = pathlib.Path(args.source) / "Neutral_ID.bvh"
    print(f"{idle.name} sha256={hashlib.sha256(idle.read_bytes()).hexdigest()}")
    calibration = posture(idle)
    for name, (kind, travel) in CLIPS.items():
        path = pathlib.Path(args.source) / f"Neutral_{kind}.bvh"
        if kind not in loaded:
            print(f"{path.name} sha256={hashlib.sha256(path.read_bytes()).hexdigest()}")
            bvh = Bvh(path)
            loaded[kind] = (bvh, *bvh.forward_kinematics())
        bvh, rotation, position = loaded[kind]
        source_leg = (np.linalg.norm(bvh.offset[bvh.index["LeftKnee"]]) + np.linalg.norm(bvh.offset[bvh.index["LeftAnkle"]])) * 0.01
        scale = skeleton.leg_length("l") / source_leg
        cycles = [retarget_cycle(bvh, rotation, position, s, e, travel, skeleton, scale, calibration)
                  for s, e in find_cycles(bvh, rotation, position, travel)]
        rotation_mean, position_mean = average(cycles)
        stride = float(np.mean([c[2] for c in cycles]))
        period = float(np.mean([c[3] for c in cycles]))
        clips[name] = (rotation_mean, position_mean, stride, period)
        print(f"{name}: {len(cycles)} cycles, stride {stride:.3f} m, period {period:.3f} s, speed {stride / period:.3f} m/s")
    write_library(args.output, clips, "Mannequin_001/Skeleton3D")
    print(f"wrote {args.output}")


if __name__ == "__main__":
    sys.exit(main())
