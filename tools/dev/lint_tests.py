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
            checkers = ["check_world_tests.py", "check_scores_tests.py", "compare_vents_tests.py", "compare_tank_paths_tests.py"]
            scenes = ["shutdown_tests.tscn", "shutdown_tests.tscn -- --paused --window-close --repeat-quit",
                      "unit_tests.tscn", "blast_query_tests.tscn", "gather_range.tscn",
                      "gather_range.tscn -- --scenario=grant-races", "game_test.tscn -- --scenario=gather",
                      "movement_tests.tscn", "bot_navigation_course.tscn -- --seeds=5", "menu_test.tscn -- --scenario=gamepad",
                      "menu_test.tscn -- --scenario=devices", "player_range.tscn -- --scenario=grip --grip-sweep --assert-grip",
                      "player_range.tscn -- --scenario=ragdoll --assert-grip", "player_range.tscn -- --scenario=feet --assert-feet",
                      "player_range.tscn -- --scenario=feet --feet-corrections", "player_range.tscn -- --scenario=feet-events",
                      "player_range.tscn -- --scenario=movement", "player_range.tscn -- --scenario=motion",
                      "generator_tests.tscn -- --seeds=100", "platform_tests.tscn"]
            first_scene = 3 + len(checkers)
            first_world = first_scene + len(scenes)
            require(result.returncode == 0 and len(commands) == first_world + 12, "check did not run every expected tool call", result.stdout)
            require("-warnaserror" in commands[1] and "--no-incremental" in commands[1], "lint must rebuild with warnings as errors")
            require(commands[2].startswith("dotnet build ") and commands[first_scene].startswith("godot --headless "), "tests ran before lint")
            require(commands[3:first_scene] == [f"python3 -B tools/dev/{checker}" for checker in checkers], "check skipped checker regressions")
            require(all(scene in command for scene, command in zip(scenes, commands[first_scene:first_world])), "check skipped a regression scene")
            require(all("--write-world-plan=" in commands[index] and
                        all("--hash-world-plan=" in command for command in commands[index + 1:index + 3])
                        for index in range(first_world, len(commands), 3)), "check skipped separate-process world builds")
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
