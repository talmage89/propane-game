#!/usr/bin/env python3
"""Independent checks for FU2a observation-only patching and final-grid diagnostics."""
import copy
import gzip
import json
import math
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from compare_tanks import compare
from compare_tanks_tests import LIMITS, log
from vent_residual import CALL, ROOT, observe
from vent_residual_report import alignment, support, trace_metadata, trace_window
from vent_correction_experiment import experiment


class VentResidualTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (ROOT / 'build').mkdir(exist_ok=True)

    def test_shared_grid_uses_brackets_not_separate_completion_times(self):
        logs = [log(), log(.025)]
        result = alignment(logs, LIMITS)
        self.assertEqual(result['verdict'], compare(logs, LIMITS))
        pair = next(p for p in result['alignment'][0]['pairs'] if p['body'] == 'b')
        self.assertLess(pair['distance'], 1e-10)
        a, b = pair['endpoints']
        self.assertNotEqual(a['final'], b['final'])
        self.assertNotEqual(a['weight'], b['weight'])
        self.assertAlmostEqual(a['interpolated'][2][0], b['interpolated'][2][0])

    def test_reports_failure_without_reclassifying_or_weakening_it(self):
        logs = [log(), log(shift=2)]
        result = alignment(logs, LIMITS)
        self.assertEqual(result['verdict'], compare(logs, LIMITS))
        self.assertTrue(result['verdict']['failures'])
        self.assertEqual(result['alignment'][0]['pairs'][0]['distance'], 2)

    def test_incomplete_logs_still_fail_closed(self):
        logs = [log(), log()]
        logs[1][1][-1].final = False
        with self.assertRaises(ValueError):
            alignment(logs, LIMITS)

    def test_cli_retains_failed_report_and_returns_nonzero(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='fu2a-cli-') as directory:
            paths = [Path(directory) / f'{peer}.log' for peer in range(2)]
            output = Path(directory) / 'report.json'
            for shift in (0, 2):
                for path, samples in zip(paths, [log(), log(shift=shift)]):
                    path.write_text('\n'.join(
                        f'tank-check match 1 time {s.time} phase {s.phase} final {s.final} length {s.length} n {len(s.tanks)}' +
                        ''.join(f' {body}:{state}:{motion}:' + ','.join(map(str, point))
                                for body, (state, motion, point) in s.tanks.items()) for s in samples[1]))
                result = subprocess.run([sys.executable, '-B', str(ROOT / 'tools/dev/vent_residual_report.py'),
                                         *map(str, paths), f'--output={output}'], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
                self.assertEqual(result.returncode, int(shift != 0), result.stdout)
                self.assertEqual(bool(json.loads(output.read_text())['verdict']['failures']), shift != 0)

    def test_diagnostics_do_not_mutate_samples(self):
        logs = [log(), log(.025)]
        before = copy.deepcopy(logs)
        alignment(logs, LIMITS)
        self.assertEqual(before, logs)

    def test_support_rotates_authored_points_about_the_mass_center(self):
        row = dict(time=100., pose=dict(p=[0, -1, 0], q=[0, 0, math.sqrt(.5), math.sqrt(.5)]),
                   center=[.25, 0, 0], target=dict(p=[0, 2, 0], q=[0, 0, 0, 1]),
                   detail=dict(goalCenter=[0, 1, 0], goalRotation=[0, 0, math.sqrt(.5), math.sqrt(.5)]))
        before = copy.deepcopy(row)
        result = support(row, [[1, 0, 0]])
        self.assertAlmostEqual(result['current_bottom'], 0)
        self.assertAlmostEqual(result['sample_bottom'], 2)
        self.assertAlmostEqual(result['predicted_bottom'], 1.75)
        self.assertEqual(row, before)

    def test_run_wrapper_retains_nonzero_status_and_exact_arguments(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='fu2a-run-') as directory:
            project = Path(directory) / 'project'
            launcher = project / 'tools/dev/net_bots.sh'
            launcher.parent.mkdir(parents=True)
            launcher.write_text('#!/bin/bash\nexit 7\n')
            launcher.chmod(0o755)
            output = Path(directory) / 'output'
            result = subprocess.run([sys.executable, '-B', str(ROOT / 'tools/dev/vent_residual_run.py'),
                                     f'--project={project}', f'--output={output}', '--', '--literal=a b'],
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            self.assertEqual(result.returncode, 7, result.stdout)
            self.assertEqual(json.loads((output / 'status.json').read_text()), dict(exit=7))
            self.assertEqual(json.loads((output / 'command.json').read_text())['command'][-1], '--literal=a b')
            self.assertIn('before monotonic=', (output / 'load.txt').read_text())
            self.assertIn('after monotonic=', (output / 'load.txt').read_text())
            # A reused directory must fail without replacing its retained command/status.
            before = (output / 'status.json').read_bytes()
            result = subprocess.run([sys.executable, '-B', str(ROOT / 'tools/dev/vent_residual_run.py'),
                                     f'--project={project}', f'--output={output}'],
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual((output / 'status.json').read_bytes(), before)

    def test_trace_window_uses_shared_clock_and_keeps_nested_receipts(self):
        rows = [dict(kind='receipt', time=100.1, network=2.1, peer=3, match=7, sample=dict(id='ab')),
                dict(kind='decision-skip', time=100.2, network=2.2, peer=3, match=7, id='ab'),
                dict(kind='decision-skip', time=100.2, network=2.2, peer=4, match=8, id='ab'),
                dict(kind='decision-skip', time=102., network=4., peer=3, match=7, id='ab')]
        with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='fu2a-window-') as directory:
            path = Path(directory) / 'trace.jsonl.gz'
            with gzip.open(path, 'wt', encoding='utf-8-sig') as stream:
                stream.write(''.join(json.dumps(row) + '\n' for row in rows))
            before = path.read_bytes()
            result = trace_window([path], 7, 'ab', 100., .5)
            self.assertEqual(result, rows[:2])
            self.assertEqual(path.read_bytes(), before)

    def test_trace_metadata_retains_configuration_and_peer_identity(self):
        rows = [dict(kind='start', match=7, peer=3, name='Bot1'),
                dict(kind='configuration', match=7, peer=3, hull=[[0, 0, 0]]),
                dict(kind='measurement-check', match=7, peer=3)]
        with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='fu2a-metadata-') as directory:
            path = Path(directory) / 'trace.jsonl'
            path.write_text(''.join(json.dumps(row) + '\n' for row in rows))
            self.assertEqual(trace_metadata([path], 7), rows[:2])
            self.assertEqual(trace_metadata([path], 8), [])

    def test_patch_only_adds_observer_statements_to_isolated_sources(self):
        names = ('src/Net/BodySync.cs', 'src/Net/BodyPublisher.cs', 'src/Net/Match.cs', 'src/Dev/NetTest.cs')
        scratch = ROOT / 'build'
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch, prefix='fu2a-test-') as directory:
            project = Path(directory)
            (project / 'src/Dev/NetScenarios').mkdir(parents=True)
            before = {}
            for name in names:
                target = project / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / name, target)
                before[name] = target.read_text()
            observe(project)
            for name in names:
                actual = (project / name).read_text()
                self.assertIn(CALL, actual)
                # Removing only injected calls must recover the exact original tokens and order.
                recovered = '\n'.join(line for line in actual.splitlines() if CALL not in line)
                self.assertEqual(''.join(before[name].split()), ''.join(recovered.split()), name)
            body = (project / 'src/Net/BodySync.cs').read_text()
            for branch in ('decision-skip', 'decision-stale', 'gather-blend', 'obstructed-snap',
                           'rest-anchor', 'velocity-blend', 'distance-snap', 'settled-contact-snap'):
                self.assertIn(branch, body)
            self.assertIn('measurement-', (project / 'src/Dev/NetScenarios/VentResidualObservation.cs').read_text())
            # Reapplying must fail rather than silently double-instrument a source tree.
            with self.assertRaises(ValueError):
                observe(project)

    def test_correction_experiments_reject_checkout_and_double_insertion(self):
        original = (ROOT / 'src/Net/BodySync.cs').read_bytes()
        with self.assertRaises(ValueError):
            experiment(ROOT)
        self.assertEqual(original, (ROOT / 'src/Net/BodySync.cs').read_bytes())
        with tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='fu2b-experiment-test-') as directory:
            project = Path(directory)
            for name in ('src/Net/BodySync.cs', 'src/Core/Tuning.70.Match.cs', 'Propane.csproj'):
                target = project / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / name, target)
            (project / 'src/Dev/NetScenarios').mkdir(parents=True)
            experiment(project)
            body = (project / 'src/Net/BodySync.cs').read_text()
            self.assertIn('VentCorrectionExperiment.Accept', body)
            self.assertIn('VentCorrectionExperiment.Predict', body)
            self.assertIn('FU2B_EXPERIMENT', (project / 'Propane.csproj').read_text())
            with self.assertRaises(ValueError):
                experiment(project)


if __name__ == '__main__':
    unittest.main()
