#!/usr/bin/env python3
"""Build an isolated parent/candidate runtime with identical optional server packet instrumentation.

All scratch stays in build/. Release references are never copied or rewritten.
"""
import argparse
from pathlib import Path
import shutil
import subprocess
import tarfile
import io

ROOT = Path(__file__).resolve().parents[2]


def copy_probe(source, target, project):
    # Keep the same probe compilable on either side of C1's body-contract relocation.
    code = source.read_text()
    if not (project / 'src/Core/BodyIds.cs').exists():
        code = code.replace('BodyIds.', 'BodySync.').replace('BodyState.State', 'TankLedger.State')
    target.write_text(code)


def prepare(revision, label, observe):
    project = ROOT / 'build' / label / 'project'
    project.mkdir(parents=True, exist_ok=False)
    if revision == 'working':
        shutil.copytree(ROOT / 'src', project / 'src')
        shutil.copyfile(ROOT / 'Propane.csproj', project / 'Propane.csproj')
    else:
        archive = subprocess.check_output(['git', 'archive', revision, 'src', 'Propane.csproj'], cwd=ROOT)
        with tarfile.open(fileobj=io.BytesIO(archive)) as entries:
            entries.extractall(project, filter='data')
    # The same harness on both revisions, without changes to production in the checkout.
    for name in ('Fixtures/ServerObservation.cs', 'ServerRunBenchTests.cs',
                 'ClientReceiveBenchTests.cs', 'DecodeTranscriptTests.cs', 'TransitionBoundaryTests.cs'):
        source = ROOT / 'src/Dev/UnitTests' / name
        if source.exists():
            target = project / 'src/Dev/UnitTests' / name
            target.parent.mkdir(parents=True, exist_ok=True)
            copy_probe(source, target, project)
    if observe:
        source = project / 'src/Net/LobbyServer.cs'
        code = source.read_text()
        for signature, call in (
            ('internal void OnReceived(int from, byte[] data, TrafficClass? incoming = null)',
             'Propane.Dev.UnitTests.Fixtures.ServerObservation.Packet("in", from, data, incoming);'),
            ('private void Send(int peer, byte[] bytes, TrafficClass traffic = TrafficClass.ReliableOrdered)',
             'Propane.Dev.UnitTests.Fixtures.ServerObservation.Packet("out", peer, bytes, traffic);'),
        ):
            needle = signature + '\n    {'
            assert code.count(needle) == 1, signature
            code = code.replace(needle, needle + '\n        ' + call)
        source.write_text(code)
    for name in ('assets', 'scenes', 'shaders', 'icon.svg', 'icon.svg.import', 'tuning.tres'):
        (project / name).symlink_to(ROOT / name, target_is_directory=(ROOT / name).is_dir())
    for name in ('project.godot', 'override.cfg', '.editorconfig'):
        shutil.copyfile(ROOT / name, project / name)
    cache = project / '.godot'
    cache.mkdir()
    (cache / 'imported').symlink_to(ROOT / '.godot/imported', target_is_directory=True)
    for name in ('uid_cache.bin', 'global_script_class_cache.cfg'):
        shutil.copyfile(ROOT / '.godot' / name, cache / name)
    subprocess.run(['dotnet', 'build', 'Propane.csproj', '-c', 'Debug', '-nologo', '-v', 'quiet', '-warnaserror'],
                   cwd=project, check=True)
    print(project)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--label', required=True, help='fresh directory name below build/')
    parser.add_argument('--observe', action='store_true')
    args = parser.parse_args()
    assert Path(args.label).name == args.label and args.label not in ('.', '..')
    prepare(args.revision, args.label, args.observe)
