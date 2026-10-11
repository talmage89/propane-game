#!/usr/bin/env python3
"""Assert native silent-peer detection using a stopped process, without sending a leave.

Run after make build. Scratch and logs stay in --output (default build/liveness).
Use --project to measure a separately built baseline and --maximum for historical
timeouts. This real-time probe is intentionally outside the fast make test gate.
"""
import argparse
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--silent", choices=("client", "server"), default="client")
    parser.add_argument("--maximum", type=float, default=10.)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot-mono"))
    args = parser.parse_args()
    project = args.project.resolve()
    output = (args.output or project / "build/liveness").resolve()
    output.mkdir(parents=True, exist_ok=True)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    processes, files = [], []

    def launch(name, command):
        stream = (output / f"{name}.log").open("w")
        files.append(stream)
        process = subprocess.Popen([args.godot, "--headless", "--path", str(project), *command],
                                   stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
        processes.append(process)
        return process

    def read(name):
        return (output / f"{name}.log").read_text(errors="replace")

    def await_condition(condition, timeout):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if condition():
                return
            if any(p.poll() is not None for p in processes):
                raise RuntimeError("a process exited before the native liveness assertion; inspect logs")
            time.sleep(.025)
        raise RuntimeError("native liveness deadline exceeded; inspect logs")

    try:
        server = launch("server", ["--", "--server", f"--port={port}", "--start-score=5"])
        await_condition(lambda: "listening on UDP" in read("server"), 10)
        bots = []
        for i in (1, 2):
            bots.append(launch(f"bot{i}", ["res://scenes/dev/net_test.tscn", "--", "--server=127.0.0.1",
                        f"--port={port}", f"--role={'host' if i == 1 else 'join'}", "--players=2", "--length=90",
                        f"--name=Bot{i}", "--brain=simple", "--tune.FixedSeed=23"]))
            time.sleep(.4)
        await_condition(lambda: ": match on" in read("server"), 40)
        time.sleep(2)
        peer = re.search(r"peer (\d+) is Bot2", read("server"))
        if peer is None:
            raise RuntimeError("missing second peer identity")
        stopped = bots[1] if args.silent == "client" else server
        started = time.monotonic()
        os.killpg(stopped.pid, signal.SIGSTOP)
        if args.silent == "client":
            condition = lambda: f"peer {peer[1]} disconnect reason" in read("server")
        else:
            condition = lambda: any("status Offline Lost the connection" in read(f"bot{i}") for i in (1, 2))
        await_condition(condition, args.maximum)
        elapsed = time.monotonic() - started
        if elapsed < 2:
            raise RuntimeError(f"peer disconnected implausibly early ({elapsed:.3f}s)")
        print(f"PASS native silent {args.silent}: detected in {elapsed:.3f}s (bound {args.maximum:g}s)")
    finally:
        for process in processes:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGCONT)
                os.killpg(process.pid, signal.SIGTERM)
        for process in processes:
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
        for stream in files:
            stream.close()


if __name__ == "__main__":
    main()
