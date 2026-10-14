#!/usr/bin/env python3
"""Build optimized source assemblies for the editor engine, restoring its Debug load directory afterward."""
import contextlib
import os
from pathlib import Path
import shutil
import signal
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def godot():
    return os.environ.get("GODOT") or shutil.which("godot-mono") or shutil.which("godot")


@contextlib.contextmanager
def assembly(config, build=True):
    if config not in ("Debug", "Release"):
        raise ValueError("configuration must be Debug or Release")
    scratch = ROOT / "build" / "source-run"
    scratch.mkdir(parents=True, exist_ok=True)
    # A mkdir lock works on Linux/macOS without additional tools, and detects an interrupted swap.
    lock = scratch / "lock"
    lock.mkdir()
    debug = ROOT / ".godot/mono/temp/bin/Debug"
    backup = scratch / "Debug.backup"
    swapped = False
    previous_temp = os.environ.get("TMPDIR")
    try:
        os.environ["TMPDIR"] = str(scratch)
        if build:
            subprocess.run(["dotnet", "build", "Propane.csproj", "-c", config, "-nologo", "-v", "quiet"], cwd=ROOT, check=True)
        if config == "Release":
            if backup.exists():
                raise RuntimeError("Debug.backup exists; recover the interrupted source run before continuing")
            if debug.exists():
                debug.rename(backup)
            swapped = True
            shutil.copytree(debug.parent / "Release", debug)
        yield
    finally:
        try:
            if swapped:
                if debug.exists():
                    shutil.rmtree(debug)
                if backup.exists():
                    backup.rename(debug)
        finally:
            if previous_temp is None:
                os.environ.pop("TMPDIR", None)
            else:
                os.environ["TMPDIR"] = previous_temp
            lock.rmdir()


def interrupted(signum, _frame):
    raise SystemExit(128 + signum)


def main():
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", choices=("Debug", "Release"), default="Release")
    parser.add_argument("args", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    signal.signal(signal.SIGTERM, interrupted)
    with assembly(args.config):
        command = [godot(), "--path", str(ROOT)] + args.args
        return subprocess.run(command, cwd=ROOT).returncode


if __name__ == "__main__":
    raise SystemExit(main())
