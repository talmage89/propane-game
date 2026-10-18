#!/usr/bin/env python3
"""Build a pre-optimization project with the current measurement harness, inside build/."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", required=True, help="common dev revision used by both builds")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    if not output.is_relative_to(project / "build") or output == project / "build":
        parser.error("baseline output must be a new directory inside this checkout's build/")
    output.mkdir(parents=True, exist_ok=False)
    with tempfile.TemporaryFile(dir=project / "build") as archive:
        subprocess.run(["git", "archive", "--format=zip", args.ref], cwd=project, stdout=archive, check=True)
        archive.seek(0)
        with zipfile.ZipFile(archive) as files:
            files.extractall(output)
    subprocess.run(["git", "apply", "--directory=" + str(output.relative_to(project)),
                    "docs/evidence/perf-vents/baseline-instrumentation.patch"], cwd=project, check=True)
    # Diagnostics only: the archived project retains its original effects, tuning and warm-up.
    for path in ("src/Dev/LabTest.cs", "src/Dev/CaptureDirector.cs", "src/Dev/FrameMetrics.cs",
                 "src/Dev/FrameMetrics.cs.uid", "src/Fx/EffectProfile.cs", "src/Fx/EffectProfile.cs.uid"):
        source = path.replace("src/Dev/CaptureDirector", "src/ReleaseSmoke/CaptureDirector").replace(
            "src/Dev/FrameMetrics", "src/ReleaseSmoke/FrameMetrics")
        text = (project / source).read_text().replace("using Propane.ReleaseSmoke;\n", "").replace(
            "namespace Propane.ReleaseSmoke;", "namespace Propane.Dev;").replace("LaunchArgs.Current.", "DevArgs.")
        (output / path).write_text(text)
    scratch = output / "build"
    scratch.mkdir(exist_ok=True)
    subprocess.run(["make", "setup", "GODOT=godot-mono"], cwd=output,
                   env={**os.environ, "TMPDIR": str(scratch)}, check=True)
    print(output)


if __name__ == "__main__":
    main()
