#!/usr/bin/env python3
"""Prove both managed and native scene-thread violations fail clearly, in disposable child processes."""
import os
import pathlib
import re
import subprocess


ROOT = pathlib.Path(__file__).resolve().parents[2]
for probe, caller in (("managed", "injected unit callback"), ("node", "GetChildCount"),
                      ("finalizer", "ThreadFinalizerProbe.Finalize")):
    command = [os.environ.get("GODOT", "godot-mono"), "--headless", "--path", str(ROOT),
               "res://scenes/dev/unit_tests.tscn", "--", f"--test-thread-violation={probe}"]
    result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
    threads = re.search(r"main=(\d+), caller=(\d+)", result.stdout)
    if (result.returncode != 1 or "[thread] FAIL off-main scene access:" not in result.stdout or
            threads is None or threads[1] == threads[2] or caller not in result.stdout):
        raise SystemExit(f"FAIL: {probe} thread probe returned {result.returncode}\n{result.stdout}")
    print(f"PASS: {probe} scene-thread violation asserted with exit 1 and its managed caller")
