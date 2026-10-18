#!/usr/bin/env python3
"""Prepare identical opt-in client-mode observations in isolated parent/candidate builds.

Run Bank/King/Lobby suites with --mode-client-transcript=ABSOLUTE_PATH, compare JSONL byte for byte.
Without --observe, the same builds run ModeClientBenchTests without observation overhead.
All copies and imports stay under build/; frozen references and checkout sources are untouched.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess

from authority_equivalence import insert
from server_equivalence import ROOT, prepare

CALL = 'Propane.Dev.UnitTests.Fixtures.ModeClientObservation'


def wrap(path, signature, owner, label):
    code = path.read_text()
    needle = signature + '\n    {'
    assert code.count(needle) == 1, (path.name, signature)
    start = code.index(needle) + len(needle)
    # Count braces in these known method bodies (no unmatched braces in their strings/comments).
    end, depth = start, 1
    while depth:
        depth += (code[end] == '{') - (code[end] == '}')
        end += 1
    body = code[start:end - 1]
    code = (code[:start] + f'\n        {CALL}.Record({owner}, "{label}:enter");\n        try\n        {{' + body +
            f'\n        }}\n        finally {{ {CALL}.Record({owner}, "{label}:exit"); }}\n    ' + code[end - 1:])
    path.write_text(code)


def observe(project):
    match = project / 'src/Net/Match.cs'
    adapters = project / 'src/Net/BankClientAdapter.cs'
    candidate = adapters.exists()
    shutil.copyfile(ROOT / 'src/Dev/UnitTests/Fixtures/ModeClientObservation.cs',
                    project / 'src/Dev/UnitTests/Fixtures/ModeClientObservation.cs')
    insert(project / 'src/Core/WorldContext.cs', 'Random.Randomize();', '        Random.Seed = 23;')
    insert(adapters if candidate else match, 'var rng = new RandomNumberGenerator();', '        rng.Seed = 23;')
    insert(project / 'src/Dev/UnitTests/TestRunner.cs', 'tests++;', f'        {CALL}.Record(this, "case", name);')
    # Observe accepted/rejected envelopes, and the synchronous operations at the same old call sites.
    insert(match, 'private void OnMode(ModeEnvelope envelope)\n    {',
           f'        {CALL}.Record(this, "envelope", Convert.ToBase64String(MessageCodec.Encode(new NetMessage(envelope.Payload is KingState or TankBankBanks ? Msg.ModeSnapshot : Msg.ModeCommand, Setup.Id, 0, envelope), true)));')
    wrap(match, 'private void OnMode(ModeEnvelope envelope)', 'this', 'apply')
    for signature, label in (
        ('private void ApplyScore(int actorId, int score, int delta)', 'score'),
        ('private void DropTanks(PlayerCharacter actor, int wanted, SpillReason reason, Vector3 origin, Vector3 away)', 'spill'),
        ('private void SpawnDroppedTank(uint id, int owner, Transform3D transform, Vector3 velocity, Vector3 spin)', 'drop'),
    ):
        wrap(adapters if candidate else match, signature, 'replica' if candidate else 'this', label)
    for signature, label in (
        ('public void PlacePile(KingPileWanted wanted)' if candidate else 'private void PlacePile(KingPileWanted wanted)', 'place'),
        ('public void SpawnPile(KingPileSpawned spawned)' if candidate else 'private void SpawnPile(KingPileSpawned spawned)', 'pile'),
    ):
        wrap(project / ('src/Net/ClientModePiles.cs' if candidate else 'src/Net/Match.King.cs'), signature,
             'replica' if candidate else 'this', label)
    lobby = project / ('src/Net/LobbyClientAdapter.cs' if candidate else 'src/Net/Match.Lobby.cs')
    for signature, label in (
        ('public void MarkStock(AuthorityMessage result)' if candidate else 'private void MarkStock(AuthorityMessage result)', 'stock'),
        ('private void ReconcileRoster(LobbyInfo lobby)', 'roster'),
        ('public void PhysicsTick()' if candidate else 'private void RefillLobbyRifles()', 'refill'),
    ):
        wrap(lobby, signature, 'replica' if candidate else 'this', label)
    code = match.read_text()
    needle = 'hudPresenter.Present(new ModeHudInput('
    assert code.count(needle) == 1
    code = code.replace(needle, CALL + '.Hud(this, ' + needle)
    needle = 'View: ModeView));'
    assert code.count(needle) == 1
    match.write_text(code.replace(needle, 'View: ModeView)));'))


def compare(parent, candidate):
    expected = [json.loads(line) for line in Path(parent).read_text().splitlines()]
    actual = [json.loads(line) for line in Path(candidate).read_text().splitlines()]
    required = {'case', 'hud', 'envelope', 'apply:enter', 'apply:exit', 'score:enter', 'score:exit',
                'spill:enter', 'spill:exit', 'drop:enter', 'drop:exit', 'place:enter', 'place:exit',
                'pile:enter', 'pile:exit', 'stock:enter', 'stock:exit', 'roster:enter', 'roster:exit',
                'refill:enter', 'refill:exit'}
    for label, rows in (('parent', expected), ('candidate', actual)):
        missing = required - {row['step'] for row in rows}
        assert not missing, f'{label}: missing observation coverage {sorted(missing)}'
        assert {row.get('mode') for row in rows} >= {2, 4, 7}, f'{label}: missing mode coverage'
    assert len(expected) == len(actual), f'truncated transcript: {len(expected)} != {len(actual)}'
    for index, (before, after) in enumerate(zip(expected, actual), 1):
        assert before == after, f'first difference at row {index}:\n{before}\n{after}'
    print(f'identical: {len(expected)} observations; all operation and mode coverage present')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--compare', nargs=2, metavar=('PARENT', 'CANDIDATE'))
    parser.add_argument('--revision')
    parser.add_argument('--label')
    parser.add_argument('--observe', action='store_true')
    args = parser.parse_args()
    if args.compare:
        compare(*args.compare)
        raise SystemExit(0)
    assert args.revision and args.label, '--revision and --label required for preparation'
    assert Path(args.label).name == args.label and args.label not in ('.', '..')
    prepare(args.revision, args.label, False)
    project = ROOT / 'build' / args.label / 'project'
    for name in ('ModeClientBenchTests.cs', 'ModeClientTests.cs'):
        source = ROOT / 'src/Dev/UnitTests' / name
        if source.exists():
            shutil.copyfile(source, project / 'src/Dev/UnitTests' / name)
    if args.observe:
        observe(project)
    subprocess.run(['dotnet', 'build', 'Propane.csproj', '-c', 'Debug', '-nologo', '-v', 'quiet', '-warnaserror'], cwd=project, check=True)
    print(project)
