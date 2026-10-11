#!/usr/bin/env python3
"""Check that the quality gate rejects representative mistakes, in an isolated scratch project."""
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


if __name__ == "__main__":
    main()
