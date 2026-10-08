using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Propane.Dev;

/// <summary>
/// Physics and render soak test on the real game: punctures every tank at once, then detonates them all together,
/// and reports frame times for each stage. Run without --fixed-fps so the timings are real.
/// Run: godot res://scenes/dev/soak.tscn -- --capture-dir=/tmp/soak
/// </summary>
public partial class Soak : Node
{
    private Game game = null!;
    private Camera3D overview = null!;
    private string stage = "settle";
    private float clock;
    private readonly Dictionary<string, List<float>> frames = new();
    private readonly Dictionary<string, List<float>> physics = new();
    private readonly Dictionary<string, int> peakBodies = new();

    public override void _Ready()
    {
        DevArgs.Setup();
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        game = new Game { PauseOnFocusLoss = false };
        AddChild(game);
        var director = new CaptureDirector { QuitAfter = 22f };
        AddChild(director);
        director.At(3.5f, () => stage = "baseline");
        director.At(6f, () =>
        {
            stage = "venting";
            foreach (var tank in game.Current!.LiveTanks)
            {
                tank.ChainHit(tank.GlobalPosition + new Vector3(1, 0.4f, 0.5f), 1f);
            }
        });
        director.At(7f, () => Overview(game.Current!.Plan.Bounds));
        director.ShotAt(8.5f, "venting");
        director.At(9.5f, () =>
        {
            stage = "single";
            game.Current!.LiveTanks.First().Detonate();
        });
        director.At(11f, () =>
        {
            stage = "detonation";
            foreach (var tank in game.Current!.LiveTanks.ToList())
            {
                tank.Detonate();
            }
        });
        director.ShotAt(11.3f, "detonation");
        director.ShotAt(12.5f, "aftermath");
        director.At(14f, () => stage = "debris");
        director.ShotAt(20f, "settled");
        director.At(21.5f, Report);
    }

    public override void _Process(double delta)
    {
        if (!frames.ContainsKey(stage))
        {
            frames[stage] = new List<float>();
            physics[stage] = new List<float>();
        }
        var ms = (float)(delta / Mathf.Max(Engine.TimeScale, 0.0001)) * 1000f;
        frames[stage].Add(ms);
        clock += ms / 1000f;
        if (ms > 40f)
        {
            GD.Print($"[soak] spike {ms:0} ms at {clock:0.00} s ({stage})");
        }
        physics[stage].Add((float)Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000f);
        var bodies = (int)Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects);
        peakBodies[stage] = Mathf.Max(peakBodies.GetValueOrDefault(stage), bodies);
    }

    private void Overview(Rect2 bounds)
    {
        overview = new Camera3D { Fov = 55, Current = true };
        AddChild(overview);
        var center = new Vector3(bounds.GetCenter().X, 0, bounds.GetCenter().Y);
        overview.LookAtFromPosition(center + new Vector3(0, bounds.Size.Length() * 0.55f, bounds.Size.Y * 0.45f), center);
    }

    private void Report()
    {
        foreach (var (name, list) in frames)
        {
            if (list.Count < 5)
            {
                continue;
            }
            var sorted = list.OrderBy(x => x).ToList();
            var p99 = sorted[(int)(sorted.Count * 0.99f)];
            GD.Print($"[soak] {name,-10} frames {list.Count,5}  avg {list.Average(),5:0.0} ms  p99 {p99,5:0.0} ms  max {sorted[^1],5:0.0} ms  " +
                     $"physics avg {physics[name].Average(),4:0.0} ms  max {physics[name].Max(),5:0.0} ms  active bodies {peakBodies[name]}");
        }
    }
}
