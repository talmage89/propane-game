#!/usr/bin/env python3
"""Check that the quality gate rejects representative mistakes, in an isolated scratch project."""
import os
import pathlib
import re
import shlex
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
    # Stub external tools only: exercise the real Makefile and world checker under parallel make.
    stub = """#!/usr/bin/env bash
set -euo pipefail
printf '%s %s\\n' "${0##*/}" "$*" >> "$COMMAND_LOG"
if [[ "${0##*/}" == dotnet && "$1" == format && "$PROBE_FAIL" == format ]]; then exit 1; fi
if [[ "${0##*/}" == dotnet && "$*" == *-warnaserror* && "$PROBE_FAIL" == build ]]; then exit 1; fi
if [[ "$PROBE_FAIL" != none && "$PROBE_FAIL" != format && "$PROBE_FAIL" != build && "$*" == *"$PROBE_FAIL"* ]]; then exit 1; fi
if [[ "${0##*/}" == godot && " $* " == *" --server "* ]]; then
  for argument in "$@"; do
    if [[ "$argument" == --port=* ]]; then printf 'listening on UDP %s (test)\\n' "${argument#*=}"; fi
  done
fi
if [[ "${0##*/}" == godot && "$*" == *--hash-world-plans=* ]]; then
  if [[ "$PROBE_FAIL" == world-missing ]]; then exit 0; fi
  count=0
  if [[ -f "$WORLD_COUNT" ]]; then read -r count < "$WORLD_COUNT"; fi
  count=$((count + 1)); printf '%s\\n' "$count" > "$WORLD_COUNT"
  if [[ "$PROBE_FAIL" == world-second && "$count" == 2 ]]; then exit 1; fi
  hash=probe
  if [[ "$PROBE_FAIL" == world-diff && "$count" == 2 ]]; then hash=changed; fi
  for formation in Rows Square Triangular Cluster; do
    if [[ "$PROBE_FAIL" == world-partial && "$formation" == Cluster ]]; then continue; fi
    printf '[world] hash %s fences 1 formation %s\\n' "$hash" "$formation"
  done
fi
if [[ "${0##*/}" == godot && "$*" == *--hash-segments=* ]]; then
  for segment in $(seq 1 60); do printf '[segments] hash run 1 segment %s probe\\n' "$segment"; done
fi
"""
    for name in ("dotnet", "godot", "python3"):
        path = scratch / name
        path.write_text(stub)
        path.chmod(0o755)
    log = scratch / "commands.log"
    counter = scratch / "world-count"
    environment = dict(os.environ, PATH=f"{scratch}:{os.environ['PATH']}", GODOT=str(scratch / "godot"),
                       COMMAND_LOG=str(log), WORLD_COUNT=str(counter), TMPDIR=str(scratch))
    checkers = ["check_test_threads.py", "bench_tests.py", "check_world_tests.py", "time_gate_tests.py", "check_scores_tests.py", "check_capacity_tests.py", "compare_vents_tests.py", "compare_tanks_tests.py", "vent_residual_tests.py", "net_bots_tests.py", "release_tests.py",
                "check_shader_capacity.py", "check_lab.py", "animation_review_tests.py", "animation_metrics.py build/animation-review-gate/metrics.csv --output build/animation-review-gate/results.json",
                # The gate self-tests, which this very script is; stubbed here, since python3 is.
                "lint_tests.py"]
    scenes = ["application_state_tests.tscn", "shutdown_tests.tscn", "shutdown_tests.tscn -- --paused --window-close --repeat-quit", "core_tests.tscn", "mode_test.tscn", "blast_query_tests.tscn", "gather_range.tscn",
              "gather_range.tscn -- --scenario=grant-races",
              "gather_range.tscn -- --scenario=appearance",
              "game_test.tscn -- --scenario=gather --tune.FixedSeed=23",
              "game_test.tscn -- --scenario=clear --quit=32 --tune.FixedSeed=23",
              "world_services_tests.tscn", "actor_detail_tests.tscn", "asset_tests.tscn", "ragdoll_pose_tests.tscn", "body_correction_tests.tscn", "server_tuning_tests.tscn -- --server --tune.MaxPlayers=6",
              "transport_physics_tests.tscn -- --server",
              "server_seed_tests.tscn -- --server --match-seed=23",
              "session_actors_tests.tscn", "upright_pile_tests.tscn", "melee_range.tscn", "rifle_reach_tests.tscn",
              "menu_composition_tests.tscn", "capacity_ui_tests.tscn -- --players=2", "capacity_ui_tests.tscn -- --players=8", "capacity_ui_tests.tscn -- --players=16",
              "animation_review.tscn -- --cases=walk_000,jog_000,creep_000,creep_180,creep_090,sprint,aim_idle",
              "player_range.tscn -- --scenario=melee --assert-motion --assert-grip",
              "bot_navigation_course.tscn -- --seeds=5",
              "menu_test.tscn -- --scenario=gamepad", "menu_test.tscn -- --scenario=devices", "menu_test.tscn -- --scenario=input", "menu_test.tscn -- --scenario=mouse",
              "menu_test.tscn -- --scenario=settings", "menu_test.tscn -- --scenario=settings-read",
              "menu_test.tscn -- --scenario=bots", "menu_test.tscn -- --scenario=king", "menu_test.tscn -- --scenario=lobby",
              "player_range.tscn -- --scenario=grip --grip-sweep --assert-grip --assert-motion",
              "player_range.tscn -- --scenario=ragdoll --assert-grip --assert-motion",
              "player_range.tscn -- --scenario=feet --assert-feet --assert-motion",
              "player_range.tscn -- --scenario=feet --feet-corrections --assert-feet --assert-motion",
              "player_range.tscn -- --scenario=feet-events --assert-feet --assert-grip --assert-motion",
              "player_range.tscn -- --scenario=movement --assert-motion --assert-grip",
              "player_range.tscn -- --scenario=motion --assert-motion --assert-grip",
              "player_range.tscn -- --scenario=recoil --stance=aim --assert-grip --assert-motion",
              "player_range.tscn -- --scenario=recoil --stance=hip --assert-grip --assert-motion",
              "player_range.tscn -- --scenario=recoil --stance=creep --assert-grip --assert-motion",
              "player_range.tscn -- --scenario=recoil --stance=hip --burst-start=0 --assert-grip --assert-motion",
              "player_range.tscn -- --scenario=bot-recoil --magazines=2",
              "generator_tests.tscn -- --seeds=20 --large-seeds=3 --formation-seeds=2"]
    asset_checkers = ["tools/assets/check.py", "tools/assets/tests.py", "tools/assets/native.py"]
    probes = asset_checkers + checkers + scenes + ["--write-world-plans=", "--hash-world-plans=", "--hash-segments=", "world-diff", "world-missing", "world-partial",
                                  "world-second"]
    architecture = ["Architecture.csproj", "Architecture.dll", "source_check.py"]
    for failure in ["none", "check_links.py", "format", "build", "ExportRelease", *architecture, *probes]:
        log.write_text("")
        counter.write_text("0\n")
        environment["PROBE_FAIL"] = failure
        result = subprocess.run(["make", "-j4", "check"], cwd=ROOT, env=environment,
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        commands = log.read_text().splitlines()
        first = ["python3 -B tools/dev/check_links.py", "dotnet format Propane.sln --verify-no-changes"]
        require(bool(commands) and commands[:2] == first[:len(commands[:2])],
                "the Markdown link check and format must run first", result.stdout)
        if failure == "none":
            require(result.returncode == 0 and len(commands) == 8 + len(checkers) + len(scenes) + len(asset_checkers) + 5,
                    "check did not run every expected tool call", result.stdout)
            require("-warnaserror" in commands[2] and "--no-incremental" in commands[2], "lint must rebuild with warnings as errors")
            require(commands[3].startswith("dotnet build Propane.csproj -c ExportRelease ") and
                    "-warnaserror" in commands[3] and "--no-incremental" in commands[3],
                    "lint must compile the shipping configuration with warnings as errors")
            require("Architecture.csproj" in commands[4] and "Architecture.dll" in commands[5] and
                    commands[6] == "python3 -B tools/architecture/source_check.py", "lint lost the architecture checks")
            require(commands[7].startswith("dotnet build "), "tests ran before lint")
            require(commands[-1] == "python3 -B tools/dev/lint_tests.py", "the gate self-tests must run last, after every suite")
            for checker in asset_checkers:
                require(commands.count(f"python3 -B {checker}") == 1, f"check skipped/duplicated {checker}")
            for checker in checkers:
                require(commands.count(f"python3 -B tools/dev/{checker}") == 1, f"check skipped/duplicated {checker}")
            for scene in scenes:
                # Match an argument boundary: settings must not also match settings-read.
                # The default gather probe has no scenario, so distinguish it from grant-races.
                matches = [command for command in commands if re.search(re.escape(scene) + r"(?:\s|$)", command) and
                           (scene not in ("gather_range.tscn", "shutdown_tests.tscn") or " -- " not in command)]
                require(len(matches) == 1, f"check skipped/duplicated {scene}", result.stdout)
            require(sum("--write-world-plans=" in c for c in commands) == 1 and
                    sum("--hash-world-plans=" in c for c in commands) == 2 and sum("--hash-segments=" in c for c in commands) == 2,
                    "check skipped separate-process world builds", result.stdout)
        elif failure in ("check_links.py", "format", "build", "ExportRelease", *architecture):
            require(result.returncode != 0 and len(commands) == {"check_links.py": 1, "format": 2, "build": 3, "ExportRelease": 4,
                    "Architecture.csproj": 5, "Architecture.dll": 6, "source_check.py": 7}[failure],
                    "lint failure did not stop check", result.stdout)
        else:
            require(result.returncode != 0, f"parallel check ignored failure: {failure}", result.stdout)
    print("PASS: parallel check runs every suite after lint and propagates every suite/hash failure")
    # Every deep command, with the label its job prints. Each bot match runs its arguments once per bot. The generator
    # shards and Lab sessions must also add up to the whole sweeps; check_generator_shards.py proves the seeds in test-full.
    generator = "generator_tests.tscn -- "
    bots = {"--melee-start --net-seed=2": 4, "--duel-blocked": 2, "--tank-check-ready-stall=Bot2": 2}
    deep = {**dict.fromkeys(bots, "bots"),
            "full_engine.py --prepare": "engine",
            "upright_pile_tests.tscn -- --deep --awake": "piles",
            "menu_test.tscn -- --scenario=lifetime": "menu",
            "--scenario=melee-reloads --assert-motion --assert-grip": "range",
            "--scenario=recoil --stance=air --assert-grip --assert-motion": "range",
            "player_range.tscn -- --scenario=bot-recoil --magazines=10": "range",
            "bot_navigation_course.tscn -- --seeds=100": "navigation",
            "check_lab.py --full --only=king-full": "lab-king",
            "check_lab.py --full --only=king-suburb": "lab-king-suburb",
            "check_lab.py --full --only=rules,aimstall,duel,ringout,king,shortfuse,shortfuse-combo,dissolve": "lab-fast",
            "check_lab.py --full --only=soak-void": "lab-soak-void",
            "check_lab.py --full --only=soak-suburb": "lab-soak-suburb",
            "check_lab.py --full --only=dissolve-pace": "lab-pace",
            "check_lab_shards.py": "lab-coverage",
            "check_generator_shards.py --full=--seeds=200 --large-seeds=100 --formation-seeds=6 --segment-seeds=100": "generator-coverage",
            generator + "--first-seed=1 --seeds=50 --first-large-seed=1 --large-seeds=25 --first-formation-seed=1 --formation-seeds=2 --first-segment-seed=1 --segment-seeds=25": "generator-1",
            generator + "--first-seed=51 --seeds=50 --first-large-seed=26 --large-seeds=25 --first-formation-seed=3 --formation-seeds=2 --first-segment-seed=26 --segment-seeds=25": "generator-2",
            generator + "--first-seed=101 --seeds=50 --first-large-seed=51 --large-seeds=25 --first-formation-seed=5 --formation-seeds=1 --first-segment-seed=51 --segment-seeds=25": "generator-3",
            generator + "--first-seed=151 --seeds=50 --first-large-seed=76 --large-seeds=25 --first-formation-seed=6 --formation-seeds=1 --first-segment-seed=76 --segment-seeds=25": "generator-4"}
    for name, session, seeds in (("ringout", "ringout-full", range(11, 14)),
                                 ("shortfuse", "shortfuse-full", range(11, 13)),
                                 ("dissolve", "dissolve-full", range(11, 13))):
        for seed in seeds:
            deep[f"check_lab.py --full --only={session} --first-seed={seed} --seeds=1"] = f"lab-{name}-{seed}"
    for shard in range(1, 5):
        deep[f"check_lab.py --full --only=lifetime --lifetime-shard={shard} --lifetime-shards=4"] = f"lab-lifetime-{shard}"
    for shard in range(1, 9):
        deep[f"check_lab.py --full --only=dials --dial-shard={shard} --dial-shards=8"] = f"lab-dials-{shard}"
    for jobs, failures in ((["-j4"], ["none", *deep]), ([], ["none", generator + "--first-seed=101"])):
        for failure in failures:
            log.write_text("")
            counter.write_text("0\n")
            environment["PROBE_FAIL"] = failure
            code, output = run(ROOT, "make", *jobs, "test-full", environment=environment)
            commands = log.read_text().splitlines()
            mode = " ".join(["make", *jobs, "test-full"])
            if failure == "none":
                require(code == 0 and any("core_tests.tscn" in c for c in commands), f"{mode} lost the gate", output)
                for probe in deep:
                    require(sum(probe in c for c in commands) == bots.get(probe, 1),
                            f"{mode} skipped or duplicated {probe}", output)
                require(sum(generator in c and "--hash-segments=" not in c for c in commands) == 4 + 1, f"{mode} runs other generator sweeps", output)
                first_bot = min(i for i, c in enumerate(commands) if any(probe in c for probe in bots))
                require(all(i < first_bot for i, c in enumerate(commands)
                            if "core_tests.tscn" in c or any(probe in c for probe in deep if probe not in bots)),
                        f"{mode} ran the real-time bot matches beside other jobs", output)
            else:
                label = deep.get(failure, "generator-3")
                require(code != 0 and f"[{label}] FAIL (exit " in output and f"test-full-{label}" in output,
                        f"{mode} ignored or misattributed deep failure: {failure}", output)
    print("PASS: plain and parallel test-full include the gate and every deep shard, and name the job that failed")


def check_scale_knobs(scratch):
    knobs = "CONFIG BOTS TANKS RENDERED SECONDS REPEATS WARMUP BENCH_ARGS".split()
    environment = {name: value for name, value in os.environ.items() if name not in knobs}
    # Append an unrelated target to the real Makefile: scale defaults must not reach it.
    probe = scratch / "scale-knobs.mk"
    probe.write_text(".PHONY: probe-scale-knobs\nprobe-scale-knobs:\n\t@echo " +
                     " ".join(f"{name}=$(origin {name})" for name in knobs) + "\n")
    arguments = ["make", "--no-print-directory", "-n", "-f", "Makefile", "-f", str(probe)]
    origins = "echo " + " ".join(f"{name}=undefined" for name in knobs)
    for target in ("probe-scale-knobs", "bench", "play"):
        code, output = run(ROOT, *arguments, target, environment=environment)
        require(code == 0, f"{target} dry run failed", output)
        if target == "probe-scale-knobs":
            require(output.strip() == origins, "scale defaults leaked into another target", output)
        elif target == "bench":
            require(shlex.split(output.splitlines()[-1]) == ["python3", "-B", "tools/dev/bench.py",
                    "--bots=0,2,4,8,16", "--tanks=1,2,4,8", "--config=Debug", "--rendered=0,1",
                    "--seconds=10", "--repeats=3", "--warmup=2"], "bench defaults changed", output)
        elif target == "play":
            require(shlex.split(output.splitlines()[-1]) == ["python3", "-B", "tools/dev/source_run.py", "--config", "Release"],
                    "play must default to Release", output)

    overrides = dict(BOTS="2", TANKS="4", RENDERED="0", SECONDS=".5", REPEATS="1", WARMUP=".2",
                     BENCH_ARGS="--output=build/probe.json")
    for source in ("command line", "environment"):
        for target in ("bench", "play"):
            configured = dict(overrides, CONFIG="Debug,Release" if target == "bench" else "Debug")
            assignments = [f"{name}={value}" for name, value in configured.items()] if source == "command line" else []
            selected = dict(environment, **configured) if source == "environment" else environment
            code, output = run(ROOT, *arguments, target, *assignments, environment=selected)
            require(code == 0, f"{target} dry run with {source} overrides failed", output)
            command = shlex.split(output.splitlines()[-1])
            expected = (["python3", "-B", "tools/dev/bench.py", "--bots=2", "--tanks=4", "--config=Debug,Release", "--rendered=0",
                         "--seconds=.5", "--repeats=1", "--warmup=.2", "--output=build/probe.json"] if target == "bench" else
                        ["python3", "-B", "tools/dev/source_run.py", "--config", "Debug"])
            require(command == expected, f"{target} lost {source} overrides", output)
    print("PASS: bench/play defaults stay local and accept command-line/environment overrides")


def check_core_wiring():
    source = (ROOT / "src/Dev/CoreTests.cs").read_text()
    scenes = re.findall(r'"([^"\n]+)"', re.search(r"Scenes = \{([^}]+)\}", source).group(1))
    require(len(scenes) == len(set(scenes)), "core runner duplicates a suite")
    required = {"platform_tests": "src/Dev/PlatformTests.cs", "movement_tests": "src/Dev/MovementTests.cs",
                "unit_tests": "src/Dev/UnitTests/TestRunner.cs"}
    for scene, script in required.items():
        require(scene in scenes, f"core runner skipped {scene}")
        suite = (ROOT / script).read_text()
        require("CoreTests.Complete(this," in suite and ".Quit(" not in suite,
                f"{scene} bypasses runner failure propagation")
    require(".Quit(" not in source.replace("ShutdownGuard.Quit(", ""),
            "core runner bypasses shutdown guard")
    require("ShutdownGuard.Quit(node.GetTree(), code)" in source, "standalone completion bypasses shutdown guard")
    # The animation harness is also transplanted onto v0.2.0, which predates ShutdownGuard.
    # Accept its wrapper only with this exact guarded current-build / legacy-build split.
    review_shutdown = re.compile(r"#if PROPANE_REVIEW_RELEASE\s+tree\.Quit\(code\);\s+#else\s+ShutdownGuard\.Quit\(tree, code\);\s+#endif")
    review = (ROOT / "src/Dev/AnimationReview.cs").read_text()
    require(review_shutdown.search(review), "animation review lost its guarded current-build shutdown")
    for path in (ROOT / "src/Dev").rglob("*.cs"):
        if path.name != "ShutdownWitness.cs":
            text = path.read_text()
            if path.name == "AnimationReview.cs":
                text = review_shutdown.sub("ShutdownGuard.Quit(tree, code);", text)
            require(".Quit(" not in text.replace("ShutdownGuard.Quit(", "").replace("AnimationReview.Quit(", ""),
                    f"{path.name} bypasses orderly shutdown")
    runner = (ROOT / "src/Dev/UnitTests/TestRunner.cs").read_text()
    require("Assembly.GetTypes()" in runner and "typeof(UnitSuiteAttribute)" in runner and
            ".Register(this)" not in runner, "unit runner must discover attributed suites")
    for path in (ROOT / "src/Dev/UnitTests").glob("*.cs"):
        text = path.read_text()
        if "public static void Register(TestRunner runner)" in text:
            require(re.search(r"\[UnitSuite(?:\(-?\d+\))?\]\s+(?:public|internal) static class", text),
                    f"{path.name} is missing its discovery marker")
    print("PASS: core failure/shutdown routing and unit discovery retain every suite")


def dev_boundary_offenders(sources):
    """Runtime code must not reach the excluded Dev namespace, including through a qualified alias."""
    reach = re.compile(r"\bPropane\.Dev\b|\bDev\.[A-Z]|\bDevArgs\b")
    return sorted(path for path, text in sources.items() if not path.startswith("src/Dev/") and
                  reach.search(re.sub(r"//[^\n]*|/\*.*?\*/", "", text, flags=re.DOTALL)))


def check_runtime_dev_boundary():
    sources = {path.relative_to(ROOT).as_posix(): path.read_text() for path in (ROOT / "src").rglob("*.cs")}
    require(not dev_boundary_offenders(sources), "runtime code depends on excluded src/Dev: " +
            ", ".join(dev_boundary_offenders(sources)))
    require("new ReleaseTest()" in sources["src/Main.cs"] and "using Propane.ReleaseSmoke;" in sources["src/Main.cs"],
            "Main lost the documented release smoke entry")
    for path in ("src/Main.cs", "src/Net/Probe.cs", "src/ReleaseSmoke/Probe.cs"):
        for text in ('Dev.DevArgs.Get("a");', 'using Propane.Dev;', 'using Probe = global::Propane.Dev.DevArgs;',
                     'DevArgs.Get("a");'):
            require(dev_boundary_offenders({path: text}) == [path], "the Dev boundary check accepted " + text)
    require(dev_boundary_offenders({"src/Main.cs": "using Propane.ReleaseSmoke;\nAddChild(new ReleaseTest());",
                                    "src/Dev/Probe.cs": "using Propane.Dev;"}) == [],
            "the Dev boundary check rejected permitted references")
    print("PASS: runtime and release smoke have no dependency on src/Dev")


def check_ci_partitions():
    makefile = (ROOT / "Makefile").read_text()
    workflow = (ROOT / ".github/workflows/ci.yml").read_text()
    local = re.search(r"^test: (.+?) ##", makefile, re.MULTILINE).group(1).split()
    matrix = re.search(r"suite: \[([^\]]+)\]", workflow).group(1).split(",")
    require(sorted(local) == sorted("test-" + name.strip() for name in matrix), "CI partitions differ from local test")
    require("run: make test-${{ matrix.suite }}" in workflow and "run: make lint" in workflow and
            "run: make test-infra" in workflow, "CI skipped the local targets")
    require("needs: [lint, infra, imports, tests]" in workflow and "if: always()" in workflow and
            'test "$LINT" = success && test "$INFRA" = success && test "$IMPORTS" = success && test "$TESTS" = success' in workflow,
            "stable CI check must require every job")
    # What CI runs as its infra job is what make check runs last locally, from this very script.
    code, output = run(ROOT, "make", "-n", "check")
    commands = [line for line in output.splitlines() if line and not line.startswith("make")]
    require(code == 0 and commands[-1] == "python3 -B tools/dev/lint_tests.py", "make check must end with the gate self-tests", output)
    code, output = run(ROOT, "make", "-n", "test-infra")
    # A recursive make (CI runs `make test-infra` itself) prints directory lines around the command.
    commands = [line for line in output.splitlines() if line and not line.startswith("make")]
    require(code == 0 and commands == ["python3 -B tools/dev/lint_tests.py"], "make test-infra must run lint_tests.py alone", output)
    print("PASS: CI runs the local partitions and the gate self-tests, and requires every result")


def check_installer():
    code, output = run(ROOT, "python3", "-B", "tools/downloads/tests.py")
    require(code == 0, "download verification fixtures failed", output)
    print(output, end="")


def main():
    (ROOT / "build").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="wptest-lint-", dir=ROOT / "build") as directory:
        scratch = pathlib.Path(directory)
        shutil.copyfile(ROOT / "Propane.csproj", scratch / "Probe.csproj")
        shutil.copyfile(ROOT / ".editorconfig", scratch / ".editorconfig")
        # The project compiles the files it lists in an explicit order (the Tuning parts); the probe has none of them.
        for part in re.findall(r'<TuningPart Include="([^"]+)"', (ROOT / "Propane.csproj").read_text()):
            (scratch / part).parent.mkdir(parents=True, exist_ok=True)
            (scratch / part).write_text("")
        # Independent console tooling must never compile into the Godot game project.
        (scratch / "tools").mkdir()
        (scratch / "tools/CheckerOnly.cs").write_text("#error tooling leaked into the game project\n")
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
        print("PASS: whitespace and style drift fail; corrected code builds while tools/ stays excluded")

        # Exercise the real project condition with a tiny coupling: only the shipping configuration omits Dev.
        dev_probe = scratch / "src/Dev/BoundaryProbe.cs"
        dev_probe.parent.mkdir(parents=True, exist_ok=True)
        dev_probe.write_text("namespace Probe; public sealed class HarnessOnly {}\n")
        source.write_text("namespace Probe; public static class Runtime { public static object Value() => new HarnessOnly(); }\n")
        for config in ("Debug", "Release", "ExportDebug"):
            code, output = run(scratch, "dotnet", "build", "Probe.csproj", "-c", config, "-nologo", "-v", "quiet", "-warnaserror")
            require(code == 0, config + " lost the Dev harnesses", output)
        code, output = run(scratch, "dotnet", "build", "Probe.csproj", "-c", "Debug", "-p:Optimize=true", "-nologo", "-v", "quiet", "-warnaserror")
        require(code == 0, "optimized Debug lost the Dev harnesses", output)
        code, output = run(scratch, "dotnet", "build", "Probe.csproj", "-c", "ExportRelease", "-nologo", "-v", "quiet", "-warnaserror")
        require(code != 0 and "CS0246" in output and "HarnessOnly" in output,
                "the shipping configuration accepted a runtime reference into Dev", output)
        source.write_text("namespace Probe; public static class Runtime { public static int Value() => 1; }\n")
        code, output = run(scratch, "dotnet", "build", "Probe.csproj", "-c", "ExportRelease", "-nologo", "-v", "quiet", "-warnaserror")
        require(code == 0, "decoupled shipping compilation failed", output)
        print("PASS: Debug/Release/ExportDebug keep Dev; ExportRelease rejects a runtime coupling and builds without it")
        code, output = run(ROOT, "python3", "-B", "tools/dev/full_engine_tests.py")
        require(code == 0, "deep engine isolation fixtures failed", output)
        code, output = run(ROOT, "python3", "-B", "tools/dev/check_lab_shards_tests.py")
        require(code == 0, "Lab coverage negative fixtures failed", output)
        code, output = run(ROOT, "python3", "-B", "tools/dev/check_links_tests.py")
        require(code == 0, "Markdown link check fixtures failed", output)
        code, output = run(ROOT, "python3", "-B", "tools/dev/mode_client_equivalence_tests.py")
        require(code == 0, "client mode transcript negative fixtures failed", output)
        check_make_order(scratch)
        check_scale_knobs(scratch)
        check_installer()
        check_core_wiring()
        check_runtime_dev_boundary()
        check_ci_partitions()
        code, output = run(ROOT, "python3", "-B", "tools/architecture/tests.py")
        require(code == 0, "architecture checker negative fixtures failed", output)
        print(output, end="")
        code, output = run(ROOT, "python3", "-B", "tools/dev/setup_import_tests.py")
        require(code == 0, "setup import negative fixtures failed", output)
        code, output = run(ROOT, "python3", "-B", "tools/server/deploy_tests.py")
        require(code == 0, "deployment identity negative fixtures failed", output)
        code, output = run(ROOT, "python3", "-B", "tools/release/artifact_tests.py")
        require(code == 0, "artifact script negative fixtures failed", output)


if __name__ == "__main__":
    main()
