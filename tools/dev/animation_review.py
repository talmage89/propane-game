#!/usr/bin/env python3
"""Prepare immutable builds and assemble animation-review media (Pillow/ffmpeg only for rendering)."""
import argparse
import hashlib
import html
import io
import itertools
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[2]
FILES = ('src/Dev/AnimationReview.cs', 'src/Dev/AnimationReviewProbe.cs',
         'scenes/dev/animation_review.tscn')


def run(args, directory, log):
    env = dict(os.environ, TMPDIR=str(directory / 'build' / 'review-tmp'))
    Path(env['TMPDIR']).mkdir(parents=True, exist_ok=True)
    with log.open('w') as stream:
        subprocess.run(args, cwd=directory, env=env, stdout=stream, stderr=subprocess.STDOUT, check=True)


def prepare(ref, projects):
    sha = subprocess.check_output(['git', 'rev-parse', '--verify', '--end-of-options', ref + '^{commit}'], cwd=ROOT, text=True).strip()
    target = projects / sha[:12]
    if not (target / 'project.godot').exists():
        target.mkdir(parents=True, exist_ok=True)
        archive = subprocess.check_output(['git', 'archive', sha], cwd=ROOT)
        with tarfile.open(fileobj=io.BytesIO(archive)) as source:
            source.extractall(target, filter='data')
    release = sha == subprocess.check_output(['git', 'rev-parse', 'v0.2.0'], cwd=ROOT, text=True).strip()
    modern = (target / 'src/ReleaseSmoke/CaptureDirector.cs').exists()
    for name in FILES:
        (target / name).parent.mkdir(parents=True, exist_ok=True)
        text = (ROOT / name).read_text()
        (target / name).write_text(text if modern else text.replace('using Propane.ReleaseSmoke;\n', ''))
    # The release predates safe managed-render shutdown and recording. Only transplant the dev capture helper.
    capture = (ROOT / 'src/ReleaseSmoke/CaptureDirector.cs').read_text()
    if not modern:
        capture = capture.replace('namespace Propane.ReleaseSmoke;', 'namespace Propane.Dev;').replace('LaunchArgs.Current.', 'DevArgs.')
    if release:
        capture = capture.replace('ShutdownGuard.Quit(GetTree())', 'GetTree().Quit()')
        capture = capture.replace('ShutdownGuard.Quit(GetTree(), 1)', 'GetTree().Quit(1)')
        capture = capture.replace('ShutdownGuard.Quit(tree, 1)', 'tree.Quit(1)')
        project = target / 'Propane.csproj'
        text = project.read_text()
        if 'PROPANE_REVIEW_RELEASE' not in text:
            text = text.replace('<PropertyGroup>', '<PropertyGroup>\n    <DefineConstants>$(DefineConstants);PROPANE_REVIEW_RELEASE</DefineConstants>\n    <DefaultItemExcludes>$(DefaultItemExcludes);build/**</DefaultItemExcludes>', 1)
            project.write_text(text)
    capture_path = 'src/ReleaseSmoke/CaptureDirector.cs' if modern else 'src/Dev/CaptureDirector.cs'
    (target / capture_path).write_text(capture)
    # Never load local/saved tuner files. The scene constructs code-default tuners, normalized aim speed on release.
    (target / 'build').mkdir(exist_ok=True)
    (target / 'build/.gdignore').touch()
    if release:
        (target / 'override.cfg').write_text('[application]\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name="Propane-review/' + sha[:12] + '"\n')
        run([shutil.which('godot-mono'), '--headless', '--path', str(target), '--import'], target, target / 'build/review-import.log')
        run(['make', 'build'], target, target / 'build/review-setup.log')
    else:
        run(['make', 'setup'], target, target / 'build/review-setup.log')
    return target, sha


def capture_frames(directory, name, expected):
    frames = sorted((p for p in directory.glob(name + '_*.png') if p.stem[len(name) + 1:].isdigit()),
                    key=lambda p: int(p.stem[len(name) + 1:]))
    wanted = [f'{name}_{i:03}.png' for i in range(1, expected + 1)]
    if [p.name for p in frames] != wanted:
        raise ValueError(f'{directory}/{name}: need {expected} consecutive frames, got {len(frames)}')
    return frames


def assemble(builds, output):
    from PIL import Image, ImageDraw, ImageOps
    for label, path in builds:
        manifest = json.loads((path / 'cases.json').read_text())
        for case in manifest:
            name = case['name']
            for view in ('side', 'player'):
                expected = math.ceil((case['end'] - case['start']) * 12 - .001) - 1
                frames = capture_frames(path / view, name, expected)
                sheet = Image.new('RGB', (960, 3 * 294), '#ddd')
                draw = ImageDraw.Draw(sheet)
                for i in range(6):
                    last = len(frames) - 1
                    if 'clip_seconds' in case and not case['loop']:
                        last = min(last, max(1, math.ceil(case['clip_seconds'] * 12) - 2))
                    frame = frames[i * last // 5]
                    with Image.open(frame) as image:
                        sheet.paste(ImageOps.contain(image.convert('RGB'), (480, 270)), ((i % 2) * 480, (i // 2) * 294))
                    draw.text(((i % 2) * 480 + 4, (i // 2) * 294 + 274), f'{label} / {view} / {name} / {frame.stem}', fill='black')
                sheet.save(path / f'{view}-{name}.jpg', quality=90)
                # Fixed-step captured frames yield exact playback cadence, independent of competing GPU load.
                subprocess.run(['ffmpeg', '-loglevel', 'error', '-y', '-framerate', '12', '-start_number', '1',
                                '-i', str(path / view / (name + '_%03d.png')), '-frames:v', str(expected), '-c:v', 'libx264', '-pix_fmt', 'yuv420p',
                                '-movflags', '+faststart', str(path / f'{view}-{name}.mp4')], check=True)
    for (left_label, left), (right_label, right) in itertools.combinations(builds, 2):
        paired = output / (left_label + '--' + right_label)
        paired.mkdir(exist_ok=True)
        for sheet in left.glob('*.jpg'):
            other = right / sheet.name
            if not other.exists():
                continue
            with Image.open(sheet) as a, Image.open(other) as b:
                result = Image.new('RGB', (a.width + b.width, max(a.height, b.height)), '#ddd')
                result.paste(a, (0, 0))
                result.paste(b, (a.width, 0))
                result.save(paired / sheet.name, quality=90)
    data = {label: {'path': path.relative_to(output).as_posix(), 'cases': json.loads((path / 'cases.json').read_text())}
            for label, path in builds}
    # One index accepts any number of runs, choosing any two at playback time. No network or CDN dependencies.
    document = '''<!doctype html><meta charset="utf-8"><title>Propane animation review</title>
<style>body{font:16px sans-serif;background:#222;color:#eee}main{display:flex;gap:1em}article{width:49%}video,img{width:100%}select,button{margin:.5em}small{display:block}</style>
<h1>Animation review</h1><p>Fixed-step 12 fps films. Select any two runs. Controller builds use identical inputs and code-default tuners; external clips are stationary previews. Each run has provenance.json.</p>
<label>Left <select id="left"></select></label><label>Right <select id="right"></select></label>
<select id="view"><option>side</option><option>player</option></select><select id="motion"></select>
<label><input id="independent" type="checkbox">Compare different clips</label><select id="rightMotion" disabled></select>
<button id="play">Restart both</button><button id="slow">Half speed</button><main></main>
<script>
const runs=DATA;
for(const id of ['left','right'])for(const label of Object.keys(runs))document.getElementById(id).add(new Option(label,label));
document.getElementById('right').selectedIndex=Math.min(1,Object.keys(runs).length-1);
for(const c of [...new Map(Object.values(runs).flatMap(r=>r.cases).map(c=>[c.name,c])).values()])for(const id of ['motion','rightMotion'])document.getElementById(id).add(new Option(c.name,c.name));
function show(){const main=document.querySelector('main');main.replaceChildren();for(const id of ['left','right']){const label=document.getElementById(id).value;const c=document.getElementById(id==='right'&&document.getElementById('independent').checked?'rightMotion':'motion').value;const v=document.getElementById('view').value;const article=document.createElement('article');const title=document.createElement('h2');title.textContent=label+' / '+v+' / '+c;article.append(title);if(runs[label].cases.some(x=>x.name===c&&x.available===false)){const note=document.createElement('small');note.textContent='Unavailable in this build; the recorded slot is idle.';article.append(note);}if(!runs[label].cases.some(x=>x.name===c)){const p=document.createElement('p');p.textContent='Unavailable in this run';article.append(p);main.append(article);continue;}const video=document.createElement('video');video.controls=true;video.loop=true;video.muted=true;video.src=runs[label].path+'/'+v+'-'+c+'.mp4';article.append(video);const image=document.createElement('img');image.src=runs[label].path+'/'+v+'-'+c+'.jpg';image.alt=title.textContent+' contact sheet';article.append(image);main.append(article);}}
for(const s of document.querySelectorAll('select'))s.onchange=show;document.getElementById('independent').onchange=()=>{document.getElementById('rightMotion').disabled=!document.getElementById('independent').checked;show();};
document.getElementById('play').onclick=()=>{for(const v of document.querySelectorAll('video')){v.currentTime=0;v.play();}};
document.getElementById('slow').onclick=()=>{for(const v of document.querySelectorAll('video'))v.playbackRate=v.playbackRate===.5?1:.5;};show();
</script>'''.replace('DATA', json.dumps(data).replace('<', '\\u003c'))
    recordings = sorted(output.glob('actions-*.mp4'))
    if recordings:
        links = ' '.join(f'<a href="{html.escape(p.name)}">{html.escape(p.stem)}</a>' for p in recordings)
        document = document.replace('<main>', '<p>Real-time action recordings: ' + links + '</p><main>')
    (output / 'index.html').write_text(document)
    (output / 'runs.json').write_text(json.dumps(data, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', action='append', default=[], metavar='LABEL=REF')
    parser.add_argument('--output', type=Path, default=ROOT / 'build/animation-review')
    parser.add_argument('--metrics-only', action='store_true')
    parser.add_argument('--assemble-only', action='store_true')
    parser.add_argument('--cases', default='')
    parser.add_argument('--candidate', metavar='LABEL=GLB', help='preview an external library on the packaged mannequin')
    parser.add_argument('--anims', default='Walk_Loop', help='candidate names, comma-separated; @reverse derives backward playback')
    parser.add_argument('--hands', action='store_true', help='candidate preview retains current rifle hand solve')
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / 'build'):
        parser.error('output must be inside this checkout’s build/')
    output.mkdir(parents=True, exist_ok=True)
    os.environ['TMPDIR'] = str(output / 'tmp')
    Path(os.environ['TMPDIR']).mkdir(exist_ok=True)
    if not args.build and not args.candidate:
        parser.error('at least one --build or --candidate is required')
    builds = []
    godot = shutil.which('godot-mono')
    if not godot:
        parser.error('godot-mono must be on PATH')
    for build in args.build:
        label, ref = build.split('=', 1)
        if not label or any(c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_' for c in label):
            parser.error('labels use letters, digits, hyphen or underscore')
        path = output / label
        path.mkdir(exist_ok=True)
        builds.append((label, path))
        if args.assemble_only:
            continue
        project, sha = prepare(ref, output / 'projects')
        (path / 'provenance.json').write_text(json.dumps({'ref': ref, 'sha': sha, 'harness_sha256': hashlib.sha256(b''.join((ROOT / f).read_bytes() for f in FILES)).hexdigest()}, indent=2))
        common = [godot, '--path', str(project), '--fixed-fps', '60', 'res://scenes/dev/animation_review.tscn', '--', f'--label={label}']
        if args.cases:
            common.append('--cases=' + args.cases)
        if args.metrics_only:
            run(common[:1] + ['--headless'] + common[1:] + [f'--metrics={path}/metrics.csv', f'--manifest={path}/cases.json'], project, path / 'metrics.log')
        else:
            for view in ('side', 'player'):
                frames = path / view
                frames.mkdir(exist_ok=True)
                run(common[:1] + ['--resolution', '960x540', '--disable-vsync'] + common[1:] + [f'--view={view}', f'--capture-dir={frames}', f'--manifest={path}/cases.json'], project, path / f'{view}.log')
    if args.candidate:
        if args.build or args.metrics_only or args.assemble_only:
            parser.error('--candidate is a separate rendered operation')
        label, source = args.candidate.split('=', 1)
        if not label or any(c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_' for c in label):
            parser.error('invalid candidate label')
        path = output / label
        path.mkdir(exist_ok=True)
        run(['make', 'build'], ROOT, path / 'build.log')
        source = Path(source).resolve()
        (path / 'provenance.json').write_text(json.dumps({'source': source.name, 'sha256': hashlib.sha256(source.read_bytes()).hexdigest(), 'anims': args.anims, 'hands': args.hands, 'production_sha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
            'harness_sha256': hashlib.sha256((ROOT / 'src/Dev/AnimationClipReview.cs').read_bytes()).hexdigest()}, indent=2))
        for view in ('side', 'player'):
            frames = path / view
            frames.mkdir(exist_ok=True)
            command = [godot, '--resolution', '960x540', '--disable-vsync', '--fixed-fps', '60', '--path', str(ROOT),
                       'res://scenes/dev/animation_clip_review.tscn', '--', '--library=' + str(source), '--anims=' + args.anims,
                       '--view=' + view, '--label=' + label, '--capture-dir=' + str(frames), '--manifest=' + str(path / 'cases.json')]
            if args.hands:
                command.append('--hands')
            run(command, ROOT, path / (view + '.log'))
        builds.append((label, path))
    if not args.metrics_only:
        if not builds:
            parser.error('at least one --build is required')
        assemble(builds, output)


if __name__ == '__main__':
    main()
