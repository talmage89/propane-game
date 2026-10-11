#!/usr/bin/env python3
"""Check that the quality gate rejects representative mistakes, in an isolated scratch project."""
import os
import pathlib
import shutil
import subprocess
import tempfile


ROOT = pathlib.Path(__file__).resolve().parents[2]


def run(directory, *arguments):
    result = subprocess.run(arguments, cwd=directory, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
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
"""
    for name in ("dotnet", "godot"):
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
            require(result.returncode == 0 and len(commands) == 4, "check did not finish in four tool calls", result.stdout)
            require("-warnaserror" in commands[1] and "--no-incremental" in commands[1], "lint must rebuild with warnings as errors")
            require(commands[2].startswith("dotnet build ") and commands[3].startswith("godot --headless "), "tests ran before lint")
        else:
            require(result.returncode != 0 and len(commands) == (1 if failure == "format" else 2), "lint failure did not stop check", result.stdout)
    print("PASS: parallel make check orders lint before tests and stops on formatter/build failures")


def main():
    with tempfile.TemporaryDirectory(prefix="wplint-gate-") as directory:
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


if __name__ == "__main__":
    main()
