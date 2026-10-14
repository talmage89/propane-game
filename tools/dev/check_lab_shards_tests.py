#!/usr/bin/env python3
"""Fail on missing/duplicate Lab fixtures and seed ranges that escape the original sessions."""
import contextlib
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

import check_lab_shards as coverage


class LabCoverageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (coverage.check_lab.ROOT / 'build').mkdir(exist_ok=True)

    def test_exact_partition_and_failures(self):
        full = [('dials', 'one'), ('dials', 'two'), ('soak', 'void')]
        coverage.check(full, [full[:1], full[1:]])
        for shards in ([full[:1]], [full, full[:1]], [full, []], [full + [('extra', 'case')]]):
            with self.subTest(shards=shards), self.assertRaises(ValueError):
                coverage.check(full, shards)
        with self.assertRaises(ValueError):
            coverage.check(full + full[:1], [full])

    def test_full_seed_split(self):
        whole = coverage.cases('--full --only=ringout-full,shortfuse-full,dissolve-full')
        parts = [coverage.cases(f'--full --only={name} --first-seed={seed} --seeds=1')
                 for name, seeds in (('ringout-full', range(11, 14)), ('shortfuse-full', range(11, 13)),
                                     ('dissolve-full', range(11, 13))) for seed in seeds]
        coverage.check(whole, parts)
        for part in ('--full --only=ringout-full --first-seed=10 --seeds=1',
                     '--full --only=dissolve-full --first-seed=12 --seeds=2',
                     '--full --only=king-full --first-seed=11 --seeds=1'):
            with self.subTest(part=part), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                coverage.cases(part)

    def test_lifetime_keeps_its_cycles_and_pairs(self):
        with patch.object(coverage, 'fixture_cases', return_value=['king/Void', 'king/Suburb']):
            whole = coverage.cases('--full --only=lifetime')
        with patch.object(coverage, 'fixture_cases', side_effect=[['king/Void'], ['king/Suburb']]):
            parts = [coverage.cases(f'--full --only=lifetime --lifetime-shard={part} --lifetime-shards=2') for part in (1, 2)]
        coverage.check(whole, parts)
        self.assertTrue(all(case[1] == ('--cycles=10',) for case in whole))

    def test_timeout_reaps_the_wrapped_engine(self):
        with tempfile.TemporaryDirectory(dir=coverage.check_lab.ROOT / 'build') as directory:
            root = Path(directory)
            marker = root / 'late-child'
            child = ('import sys, time; from pathlib import Path; print("child started", flush=True); '
                     'time.sleep(3); Path(sys.argv[1]).write_text("survived")')
            command = [sys.executable, '-c',
                       f'import subprocess, sys; subprocess.run([sys.executable, "-c", {child!r}, sys.argv[1]])', str(marker)]
            with self.assertRaises(subprocess.TimeoutExpired):
                coverage.check_lab.run_scene(command, root / 'log', 2)
            self.assertIn('child started', (root / 'log').read_text())
            time.sleep(1.5)
            self.assertFalse(marker.exists(), 'timed-out engine survived its wrapper')

    def test_selection_and_dial_listing(self):
        with patch.object(coverage, 'fixture_cases', return_value=['fixture']) as listing:
            self.assertEqual(coverage.cases('--full --only=dials --dial-shard=2 --dial-shards=8'), [('dials', (), 'fixture')])
            self.assertEqual(listing.call_args.args, ('dials', ['--dial-shard=2', '--dial-shards=8']))
        for selection in ('--only=dials', '--full --only=missing', '--full --only=duel,duel'):
            with self.subTest(selection=selection), self.assertRaises(ValueError):
                coverage.cases(selection)

    def test_listing_timeout_reaps_the_wrapped_engine(self):
        with tempfile.TemporaryDirectory(dir=coverage.check_lab.ROOT / 'build') as directory:
            root = Path(directory)
            marker = root / 'late-child'
            child = ('import time; from pathlib import Path; print("child started", flush=True); '
                     f'time.sleep(3); Path({str(marker)!r}).write_text("survived")')
            wrapper = root / 'godot'
            wrapper.write_text(f'#!{sys.executable}\nimport subprocess, sys\n'
                               f'subprocess.run([sys.executable, "-c", {child!r}])\n')
            wrapper.chmod(0o755)
            runner = coverage.check_lab.run_scene
            with patch.dict(os.environ, GODOT=str(wrapper)), \
                    patch.object(coverage.check_lab, 'run_scene', side_effect=lambda command, log, budget: runner(command, log, 2)), \
                    self.assertRaisesRegex(ValueError, 'listing timed out: child started'):
                coverage.fixture_cases('dials', [])
            time.sleep(1.5)
            self.assertFalse(marker.exists(), 'listing engine survived its wrapper')


if __name__ == '__main__':
    unittest.main()
