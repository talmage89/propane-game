#!/usr/bin/env python3
"""Prepare identical opt-in actor/packet ordering observers on the parent and candidate under build/."""
import argparse
import shutil
import subprocess
from pathlib import Path

from server_equivalence import ROOT, copy_probe, prepare


def insert(path, needle, addition):
    code = path.read_text()
    assert code.count(needle) == 1, (path, needle)
    path.write_text(code.replace(needle, needle + '\n' + addition))


def observe(project):
    # These fixtures use only parent-compatible APIs; do not copy candidate-only registration tests.
    for name in ('ActorRetirementObservation.cs', 'ActorLifetimeFixture.cs'):
        copy_probe(ROOT / 'src/Dev/UnitTests/Fixtures' / name, project / 'src/Dev/UnitTests/Fixtures' / name, project)
    for name in ('ActorTacticsTests.cs', 'ActorTickBenchTests.cs', 'BotTacticsTests.cs', 'Fixtures/TacticalWorld.cs'):
        copy_probe(ROOT / 'src/Dev/UnitTests' / name, project / 'src/Dev/UnitTests' / name, project)
    cycles(project)
    call = 'Propane.Dev.UnitTests.Fixtures.ActorRetirementObservation.Record'
    session = project / 'src/Session/SessionContext.cs'
    insert(session, 'retired.Add(actor.Character.ActorId);',
           f'        if (World.ViewRoot is Propane.Net.Match match) {call}(match, "removed", actor.Character.ActorId);')
    match = project / 'src/Net/Match.cs'
    insert(match, 'var left = (PlayerLeftMessage)message.Payload;', f'                {call}(this, "departure");')
    insert(match, 'bodies.Host = left.Host;', f'                {call}(this, "transfer");')
    insert(match, 'private void StopReplication()\n    {', f'        {call}(this, "stop-replication");')
    assembly = project / 'src/Net/Match.Assembly.cs'
    insert(assembly, 'World.Events.LocalBlast += OnLocalBlast;', f'        {call}(this, "attached");')
    shutil.copytree(ROOT / 'tools/dev', project / 'tools/dev', dirs_exist_ok=True)


def cycles(project):
    # Transplant the exact gate assertions without the candidate-only attachment API used by its other cases.
    code = (ROOT / 'src/Dev/UnitTests/ActorLifetimeTests.cs').read_text()
    start = '        foreach (var (roaming, king)'
    end = '        runner.Run("actor lifetime/primary actor'
    assert code.count(start) == code.count(end) == 1
    body = code[code.index(start):code.index(end)]
    wrapper = '''using System;
using System.Linq;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;
using static Propane.Dev.UnitTests.TestRunner;
namespace Propane.Dev.UnitTests;
[UnitSuite]
public static class ActorLifetimeCycleProbe
{
    public static void Register(TestRunner runner)
    {
''' + body + '''    }
}
'''
    (project / 'src/Dev/UnitTests/ActorLifetimeCycleProbe.cs').write_text(wrapper)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--label', required=True)
    args = parser.parse_args()
    assert Path(args.label).name == args.label and args.label not in ('.', '..')
    prepare(args.revision, args.label, True)
    project = ROOT / 'build' / args.label / 'project'
    observe(project)
    subprocess.run(['dotnet', 'build', 'Propane.csproj', '-c', 'Debug', '-nologo', '-v', 'quiet', '-warnaserror'], cwd=project, check=True)
    print(project)
