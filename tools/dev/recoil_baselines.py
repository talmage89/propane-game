#!/usr/bin/env python3
"""Measure historical recoil in isolated archives under build/, without switching a checkout.

Run after make build. Baseline imports can take a minute. Nothing enters the fast gate.
"""
import argparse
import csv
import io
import os
from pathlib import Path
import subprocess
import tarfile

REPO = Path(__file__).resolve().parents[2]
PROBE = r'''using System;
using System.IO;
using Godot;
using Propane.Core;
using Propane.Player;
namespace Propane.Dev;
public partial class BaselineRecoilProbe : Node
{
    public PlayerCharacter Player { get; init; } = null!;
    private StreamWriter csv = null!;
    private Rifle rifle = null!;
    private float time;
    private int shots;
    private float Yaw => Mathf.RadToDeg(Mathf.Atan2(Player.CameraRig.Camera.GlobalBasis.Z.X, Player.CameraRig.Camera.GlobalBasis.Z.Z));
    public override void _Ready()
    {
        rifle = Player.GetNode<Rifle>("Rifle");
        csv = new StreamWriter(DevArgs.Get("metrics")!);
        csv.WriteLine("time,pitch,yaw,shots,angle");
        Player.Fired += Shot;
    }
    private void Shot(ShotReport shot)
    {
        shots++;
        var angle = Mathf.RadToDeg((-rifle.GlobalBasis.Z).AngleTo(shot.End - shot.Muzzle));
        csv.WriteLine(FormattableString.Invariant($"{time:F6},{Player.CameraRig.Pitch:F6},{Yaw:F6},{shots},{angle:F6}"));
    }
    public override void _Process(double delta)
    {
        time += (float)delta;
        csv.WriteLine(FormattableString.Invariant($"{time:F6},{Player.CameraRig.Pitch:F6},{Yaw:F6},{shots},"));
    }
    public void Finish()
    {
        GD.Print($"[baseline-recoil] shots={shots}");
        if (shots != 30) { GD.PushError("incomplete magazine"); GetTree().Quit(1); }
    }
    public override void _ExitTree() { Player.Fired -= Shot; csv.Dispose(); }
}
'''


def run(command, cwd, log):
    with log.open("w") as output:
        subprocess.run(command, cwd=cwd, stdout=output, stderr=subprocess.STDOUT, check=True,
                       env={**os.environ, "TMPDIR": str(log.parent)})


def archive(ref, destination):
    destination.mkdir(parents=True, exist_ok=False)
    data = subprocess.check_output(["git", "archive", ref], cwd=REPO)
    with tarfile.open(fileobj=io.BytesIO(data)) as source:
        source.extractall(destination, filter="data")


def instrument(destination, release):
    player_range = destination / "src/Dev/PlayerRange.cs"
    text = player_range.read_text()
    recoil = '''
            case "recoil":
                SEED
                player.Ammo = new AmmoState { TUNING Magazine = 30, Reserve = 0 };
                script.Hold(.1f, 9.9f, new PlayerIntent { Aim = DevArgs.Get("stance") != "hip", Move = DevArgs.Get("stance") == "creep" ? Vector2.Down : Vector2.Zero });
                script.Hold(1, 4.6f, new PlayerIntent { FireHeld = true });
                var recoil = new BaselineRecoilProbe { Player = player };
                AddChild(recoil);
                director.At(9.8f, recoil.Finish);
                director.QuitAfter = 10;
                break;
'''.replace("SEED", "GD.Seed(19019);" if release else "world.Random.Seed = 19019;")
    recoil = recoil.replace("TUNING", "" if release else "Tuning = world.Tuning,")
    if not release:
        current = (REPO / "src/Dev/PlayerRange.cs").read_text()
        first = current.index('            case "bot-recoil":')
        last = current.index('            case "recoil":', first)
        recoil += current[first:last]
        modern = (destination / "src/ReleaseSmoke/Bot.cs").exists()
        probe = (REPO / "src/Dev/BotRecoilProbe.cs").read_text()
        (destination / "src/Dev/BotRecoilProbe.cs").write_text(
            probe if modern else probe.replace("using Propane.ReleaseSmoke;\n", ""))
        bot = destination / ("src/ReleaseSmoke/Bot.cs" if modern else "src/Dev/Bot.cs")
        bot.write_text(bot.read_text().replace("private void Face(", "internal void Face(").replace(
            "var view = point - aim.EyeOrigin;", "tuning = player.World.Tuning;\n        var view = point - aim.EyeOrigin;"))
        simple = destination / "src/Dev/SimpleBot.cs"
        simple.write_text(simple.read_text().replace("    private Node3D? Choose(", '''
    internal void Face(PlayerCharacter player, Vector3 point, float dt)
    {
        var view = point - player.Aim.EyeOrigin;
        player.Aim.SetOrientation(Mathf.RadToDeg(Mathf.Atan2(-view.X, -view.Z)), Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(view.Normalized().Y, -1, 1))));
    }
    private Node3D? Choose('''))
    marker = "switch (scenario)\n        {"
    assert marker in text, "baseline PlayerRange layout changed"
    player_range.write_text(text.replace(marker, marker + recoil, 1))
    (destination / "src/Dev/BaselineRecoilProbe.cs").write_text(PROBE)
    (destination / "override.cfg").write_text('[application]\nconfig/use_custom_user_dir=true\n'
                                              f'config/custom_user_dir_name="Propane-dev/{REPO.name}-{destination.name}"\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dev-ref", default="8eafd3c")
    parser.add_argument("--output", type=Path, default=REPO / "build/rcl-baselines")
    parser.add_argument("--godot", default="godot-mono")
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(REPO / "build"):
        parser.error("scratch output must be under this checkout's build/")
    output.mkdir(parents=True, exist_ok=True)
    for label, ref in [("v020", "v0.2.0"), ("dev", args.dev_ref)]:
        destination = output / label
        print(f"Importing {ref} in {destination}", flush=True)
        archive(ref, destination)
        instrument(destination, label == "v020")
        run([args.godot, "--headless", "--path", ".", "--import"], destination, output / f"{label}-import.log")
        run(["make", "build"], destination, output / f"{label}-build.log")
        for stance in ["aim", "hip", "creep"]:
            metrics = output / f"{label}-{stance}.csv"
            run([args.godot, "--headless", "--fixed-fps", "60", "--path", ".",
                 "res://scenes/dev/player_range.tscn", "--", "--scenario=recoil", f"--stance={stance}",
                 f"--metrics={metrics}"], destination, output / f"{label}-{stance}.log")
            rows = list(csv.DictReader(metrics.open()))
            print(label, stance, "peak", max(float(row["pitch"]) for row in rows), "last", rows[-1]["pitch"], flush=True)
        if label == "dev":
            for brain, skill in [("smart", 0), ("smart", 1), ("simple", 1)]:
                for distance in [8, 20]:
                    run([args.godot, "--headless", "--fixed-fps", "60", "--path", ".",
                         "res://scenes/dev/player_range.tscn", "--", "--scenario=bot-recoil", f"--brain={brain}",
                         f"--skill={skill}", f"--distance={distance}"], destination, output / f"dev-bot-{brain}-{skill}-{distance}.log")


if __name__ == "__main__":
    main()
