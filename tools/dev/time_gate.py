#!/usr/bin/env python3
"""Time a make target and every external step, including Godot entry into the test scene.

Usage: python3 tools/dev/time_gate.py --output=build/gate-timing/test -- make test
For per-job timing, set make's LABEL to
"python3 -B tools/dev/time_gate.py --probe label $PWD/tools/dev/label.sh".
Godot startup ends at DevArgs.MarkReady; work includes scene execution and shutdown.
Import/editor runs have no scene marker and report only their total duration.
"""
import argparse
import json
import os
import pathlib
import shlex
import shutil
import signal
import subprocess
import sys
import tempfile
import time


def probe(name, executable, arguments):
    start = time.monotonic()
    started = time.time()
    ready = None
    process = subprocess.Popen([executable, *arguments], stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True)
    # Launchers intentionally stop a background server by signaling GODOT. Preserve that behavior through the
    # profiler; killing only this Python wrapper would leave the engine alive after its match.
    def forward(number, _frame):
        try:
            process.send_signal(number)
        except ProcessLookupError:
            pass

    previous = {}
    for signum in (signal.SIGTERM, signal.SIGINT):
        previous[signum] = signal.signal(signum, forward)
    try:
        for line in process.stdout:
            if "[gate-timing] ready" in line and ready is None:
                ready = time.monotonic() - start
            print(line, end="", flush=True)
        code = process.wait()
    finally:
        process.stdout.close()
        for signum, handler in previous.items():
            signal.signal(signum, handler)
    total = time.monotonic() - start
    row = dict(command=[name, *arguments], started=started, finished=time.time(), total=total, startup=ready,
               work_and_exit=None if ready is None else total - ready, code=code)
    # One append per record also works when make runs partitions in parallel.
    descriptor = os.open(os.environ["PROPANE_TIMING_LOG"], os.O_WRONLY | os.O_APPEND | os.O_CREAT, 0o600)
    try:
        os.write(descriptor, (json.dumps(row) + "\n").encode())
    finally:
        os.close(descriptor)
    return code


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("supply a command after --")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    log = output / "steps.jsonl"
    log.write_text("")
    godot = os.environ.get("GODOT") or shutil.which("godot-mono") or shutil.which("godot")
    if not godot:
        parser.error("Godot was not found; set GODOT to its executable")
    executables = dict(godot=shutil.which(godot), dotnet=shutil.which("dotnet"), python3=sys.executable)
    if not all(executables.values()):
        parser.error("Godot and dotnet must be executable")
    with tempfile.TemporaryDirectory(prefix="tools-", dir=output) as directory:
        for name, executable in executables.items():
            wrapper = pathlib.Path(directory) / name
            arguments = [sys.executable, str(pathlib.Path(__file__).resolve()), "--probe", name, executable]
            wrapper.write_text("#!/bin/sh\nexec " + shlex.join(arguments) + ' "$@"\n')
            wrapper.chmod(0o755)
        environment = dict(os.environ, PATH=f"{directory}:{os.environ['PATH']}",
                           GODOT=str(pathlib.Path(directory) / "godot"),
                           PROPANE_TIMING_LOG=str(log), PROPANE_GATE_TIMING="1")
        start = time.monotonic()
        started = time.time()
        load = os.getloadavg()
        code = subprocess.call(command, env=environment)
        elapsed = time.monotonic() - start
    (output / "total.json").write_text(json.dumps(dict(command=command, started=started, finished=time.time(), load=load, total=elapsed, code=code)) + "\n")
    print(f"\nGate timing: {elapsed:.3f}s, exit {code}; steps: {log}")
    for row in map(json.loads, log.read_text().splitlines()):
        phases = "" if row["startup"] is None else f'; startup {row["startup"]:.3f}s, work/exit {row["work_and_exit"]:.3f}s'
        print(f'{row["total"]:8.3f}s exit {row["code"]}: {shlex.join(row["command"])}{phases}')
    return code


if __name__ == "__main__":
    if sys.argv[1:2] == ["--probe"]:
        sys.exit(probe(sys.argv[2], sys.argv[3], sys.argv[4:]))
    sys.exit(main())
