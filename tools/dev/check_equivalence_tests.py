#!/usr/bin/env python3
"""First-difference and malformed-reference checks use only Python's standard library."""
import gzip
import tempfile
from pathlib import Path
import unittest

from check_equivalence import first_difference, new_output_directory, read_json


class ComparisonTests(unittest.TestCase):
    def test_first_named_field_in_order(self):
        self.assertEqual(first_difference({'actor': {'ammo': 3, 'life': 1}}, {'actor': {'ammo': 4, 'life': 2}}),
                         '$.actor.ammo: expected 3, actual 4')

    def test_exact_numbers_and_types(self):
        self.assertIsNotNone(first_difference(0.1, 0.10000000000000002))
        self.assertIsNotNone(first_difference(1, True))
        self.assertIsNone(first_difference({'a': [1, 2]}, {'a': [1, 2]}))

    def test_inventory_and_truncation(self):
        self.assertIn('inventory/order', first_difference({'a': 1}, {'a': 1, 'b': 2}))
        self.assertIn('.length', first_difference([1, 2], [1]))
        self.assertIn('inventory/order', first_difference({'a': 1, 'b': 2}, {'b': 2, 'a': 1}))

    def test_preserve_previous_evidence(self):
        scratch = Path(__file__).resolve().parents[2] / 'build'
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as directory:
            path = Path(directory) / 'evidence'
            self.assertEqual(new_output_directory(path), path)
            sentinel = path / 'failed.log'
            sentinel.write_text('failure')
            self.assertEqual(new_output_directory(path), path.with_name('evidence-2'))
            self.assertEqual(sentinel.read_text(), 'failure')

    def test_missing_and_truncated_fixture(self):
        scratch = Path(__file__).resolve().parents[2] / 'build'
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as directory:
            path = Path(directory) / 'reference.json.gz'
            with self.assertRaises(FileNotFoundError): read_json(path)
            path.write_bytes(gzip.compress(b'{"a":1}')[:-4])
            with self.assertRaises(EOFError): read_json(path)


if __name__ == '__main__':
    unittest.main()
