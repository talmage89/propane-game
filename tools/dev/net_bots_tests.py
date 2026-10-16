#!/usr/bin/env python3
"""Prove the launcher propagates both checker failures, keeps earlier logs, and owns its whole process tree, without
launching Godot."""
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import unittest

SCRIPT = Path(__file__).with_name("net_bots.sh")

# A fake Godot, server and client in one. NAS_FAULT picks the failure to inject. For the process tests, NAS_PIDS names a
# directory where every process the fake starts is recorded, NAS_LINGER keeps the server running like a real one,
# NAS_HANG keeps the clients running, and NAS_STUBBORN adds a child that ignores SIGTERM.
FAKE_GODOT = '''#!/usr/bin/env python3
import os
import subprocess
import sys
import time
STILL = [sys.executable, "-c", "import time; time.sleep(120)"]
STUBBORN = [sys.executable, "-c", "import signal, time; signal.signal(signal.SIGTERM, signal.SIG_IGN); time.sleep(120)"]


def helpers():
    # A child in this process's group, one in a group and session of its own (like a windowed wrapper), and optionally
    # one that ignores SIGTERM. The launcher must find all of them from the process it started.
    started = [subprocess.Popen(STILL), subprocess.Popen(STILL, start_new_session=True)]
    if os.environ.get("NAS_STUBBORN") == "1":
        started.append(subprocess.Popen(STUBBORN))
    with open(os.path.join(os.environ["NAS_PIDS"], "pids"), "a") as stream:
        for pid in [os.getpid(), *[child.pid for child in started]]:
            stream.write(f"{pid}\\n")


if "--server" in sys.argv:
    if os.environ["NAS_FAULT"] == "startup":
        sys.exit(7)
    if os.environ.get("NAS_LINGER") == "1":
        helpers()
    print("listening on UDP " + next(arg.split("=", 1)[1] for arg in sys.argv if arg.startswith("--port=")) + " (test)", flush=True)
    print("match 1: bank audit initial=0 map=1 drops=0 spills=0 banked=1 expected=1 ok=True", flush=True)
    if os.environ.get("NAS_LINGER") == "1":
        time.sleep(120)
else:
    assert "--tank-check" in sys.argv
    second = "--name=Bot2" in sys.argv
    fault = os.environ["NAS_FAULT"]
    if os.environ.get("NAS_HANG") == "1":
        helpers()
        time.sleep(120)
    if fault == "process" and second:
        sys.exit(7)
    if "--leave-who=Bot2" in sys.argv and second:
        print("leaving the match on purpose")
        sys.exit(0)
    if "--scenario=capacity" in sys.argv and not second:
        print("capacity creator departure")
        sys.exit(0)
    if fault == "engine" and second:
        print("ERROR: rifle contact failure")
    for tick in range(61):
        phase = "P" if tick < 40 else "E"
        x = 2 if fault == "tank" and second else 0
        print(f"tank-check match 1 time {tick / 10:.6f} phase {phase} final {tick == 60} length 4 n 1 a:I:R:{x},0,0")
    scores = "2=0 3=1" if fault == "score" and second else "2=1 3=0"
    print(f"finished match 1; final scores {scores}")
'''


def write_fake(root):
    fake = root / "godot"
    fake.write_text(FAKE_GODOT)
    fake.chmod(0o755)
    return fake


class LauncherChecks(unittest.TestCase):
    def run_launcher(self, fault, *flags, prepare=None, environment=None):
        with tempfile.TemporaryDirectory(prefix="nas-launcher-") as directory:
            root = Path(directory)
            fake = write_fake(root)
            if prepare:
                prepare(root / "logs")
            # Only orchestrator startup sleeps are elided. The fake emits complete captured data immediately.
            command = ["bash", "-c", 'sleep() { :; }; export -f sleep; bash "$@"', "nas-tests",
                       str(SCRIPT), "-g", str(fake), "-n", "2", "-l", "4",
                       "-o", str(root / "logs"), *flags]
            result = subprocess.run(command, env={**os.environ, "NAS_FAULT": fault, **(environment or {})}, capture_output=True, text=True)
            self.logs = {path.name: path.read_text() for path in sorted((root / "logs").glob("*")) if path.is_file()}
            return result

    def test_failed_server_stops_before_clients_launch(self):
        result = self.run_launcher("startup")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("server did not become ready", result.stderr)
        self.assertNotIn("bots finished", result.stdout)

    def test_pass_and_both_independent_failures(self):
        for fault, status, output in (("none", 0, "PASS tanks"), ("tank", 1, "FAIL tanks"),
                                      ("score", 1, "individual scores disagree"), ("engine", 1, "FAIL engine or protocol"), ("process", 1, "no tank-check samples")):
            with self.subTest(fault=fault):
                result = self.run_launcher(fault)
                self.assertEqual(result.returncode, status, result.stdout + result.stderr)
                self.assertIn(output, result.stdout + result.stderr)

    def test_skip_flag_and_process_failure_preserved(self):
        self.assertEqual(self.run_launcher("tank", "-k").returncode, 0)
        self.assertEqual(self.run_launcher("process", "-k").returncode, 7)

    def test_capacity_only_omits_its_intentionally_departed_creator(self):
        result = self.run_launcher("none", "--", "--scenario=capacity")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("tank comparison skipped", result.stdout)
        result = self.run_launcher("process", "--", "--scenario=capacity")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)

    def test_remote_requires_separate_audit(self):
        result = self.run_launcher("none", "-S", "localhost:24680")
        self.assertEqual(result.returncode, 2)
        self.assertIn("remote server audit log", result.stderr)

    def test_a_client_leaving_on_purpose_is_omitted_from_completion_checks(self):
        result = self.run_launcher("none", "--", "--leave-at=2", "--leave-who=Bot2")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("tank comparison skipped", result.stdout)
        self.assertEqual(self.run_launcher("score", "--", "--leave-at=2", "--leave-who=Bot2").returncode, 0)

    def test_a_successful_run_names_its_logs_as_before(self):
        self.assertEqual(self.run_launcher("none").returncode, 0)
        self.assertEqual(sorted(self.logs), ["bot1.log", "bot2.log", "server.log"])

    def test_earlier_logs_are_refused_and_left_alone(self):
        def earlier(logs):
            logs.mkdir()
            (logs / "server.log").write_text("evidence from the last run\n")
            (logs / "bot1.log").write_text("more evidence\n")
            (logs / "notes.txt").write_text("not a log\n")
        result = self.run_launcher("none", prepare=earlier)
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("already holds 2 log file(s)", result.stderr)
        self.assertIn("-f", result.stderr)
        self.assertEqual(self.logs, {"bot1.log": "more evidence\n", "notes.txt": "not a log\n",
                                     "server.log": "evidence from the last run\n"})
        self.assertNotIn("bots finished", result.stdout)

    def test_the_replace_flag_replaces_logs_and_only_logs(self):
        def earlier(logs):
            logs.mkdir()
            (logs / "server.log").write_text("evidence from the last run\n")
            (logs / "bot9.log").write_text("a bigger earlier run\n")
            (logs / "metrics.csv").write_text("kept\n")
        result = self.run_launcher("none", "-f", prepare=earlier)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(sorted(self.logs), ["bot1.log", "bot2.log", "metrics.csv", "server.log"])
        self.assertEqual(self.logs["metrics.csv"], "kept\n")
        self.assertIn("listening on UDP", self.logs["server.log"])

    def test_other_files_in_the_directory_are_not_a_reason_to_refuse(self):
        def earlier(logs):
            logs.mkdir()
            (logs / "metrics.csv").write_text("kept\n")
        self.assertEqual(self.run_launcher("none", prepare=earlier).returncode, 0)
        self.assertEqual(self.logs["metrics.csv"], "kept\n")


class ProcessOwnership(unittest.TestCase):
    """Nothing the launcher starts, or that its processes start, may outlive it."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="nas-owner-")
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.fake = write_fake(self.root)
        self.pids = self.root / "pids"
        self.pids.mkdir()
        self.runs = 0
        self.addCleanup(self.kill_survivors)

    def recorded(self):
        path = self.pids / "pids"
        return [int(line) for line in path.read_text().split()] if path.exists() else []

    @staticmethod
    def running(pid):
        state = subprocess.run(["ps", "-o", "stat=", "-p", str(pid)], capture_output=True, text=True).stdout.strip()
        return bool(state) and not state.startswith("Z")

    def kill_survivors(self):
        for pid in self.recorded():
            if self.running(pid):
                os.kill(pid, signal.SIGKILL)

    def assert_nothing_left(self, since, expected):
        pids = self.recorded()[since:]
        self.assertEqual(len(pids), expected, f"the fake did not start what the test needs: {pids}")
        self.assertEqual([pid for pid in pids if self.running(pid)], [], "processes outlived the launcher")

    def launch(self, *flags, **environment):
        self.runs += 1
        env = {**os.environ, "NAS_FAULT": "none", "NAS_PIDS": str(self.pids), "NET_BOTS_GRACE": "1", **environment}
        return subprocess.Popen(["bash", str(SCRIPT), "-g", str(self.fake), "-n", "2", "-l", "4",
                                 "-o", str(self.root / f"logs{self.runs}"), *flags], env=env, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)

    def wait_for_processes(self, since, count, launcher):
        deadline = time.monotonic() + 30
        while len(self.recorded()) < since + count:
            self.assertIsNone(launcher.poll(), "the launcher ended before everything was running")
            self.assertLess(time.monotonic(), deadline, f"only {self.recorded()} started")
            time.sleep(0.05)

    def test_an_interrupt_stops_the_server_every_client_and_their_children(self):
        # SIGTERM also meets a child that ignores it, which costs the grace period (1 s here) and then goes by SIGKILL.
        for number, expected, stubborn in ((signal.SIGINT, 130, "0"), (signal.SIGTERM, 143, "1"), (signal.SIGHUP, 129, "0")):
            with self.subTest(signal=number.name):
                since = len(self.recorded())
                launcher = self.launch(NAS_LINGER="1", NAS_HANG="1", NAS_STUBBORN=stubborn)
                # The server and both clients, each the process itself plus its children.
                count = 3 * (4 if stubborn == "1" else 3)
                self.wait_for_processes(since, count, launcher)
                launcher.send_signal(number)
                launcher.communicate(timeout=30)
                self.assertEqual(launcher.returncode, expected)
                self.assert_nothing_left(since, count)

    def test_finished_and_failed_runs_stop_the_server_and_its_children(self):
        for fault, status, stubborn in (("none", 0, "1"), ("process", 1, "0"), ("tank", 1, "0")):
            with self.subTest(fault=fault):
                since = len(self.recorded())
                launcher = self.launch(NAS_FAULT=fault, NAS_LINGER="1", NAS_STUBBORN=stubborn)
                out, err = launcher.communicate(timeout=60)
                self.assertEqual(launcher.returncode, status, out + err)
                self.assert_nothing_left(since, 4 if stubborn == "1" else 3)

    def test_a_bad_color_launches_nothing(self):
        launcher = self.launch("-c", "12ABEF,not-hex", NAS_LINGER="1")
        out, err = launcher.communicate(timeout=30)
        self.assertEqual(launcher.returncode, 2, out + err)
        self.assertIn("missing or invalid hex color for bot 2", err)
        self.assertEqual(self.recorded(), [])
        self.assertFalse((self.root / "logs1" / "server.log").exists())

    def test_refusing_an_old_output_directory_launches_nothing(self):
        (self.root / "logs1").mkdir()
        (self.root / "logs1" / "server.log").write_text("evidence\n")
        launcher = self.launch(NAS_LINGER="1")
        out, err = launcher.communicate(timeout=30)
        self.assertEqual(launcher.returncode, 2, out + err)
        self.assertEqual(self.recorded(), [])
        self.assertEqual((self.root / "logs1" / "server.log").read_text(), "evidence\n")


if __name__ == "__main__":
    unittest.main()
