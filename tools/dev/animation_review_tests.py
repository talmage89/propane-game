#!/usr/bin/env python3
"""Checks for misleading animation-review evidence: frozen poses, missing frames and ignored failures."""
import csv
import json
import math
from pathlib import Path
import tempfile
import unittest

import animation_metrics as metrics
import animation_review as review


class MetricsTests(unittest.TestCase):
    def test_alternating_and_hopping_feet(self):
        left = [math.sin(i / 10) for i in range(120)]
        self.assertAlmostEqual(metrics.correlation(left, [-v for v in left]), -1)
        self.assertAlmostEqual(metrics.correlation(left, left), 1)

    def test_frozen_feet_cannot_pass(self):
        with self.assertRaises(ValueError):
            metrics.correlation([0] * 120, [0] * 120)

    def test_release_reference_passes_gait_but_not_everything(self):
        calibration = json.loads(metrics.CALIBRATION.read_text())
        checks = metrics.evaluate(calibration['reference_measurements'], calibration)
        gait = [c for c in checks if c['metric'] in ('alternation', 'head_span', 'pelvis_span', 'knee_gap', 'thigh_gap')]
        self.assertTrue(gait)
        self.assertTrue(all(c['pass'] for c in gait))
        self.assertFalse(all(c['pass'] for c in checks))

    def test_collision_and_shouldering_fail_independently(self):
        calibration = json.loads(metrics.CALIBRATION.read_text())
        result = {'aim_idle': {'stock_head': -.05, 'stock_shoulder': .09}}
        checks = metrics.evaluate(result, calibration)
        self.assertEqual(len(checks), 2)
        self.assertTrue(all(not c['pass'] for c in checks))
        result['aim_idle'] = {'stock_head': .01, 'stock_shoulder': 0}
        self.assertTrue(all(c['pass'] for c in metrics.evaluate(result, calibration)))

    def write_csv(self, path, count=180, missing=False, invalid=False):
        keys = ('time', 'localTime', 'speed', 'aim', 'hipAimYaw', 'leftX', 'leftY', 'leftZ', 'rightX',
                'rightY', 'rightZ', 'pelvisY', 'headY', 'kneeGap', 'thighGap', 'stockHead', 'stockShoulder')
        with path.open('w', newline='') as stream:
            writer = csv.DictWriter(stream, fieldnames=['case', *keys])
            writer.writeheader()
            for i in range(count):
                if missing and i == 75:
                    continue
                t = i / 60
                phase = t * 2 * math.pi
                row = dict.fromkeys(keys, 0)
                row.update(case='walk_000', time=t, localTime=t, speed=1.5,
                           leftY=.05 + .05 * math.sin(phase), rightY=.05 - .05 * math.sin(phase),
                           headY=1.6 + .02 * math.cos(phase), pelvisY=.9 + .02 * math.cos(phase),
                           kneeGap=.18, thighGap=.18, stockHead=.1)
                if invalid and i == 90:
                    row['headY'] = 'nan'
                writer.writerow(row)

    def test_complete_motion_is_measurable(self):
        with tempfile.TemporaryDirectory(dir=Path('build')) as directory:
            path = Path(directory) / 'samples.csv'
            self.write_csv(path)
            result = metrics.measure(path)['walk_000']
            self.assertAlmostEqual(result['alternation'], -1)
            self.assertGreater(result['contact_pairs'], 12)

    def test_missing_frame_fails_loudly(self):
        with tempfile.TemporaryDirectory(dir=Path('build')) as directory:
            path = Path(directory) / 'samples.csv'
            self.write_csv(path, missing=True)
            with self.assertRaisesRegex(ValueError, 'non-consecutive'):
                metrics.measure(path)

    def test_short_run_fails_loudly(self):
        with tempfile.TemporaryDirectory(dir=Path('build')) as directory:
            path = Path(directory) / 'samples.csv'
            self.write_csv(path, count=120)
            with self.assertRaisesRegex(ValueError, 'coverage'):
                metrics.measure(path)

    def test_nonfinite_sample_fails_loudly(self):
        with tempfile.TemporaryDirectory(dir=Path('build')) as directory:
            path = Path(directory) / 'samples.csv'
            self.write_csv(path, invalid=True)
            with self.assertRaisesRegex(ValueError, 'non-finite'):
                metrics.measure(path)


class CaptureTests(unittest.TestCase):
    def test_reverse_clip_is_not_mixed_into_forward_clip(self):
        with tempfile.TemporaryDirectory(dir=Path('build')) as directory:
            path = Path(directory)
            for name in ('Walk_Loop_001.png', 'Walk_Loop_002.png', 'Walk_Loop_reverse_001.png'):
                (path / name).touch()
            self.assertEqual(len(review.capture_frames(path, 'Walk_Loop', 2)), 2)

    def test_same_count_with_a_gap_is_rejected(self):
        with tempfile.TemporaryDirectory(dir=Path('build')) as directory:
            path = Path(directory)
            for name in ('walk_001.png', 'walk_003.png'):
                (path / name).touch()
            with self.assertRaisesRegex(ValueError, 'consecutive'):
                review.capture_frames(path, 'walk', 2)


if __name__ == '__main__':
    Path('build').mkdir(exist_ok=True)
    unittest.main()
