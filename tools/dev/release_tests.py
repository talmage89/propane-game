#!/usr/bin/env python3
"""Prove the release scripts without publishing anything: tools/release/cut.sh against a throwaway repository whose
origin is a local bare repository, and tools/release/build.sh against fake Godot, rcodesign and curl.

Both scripts locate the project from their own path, so each test copies them into a scratch project. Nothing here
touches this checkout, a real remote or the network. The packaging test needs zip, unzip and tar, as build.sh does.
"""
import hashlib
import os
import shutil
import stat
import subprocess
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RELEASE = ROOT / "tools" / "release"

SHARED_PATHS = ["src/Net/Message.cs", "src/World/Suburb.cs", "src/Tank/Tank.cs", "src/Player/PlayerNet.cs",
                "src/Core/ActorIdentity.cs", "src/Core/AppearanceRecord.cs", "src/Core/AppearancePalette.cs",
                "src/Core/BodyIds.cs", "src/Core/BodyState.cs", "src/Player/PlayerNetState.cs",
                "src/Core/ServerSettings.cs", "src/Core/ProcessPhysicsPolicies.cs",
                "src/Core/Tuning.cs", "src/Core/Tuning.70.Match.cs", "src/Core/Tuning.85.Lab.cs", "src/Modes/KingRules.cs"]
UNSHARED_PATHS = ["src/Dev/Probe.cs", "src/Core/Other.cs", "src/Player/Player.cs", "README.md"]


def executable(path, text):
    path.write_text(text)
    path.chmod(path.stat().st_mode | stat.S_IXUSR)


def git_environment(home):
    return {**os.environ, "HOME": str(home), "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_SYSTEM": os.devnull,
            "GIT_AUTHOR_NAME": "Release Test", "GIT_AUTHOR_EMAIL": "test@example.invalid",
            "GIT_COMMITTER_NAME": "Release Test", "GIT_COMMITTER_EMAIL": "test@example.invalid",
            "GIT_TERMINAL_PROMPT": "0"}


class CutScript(unittest.TestCase):
    """cut.sh runs the real gate before it changes or publishes anything."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="release-cut-")
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.env = git_environment(self.root / "home")
        (self.root / "home").mkdir()
        self.remote = self.root / "remote.git"
        self.git(self.root, "init", "--bare", "--quiet", "-b", "dev", str(self.remote))
        seed = self.root / "seed"
        (seed / "tools" / "release").mkdir(parents=True)
        for name in ("cut.sh", "check_version.sh"):
            shutil.copy(RELEASE / name, seed / "tools" / "release" / name)
        for path in ["src/Dev/Probe.cs", "src/Core/Other.cs", "src/Player/Player.cs", "README.md", *SHARED_PATHS]:
            (seed / path).parent.mkdir(parents=True, exist_ok=True)
            (seed / path).write_text("// 1\n")
        (seed / "src/Net/Protocol.cs").write_text("public static class Protocol { public const int Version = 3; }\n")
        (seed / "project.godot").write_text('[application]\nconfig/version="1.2.0"\n')
        (seed / ".gitignore").write_text("build/\n")
        self.git(seed, "init", "--quiet", "-b", "dev")
        self.git(seed, "add", "-A")
        self.git(seed, "commit", "--quiet", "-m", "Release v1.2.0")
        self.git(seed, "tag", "v1.2.0")
        self.git(seed, "remote", "add", "origin", str(self.remote))
        self.git(seed, "branch", "main")
        self.git(seed, "push", "--quiet", "--tags", "origin", "dev", "main")
        self.seed = seed
        self.work = self.root / "work"
        self.git(self.root, "clone", "--quiet", "-b", "dev", str(self.remote), str(self.work))
        self.git(self.work, "branch", "main", "origin/main")
        # The script under test must never see a remote outside this scratch directory.
        self.assertTrue(self.git(self.work, "remote", "get-url", "origin").startswith(str(self.root)))
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.log = self.root / "fake.log"
        self.state = self.root / "fake-state"
        self.state.mkdir()
        executable(self.bin / "make", f'''#!/usr/bin/env bash
# The gate stand-in: record what the release script had and had not done when it ran, then pass or fail on request.
{{
  echo "make $* GODOT=$GODOT"
  echo "  version $(sed -n 's/^config\\/version="\\(.*\\)"$/\\1/p' project.godot)"
  echo "  head $(git rev-parse HEAD)"
  echo "  remote-main $(git --git-dir="{self.remote}" rev-parse refs/heads/main)"
}} >>"{self.log}"
echo "fake gate output line"
exit "$(cat "{self.state}/make" 2>/dev/null || echo 0)"
''')
        executable(self.bin / "godot", f'''#!/usr/bin/env bash
echo "godot $*" >>"{self.log}"
exit "$(cat "{self.state}/godot" 2>/dev/null || echo 0)"
''')
        self.env["PATH"] = f"{self.bin}:{self.env['PATH']}"
        self.env["GODOT"] = str(self.bin / "godot")

    def git(self, directory, *arguments):
        result = subprocess.run(["git", "-C", str(directory), *arguments], env=self.env, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        self.assertEqual(result.returncode, 0, f"git {' '.join(arguments)}: {result.stdout}")
        return result.stdout.strip()

    def commit(self, directory, path, text="// changed\n", message="Change"):
        (Path(directory) / path).parent.mkdir(parents=True, exist_ok=True)
        (Path(directory) / path).write_text(text)
        self.git(directory, "add", "-A")
        self.git(directory, "commit", "--quiet", "-m", message)

    def snapshot(self):
        """Everything a release could change, local and remote."""
        def ref(directory, name):
            result = subprocess.run(["git", "-C", str(directory), "rev-parse", "--verify", "--quiet", name],
                                    env=self.env, text=True, stdout=subprocess.PIPE)
            return result.stdout.strip() or None
        return {"project": (self.work / "project.godot").read_text(), "dev": ref(self.work, "refs/heads/dev"),
                "main": ref(self.work, "refs/heads/main"), "branch": self.git(self.work, "branch", "--show-current"),
                "tags": self.git(self.work, "tag", "-l"), "remote dev": ref(self.remote, "refs/heads/dev"),
                "remote main": ref(self.remote, "refs/heads/main"), "remote tags": self.git(self.remote, "tag", "-l")}

    def cut(self, *arguments, stdin="", make_exit=0, godot_exit=0):
        self.log.write_text("")
        (self.state / "make").write_text(f"{make_exit}\n")
        (self.state / "godot").write_text(f"{godot_exit}\n")
        result = subprocess.run(["bash", "tools/release/cut.sh", *arguments], cwd=self.work, env=self.env, text=True,
                                input=stdin, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120)
        return result, self.log.read_text()

    def refuses(self, expected, *arguments, stdin=""):
        """The script stops with an error before the gate runs, and nothing it guards has changed."""
        before = self.snapshot()
        result, log = self.cut(*arguments, stdin=stdin)
        output = result.stdout + result.stderr
        self.assertNotEqual(result.returncode, 0, output)
        self.assertIn(expected, output)
        self.assertEqual(log, "", f"the gate or a test ran before the refusal: {log}")
        self.assertEqual(self.snapshot(), before)
        return result

    def test_success_runs_the_gate_before_any_change_then_pushes_once(self):
        before = self.snapshot()
        result, log = self.cut("patch", "-y")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        lines = log.splitlines()
        # make check first, seeing the released version (uncommitted), the old head and an untouched main; then the
        # deeper generator sweep.
        self.assertEqual(lines[0], f"make check GODOT={self.bin / 'godot'}")
        self.assertEqual(lines[1], "  version 1.2.1")
        self.assertEqual(lines[2], f"  head {before['dev']}")
        self.assertEqual(lines[3], f"  remote-main {before['remote main']}")
        self.assertEqual(lines[4:], ["godot --headless --path . res://scenes/dev/generator_tests.tscn -- --seeds=100"])
        after = self.snapshot()
        self.assertIn('config/version="1.2.1"', after["project"])
        self.assertEqual(self.git(self.work, "log", "-1", "--format=%s"), "Release v1.2.1")
        self.assertEqual(self.git(self.work, "show", "--stat", "--format=", "HEAD"), "project.godot | 2 +-\n 1 file changed, 1 insertion(+), 1 deletion(-)")
        self.assertEqual({after["dev"], after["main"], after["remote dev"], after["remote main"]}, {after["dev"]})
        self.assertNotEqual(after["dev"], before["dev"])
        self.assertEqual(self.git(self.work, "rev-parse", "HEAD~1"), before["dev"])
        self.assertIn("Pushed v1.2.1 to main", result.stdout)

    def test_a_failing_gate_changes_and_pushes_nothing(self):
        for stage, kwargs in (("make check", {"make_exit": 1}), ("generator sweep", {"godot_exit": 1})):
            with self.subTest(stage=stage):
                before = self.snapshot()
                result, log = self.cut("patch", "-y", **kwargs)
                output = result.stdout + result.stderr
                self.assertEqual(result.returncode, 1, output)
                self.assertIn("the tests failed", output)
                self.assertIn("nothing was changed or pushed", output)
                if stage == "make check":
                    self.assertIn("fake gate output line", result.stderr, "the failure shows the end of the gate log")
                    self.assertNotIn("godot --headless", log, "the sweep must not run after a failed gate")
                self.assertEqual(self.snapshot(), before)
                self.assertEqual(self.git(self.work, "status", "--porcelain"), "")

    def test_a_dev_already_at_the_version_is_gated_and_pushed_without_a_new_bump(self):
        # A release whose workflow failed before publishing: dev carries the bump, no tag exists, a fix follows it.
        self.commit(self.work, "project.godot", '[application]\nconfig/version="1.2.1"\n', message="Release v1.2.1")
        self.commit(self.work, "src/Dev/Probe.cs", message="Fix the release gate")
        self.git(self.work, "push", "--quiet", "origin", "dev")
        before = self.snapshot()
        result, log = self.cut("patch", "-y")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(log.splitlines()[1], "  version 1.2.1")
        after = self.snapshot()
        self.assertEqual(after["dev"], before["dev"], "no second bump commit")
        self.assertEqual({after["main"], after["remote dev"], after["remote main"]}, {before["dev"]})
        self.assertEqual(self.git(self.work, "status", "--porcelain"), "")

    def test_a_failing_gate_stops_every_kind_of_release(self):
        before = self.snapshot()
        for bump in ("minor", "major", "1.2.5"):
            self.assertEqual(self.cut(bump, "-y", make_exit=2)[0].returncode, 1, bump)
            self.assertEqual(self.snapshot(), before, bump)

    def test_refusals_stop_before_the_gate(self):
        self.refuses("usage:")
        self.refuses("is not MAJOR.MINOR.PATCH", "banana", "-y")
        self.refuses("is not newer than the latest release", "1.2.0", "-y")
        self.refuses("is not newer than the latest release", "1.1.9", "-y")
        self.refuses("stopped", "patch", stdin="n\n")
        self.refuses("stopped", "patch", stdin="")

    def test_refuses_a_patch_after_a_protocol_change(self):
        self.commit(self.work, "src/Net/Protocol.cs", "public static class Protocol { public const int Version = 4; }\n")
        self.refuses("the network protocol changed since v1.2.0 (3 to 4)", "patch", "-y")
        # The same change is fine as a minor release.
        result, log = self.cut("minor", "-y")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('config/version="1.3.0"', (self.work / "project.godot").read_text())

    def test_refuses_the_wrong_branch_a_dirty_tree_and_a_stale_dev(self):
        (self.work / "README.md").write_text("// dirty\n")
        self.refuses("commit or stash your changes first", "patch", "-y")
        (self.work / "README.md").write_text("// 1\n")
        (self.work / "untracked.txt").write_text("x\n")
        self.refuses("commit or stash your changes first", "patch", "-y")
        (self.work / "untracked.txt").unlink()
        # Someone else pushed to dev.
        other = self.root / "other"
        self.git(self.root, "clone", "--quiet", "-b", "dev", str(self.remote), str(other))
        self.commit(other, "src/Dev/Probe.cs", message="Elsewhere")
        self.git(other, "push", "--quiet", "origin", "dev")
        self.refuses("dev is behind origin/dev", "patch", "-y")
        self.git(self.work, "pull", "--quiet", "--ff-only")
        # main moved without dev.
        self.git(other, "checkout", "--quiet", "-b", "side", "origin/main")
        self.commit(other, "README.md", "// side\n", message="Hotfix on main")
        self.git(other, "push", "--quiet", "origin", "side:main")
        self.refuses("main has commits that dev lacks", "patch", "-y")
        self.git(self.work, "fetch", "--quiet", "origin")
        self.git(self.work, "merge", "--quiet", "--no-edit", "origin/main")
        self.git(self.work, "push", "--quiet", "origin", "dev")
        self.git(self.work, "checkout", "--quiet", "-b", "topic")
        self.refuses("releases are cut from dev", "patch", "-y")

    def test_changed_shared_files_need_a_confirmation_for_a_patch(self):
        for path in SHARED_PATHS:
            with self.subTest(path=path):
                self.git(self.work, "reset", "--quiet", "--hard", "v1.2.0")
                self.commit(self.work, path)
                result = self.refuses("these files that players in a match share changed since v1.2.0",
                                      "patch", stdin="n\n")
                self.assertIn(f"  {path}", result.stdout)
                # Stopping at the first prompt: consenting there runs the gate and releases.
                self.refuses("stopped", "patch", stdin="")

    def test_consenting_to_a_shared_patch_runs_the_gate_and_releases(self):
        self.commit(self.work, "src/Modes/KingRules.cs")
        result, log = self.cut("patch", stdin="y\ny\n")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(log.startswith("make check GODOT="))
        self.assertIn('config/version="1.2.1"', (self.work / "project.godot").read_text())

    def test_unshared_files_do_not_prompt_for_a_patch(self):
        for path in UNSHARED_PATHS:
            self.commit(self.work, path)
        result, log = self.cut("patch", stdin="y\n")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertNotIn("players in a match share", result.stdout)

    def test_shared_files_do_not_prompt_for_a_minor_release(self):
        for path in SHARED_PATHS:
            self.commit(self.work, path)
        result, log = self.cut("minor", stdin="y\n")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertNotIn("players in a match share", result.stdout)

    def test_a_rejected_push_keeps_the_local_bump_and_leaves_main_alone(self):
        hook = self.remote / "hooks" / "pre-receive"
        executable(hook, "#!/bin/sh\necho rejected on purpose >&2\nexit 1\n")
        before = self.snapshot()
        result, log = self.cut("patch", "-y")
        output = result.stdout + result.stderr
        self.assertEqual(result.returncode, 1, output)
        self.assertIn("the push failed; the version bump is committed on dev", output)
        after = self.snapshot()
        self.assertEqual((after["remote dev"], after["remote main"], after["main"]),
                         (before["remote dev"], before["remote main"], before["main"]))
        self.assertEqual(self.git(self.work, "log", "-1", "--format=%s"), "Release v1.2.1")

    def test_a_failed_commit_restores_the_version(self):
        executable(self.work / ".git" / "hooks" / "pre-commit", "#!/bin/sh\nexit 1\n")
        before = self.snapshot()
        result, log = self.cut("patch", "-y")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("could not commit the version bump", result.stdout + result.stderr)
        self.assertEqual(self.snapshot(), before)
        self.assertEqual(self.git(self.work, "status", "--porcelain"), "")


class PackageScript(unittest.TestCase):
    """build.sh packages from its own staging directory and leaves the rest of build/ alone."""

    def setUp(self):
        for tool in ("zip", "unzip", "tar"):
            self.assertIsNotNone(shutil.which(tool), f"{tool} is required to run the packaging test")
        self.directory = tempfile.TemporaryDirectory(prefix="release-package-")
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.project = self.root / "project"
        (self.project / "tools" / "release").mkdir(parents=True)
        shutil.copy(RELEASE / "build.sh", self.project / "tools" / "release" / "build.sh")
        (self.project / "tools/downloads").mkdir()
        shutil.copyfile(RELEASE.parent / "downloads/verify.py", self.project / "tools/downloads/verify.py")
        notice_hash = hashlib.sha512(b"fixture notice\n").hexdigest()
        (self.project / "tools/downloads/pins.txt").write_text(
            "".join(f"4.7.2 {name}.txt sha512 {notice_hash}\n" for name in ("LICENSE", "COPYRIGHT")))
        (self.project / "project.godot").write_text('[application]\nconfig/version="1.2.3"\n')
        (self.project / "CREDITS.md").write_text("credits\n")
        (self.project / "tools/assets").mkdir()
        (self.project / "tools/assets/check.py").write_text(
            'import pathlib, sys\n'
            f'fault = pathlib.Path({str(self.root / "fault")!r})\n'
            'if fault.exists() and fault.read_text() == "assets": raise SystemExit("invalid assets")\n'
            'pathlib.Path(sys.argv[sys.argv.index("--report") + 1]).write_text("{}")\n')
        for kit in ('cars', 'furniture', 'nature', 'suburban'):
            notice = self.project / f'assets/third_party/kenney/{kit}/License.txt'
            notice.parent.mkdir(parents=True)
            notice.write_text(f'{kit} CC0 notice\n')

        self.bin = self.root / "bin"
        self.bin.mkdir()
        # Fake Godot: report a version, and "export" by writing a file (macOS: a zip holding the app).
        executable(self.bin / "godot", f'''#!{shutil.which("python3")}
import os, sys, zipfile
args = sys.argv[1:]
if "--version" in args:
    print("4.7.2.stable.mono.official.fake")
elif "--export-release" in args:
    preset, path = args[args.index("--export-release") + 1:][:2]
    fault = open("{self.root}/fault").read().strip() if os.path.exists("{self.root}/fault") else ""
    if fault == preset + "-log":
        print("ERROR: injected export failure")
    if fault == preset + "-exit":
        sys.exit(3)
    if preset == "macOS":
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("Propane.app/Contents/MacOS/Propane", "mac binary")
    else:
        open(path, "w").write(preset + " binary")
''')
        executable(self.bin / "rcodesign", '#!/usr/bin/env bash\n[[ "$1" == sign ]] && echo signed > "$2/signed.txt"\n')
        executable(self.bin / "curl", '#!/usr/bin/env bash\nwhile [[ $# -gt 0 ]]; do [[ "$1" == -o ]] && echo "fixture notice" > "$2"; shift; done\n')
        self.env = {**os.environ, "PATH": f"{self.bin}:{os.environ['PATH']}", "RCODESIGN": str(self.bin / "rcodesign")}
        build = self.project / "build"
        self.sentinels = {"keep/evidence.log": "other verification output\n", "shots/a.png": "png",
                          "rifle-soak-gate/server.log": "gate logs\n", "net/bot1.log": "bot\n", "sentinel.txt": "top level\n",
                          "tmp/scratch": "x"}
        for name, text in self.sentinels.items():
            (build / name).parent.mkdir(parents=True, exist_ok=True)
            (build / name).write_text(text)

    def package(self, fault=None, environment=None, unsigned=False):
        (self.root / "fault").unlink(missing_ok=True)
        if fault:
            (self.root / "fault").write_text(fault)
        return subprocess.run(["bash", "tools/release/build.sh", str(self.bin / "godot"), *(["--unsigned-macos"] if unsigned else [])], cwd=self.project,
                              env={**self.env, **(environment or {})}, text=True, stdout=subprocess.PIPE,
                              stderr=subprocess.STDOUT, timeout=120)

    def assert_sentinels(self):
        for name, text in self.sentinels.items():
            self.assertEqual((self.project / "build" / name).read_text(), text, f"{name} was changed or deleted")

    def test_packages_into_dist_and_keeps_other_build_output(self):
        stale = self.project / "build" / "dist" / "Propane-0.0.1-linux-x86_64.tar.gz"
        stale.parent.mkdir(parents=True)
        stale.write_text("an older release")
        result = self.package()
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assert_sentinels()
        build = self.project / "build"
        self.assertEqual(sorted(path.name for path in (build / "dist").iterdir()),
                         ["Propane-1.2.3-linux-x86_64.tar.gz", "Propane-1.2.3-macos-arm64.zip", "Propane-1.2.3-windows-x86_64.zip"],
                         "the release workflow uploads build/dist/* whole, so a stale archive must not survive")
        # Everything else packaging produced is under its own staging directory.
        self.assertEqual(sorted(path.name for path in build.iterdir()), ["dist", "keep", "net", "package", "rifle-soak-gate", "sentinel.txt", "shots", "tmp"])
        self.assertEqual(sorted(path.name for path in (build / "package").glob("*.log")),
                         ["import.log", "linux.log", "macos.log", "sign.log", "windows.log"])
        notices = {"CREDITS.md", "GODOT_LICENSE.txt", "GODOT_COPYRIGHT.txt"} | {f"KENNEY_{kit}_LICENSE.txt" for kit in ("cars", "furniture", "nature", "suburban")}
        with tarfile.open(build / "dist" / "Propane-1.2.3-linux-x86_64.tar.gz") as archive:
            self.assertEqual({n for n in archive.getnames() if n.count("/") == 1 and not n.endswith("/")},
                             {f"Propane-1.2.3-linux-x86_64/{n}" for n in notices | {"Propane.x86_64"}})
        with zipfile.ZipFile(build / "dist" / "Propane-1.2.3-windows-x86_64.zip") as archive:
            self.assertEqual(set(archive.namelist()), notices | {"Propane.exe"})
        with zipfile.ZipFile(build / "dist" / "Propane-1.2.3-macos-arm64.zip") as archive:
            self.assertEqual(set(archive.namelist()), notices | {"Propane.app/", "Propane.app/Contents/", "Propane.app/Contents/MacOS/",
                                                                  "Propane.app/Contents/MacOS/Propane", "Propane.app/signed.txt"})
        self.assertFalse((self.project / "build_id.txt").exists(), "the build id file is removed again")

    def test_invalid_assets_stop_before_export(self):
        result = self.package(fault='assets')
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn('invalid assets', result.stdout)
        self.assertFalse((self.project / 'build/package/import.log').exists())
        self.assertEqual(list((self.project / 'build/dist').iterdir()), [])
        self.assert_sentinels()

    def test_unverified_notices_never_reach_a_package(self):
        pins = self.project / "tools/downloads/pins.txt"
        original = pins.read_text()
        for content, message in (("", "Missing or duplicate pin"),
                                 (original.replace(hashlib.sha512(b"fixture notice\n").hexdigest(), "0" * 128),
                                  "Checksum mismatch")):
            with self.subTest(message=message):
                pins.write_text(content)
                result = self.package()
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn(message, result.stdout)
                self.assertIn("LICENSE.txt", result.stdout)
                self.assertEqual(list((self.project / "build/dist").iterdir()), [])
                self.assert_sentinels()

    def test_a_second_run_replaces_the_first_and_still_keeps_other_output(self):
        self.assertEqual(self.package().returncode, 0)
        (self.project / "build" / "package" / "left-over").write_text("x")
        self.assertEqual(self.package().returncode, 0)
        self.assertFalse((self.project / "build" / "package" / "left-over").exists())
        self.assert_sentinels()

    def test_export_failures_fail_the_script_and_keep_other_build_output(self):
        for fault in ("macOS-log", "Linux-log", "Windows-exit", "Linux-exit"):
            with self.subTest(fault=fault):
                result = self.package(fault)
                self.assertEqual(result.returncode, 1, result.stdout)
                self.assertIn("Godot step failed", result.stdout)
                self.assert_sentinels()
                self.assertEqual(list((self.project / "build" / "dist").iterdir()), [])

    def test_without_rcodesign_nothing_is_touched(self):
        stale = self.project / "build" / "dist" / "keep.zip"
        stale.parent.mkdir(parents=True)
        stale.write_text("kept")
        result = self.package(environment={"RCODESIGN": "", "PATH": f"{self.bin}/nowhere:/usr/bin:/bin"})
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("rcodesign not found", result.stdout)
        self.assertEqual(stale.read_text(), "kept")
        self.assert_sentinels()

    def test_explicit_unsigned_macos_is_marked_for_verification_only(self):
        result = self.package(unsigned=True)
        self.assertEqual(result.returncode, 0, result.stdout)
        with zipfile.ZipFile(self.project / "build/dist/Propane-1.2.3-macos-arm64.zip") as archive:
            self.assertIn('UNSIGNED.txt', archive.namelist())
            self.assertNotIn('Propane.app/signed.txt', archive.namelist())


if __name__ == "__main__":
    unittest.main()
