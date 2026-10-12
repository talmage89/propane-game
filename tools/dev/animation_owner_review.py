#!/usr/bin/env python3
"""Compose the owner's three-build, two-camera animation clips from review media (optional ffmpeg)."""
import argparse
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]
CASES = {
    'walk_000': 'Alternating legs, weight transfer, head motion and a low rifle carry.',
    'creep_180': 'Backpedal while aiming forward; hips stay with aim.',
    'creep_270': 'Left strafe while aiming forward; knees and stock stay clear.',
    'creep_090': 'Right strafe while aiming forward; compare with the left strafe.',
    'creep_045': 'Diagonal travel while aiming forward; planted feet and limb clearance.',
    'creep_000': 'Slow aimed walk; head motion, shoulder pocket and muzzle stability.',
    'jog_000': 'Run cadence, alternating legs and upper-body weight.',
    'sprint': 'Forward sprint, flight phase, low ready and forearm/thigh clearance.',
    'start_stop': 'Idle-to-movement blend and settling after release.',
    'turn': 'Standing half-turn and return; both feet lift.',
    'jump_land': 'Takeoff, held aim suppression, landing and recovery.',
    'burst': 'Aimed burst: climb, shoulder/head clearance and attached hands.',
    'hip_burst': 'First hip shot: small carry-to-fire lift, then sustained fire.',
    'reload': 'Support-hand magazine contact and smooth return to the foregrip.',
    'melee': 'Windup, contact and return; release has no melee and its slot is labeled.',
}


def compose(source, output, cases):
    output.mkdir(parents=True, exist_ok=True)
    provenance = {}
    for label in ('release', 'dev', 'candidate'):
        provenance[label] = json.loads((source / label / 'provenance.json').read_text())
    if len({entry['harness_sha256'] for entry in provenance.values()}) != 1:
        raise ValueError('comparison sources use different review harnesses')
    (output / 'provenance.json').write_text(json.dumps(provenance, indent=2) + '\n')
    readme = ['# Character animation review', '',
              'Columns: **v0.2.0 | dev | wp/anim-1**. Player camera above, side camera below.',
              'Fixed-step frames, H.264 at 1280×720. Sources and exact commits are in `provenance.json`.',
              'The release melee slot is unavailable. Code defaults are used; release creep is normalized to 1.6 m/s.', '',
              '| File | Look for |', '| --- | --- |']
    for case in cases:
        inputs = [source / label / f'{view}-{case}.mp4'
                  for view in ('player', 'side') for label in ('release', 'dev', 'candidate')]
        if any(not path.is_file() for path in inputs):
            raise ValueError(f'missing media for {case}')
        command = ['ffmpeg', '-loglevel', 'error', '-y', '-filter_complex_threads', '1']
        for path in inputs:
            command += ['-i', str(path)]
        filters = [f'[{i}:v]scale=426:320:force_original_aspect_ratio=decrease,'
                   f'pad=426:320:(ow-iw)/2:(oh-ih)/2,setsar=1[c{i}]' for i in range(6)]
        filters += ['[c0][c1][c2]hstack=inputs=3[top]', '[c3][c4][c5]hstack=inputs=3[bottom]',
                    '[top][bottom]vstack=inputs=2,pad=1280:720:1:40:black,'
                    "drawtext=text='v0.2.0':x=12:y=7:fontsize=24:fontcolor=white,"
                    "drawtext=text='dev':x=440:y=7:fontsize=24:fontcolor=white,"
                    "drawtext=text='wp/anim-1':x=866:y=7:fontsize=24:fontcolor=white,"
                    "drawtext=text='Player camera':x=12:y=42:fontsize=16:fontcolor=white:box=1:boxcolor=black@0.6,"
                    "drawtext=text='Side camera':x=12:y=362:fontsize=16:fontcolor=white:box=1:boxcolor=black@0.6,"
                    f"drawtext=text='{case}':x=12:y=691:fontsize=20:fontcolor=white[out]"]
        path = output / f'{case}.mp4'
        command += ['-filter_complex', ';'.join(filters), '-map', '[out]', '-an', '-c:v', 'libx264',
                    '-threads', '2', '-preset', 'medium', '-crf', '20', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', str(path)]
        subprocess.run(command, check=True)
        metadata = json.loads(subprocess.check_output(
            ['ffprobe', '-v', 'error', '-show_streams', '-of', 'json', str(path)], text=True))['streams'][0]
        if metadata['codec_name'] != 'h264' or (metadata['width'], metadata['height']) != (1280, 720) or path.stat().st_size >= 8_000_000:
            raise ValueError(f'invalid owner clip format or size: {path.name}')
        readme.append(f'| [{path.name}]({path.name}) | {CASES[case]} |')
    (output / 'README.md').write_text('\n'.join(readme) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('--output', type=Path, default=ROOT / 'build/owner-review')
    parser.add_argument('--cases', default=','.join(CASES))
    args = parser.parse_args()
    source, output = args.source.resolve(), args.output.resolve()
    if not source.is_relative_to(ROOT / 'build') or not output.is_relative_to(ROOT / 'build'):
        parser.error('source and output must be inside this checkout’s build/')
    cases = args.cases.split(',')
    if not cases or any(case not in CASES for case in cases):
        parser.error('unknown owner review case')
    compose(source, output, cases)


if __name__ == '__main__':
    main()
