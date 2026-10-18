#!/usr/bin/env python3
"""Import with a narrow cold-C# bootstrap exception; keep complete diagnostics on disk."""
import argparse
from pathlib import Path
import re
import subprocess
import sys


BOOTSTRAP = "ERROR: Failed to create an autoload, script 'res://src/Core/ShutdownGuard.cs' is not compiling."
ANSI = re.compile(r'\x1b\[[0-9;]*m')


def failures(output, cold):
    lines = ANSI.sub('', output).splitlines()
    errors = [line for line in lines if re.match(r'\s*(?:ERROR:|SCRIPT ERROR:|Parse Error:)', line)]
    return [line for line in errors if not (cold and line == BOOTSTRAP)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('godot')
    parser.add_argument('--log', default='build/setup-import.log')
    parser.add_argument('--strict', action='store_true')
    args = parser.parse_args()
    cold = not args.strict and not Path('.godot/mono/temp/bin/Debug/Propane.dll').is_file()
    log = Path(args.log)
    log.parent.mkdir(parents=True, exist_ok=True)
    with log.open('w') as stream:
        result = subprocess.run([args.godot, '--headless', '--path', '.', '--import'],
                                stdout=stream, stderr=subprocess.STDOUT)
    output = log.read_text(errors='replace')
    errors = failures(output, cold)
    if result.returncode or errors:
        print(f'Godot import failed (exit {result.returncode}; full log: {log}):', file=sys.stderr)
        # Include context around every error; exit-only failures still show their tail.
        lines = ANSI.sub('', output).splitlines()
        selected = set()
        for index, line in enumerate(lines):
            if line in errors or 'ERROR:' in line:
                selected.update(range(max(0, index - 1), min(len(lines), index + 8)))
        print('\n'.join(lines[i] for i in sorted(selected)) if selected else '\n'.join(lines[-40:]), file=sys.stderr)
        return 1
    if cold and BOOTSTRAP in output:
        print('Cold C# bootstrap: ShutdownGuard assembly is not built yet; setup will compile and recheck the import.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
