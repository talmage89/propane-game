#!/usr/bin/env python3
"""Prepare an opt-in combined callback/lifetime witness in an isolated source build.

Run MatchCompositionTests, ActorLifetimeTests and MatchPlayerTests on each printed project with
--composition-transcript=ABSOLUTE_PATH, then compare the corresponding JSONL files with cmp.
All scratch stays under build/. The release reference inventory is never changed.
"""
import argparse
import re
import shutil
import subprocess
from server_equivalence import ROOT, copy_probe, prepare

CALL = 'Propane.Dev.UnitTests.Fixtures.MatchCompositionObservation.Record'


def owned_bot_benchmark(project):
    """Keep actors stationary while two real owned brains run inside the complete playing client callback."""
    source = project / 'src/Dev/UnitTests/AuthorityBenchTests.cs'
    code = source.read_text()
    replacements = {
        'new ServerFixture(length: 3600)': 'new ServerFixture(length: 3600, bots: 2)',
        'authoritySend: sent.Enqueue)': 'authoritySend: sent.Enqueue, createLocalView: false)',
        'var match = client.Match;': 'var match = client.Match;\n        Assert(match.Session.Actors.Count == 4 && match.BotCost.Bots == 2, "owned bot benchmark workload changed");',
        'packets = notifications = 0;': 'var botsBefore = match.BotCost;\n        packets = notifications = 0;',
        'var bytesPerTick = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)ticks;':
            'var bytesPerTick = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)ticks;\n'
            '        var botsAfter = match.BotCost;\n'
            '        Assert(botsAfter.Thinks - botsBefore.Thinks == 2L * ticks, "owned brains skipped a measured tick");',
        'notifications={notifications} bytes_per_tick=':
            'notifications={notifications} bots={botsAfter.Bots} thinks={botsAfter.Thinks - botsBefore.Thinks} '
            'queries={botsAfter.Queries - botsBefore.Queries} rays={botsAfter.Rays - botsBefore.Rays} bytes_per_tick=',
    }
    for old, new in replacements.items():
        assert code.count(old) == 1, old
        code = code.replace(old, new)
    source.write_text(code)


def inject(project, paths, signature, label, detail='null'):
    found = []
    for path in paths:
        source = project / path
        if not source.exists():
            continue
        code = source.read_text()
        pattern = re.compile(r'(' + signature + r'\s*\n    \{)')
        if pattern.search(code):
            found.append((source, code, pattern))
    assert len(found) == 1, (signature, [str(x[0]) for x in found])
    source, code, pattern = found[0]
    source.write_text(pattern.sub(lambda m: m[0] + f'\n        {CALL}("{label}", {detail});', code, count=1))


def observe(project):
    sources = [str(p.relative_to(project)) for p in (project / 'src/Net').glob('*.cs')]
    sources += [str(p.relative_to(project)) for p in (project / 'src/Session').glob('*.cs')]
    # These names identify the original synchronous operations, independent of the owning class.
    for name, args in (
        ('BuildContent', ''), ('BuildActors', ''), ('BuildViews', ''), ('RegisterReplication', ''),
        ('PreparePresentation', ''), ('ReadyLobby', ''), ('RefreshPlayer', ''), ('StopReplication', ''),
        ('AttachBots', ''), ('RefreshBotPeers', ''), ('ThinkBots', 'float dt'),
        ('SendPlayerState', 'PlayerCharacter actor, double now'), ('SendBodies', 'double now'),
        ('OnRelay', 'NetMessage message'), ('ReceiveGather', 'int sender, GatherMessage message'),
    ):
        inject(project, sources, r'(?:private|internal|public) void ' + name + r'\(' + re.escape(args) + r'\)', name)
    for path, signature, label, detail in (
        ('src/Session/SessionContext.cs', r'public ActorRuntime AddActor\(PlayerCharacter character\)', 'session.add', 'character.ActorId.ToString()'),
        ('src/Session/SessionContext.cs', r'public void RemoveActor\(ActorRuntime actor\)', 'session.remove', 'actor.Character.ActorId.ToString()'),
        ('src/Session/SessionContext.cs', r'private void RetireRegistration\(ActorRuntime actor\)', 'registration.retire', 'actor.Character.ActorId.ToString()'),
        ('src/Session/SessionContext.cs', r'private void Stop\(\)', 'session.stop', 'null'),
        ('src/Session/SessionContext.cs', r'public void Dispose\(\)', 'session.dispose', 'null'),
        ('src/Session/ActorRegistration.cs', r'internal void Unsubscribe\(\)', 'registration.unsubscribe', 'null'),
        ('src/Session/ActorRegistration.cs', r'public void Dispose\(\)', 'registration.dispose', 'null'),
        ('src/Core/WorldContext.cs', r'public void Invalidate\(\)', 'world.invalidate', 'null'),
        ('src/Core/WorldContext.cs', r'public void Dispose\(\)', 'world.dispose', 'null'),
        ('src/Net/BodySync.cs', r'public void Correct\(double now\)', 'body.correct', 'null'),
        ('src/Net/BodyPublisher.cs', r'public void Publish\(double now, IReadOnlySet<uint> pendingCreation\)', 'body.publish', 'null'),
        ('src/Player/LogicalAim.cs', r'public void Advance\(double delta\)', 'aim.advance', 'null'),
        ('src/Player/PlayerCharacter.cs', r'public override void _PhysicsProcess\(double delta\)', 'actor.physics', 'ActorId.ToString()'),
        ('src/Player/PlayerCharacter.Presentation.cs', r'public override void _Process\(double delta\)', 'actor.render', 'ActorId.ToString()'),
        ('src/Player/PlayerCharacter.cs', r'public void SetRemoteState\(PlayerNetState remote\)', 'actor.snapshot', 'ActorId.ToString()'),
        ('src/Player/PlayerCharacter.cs', r'public override void _ExitTree\(\)', 'actor.exit', 'ActorId.ToString()'),
        ('src/Player/LocalView.cs', r'public override void _Ready\(\)', 'view.ready', 'Actor.ActorId.ToString()'),
        ('src/Player/LocalView.cs', r'public void AssignDevice\(DeviceInput\? device\)', 'view.device', 'Actor.ActorId.ToString()'),
        ('src/Player/LocalView.cs', r'public override void _ExitTree\(\)', 'view.exit', 'Actor.ActorId.ToString()'),
        ('src/Net/NetClient.cs', r'public SendResult Send\(byte\[\] message, TrafficClass traffic = TrafficClass.ReliableOrdered\)', 'send', 'System.Convert.ToBase64String(message)'),
    ):
        inject(project, [path], signature, label, detail)
    actor = project / 'src/Player/PlayerCharacter.cs'
    code = actor.read_text()
    needle = 'intent = Runtime.IntentSource?.Read() ?? default;'
    assert code.count(needle) == 1
    actor.write_text(code.replace(needle, f'{CALL}("intent.read", ActorId.ToString());\n        ' + needle))
    runner = project / 'src/Dev/UnitTests/TestRunner.cs'
    runner.write_text(runner.read_text().replace('tests++;', f'tests++;\n        {CALL}("case", name);'))
    world = project / 'src/Core/WorldContext.cs'
    world.write_text(world.read_text().replace('Random.Randomize();', 'Random.Randomize();\n        Random.Seed = 23;'))
    # Drop and native blast entropy is controlled only in observed scratch builds.
    bank = project / 'src/Net/BankClientAdapter.cs'
    bank.write_text(bank.read_text().replace('var rng = new RandomNumberGenerator();', 'var rng = new RandomNumberGenerator();\n        rng.Seed = 23;'))
    world.write_text(world.read_text().replace('static () => new();', 'static () => new() { Seed = 23 };'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--label', required=True)
    parser.add_argument('--observe', action='store_true')
    parser.add_argument('--owned-bots-bench', action='store_true', help='add two owned brains to the controlled authority benchmark')
    args = parser.parse_args()
    assert args.label and '/' not in args.label and args.label not in ('.', '..')
    prepare(args.revision, args.label, False)
    project = ROOT / 'build' / args.label / 'project'
    # Identical workloads and boundary accessors, including parents that predate C2's assertions.
    for name in ('MatchCompositionTests.cs', 'ActorLifetimeTests.cs', 'MatchPlayerTests.cs', 'ActorTickBenchTests.cs',
                 'Fixtures/ActorLifetimeFixture.cs', 'Fixtures/ClientActorAccess.cs', 'Fixtures/MatchClient.cs'):
        copy_probe(ROOT / 'src/Dev/UnitTests' / name, project / 'src/Dev/UnitTests' / name, project)
    copy_probe(ROOT / 'src/Dev/ActorView.cs', project / 'src/Dev/ActorView.cs', project)
    if args.owned_bots_bench:
        owned_bot_benchmark(project)
    if args.observe:
        shutil.copyfile(ROOT / 'src/Dev/UnitTests/Fixtures/MatchCompositionObservation.cs', project / 'src/Dev/UnitTests/Fixtures/MatchCompositionObservation.cs')
        observe(project)
    subprocess.run(['dotnet', 'build', 'Propane.csproj', '-c', 'Debug', '-nologo', '-v', 'quiet', '-warnaserror'], cwd=project, check=True)
    print(project)
