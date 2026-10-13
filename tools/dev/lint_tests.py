#!/usr/bin/env python3
"""Check that the quality gate rejects representative mistakes, in an isolated scratch project."""
import os
import pathlib
import re
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
"""
    for name in ("dotnet", "godot", "python3"):
        path = scratch / name
        path.write_text(stub)
        path.chmod(0o755)
    log = scratch / "commands.log"
    counter = scratch / "world-count"
    environment = dict(os.environ, PATH=f"{scratch}:{os.environ['PATH']}", GODOT=str(scratch / "godot"),
                       COMMAND_LOG=str(log), WORLD_COUNT=str(counter), TMPDIR=str(scratch))
    checkers = ["check_test_threads.py", "check_world_tests.py", "time_gate_tests.py", "check_scores_tests.py", "check_capacity_tests.py", "compare_vents_tests.py", "compare_tanks_tests.py", "net_bots_tests.py",
                "check_shader_capacity.py", "check_lab.py", "animation_review_tests.py", "animation_metrics.py build/animation-review-gate/metrics.csv --output build/animation-review-gate/results.json"]
    scenes = ["shutdown_tests.tscn", "shutdown_tests.tscn -- --paused --window-close --repeat-quit", "core_tests.tscn", "blast_query_tests.tscn", "gather_range.tscn",
              "gather_range.tscn -- --scenario=grant-races",
              "gather_range.tscn -- --scenario=appearance",
              "game_test.tscn -- --scenario=gather --tune.FixedSeed=23",
              "game_test.tscn -- --scenario=clear --quit=32 --tune.FixedSeed=23",
              "world_services_tests.tscn", "body_correction_tests.tscn", "server_tuning_tests.tscn -- --server --tune.MaxPlayers=6",
              "transport_physics_tests.tscn -- --server",
              "server_seed_tests.tscn -- --server --match-seed=23",
              "session_actors_tests.tscn", "upright_pile_tests.tscn", "melee_range.tscn", "rifle_reach_tests.tscn",
              "capacity_ui_tests.tscn -- --players=2", "capacity_ui_tests.tscn -- --players=8", "capacity_ui_tests.tscn -- --players=16",
              "animation_review.tscn -- --cases=walk_000,jog_000,creep_000,creep_180,creep_090,sprint,aim_idle",
              "player_range.tscn -- --scenario=melee --assert-motion --assert-grip",
              "bot_navigation_course.tscn -- --seeds=5",
              "menu_test.tscn -- --scenario=gamepad", "menu_test.tscn -- --scenario=devices", "menu_test.tscn -- --scenario=input", "menu_test.tscn -- --scenario=mouse",
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
    probes = checkers + scenes + ["--write-world-plans=", "--hash-world-plans=", "world-diff", "world-missing", "world-partial", "world-second"]
    for failure in ["none", "format", "build", *probes]:
        log.write_text("")
        counter.write_text("0\n")
        environment["PROBE_FAIL"] = failure
        result = subprocess.run(["make", "-j4", "check"], cwd=ROOT, env=environment,
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        commands = log.read_text().splitlines()
        require(bool(commands) and commands[0] == "dotnet format Propane.sln --verify-no-changes", "format must run first", result.stdout)
        if failure == "none":
            require(result.returncode == 0 and len(commands) == 3 + len(checkers) + len(scenes) + 3,
                    "check did not run every expected tool call", result.stdout)
            require("-warnaserror" in commands[1] and "--no-incremental" in commands[1], "lint must rebuild with warnings as errors")
            require(commands[2].startswith("dotnet build "), "tests ran before lint")
            for checker in checkers:
                require(commands.count(f"python3 -B tools/dev/{checker}") == 1, f"check skipped/duplicated {checker}")
            for scene in scenes:
                # The default gather probe has no scenario, so distinguish it from grant-races.
                matches = [command for command in commands if scene in command and
                           (scene not in ("gather_range.tscn", "shutdown_tests.tscn") or " -- " not in command)]
                require(len(matches) == 1, f"check skipped/duplicated {scene}", result.stdout)
            require(sum("--write-world-plans=" in c for c in commands) == 1 and
                    sum("--hash-world-plans=" in c for c in commands) == 2,
                    "check skipped separate-process world builds", result.stdout)
        elif failure in ("format", "build"):
            require(result.returncode != 0 and len(commands) == (1 if failure == "format" else 2),
                    "lint failure did not stop check", result.stdout)
        else:
            require(result.returncode != 0, f"parallel check ignored failure: {failure}", result.stdout)
    print("PASS: parallel check runs every suite after lint and propagates every suite/hash failure")
    # Every deep command, with the label its job prints. Each bot match runs its arguments once per bot. The generator
    # shards and Lab sessions must also add up to the whole sweeps; check_generator_shards.py proves the seeds in test-full.
    generator = "generator_tests.tscn -- "
    bots = {"--melee-start --net-seed=2": 4, "--duel-blocked": 2, "--tank-check-ready-stall=Bot2": 2}
    deep = {**dict.fromkeys(bots, "bots"),
            "upright_pile_tests.tscn -- --deep --awake": "piles",
            "menu_test.tscn -- --scenario=lifetime": "menu",
            "--scenario=melee-reloads --assert-motion --assert-grip": "range",
            "--scenario=recoil --stance=air --assert-grip --assert-motion": "range",
            "player_range.tscn -- --scenario=bot-recoil --magazines=10": "range",
            "bot_navigation_course.tscn -- --seeds=100": "navigation",
            "check_lab.py --full --only=soak-void": "lab-1",
            "check_lab.py --full --only=soak-suburb": "lab-2",
            "check_lab.py --full --only=lifetime,rules,aimstall,duel,dials": "lab-3",
            "check_generator_shards.py --full=--seeds=200 --large-seeds=100 --formation-seeds=6": "generator-coverage",
            generator + "--first-seed=1 --seeds=50 --first-large-seed=1 --large-seeds=25 --first-formation-seed=1 --formation-seeds=2": "generator-1",
            generator + "--first-seed=51 --seeds=50 --first-large-seed=26 --large-seeds=25 --first-formation-seed=3 --formation-seeds=2": "generator-2",
            generator + "--first-seed=101 --seeds=50 --first-large-seed=51 --large-seeds=25 --first-formation-seed=5 --formation-seeds=1": "generator-3",
            generator + "--first-seed=151 --seeds=50 --first-large-seed=76 --large-seeds=25 --first-formation-seed=6 --formation-seeds=1": "generator-4"}
    lab = ["lifetime", "rules", "aimstall", "duel", "dials", "soak-void", "soak-suburb"]
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
                require(sum(generator in c for c in commands) == 4 + 1, f"{mode} runs other generator sweeps", output)
                sessions = [name for c in commands if "check_lab.py --full" in c for name in c.split("--only=")[1].split(",")]
                require(sorted(sessions) == sorted(lab), f"{mode} Lab shards differ from every --full session", output)
                first_bot = min(i for i, c in enumerate(commands) if any(probe in c for probe in bots))
                require(all(i < first_bot for i, c in enumerate(commands)
                            if "core_tests.tscn" in c or any(probe in c for probe in deep if probe not in bots)),
                        f"{mode} ran the real-time bot matches beside other jobs", output)
            else:
                label = deep.get(failure, "generator-3")
                require(code != 0 and f"[{label}] FAIL (exit " in output and f"test-full-{label}" in output,
                        f"{mode} ignored or misattributed deep failure: {failure}", output)
    print("PASS: plain and parallel test-full include the gate and every deep shard, and name the job that failed")



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


def check_ci_partitions():
    makefile = (ROOT / "Makefile").read_text()
    workflow = (ROOT / ".github/workflows/ci.yml").read_text()
    local = re.search(r"^test: (.+?) ##", makefile, re.MULTILINE).group(1).split()
    matrix = re.search(r"suite: \[([^\]]+)\]", workflow).group(1).split(",")
    require(sorted(local) == sorted("test-" + name.strip() for name in matrix), "CI partitions differ from local test")
    require("run: make test-${{ matrix.suite }}" in workflow and "run: make lint" in workflow,
            "CI skipped the local targets")
    require("needs: [lint, imports, tests]" in workflow and "if: always()" in workflow and
            'test "$LINT" = success && test "$IMPORTS" = success && test "$TESTS" = success' in workflow,
            "stable CI check must require every job")
    print("PASS: CI runs the local partitions and requires every result")


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
    with tempfile.TemporaryDirectory(prefix="wptest-lint-") as directory:
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
        check_core_wiring()
        check_ci_partitions()


if __name__ == "__main__":
    main()
