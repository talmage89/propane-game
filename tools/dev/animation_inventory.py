#!/usr/bin/env python3
"""Inventory the official UAL Standard GLBs and root-motion variants; no engine or optional dependencies."""
import argparse
import csv
import json
import math
from pathlib import Path
import struct


def read_glb(path):
    data = path.read_bytes()
    magic, version, length = struct.unpack_from('<4sII', data)
    if magic != b'glTF' or version != 2 or length != len(data):
        raise ValueError('invalid GLB: ' + str(path))
    count, kind = struct.unpack_from('<II', data, 12)
    if kind != 0x4e4f534a:
        raise ValueError('missing JSON chunk')
    model = json.loads(data[20:20 + count])
    count, kind = struct.unpack_from('<II', data, 20 + count)
    if kind != 0x004e4942:
        raise ValueError('missing binary chunk')
    binary = data[-count:]
    return model, binary


def accessor(model, binary, index):
    value = model['accessors'][index]
    view = model['bufferViews'][value['bufferView']]
    codes = {5126: 'f', 5125: 'I', 5123: 'H', 5121: 'B'}
    components = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}[value['type']]
    fmt = '<' + codes[value['componentType']] * components
    offset = view.get('byteOffset', 0) + value.get('byteOffset', 0)
    stride = view.get('byteStride', struct.calcsize(fmt))
    return [struct.unpack_from(fmt, binary, offset + i * stride) for i in range(value['count'])]


def animations(path):
    model, binary = read_glb(path)
    root = next(i for i, node in enumerate(model['nodes']) if node.get('name') == 'root')
    result = {}
    for animation in model.get('animations', []):
        time = max(max(v[0] for v in accessor(model, binary, sampler['input'])) for sampler in animation['samplers'])
        tracks = [c for c in animation['channels'] if c['target']['node'] == root]
        translation = next((c for c in tracks if c['target']['path'] == 'translation'), None)
        if translation:
            values = accessor(model, binary, animation['samplers'][translation['sampler']]['output'])
            displacement = [values[-1][i] - values[0][i] for i in range(3)]
            span = [max(v[i] for v in values) - min(v[i] for v in values) for i in range(3)]
        else:
            displacement = span = [0, 0, 0]
        rotation = next((c for c in tracks if c['target']['path'] == 'rotation'), None)
        turn = 0
        if rotation:
            values = accessor(model, binary, animation['samplers'][rotation['sampler']]['output'])
            dot = abs(sum(a * b for a, b in zip(values[0], values[-1]))) / math.sqrt(sum(a*a for a in values[0]) * sum(a*a for a in values[-1]))
            turn = math.degrees(2 * math.acos(min(1, dot)))
        result[animation['name']] = {'seconds': time, 'root_delta': displacement, 'root_span': span, 'root_turn_degrees': turn}
    return result


def classify(name):
    direction = 'forward' if name in ('Walk_Loop', 'Walk_Formal_Loop', 'Jog_Fwd_Loop', 'Sprint_Loop', 'Crouch_Fwd_Loop',
                                    'Walk_Carry_Loop', 'Zombie_Walk_Fwd_Loop', 'Swim_Fwd_Loop') else 'non-directional action / stationary'
    carry = 'two-hand box carry; no rifle' if name == 'Walk_Carry_Loop' else 'two-hand push; no rifle' if name == 'Push_Loop' else 'pistol' if name.startswith('Pistol_') else 'one-hand sword / shield' if name.startswith(('Sword_', 'Shield_')) else 'no rifle'
    return direction, carry


def travel_direction(delta):
    x, y, z = delta
    if math.hypot(x, z) < .01:
        return 'vertical action' if abs(y) >= .01 else 'stationary'
    angle = (math.degrees(math.atan2(x, z)) + 360 + 22.5) % 360
    return ('forward', 'forward-right', 'right', 'back-right', 'back', 'back-left', 'left', 'forward-left')[int(angle // 45)]


def inventory(sources):
    result = []
    for number in (1, 2):
        matches = list((sources / f'UAL{number}-Standard').rglob(f'UAL{number}_Standard.glb'))
        if len(matches) != 1:
            raise ValueError(f'need exactly one official UAL{number}_Standard.glb')
        path = matches[0]
        regular = animations(path)
        motion = animations(path.with_name(f'UAL{number}_Standard_RM.glb'))
        if regular.keys() != motion.keys():
            raise ValueError('root-motion and in-place inventories differ')
        for name, value in regular.items():
            direction, carry = classify(name)
            result.append({'library': f'UAL{number}', 'name': name, 'seconds': round(value['seconds'], 6),
                           'direction': direction, 'carry': carry, 'root_travel_direction': travel_direction(motion[name]['root_delta']),
                           'in_place_root_delta_m': ','.join(f'{v:.6f}' for v in value['root_delta']),
                           'rm_root_delta_m': ','.join(f'{v:.6f}' for v in motion[name]['root_delta']),
                           'rm_root_span_m': ','.join(f'{v:.6f}' for v in motion[name]['root_span']),
                           'rm_root_turn_degrees': round(motion[name]['root_turn_degrees'], 3)})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('sources', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    rows = inventory(args.sources)
    with args.output.open('w', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=rows[0].keys(), lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)
    print(f'PASS {len(rows)} clips plus their root-motion variants')


if __name__ == '__main__':
    main()
