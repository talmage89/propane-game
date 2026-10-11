#!/usr/bin/env python3
"""Check that the quality gate rejects representative mistakes, in an isolated scratch project."""
import os
import pathlib
import shutil
import subprocess
import tempfile


ROOT = pathlib.Path(__file__).resolve().parents[2]


def run(directory, *arguments, environment=None):
    result = subprocess.run(arguments, cwd=directory, env=environment, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    return result.returncode, result.stdout


def require(condition, message, output=""):
    if not condition:
        raise SystemExit(f"FAIL: {message}\n{output}")


def check_make_order(scratch):
    # Stub only the external tools: exercise the real Makefile, including parallel make and failures.
    stub = """#!/usr/bin/env bash
set -euo pipefail
printf '%s %s\\n' "${0##*/}" "$*" >> "$COMMAND_LOG"
if [[ "${0##*/}" == dotnet && "$1" == format && "$PROBE_FAIL" == format ]]; then exit 1; fi
if [[ "${0##*/}" == dotnet && "$*" == *-warnaserror* && "$PROBE_FAIL" == build ]]; then exit 1; fi
if [[ "${0##*/}" == godot && "$*" == *--hash-world-plan=* ]]; then printf '[world] hash probe fences 1\\n'; fi
"""
    for name in ("dotnet", "godot", "python3"):
        path = scratch / name
        path.write_text(stub)
        path.chmod(0o755)
    log = scratch / "commands.log"
    environment = dict(os.environ, PATH=f"{scratch}:{os.environ['PATH']}", GODOT=str(scratch / "godot"), COMMAND_LOG=str(log))
    for failure in ("none", "format", "build"):
        log.write_text("")
        environment["PROBE_FAIL"] = failure
        result = subprocess.run(["make", "-j4", "check"], cwd=ROOT, env=environment,
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        commands = log.read_text().splitlines()
        require(bool(commands) and commands[0] == "dotnet format Propane.sln --verify-no-changes", "format must run first", result.stdout)
        if failure == "none":
            require(result.returncode == 0 and len(commands) == 11, "check did not finish in eleven tool calls", result.stdout)
            require("-warnaserror" in commands[1] and "--no-incremental" in commands[1], "lint must rebuild with warnings as errors")
            require(commands[2].startswith("dotnet build ") and commands[4].startswith("godot --headless "), "tests ran before lint")
            require(commands[3] == "python3 -B tools/dev/check_scores_tests.py", "check skipped score-checker regressions")
            scenes = ["unit_tests.tscn", "movement_tests.tscn", "generator_tests.tscn", "platform_tests.tscn"]
            require(all(scene in command for scene, command in zip(scenes, commands[4:8])), "check skipped a regression scene")
            require("--seeds=100" in commands[6], "check changed the generator seed count")
            require("--write-world-plan=" in commands[8] and
                    all("--hash-world-plan=" in command for command in commands[9:]), "check skipped separate-process world builds")
        else:
            require(result.returncode != 0 and len(commands) == (1 if failure == "format" else 2), "lint failure did not stop check", result.stdout)
    print("PASS: parallel make check orders lint before tests and stops on formatter/build failures")


def check_installer(scratch):
    # A download failure must propagate; a valid cache must not attempt a download.
    curl = scratch / "curl"
    curl.write_text("#!/usr/bin/env bash\nexit 23\n")
    curl.chmod(0o755)
    cache = scratch / "editor-cache"
    binary = cache / "Godot_v4.7.2-stable_mono_linux_x86_64" / "Godot_v4.7.2-stable_mono_linux.x86_64"
    binary.parent.mkdir(parents=True)
    binary.write_text("#!/usr/bin/env bash\nexit 0\n")
    binary.chmod(0o755)
    exports = scratch / "github-env"
    environment = dict(os.environ, PATH=f"{scratch}:{os.environ['PATH']}", GITHUB_ENV=str(exports))
    installer = ROOT / ".github/actions/setup-godot/install.sh"
    code, output = run(scratch, "bash", str(installer), "4.7.2", str(cache), environment=environment)
    require(code == 0 and exports.read_text() == f"GODOT={binary}\n", "cached editor setup failed", output)
    environment["GITHUB_ENV"] = str(scratch / "failed-env")
    code, output = run(scratch, "bash", str(installer), "4.7.2", str(scratch / "empty-cache"), environment=environment)
    require(code == 23 and not pathlib.Path(environment["GITHUB_ENV"]).exists(), "failed download was ignored", output)
    print("PASS: Godot setup exports a cached editor and propagates download failures")


def main():
    with tempfile.TemporaryDirectory(prefix="mnet-lint-") as directory:
        scratch = pathlib.Path(directory)
        shutil.copyfile(ROOT / "Propane.csproj", scratch / "Probe.csproj")
        shutil.copyfile(ROOT / ".editorconfig", scratch / ".editorconfig")
        source = scratch / "Probe.cs"
        source.write_text("""using System.IO;
using System.Threading.Tasks;

namespace Probe;

public sealed class Owner
{
    private readonly FileStream stream = new("probe", FileMode.Open);

    public void Write() => stream.WriteByte(1);
}

public static class Mistakes
{
    public static int Nullable(string? text) => text.Length;

    public static async Task Context() { await Task.Delay(1); }

    private static ValueTask<int> Read() => new(Task.FromResult(1));

    public static int Consume() { var value = Read(); return value.Result + value.Result; }
}
""")
        code, output = run(scratch, "dotnet", "build", "Probe.csproj", "-nologo", "-v", "quiet", "-warnaserror")
        require(code != 0, "bad code passed the warning-as-error build", output)
        for diagnostic in ("CS8602", "CA1001", "CA2007", "CA2012"):
            require(f"error {diagnostic}:" in output, f"{diagnostic} was not enforced", output)
        print("PASS: nullability, disposable ownership, async context and ValueTask misuse are errors")

        source.write_text("""namespace Probe;
public static class Formatting
{
 public static int Value() { int value = 1; return value; }
}
""")
        for kind in ("whitespace", "style"):
            code, output = run(scratch, "dotnet", "format", kind, "Probe.csproj", "--no-restore", "--verify-no-changes")
            require(code != 0, f"{kind} drift passed verification", output)
            code, output = run(scratch, "dotnet", "format", kind, "Probe.csproj", "--no-restore")
            require(code == 0, f"{kind} correction failed", output)
        code, output = run(scratch, "dotnet", "format", "Probe.csproj", "--no-restore", "--verify-no-changes")
        require(code == 0, "corrected formatting did not pass", output)
        code, output = run(scratch, "dotnet", "build", "Probe.csproj", "-nologo", "-v", "quiet", "-warnaserror")
        require(code == 0, "corrected code did not pass", output)
        print("PASS: whitespace and style drift fail; corrected code passes formatting and build")
        check_make_order(scratch)
        check_installer(scratch)


if __name__ == "__main__":
    main()
