#!/usr/bin/env python3
"""Run one retained FU2a workload with command identity and shared-machine load snapshots.

Everything after -- is passed verbatim to net_bots.sh. This wrapper never changes
its verdict or retries a failure. The output directory must be fresh.
"""
import argparse
import json
import os
import signal
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]


def interrupted(signum, _frame):
    raise SystemExit(128 + signum)


def snapshot(stream, phase):
    stream.write(f'\n{phase} monotonic={time.monotonic():.6f}\n')
    stream.flush()
    subprocess.run(['uptime'], stdout=stream, check=True)
    processes = subprocess.check_output(['ps', '-eo', 'pid,ppid,pcpu,pmem,comm,args'], text=True)
    stream.write('\n'.join(line for line in processes.splitlines() if len(line.split(None, 5)) == 6
                           and line.split(None, 5)[4] in ('godot-mono', 'gamescope')) + '\n')
    stream.flush()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', type=Path, default=ROOT)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('arguments', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    for signum in (signal.SIGTERM, signal.SIGHUP):
        signal.signal(signum, interrupted)
    args.output.mkdir(parents=True, exist_ok=False)
    project, output = args.project.resolve(), args.output.resolve()
    arguments = args.arguments[1:] if args.arguments[:1] == ['--'] else args.arguments
    command = [str(project / 'tools/dev/net_bots.sh'), '-o', str(output), *arguments]
    (output / 'command.json').write_text(json.dumps(dict(command=command, project=str(project),
        checkout_head=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()), indent=2) + '\n')
    scratch = ROOT / 'build/fu2a-tmp'
    scratch.mkdir(exist_ok=True)
    environment = dict(os.environ, TMPDIR=str(scratch))
    with (output / 'load.txt').open('w') as load_log, (output / 'launcher.txt').open('w') as log:
        snapshot(load_log, 'before')
        process = subprocess.Popen(command, cwd=project, env=environment, stdout=log, stderr=subprocess.STDOUT)
        try:
            while process.poll() is None:
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    snapshot(load_log, 'during')
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=30)
            snapshot(load_log, 'after')
    (output / 'status.json').write_text(json.dumps(dict(exit=process.returncode)) + '\n')
    raise SystemExit(process.returncode)


if __name__ == '__main__':
    main()
