#!/usr/bin/env python3
"""Prepare FU2a observation in an isolated parent source build; never patch checkout runtime code.

The opt-in --vent-residual-dir=ABSOLUTE_PATH writes JSONL per peer/match. Run the copied
net_bots.sh in the printed project. Original correction and fixture endings stay unchanged.
"""
import argparse
import difflib
import hashlib
import json
import re
from pathlib import Path
import shutil
import subprocess

from server_equivalence import ROOT, prepare

CALL = 'Propane.Dev.NetScenarios.VentResidualObservation'


def insert(path, needle, statement, before=False):
    code = path.read_text()
    if code.count(needle) != 1:
        raise ValueError(f'{path.name}: expected one observation anchor: {needle}')
    start = code.index(needle)
    line = code.rfind('\n', 0, start) + 1
    point = line if before else code.index('\n', start + len(needle)) + 1
    indentation = point if not before and needle.rstrip().endswith('{') else line
    indent = re.match(r' *', code[indentation:]).group()
    path.write_text(code[:point] + indent + statement.strip() + '\n' + code[point:])


def observe(project):
    if CALL in (project / 'src/Net/BodySync.cs').read_text():
        raise ValueError('source already observed')
    source = ROOT / 'src/Dev/NetScenarios/VentResidualObservation.cs'
    shutil.copyfile(source, project / source.relative_to(ROOT))
    # The controlled acceleration probe is a new Dev case; existing fixture endings stay untouched.
    for name in ('src/Dev/NetScenarios/BallisticScenario.cs', 'src/Dev/NetScenarios/NetScenarioRegistry.cs',
                 'src/Dev/UnitTests/NetScenarioTests.cs'):
        target = project / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / name, target)
    body = project / 'src/Net/BodySync.cs'
    insert(body, 'entries[id] = entry;', f'        {CALL}.Record(entry, "register");')
    insert(body, 'resolved = authority;', f'        {CALL}.Authority(this, authority);')
    insert(body, 'public void OnState(int sender, BodyState state, bool rest, double now, double transit = 0)\n    {',
           f'        {CALL}.Receipt(this, sender, state, rest, now, transit);')
    insert(body, 'entry.SettledMismatchSince = null;\n                continue;\n            }\n            var body = entry.Body;',
           f'                {CALL}.Record(entry, "decision-skip", !entry.HasTarget ? "no-target" : entry.Owner == Me ? "owner" : entry.ClaimPending && !entry.ObserveWhilePending ? "predicted-claim" : "invalid-body");', before=True)
    insert(body, 'Trace?.Invoke(entry, $"stale age={age:0.000} quiet={now - entry.TargetUpdated:0.000}");',
           f'                {CALL}.Record(entry, "decision-stale");')
    decision = f'{CALL}.Decision(entry, REASON, targetCenter, targetRotation, error, angle, lead);'
    linear_assignment = 'body.LinearVelocity = targetLinear + error / blend;' if 'var targetLinear = entry.TargetLinear;' in body.read_text() else 'body.LinearVelocity = entry.TargetLinear + error / blend;'
    for needle, reason in (
        ('var weight = Mathf.Clamp((float)body.GetPhysicsProcessDeltaTime() / blend, 0, 1);', 'gather-blend'),
        ('Snap(body, entry.Target, entry.TargetLinear, entry.TargetAngular);', 'obstructed-snap'),
        ('if (!body.Sleeping || !current.IsEqualApprox(pose))', 'rest-anchor'),
        (linear_assignment, 'velocity-blend'),
    ):
        insert(body, needle, decision.replace('REASON', '"' + reason + '"'), before=True)
    insert(body, 'if (now - entry.SettledMismatchSince >= blend * 3)\n                {',
           decision.replace('REASON', '"settled-contact-snap"'))
    insert(body, 'if (error.Length() > tuning.SnapDistance)\n            {',
           decision.replace('REASON', '"distance-snap"'))
    match = project / 'src/Net/Match.cs'
    # Observe the same callback boundary before and after C2 moves body ownership out of Match.
    if 'worldReplica.Correct(now);' in match.read_text():
        insert(match, 'worldReplica.Correct(now);', f'        foreach (var observed in worldReplica.Bodies.Entries) {{ {CALL}.Record(observed, "after-correct"); }}')
    else:
        insert(match, 'bodies.Correct(now);', f'        foreach (var observed in bodies.Entries) {{ {CALL}.Record(observed, "after-correct"); }}')
    publisher = project / 'src/Net/BodyPublisher.cs'
    insert(publisher, 'writer.UInt(entry.Id).Transform(entry.Body.GlobalTransform).Vec3(entry.Body.LinearVelocity).Vec3(entry.Body.AngularVelocity)\n                            .UInt(entry.Epoch).UInt(checked(++entry.Sequence));',
           f'                        {CALL}.Record(entry, "publication", stream.Traffic.ToString());')
    insert(publisher, 'send(new NetWriter(Msg.BodyRest, matchId).UInt(entry.Id).Transform(entry.Body.GlobalTransform).Double(now)\n                    .UInt(entry.Epoch).UInt(checked(++entry.Sequence)), TrafficClass.ReliableOrdered);',
           f'                {CALL}.Record(entry, "publication-rest");')
    host = project / 'src/Dev/NetTest.cs'
    insert(host, 'finishMatch = () =>', f'        {CALL}.Attach(match);', before=True)
    insert(host, 'LogTankCheck(completion: true);', f'            {CALL}.Close();')
    insert(host, 'public override void _ExitTree()\n    {', f'        {CALL}.Close();')
    insert(host, 'Log(FormattableString.Invariant($"tank-{kind} match {match.Setup.Id} time {sampleTime:0.000000} phase {phase} final {final || completion} length {match.Setup.Length:0.000} n {count}{tanks}"));',
           f'        {CALL}.Measurement(match, sampleTime, kind);')
    tools = project / 'tools/dev'
    tools.mkdir(parents=True)
    for name in ('net_bots.sh', 'compare_tanks.py', 'check_scores.py'):
        shutil.copy2(ROOT / 'tools/dev' / name, tools / name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--label', required=True)
    args = parser.parse_args()
    if Path(args.label).name != args.label or args.label in ('.', '..'):
        parser.error('label must be a fresh directory name under build/')
    prepare(args.revision, args.label, False)
    project = ROOT / 'build' / args.label / 'project'
    paths = sorted((project / 'src').rglob('*.cs'))
    before = {str(p.relative_to(project)): p.read_bytes() for p in paths}
    observe(project)
    changes = {name: dict(before=hashlib.sha256(raw).hexdigest(), after=hashlib.sha256((project / name).read_bytes()).hexdigest())
               for name, raw in before.items() if raw != (project / name).read_bytes()}
    expected = {'src/Net/BodySync.cs', 'src/Net/BodyPublisher.cs', 'src/Net/Match.cs', 'src/Dev/NetTest.cs',
                'src/Dev/NetScenarios/NetScenarioRegistry.cs', 'src/Dev/UnitTests/NetScenarioTests.cs'}
    optional = {'src/Dev/NetScenarios/NetScenarioRegistry.cs', 'src/Dev/UnitTests/NetScenarioTests.cs'}
    expected -= {name for name in optional if before[name] == (project / name).read_bytes()}
    if set(changes) != expected:
        raise ValueError(f'unexpected source delta: {set(changes)}')
    (project.parent / 'observation.json').write_text(json.dumps(dict(revision=args.revision, changes=changes, observer_sha256=hashlib.sha256((project / 'src/Dev/NetScenarios/VentResidualObservation.cs').read_bytes()).hexdigest(), tuning_sha256=hashlib.sha256((project / 'tuning.tres').read_bytes()).hexdigest(), new_sources={str(p.relative_to(project)): hashlib.sha256(p.read_bytes()).hexdigest() for p in (project / 'src').rglob('*.cs') if str(p.relative_to(project)) not in before}), indent=2) + '\n')
    (project.parent / 'observation.patch').write_text(''.join(''.join(difflib.unified_diff(before[name].decode().splitlines(True),
        (project / name).read_text().splitlines(True), fromfile='parent/' + name, tofile='observed/' + name)) for name in sorted(changes)))
    subprocess.run(['dotnet', 'build', 'Propane.csproj', '-c', 'Debug', '-nologo', '-v', 'quiet', '-warnaserror'], cwd=project, check=True)
    print(project)


if __name__ == '__main__':
    main()
