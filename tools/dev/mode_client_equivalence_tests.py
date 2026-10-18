#!/usr/bin/env python3
"""Negative proof for the client-mode transcript comparator; uses only synthetic data under build/."""
import contextlib
import copy
import io
import json
from pathlib import Path
import tempfile
import unittest

from mode_client_equivalence import compare

ROOT = Path(__file__).resolve().parents[2]


class ComparisonTests(unittest.TestCase):
    def setUp(self):
        (ROOT / 'build').mkdir(exist_ok=True)
        self.scratch = tempfile.TemporaryDirectory(dir=ROOT / 'build', prefix='b3b1-comparator-')
        self.addCleanup(self.scratch.cleanup)
        self.parent = Path(self.scratch.name) / 'parent.jsonl'
        self.candidate = Path(self.scratch.name) / 'candidate.jsonl'
        self.rows = [{'step': step} for step in (
            'case', 'hud', 'envelope', 'adapter', 'mode-send', 'apply:enter', 'apply:exit',
            'score:enter', 'score:exit', 'spill:enter', 'spill:exit', 'drop:enter', 'drop:exit',
            'place:enter', 'place:exit', 'pile:enter', 'pile:exit', 'stock:enter', 'stock:exit',
            'roster:enter', 'roster:exit', 'refill:enter', 'refill:exit')]
        self.rows += [{'step': 'hud', 'mode': mode, 'value': 4} for mode in (2, 4, 7)]
        self.write(self.parent, self.rows)

    @staticmethod
    def write(path, rows):
        path.write_text(''.join(json.dumps(row) + '\n' for row in rows))

    def check(self, rows):
        self.write(self.candidate, rows)
        with contextlib.redirect_stdout(io.StringIO()):
            compare(self.parent, self.candidate)

    def test_identical(self):
        self.check(copy.deepcopy(self.rows))

    def test_value_change(self):
        changed = copy.deepcopy(self.rows)
        changed[-1]['value'] += 1
        with self.assertRaisesRegex(AssertionError, 'first difference'):
            self.check(changed)

    def test_callback_order(self):
        changed = copy.deepcopy(self.rows)
        changed[7], changed[8] = changed[8], changed[7]
        with self.assertRaisesRegex(AssertionError, 'first difference'):
            self.check(changed)

    def test_truncation(self):
        with self.assertRaises(AssertionError):
            self.check(self.rows[:-1])

    def test_missing_operation(self):
        with self.assertRaisesRegex(AssertionError, 'missing observation coverage'):
            self.check([row for row in self.rows if row['step'] != 'stock:enter'])

    def test_empty_transcripts(self):
        self.write(self.parent, [])
        with self.assertRaisesRegex(AssertionError, 'missing observation coverage'):
            self.check([])

    def test_missing_mode_on_both_sides(self):
        changed = [row for row in self.rows if row.get('mode') != 7]
        self.write(self.parent, changed)
        with self.assertRaisesRegex(AssertionError, 'missing mode coverage'):
            self.check(changed)


if __name__ == '__main__':
    unittest.main()
