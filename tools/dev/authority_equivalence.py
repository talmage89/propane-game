#!/usr/bin/env python3
"""Prepare identical, opt-in authority observation in isolated parent/candidate source builds.

No release fixtures or checkout production sources are changed. Run AuthorityTests and GatherTests in each
printed project with --authority-transcript=ABSOLUTE_PATH --match-seed=23, then compare the JSONL files.
"""
import argparse
from pathlib import Path
import shutil
import subprocess

from server_equivalence import ROOT, prepare

CALL = 'Propane.Dev.UnitTests.Fixtures.AuthorityObservation.Record'


def insert(source, needle, statement, before=False):
    code = source.read_text()
    assert code.count(needle) == 1, (source.name, needle, code.count(needle))
    code = code.replace(needle, statement + '\n' + needle if before else needle + '\n' + statement)
    source.write_text(code)


def observe(project):
    match = project / 'src/Net/Match.cs'
    owner = project / 'src/Net/ClientAuthorityTransactions.cs'
    if not owner.exists():
        owner = match
    observer = project / 'src/Dev/UnitTests/Fixtures/AuthorityObservation.cs'
    shutil.copyfile(ROOT / 'src/Dev/UnitTests/Fixtures/AuthorityObservation.cs', observer)
    # Native randomness is fixed only in these isolated differential runs, before any world construction.
    world = project / 'src/Core/WorldContext.cs'
    insert(world, 'Random.Randomize();', '        Random.Seed = 23;')
    spills = project / 'src/Net/BankClientAdapter.cs'
    insert(spills if spills.exists() else match, 'var rng = new RandomNumberGenerator();', '        rng.Seed = 23;')
    runner = project / 'src/Dev/UnitTests/TestRunner.cs'
    insert(runner, 'tests++;', f'        {CALL}(this, "case", name);')
    insert(owner, 'outgoingAuthority.Add(request);', f'        {CALL}(this, "queued");')
    insert(owner, '// A chain\'s root is sent before its children, even when a puncture/ownership grant held it back.',
           f'            {CALL}(this, "reserved");', before=True)
    insert(match, 'if (AuthoritySendOverride != null)', f'            {CALL}(this, "send", Convert.ToBase64String(bytes));', before=True)
    insert(owner, 'AuthorityRequest? finished = null;',
           f'        {CALL}(this, "resolve", Convert.ToBase64String(MessageCodec.Encode(new NetMessage(Msg.Authority, 1, 0, result), true)));')
    insert(owner, 'foreach (var next in outgoingAuthority)', f'                {CALL}(this, "released");', before=True)
    insert(owner, 'AuthorityResolved?.Invoke(result);' if owner == match else 'replica.Notify(result);',
           f'        {CALL}(this, "notify");', before=True)
    insert(owner, 'if (result.Peer == me) { gatherRequests.Remove(result.Request); }', f'        {CALL}(this, "complete");')
    # Physical callback order, including no-op restoration, is observable independently of final body state.
    for needle, label in (
        ('if (entry != null && !bodies.OnAuthority(authority, out authority, Clock.Seconds))', 'adopt'),
        ('if (predictedExplosions.Remove(id))', 'explode'),
        ('var recreated = !tanks.TryGetValue(id, out var tank)', 'restore'),
        ('var pilePose = tank?.PilePose;', 'recreate'),
        ('if (authority.State == TankLedger.State.Intact) { tank.RestoreIntact(); }', 'intact'),
        ('BodySync.Snap(tank, authority.Body.Transform, authority.Body.Linear, authority.Body.Angular);', 'snap'),
        ('if (authority.Vent is { } vent && authority.State == TankLedger.State.Venting)', 'vent'),
    ):
        # Insert at the beginning of the statement's line; some needles are partial statements.
        code = match.read_text()
        assert code.count(needle) == 1, needle
        line = next(line for line in code.splitlines() if needle in line)
        insert(match, line, f'        {CALL}(this, "{label}", id.ToString());', before=True)
    code = match.read_text()
    needle = '{ changed.Disturb(); }'
    assert code.count(needle) == 1
    match.write_text(code.replace(needle, '{ ' + CALL + '(this, \"disturb\", id.ToString()); changed.Disturb(); }'))
    insert(match, 'foreach (var tank in LiveTanks.Where(t => t.ChainCause == finished.Request))' if owner == match else
           'foreach (var tank in LiveTanks.Where(t => t.ChainCause == request))', f'        {CALL}(this, "cancel-root");', before=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--label', required=True)
    parser.add_argument('--observe', action='store_true')
    args = parser.parse_args()
    assert Path(args.label).name == args.label and args.label not in ('.', '..')
    prepare(args.revision, args.label, False)
    project = ROOT / 'build' / args.label / 'project'
    if args.observe:
        observe(project)
    for name in ('AuthorityBenchTests.cs', 'AuthorityLeaseTests.cs', 'Fixtures/AuthorityTransactions.cs'):
        source = ROOT / 'src/Dev/UnitTests' / name
        if source.exists():
            shutil.copyfile(source, project / 'src/Dev/UnitTests' / name)
    subprocess.run(['dotnet', 'build', 'Propane.csproj', '-c', 'Debug', '-nologo', '-v', 'quiet', '-warnaserror'],
                   cwd=project, check=True)
    print(project)
