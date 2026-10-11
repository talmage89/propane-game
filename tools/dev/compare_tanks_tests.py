#!/usr/bin/env python3
"""Tank convergence checks reject incomplete and contradictory snapshots."""
import contextlib
import io
from pathlib import Path
import tempfile
import unittest
from compare_tanks import check

class TankChecks(unittest.TestCase):
    snapshot = 'tanks clock 0.0 n 1 1:I:0,0,0\n'
    def run_check(self, first, second, **kwargs):
        with tempfile.TemporaryDirectory(prefix='tank-check-') as directory:
            paths = [Path(directory) / 'a.log', Path(directory) / 'b.log']
            for path, text in zip(paths, (first, second)):
                path.write_text(text)
            with contextlib.redirect_stdout(io.StringIO()):
                return check(paths, **kwargs)
    def test_bounded_drift(self):
        self.assertEqual(self.run_check(self.snapshot, self.snapshot.replace('0,0,0', '3,0,0'), max_drift=3), 3)
        with self.assertRaisesRegex(ValueError, 'drift='):
            self.run_check(self.snapshot, self.snapshot.replace('0,0,0', '3.01,0,0'), max_drift=3)
    def test_missing_ids_and_states_fail(self):
        for other in (self.snapshot.replace('1:I:', '2:I:'), self.snapshot.replace(':I:', ':V:')):
            with self.subTest(other=other), self.assertRaisesRegex(ValueError, 'final tanks disagree'):
                self.run_check(self.snapshot, other)
    def test_missing_final_snapshot_fails(self):
        with self.assertRaisesRegex(ValueError, 'completed-match'):
            self.run_check(self.snapshot, self.snapshot.replace('clock 0.0', 'clock 1.0'))
    def test_invalid_limits_fail_and_equivalent_hex_ids_agree(self):
        for limit in (-1, float('nan'), float('inf')):
            with self.subTest(limit=limit), self.assertRaises(ValueError):
                self.run_check(self.snapshot, self.snapshot, max_drift=limit)
        self.assertEqual(self.run_check(self.snapshot.replace('1:I:', '000A:I:'), self.snapshot.replace('1:I:', 'a:I:')), 0)
    def test_malformed_snapshots_fail(self):
        for other in (self.snapshot.replace('n 1', 'n 2'), self.snapshot.replace('1:I:', 'garbage:I:'),
                      self.snapshot.replace('0,0,0', 'nan,0,0'), self.snapshot.replace(':I:', ':Z:'),
                      self.snapshot.replace('clock 0.0', 'clock nan'), self.snapshot.replace('clock 0.0', 'clock -2'),
                      self.snapshot.replace('1:I:', '100000000:I:'),
                      'tanks clock 0.0 n 2 a:I:0,0,0 A:I:0,0,0\n',
                      'tanks clock 0.0 n 2 1:I:0,0,0 1:I:0,0,0\n'):
            with self.subTest(other=other), self.assertRaises(ValueError):
                self.run_check(self.snapshot, other)
if __name__ == '__main__':
    unittest.main()
