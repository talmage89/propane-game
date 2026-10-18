#!/usr/bin/env python3
"""Verify a pinned file; optionally fetch a missing file with the repository's curl.

The checked-in manifest is the only source of expected hashes. Cache hits are verified too.
A failed/partial download is never promoted into the cache. This process owns its temporary
file until verification and rename; extracted installations are recreated by the callers.
"""
import hashlib
from pathlib import Path
import re
import subprocess
import sys
import tempfile


def digest(path, algorithm):
    value = hashlib.new(algorithm)
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


def verify(path, filename, algorithm, expected):
    actual = digest(path, algorithm)
    if actual != expected:
        raise ValueError(f"Checksum mismatch for {filename} ({path}): expected {algorithm} {expected}; actual {actual}")
    print(f"Verified {filename}: {algorithm} {actual}")


def main():
    if len(sys.argv) not in (4, 5):
        raise ValueError('usage: verify.py VERSION FILENAME PATH [URL]')
    version, filename, destination = sys.argv[1:4]
    path = Path(destination)
    pins = []
    manifest = Path(__file__).with_name('pins.txt')
    try:
        lines = manifest.read_text().splitlines()
    except FileNotFoundError:
        lines = []
    for line in lines:
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        fields = line.split()
        if len(fields) != 4:
            raise ValueError('Malformed download pin: ' + line)
        if fields[:2] == [version, filename]:
            pins.append(fields[2:])
    if len(pins) != 1:
        actual = digest(path, 'sha512') if path.is_file() else 'unavailable (file absent)'
        raise ValueError(f"Missing or duplicate pin for {filename} version {version} in {manifest}: expected one pinned hash; actual SHA512 {actual}")
    algorithm, expected = pins[0]
    if algorithm not in ('sha512', 'sha256') or not re.fullmatch('[0-9a-f]{' + str(hashlib.new(algorithm).digest_size * 2) + '}', expected):
        raise ValueError(f"Invalid pin for {filename}: expected sha512/sha256 hex digest; actual {algorithm} {expected}")
    if path.exists():
        verify(path, filename, algorithm, expected)
        return 0
    if len(sys.argv) != 5:
        raise ValueError(f"Missing file {filename} ({path}): expected {algorithm} {expected}; actual unavailable")
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(prefix=path.name + '.', suffix='.part', dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
    try:
        result = subprocess.run(['curl', '-fsSL', '--retry', '3', '-o', str(temporary), sys.argv[4]])
        if result.returncode:
            return result.returncode
        verify(temporary, filename, algorithm, expected)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (ValueError, OSError) as error:
        sys.exit(str(error))
