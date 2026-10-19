#!/usr/bin/env python3
"""Compiled negative fixtures and source-rule rejection, independent of Godot/imported state."""
import json
import pathlib
import shutil
import struct
import subprocess
import tempfile
import time

from source_check import ROOT, check, sites
from process_state_tests import exercise_state

POLICY = ROOT / 'tools/architecture'
TOOL = ROOT / 'build/architecture/bin/Debug/net10.0/Architecture.dll'


def run(*args):
    result = subprocess.run(args, cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    return result.returncode, result.stdout


def require(ok, message, output=''):
    if not ok:
        raise SystemExit(f'FAIL architecture: {message}\n{output}')


def main():
    started = time.monotonic()
    require((POLICY / '.gdignore').is_file(), 'tooling must be excluded from Godot imports/exports')
    code, output = run('dotnet', 'build', str(POLICY / 'Architecture.csproj'), '-nologo', '-v', 'quiet')
    require(code == 0, 'checker build', output)
    (ROOT / 'build').mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='a0-fixtures-') as directory:
        scratch = pathlib.Path(directory)
        (scratch / 'src').mkdir()
        source = scratch / 'src/Fixture.cs'
        project = scratch / 'Fixture.csproj'
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                           '<Nullable>enable</Nullable><DebugType>portable</DebugType><TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
                           '</PropertyGroup></Project>')
        shutil.copyfile(POLICY / 'modules.json', scratch / 'modules.json')
        baseline = scratch / 'baseline.tsv'
        baseline.write_text('')
        state_policy = scratch / 'process-state.tsv'
        state_policy.write_text('')
        assembly = scratch / 'bin/Debug/net10.0/Fixture.dll'
        def compile_source(text):
            source.write_text(text)
            code, output = run('dotnet', 'build', str(project), '-nologo', '-v', 'quiet')
            require(code == 0, 'fixture compilation', output)
        def inspect(mode='check'):
            return run('dotnet', str(TOOL), mode, str(assembly), str(scratch))
        # Independent inventory fixture: shipped assemblies have no PDB. Exercise real PCK bytes,
        # not a precomputed path listing, and require named failures rather than any nonzero exit.
        pack = scratch / 'game.pck'
        (scratch / 'artifact.json').write_text(json.dumps({
            'SmokeTypes': ['Propane.ReleaseSmoke.ReleaseTest'],
            'EntryMethods': {'Propane.Main': ['_Ready']},
            'Resources': ['project.binary', 'scenes/main.tscn.remap', 'src/Main.cs']}))
        def string_setting(key, value):
            encoded = value.encode()
            variant = struct.pack('<II', 4, len(encoded)) + encoded + bytes((-len(encoded)) % 4)
            return struct.pack('<I', len(key)) + key.encode() + struct.pack('<I', len(variant)) + variant
        config = b'ECFG' + struct.pack('<I', 2) + string_setting('application/run/main_scene', 'res://scenes/main.tscn') + string_setting('autoload/ShutdownGuard', '*res://src/Core/ShutdownGuard.cs')
        resources = {'src/Main.cs': b' ', 'main.scn': b'RSRC',
                     'scenes/main.tscn.remap': b'[remap]\npath="res://main.scn"\n',
                     'project.binary': config}
        def write_pack(items):
            data = b''.join(items.values())
            header = struct.pack('<6I2Q', 0x43504447, 4, 4, 7, 2, 2, 112, 112 + len(data)) + bytes(72)
            directory = struct.pack('<I', len(items))
            offset = 0
            for name, content in items.items():
                encoded = name.encode() + b'\0'
                directory += struct.pack('<I', len(encoded)) + encoded + struct.pack('<QQ', offset, len(content)) + bytes(20)
                offset += len(content)
            pack.write_bytes(header + data + directory)
        def inventory():
            return run('dotnet', str(TOOL), 'inventory', str(assembly), str(pack), str(scratch))
        good_source = ('namespace Propane { public class Main { public void _Ready() {} } }\n'
                       'namespace Propane.ReleaseSmoke { public class ReleaseTest {} }')
        compile_source(good_source)
        assembly.with_suffix('.pdb').unlink()
        write_pack(resources)
        code, output = inventory()
        require(code == 0, 'shipping inventory without symbols', output)
        for fixture_source, message in [
            (good_source + '\nnamespace Propane.Dev { public class Hidden {} }', 'Dev type in artifact: Propane.Dev.Hidden'),
            (good_source.replace('class ReleaseTest', 'class Missing'), 'Missing smoke type: Propane.ReleaseSmoke.ReleaseTest'),
            (good_source.replace('_Ready', 'Other'), 'Missing entry method: Propane.Main._Ready'),
        ]:
            compile_source(fixture_source)
            code, output = inventory()
            require(code == 1 and message in output, 'inventory accepted ' + message, output)
            print(output.strip())
        compile_source(good_source)
        for items, message in [
            ({**resources, 'scenes/dev/hidden.tscn': b' '}, 'Dev resource in artifact: scenes/dev/hidden.tscn'),
            ({**resources, 'src/Dev/hidden.cs': b' '}, 'Dev resource in artifact: src/Dev/hidden.cs'),
            ({k: v for k, v in resources.items() if k != 'src/Main.cs'}, 'Missing artifact resource: src/Main.cs'),
            ({k: v for k, v in resources.items() if k != 'main.scn'}, 'Missing main scene remap target'),
            ({**resources, 'project.binary': config.replace(b'scenes/main', b'scenes/fake')}, 'Wrong or missing packed entry setting: application/run/main_scene'),
        ]:
            write_pack(items)
            code, output = inventory()
            require(code == 1 and message in output, 'inventory accepted ' + message, output)
        pack.write_bytes(b'GDPC\x04')
        code, output = inventory()
        require(code != 0, 'inventory accepted truncated PCK', output)
        print('PASS architecture: artifact Dev/missing smoke/entry/resource/remap/truncation negatives')
        compile_source('namespace Godot { public struct Vector3 { public float X; } }\n'
                       'namespace Propane.World.Plan { public class ValueOnly { public Godot.Vector3 Run(Godot.Vector3 v) => v; } }\n'
                       'namespace Propane.ReleaseSmoke { public class ReleaseTest { } }\n'
                       'namespace Propane { public class Main { public object Run() => new ReleaseSmoke.ReleaseTest(); } }')
        code, output = inspect()
        require(code == 0, 'value types and exact smoke entry must pass', output)
        print('PASS architecture: Godot values and exact Main → ReleaseTest entry')
        baseline.write_text('direction\tPropane.World.Plan.ValueOnly\tPropane.Bots.Brain\tA0\tDeliberately stale fixture\n')
        code, output = inspect()
        require(code == 1 and 'STALE baseline' in output, 'stale baseline must fail', output)
        print(output.strip())
        baseline.write_text('')
        state_policy.write_text('static-property\tGodot.Engine\tTimeScale\t1\tFixture\tStub engine property storage\n')
        compile_source((POLICY / 'fixtures.cs.txt').read_text())
        code, candidate = inspect('candidate')
        require(code == 0, 'candidate generation', candidate)
        edges = {tuple(line.split('\t')[:3]) for line in candidate.splitlines()}
        expected = [
            ('direction', 'Propane.Player.BadView', 'Propane.Player.LocalView'),
            ('direction', 'Propane.Net.ServerRun', 'Propane.Core.Tuning'),
            ('direction', 'Propane.Net.WireCatalog', 'Propane.Modes.ModeCatalog'),
            ('runtime-dev', 'Propane.Session.BadFacade', 'Propane.Dev.MessageCodec'),
            ('direction', 'Propane.Modes.AliasReference', 'Propane.Net.NetClient'),
            ('direction', 'Propane.Modes.InheritsBot', 'Propane.Bots.Brain'),
            ('direction', 'Propane.Modes.IExtendsBot', 'Propane.Bots.IObservation'),
            ('direction', 'Propane.Modes.IEvent', 'Propane.Bots.Brain'),
            ('direction', 'Propane.Modes.Locals', 'Propane.Bots.Brain'),
            ('bot-live-owner', 'Propane.Bots.Inherited', 'Propane.Modes.DerivedRules'),
            ('bot-live-owner', 'Propane.Bots.RuleInterface', 'Propane.Modes.IModeRules'),
            ('direction', 'Propane.Modes.BadBot', 'Propane.Bots.Brain'),
            ('direction', 'Propane.Modes.VarargCall', 'Propane.Net.Varargs'),
            ('direction', 'Propane.Modes.Qualified', 'Propane.Net.NetClient'),
            ('pure-native', 'Propane.Modes.EngineClock', 'Godot.Engine'),
            ('rule-clock', 'Propane.Modes.ElapsedClock', 'System.Diagnostics.Stopwatch.get_ElapsedTicks'),
            ('pure-native', 'Propane.World.Plan.Platform', 'Godot.OS'),
            ('rule-clock', 'Propane.Modes.Member', 'Godot.Time.GetTicksMsec'),
            ('pure-native', 'Propane.Modes.NativeBase', 'Godot.Node3D'),
            ('pure-native', 'Propane.Modes.ResourceField', 'Godot.Resource'),
            ('direction', 'Propane.Modes.Nested', 'Propane.UI.Hud'),
            ('direction', 'Propane.Modes.Generated', 'Propane.Net.Match'),
            ('direction', 'Propane.Modes.GenericField', 'Propane.Bots.Brain'),
            ('direction', 'Propane.Modes.GenericMethod', 'Propane.Bots.Brain'),
            ('direction', 'Propane.Modes.Signature', 'Propane.Bots.Brain'),
            ('direction', 'Propane.Modes.Signature', 'Propane.Net.Match'),
            ('direction', 'Propane.Modes.Interface', 'Propane.Bots.Brain'),
            ('direction', 'Propane.Modes.IBad', 'Propane.Bots.Brain'),
            ('direction', 'Propane.Modes.Constraint`1', 'Propane.Bots.Brain'),
            ('rule-clock', 'Propane.Modes.BadDelay', 'System.Threading.Tasks.Task.Delay'),
            ('rule-clock', 'Propane.World.Plan.Clock', 'Godot.Time.GetTicksMsec'),
            ('plan-resource', 'Propane.World.Plan.Load', 'Godot.GD.Load'),
            ('pure-native', 'Propane.World.Plan.Scene', 'Godot.Node3D'),
            ('timer-owner', 'Propane.Player.BlockingDelay', 'Godot.OS.DelayMsec'),
            ('timer-owner', 'Propane.Player.BadClock', 'System.Diagnostics.Stopwatch..ctor'),
            ('simulation-boundary', 'Propane.Player.BadClient', 'Propane.Net.NetClient'),
            ('simulation-boundary', 'Propane.Player.BadCodec', 'Propane.Net.PacketCodec'),
            ('simulation-boundary', 'Propane.Player.BadHud', 'Propane.UI.Hud'),
            ('bot-live-owner', 'Propane.Bots.BadMatch', 'Propane.Net.Match'),
            ('bot-live-owner', 'Propane.Bots.BadLiveRule', 'Propane.Modes.LiveRules'),
            ('transport-backend', 'Propane.Net.BackendLeak', 'Godot.ENetMultiplayerPeer'),
            ('server-physical', 'Propane.Net.ServerRun', 'Godot.Camera3D'),
            ('global-owner', 'Propane.Session.BypassQuit', 'System.Environment.Exit'),
            ('transport-backend', 'Propane.Net.FaultPacketTransport', 'Propane.Net.NetTransport'),
            ('server-physical', 'Propane.Net.LobbyServer', 'Propane.Player.ActorRuntime'),
            ('direction', 'Propane.Net.WireGameplay', 'Propane.Player.ActorRuntime'),
            ('global-owner', 'Propane.Session.GlobalWrite', 'Godot.Engine.set_TimeScale'),
            ('global-owner', 'Propane.Session.PauseInitializer', 'Godot.SceneTree.set_Paused'),
            ('runtime-dev', 'Propane.Session.BadDev', 'Propane.Dev.Harness'),
            ('runtime-dev', 'Propane.ReleaseSmoke.BadDev', 'Propane.Dev.Harness'),
            ('smoke-entry', 'Propane.Session.BadSmoke', 'Propane.ReleaseSmoke.Other'),
            ('worker-native', 'Propane.Session.StaticResource', 'Godot.Node'),
            ('worker-native', 'Propane.Session.AsyncWorker', 'Godot.Node'),
            ('worker-native', 'Propane.Session.FactoryWorker', 'Godot.Node'),
            ('worker-native', 'Propane.Session.Worker', 'Godot.Node'),
        ]
        require(set(expected) <= edges, 'missing compiled edges: ' + str(set(expected) - edges), candidate)
        # Each fixture must fail independently. Other deliberately bad edges are temporarily reviewed;
        # expected triples above are an independent oracle, not generated from scanner output.
        for edge in expected:
            baseline.write_text(''.join('\t'.join((*other, 'Fixture', 'Other negative case')) + '\n'
                                        for other in sorted(edges - {edge})))
            code, output = inspect()
            require(code == 1 and ' '.join(edge) in output and 'src/Fixture.cs' in output,
                    'compiled negative did not name file/type/rule: ' + str(edge), output)
            print(next(line for line in output.splitlines() if line.startswith('NEW ')))
        baseline.write_text(''.join('\t'.join((*edge, 'Fixture', 'Reviewed negative fixture')) + '\n' for edge in sorted(edges)))
        code, output = inspect()
        require(code == 0, 'exact reviewed baseline must pass', output)
        # Candidate must not overwrite it, and a missing symbol file must fail closed.
        before = baseline.read_bytes()
        inspect('candidate')
        require(baseline.read_bytes() == before, 'candidate rewrote baseline')
        assembly.with_suffix('.pdb').rename(scratch / 'saved.pdb')
        code, output = inspect()
        require(code != 0 and '.pdb' in output, 'missing PDB accepted', output)
        print('PASS architecture: exact baseline, read-only candidate and missing-symbol rejection')
        baseline.write_text('')
        state_policy.write_text('')
        exercise_state(compile_source, inspect, state_policy, require)
    for rule, text in [
        ('engine-scale', 'Godot.Engine.TimeScale = 2;'),
        ('engine-catchup', 'using E = Godot.Engine; E.MaxPhysicsStepsPerFrame = 1;'),
        ('tree-pause', 'GetTree().Paused = true;'),
        ('shader-global', 'RenderingServer.GlobalShaderParameterSet("world", value);'),
        ('direct-quit', 'GetTree().Quit(1);'),
        ('timer', 'await System.Threading.Tasks.Task.Delay(2);'),
        ('timer', 'GetTree().CreateTimer(1);'),
        ('timer', 'using static Godot.OS; DelayMsec(1);'),
        ('timer', 'using static System.Threading.Tasks.Task; await Delay(1);'),
        ('timer', 'var callback = System.Threading.Thread.Sleep;'),
        ('timer', 'new System.Threading.Timer { };'),
        ('wall-clock', 'using Clock = Godot.Time; var x = Clock.GetTicksMsec();'),
        ('wall-clock', 'var x = $"now {Time.GetTicksMsec()}";'),
        ('worker-entry', 'new System.Threading.Thread(Work);'),
        ('runtime-dev-source', 'using Alias = global::Propane.Dev.Harness;'),
        ('runtime-dev-source', 'global::Propane.Dev.MessageCodec.Decode(packet, true);'),
        ('runtime-dev-source', 'using Propane.Dev;'),
    ]:
        source = {'src/Fixture.cs': 'namespace Propane.Session; ' + text}
        failures = check(source, {})
        require(any(rule in failure for failure in failures), 'source negative accepted: ' + text)
        print(failures[0])
    harmless = {'src/Fixture.cs': '// Engine.TimeScale = 2;\nvar s = "Task.Delay(1)"; ShutdownGuard.Quit(tree);'}
    require(not check(harmless, {}), 'comment/literal/guard false positive')
    approved = {'src/Fixture.cs': 'namespace Propane.Session; Time.GetTicksMsec();'}
    counts = dict(sites(approved)[0])
    require(not check(approved, counts), 'approved source site rejected')
    require(check({}, counts) and check({'src/Fixture.cs': approved['src/Fixture.cs'] * 2}, counts),
            'stale or increased source count passed')
    print(f'PASS architecture: {len(expected)} compiled negatives, 17 source negatives, stale/increased site counts ({time.monotonic() - started:.2f}s)')


if __name__ == '__main__':
    main()
