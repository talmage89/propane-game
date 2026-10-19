#!/usr/bin/env python3
"""Insert Dev-only FU2b experiments into a fresh, already observed scratch project."""
import argparse
import difflib
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

from server_equivalence import ROOT

CALL = 'Propane.Dev.NetScenarios.VentCorrectionExperiment'


def replace(path, old, new):
    code = path.read_text()
    if code.count(old) != 1:
        raise ValueError(f'{path}: expected one experiment anchor: {old}')
    path.write_text(code.replace(old, new))


def experiment(project):
    if not project.resolve().is_relative_to(ROOT / 'build'):
        raise ValueError('experiments belong under build/')
    body = project / 'src/Net/BodySync.cs'
    if 'TargetAcceleration;' in body.read_text():
        raise ValueError('use a pre-FU2b parent (3c07559) for isolated historical experiments')
    replace(body, 'public required uint Id;', f'public required uint Id;\n        internal readonly {CALL}.Estimate VentEstimate = new();')
    replace(body, 'private static void SetTarget(', 'private void SetTarget(')
    replace(body, 'entry.HasTarget = true;', f'{CALL}.Accept(entry, state, rest, sampled, settings);\n        entry.HasTarget = true;')
    replace(body, 'var targetPosition = targetCenter - new Basis(targetRotation) * center;',
            f'var targetLinear = entry.TargetLinear;\n            {CALL}.Predict(entry, center, targetRotation, lead, tuning, ref targetCenter, ref targetLinear);\n            var targetPosition = targetCenter - new Basis(targetRotation) * center;')
    replace(body, 'body.LinearVelocity = entry.TargetLinear + error / blend;', 'body.LinearVelocity = targetLinear + error / blend;')
    replace(body, 'System.Math.Min(age, BodyState.MaxExtrapolation)',
            'System.Math.Min(age, Propane.Dev.DevArgs.Get("vent-experiment-age") != null ? tuning.CorrectionLeadLimit : BodyState.MaxExtrapolation)')
    replace(body, '>= StaleAfter / 2;', '>= StaleAfter / (Propane.Dev.DevArgs.Get("vent-experiment-cadence") != null ? 4 : 2);')
    source = ROOT / 'src/Dev/NetScenarios/VentCorrectionExperiment.cs'
    shutil.copyfile(source, project / source.relative_to(ROOT))
    tuning = project / 'src/Core/Tuning.70.Match.cs'
    code = tuning.read_text()
    position = code.rindex('}')
    tuning.write_text(code[:position] + '''
    /// <summary>FU2b experiment: maximum estimated linear acceleration (m/s²).</summary>
    [Export(PropertyHint.Range, "0,100,1")] public float CorrectionMaxAcceleration { get; set; } = 30f;
    /// <summary>FU2b experiment: angular speed above which linear prediction is disabled (rad/s).</summary>
    [Export(PropertyHint.Range, "0,100,1")] public float CorrectionAccelerationMaxSpin { get; set; } = 6f;
    /// <summary>FU2b experiment: minimum upward support normal.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float CorrectionFloorNormal { get; set; } = .9f;
    /// <summary>FU2b experiment: maximum prediction age (network seconds).</summary>
    [Export(PropertyHint.Range, "0.3,1,0.01")] public float CorrectionLeadLimit { get; set; } = .6f;
    /// <summary>FU2b experiment: hull-contact query margin (meters).</summary>
    [Export(PropertyHint.Range, "0.001,0.1,0.001")] public float CorrectionContactMargin { get; set; } = .01f;
''' + code[position:])
    project_file = project / 'Propane.csproj'
    replace(project_file, '</Project>', '<PropertyGroup><DefineConstants>$(DefineConstants);FU2B_EXPERIMENT</DefineConstants></PropertyGroup>\n</Project>')


def settling(project):
    """A separate Dev switch for vent-only steering on the corrected checkpoint candidate."""
    if not project.resolve().is_relative_to(ROOT / 'build'):
        raise ValueError('experiments belong under build/')
    body = project / 'src/Net/BodySync.cs'
    if 'FU2B_SETTLING' in body.read_text() or 'TargetAcceleration;' not in body.read_text():
        raise ValueError('settling requires an unmodified FU2b correction candidate')
    replace(body, 'var blend = Mathf.Max(tuning.CorrectionBlendTime, 0.02f);',
            'var defaultBlend = Mathf.Max(tuning.CorrectionBlendTime, 0.02f);')
    replace(body, 'foreach (var entry in entries.Values)\n        {\n            var contactsReady = true;', '''foreach (var entry in entries.Values)
        {
            var blend = defaultBlend;
#if FU2B_SETTLING
            if (!entry.TargetRest && Propane.Dev.DevArgs.Get("vent-experiment-blend") != null &&
                entry.Body is Tank.PropaneTank { State: Tank.PropaneTank.TankState.Venting })
            {
                blend = Mathf.Max(tuning.CorrectionVentBlendTime, 0.02f);
            }
#endif
            var contactsReady = true;''')
    tuning = project / 'src/Core/Tuning.70.Match.cs'
    code = tuning.read_text()
    position = code.rindex('}')
    tuning.write_text(code[:position] + '''
    /// <summary>FU2b experiment: vent-only correction blend time (seconds).</summary>
    [Export(PropertyHint.Range, "0.02,0.25,0.01")] public float CorrectionVentBlendTime { get; set; } = .1f;
''' + code[position:])
    replace(project / 'Propane.csproj', '</Project>', '<PropertyGroup><DefineConstants>$(DefineConstants);FU2B_SETTLING</DefineConstants></PropertyGroup>\n</Project>')
    host = project / 'src/Dev/NetTest.cs'
    if '"expect-vent-blend"' not in host.read_text():
        replace(host, '("CorrectionAccelerationMaxSpin", "expect-acceleration-spin") }',
                '("CorrectionAccelerationMaxSpin", "expect-acceleration-spin"), ("CorrectionVentBlendTime", "expect-vent-blend") }')


def spin_average(project):
    """Average rotating transverse force while retaining observed axial acceleration."""
    if not project.resolve().is_relative_to(ROOT / 'build'):
        raise ValueError('experiments belong under build/')
    body = project / 'src/Net/BodySync.cs'
    if 'FU2B_SPIN_AVERAGE' in body.read_text() or 'TargetAcceleration;' not in body.read_text():
        raise ValueError('spin averaging requires an unmodified FU2b correction candidate')
    flag = 'Propane.Dev.DevArgs.Get("vent-experiment-spin-average") != null'
    replace(body, 'entry.TargetAngular.LengthSquared() <= tuning.CorrectionAccelerationMaxSpin * tuning.CorrectionAccelerationMaxSpin;',
            '(entry.TargetAngular.LengthSquared() <= tuning.CorrectionAccelerationMaxSpin * tuning.CorrectionAccelerationMaxSpin ||\n' +
            f'                    {flag} && tuning.CorrectionSpinAverageMin > 0 && entry.TargetAngular.LengthSquared() >= tuning.CorrectionSpinAverageMin * tuning.CorrectionSpinAverageMin);')
    replace(body, 'targetCenter += entry.TargetAcceleration * (lead * lead / 2);\n                targetLinear += entry.TargetAcceleration * lead;', f'''var acceleration = entry.TargetAcceleration;
#if FU2B_SPIN_AVERAGE
                if ({flag} &&
                    entry.TargetAngular.LengthSquared() > tuning.CorrectionAccelerationMaxSpin * tuning.CorrectionAccelerationMaxSpin && body.IsInsideTree())
                {{
                    var gravity = PhysicsServer3D.BodyGetDirectState(body.GetRid())?.TotalGravity ?? Vector3.Zero;
                    var axisOfSpin = entry.TargetAngular.Normalized();
                    acceleration = gravity + axisOfSpin * (acceleration - gravity).Dot(axisOfSpin);
                    if (acceleration.LengthSquared() > tuning.CorrectionMaxAcceleration * tuning.CorrectionMaxAcceleration) {{ acceleration = Vector3.Zero; }}
                }}
#endif
                targetCenter += acceleration * (lead * lead / 2);
                targetLinear += acceleration * lead;''')
    tuning = project / 'src/Core/Tuning.70.Match.cs'
    code = tuning.read_text()
    position = code.rindex('}')
    tuning.write_text(code[:position] + '''
    /// <summary>FU2b experiment: minimum spin for transverse force averaging (rad/s); zero disables it.</summary>
    [Export(PropertyHint.Range, "0,100,1")] public float CorrectionSpinAverageMin { get; set; } = 20f;
''' + code[position:])
    replace(project / 'Propane.csproj', '</Project>', '<PropertyGroup><DefineConstants>$(DefineConstants);FU2B_SPIN_AVERAGE</DefineConstants></PropertyGroup>\n</Project>')
    host = project / 'src/Dev/NetTest.cs'
    anchor = '("CorrectionAccelerationMaxSpin", "expect-acceleration-spin") }'
    if '"expect-vent-blend"' in host.read_text():
        anchor = '("CorrectionVentBlendTime", "expect-vent-blend") }'
    replace(host, anchor, anchor[:-2] + ', ("CorrectionSpinAverageMin", "expect-spin-average") }')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', required=True, type=Path)
    parser.add_argument('--settling', action='store_true', help='vent-only blend on an FU2b candidate')
    parser.add_argument('--spin-average', action='store_true', help='average fast transverse force on an FU2b candidate')
    args = parser.parse_args()
    project = args.project.resolve()
    paths = [project / 'src/Net/BodySync.cs', project / 'src/Core/Tuning.70.Match.cs', project / 'Propane.csproj']
    if args.settling or args.spin_average:
        paths.append(project / 'src/Dev/NetTest.cs')
    before = {p: p.read_text() for p in paths}
    if args.settling:
        settling(project)
    if args.spin_average:
        spin_average(project)
    if not args.settling and not args.spin_average:
        experiment(project)
    (project.parent / 'experiment.patch').write_text(''.join(''.join(difflib.unified_diff(code.splitlines(True),
        path.read_text().splitlines(True), fromfile='observed/' + str(path.relative_to(project)),
        tofile='experiment/' + str(path.relative_to(project)))) for path, code in before.items()))
    (project.parent / 'experiment.json').write_text(json.dumps({str(p.relative_to(project)):
        hashlib.sha256(p.read_bytes()).hexdigest() for p in [*paths, project / 'src/Dev/NetScenarios/VentCorrectionExperiment.cs']}, indent=2) + '\n')
    subprocess.run(['dotnet', 'build', 'Propane.csproj', '-c', 'Debug', '-nologo', '-v', 'quiet', '-warnaserror'], cwd=project, check=True)


if __name__ == '__main__':
    main()
