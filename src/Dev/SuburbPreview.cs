using System.Linq;
using Godot;
using Propane.Core;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Dev;

/// <summary>
/// Generates and builds a suburb, prints a report, and captures an overhead map plus street-level views.
/// Run: godot --fixed-fps 60 res://scenes/dev/suburb_preview.tscn -- --capture-dir=/tmp/s --seed=7
/// </summary>
public partial class SuburbPreview : Node3D
{
    public override void _Ready()
    {
        DevArgs.Setup();
        var effects = new Node3D { Name = "Effects" };
        AddChild(effects);
        Spawn.SetEffectsRoot(effects);
        var env = new VoidEnvironment();
        AddChild(env);

        var seed = (int)DevArgs.GetFloat("seed", 7);
        var settings = new SuburbSettings();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var plan = new SuburbGenerator(settings, new GameCatalog(), seed).Generate();
        var planMs = timer.ElapsedMilliseconds;
        timer.Restart();
        var suburb = Suburb.Build(plan, settings);
        AddChild(suburb);
        Spawn.SetWorldRoot(suburb.DynamicRoot);
        GD.Print($"[suburb] seed {seed} roads {plan.Roads.Count} bulbs {plan.CulDeSacs.Count} lots {plan.Lots.Count} houses {plan.Lots.Count(l => l.HouseModel >= 0)} " +
                 $"fences {plan.Fences.Count} props {plan.Props.Count} tanks {plan.Tanks.Count} plan {planMs} ms build {timer.ElapsedMilliseconds} ms nodes {CountNodes(suburb)}");
        foreach (var tank in plan.Tanks)
        {
            GD.Print($"[tank] {tank.Position} elev {tank.Elevation} {tank.Reason}");
        }

        var camera = new Camera3D { Current = true, Far = 3000 };
        AddChild(camera);
        env.Follow = camera;
        var director = new CaptureDirector { QuitAfter = 4.5f };
        AddChild(director);
        var view = DevArgs.Get("view") ?? "all";
        director.At(0.1f, () =>
        {
            env.Environment.FogEnabled = false;
            camera.Projection = Camera3D.ProjectionType.Orthogonal;
            camera.Size = Mathf.Max(plan.Bounds.Size.X, plan.Bounds.Size.Y) * 1.08f;
            var c = plan.Bounds.GetCenter();
            camera.LookAtFromPosition(new Vector3(c.X, 400, c.Y + 0.01f), new Vector3(c.X, 0, c.Y), Vector3.Forward);
        });
        director.ShotAt(1.0f, $"{seed}_00_overhead");
        director.At(1.1f, () =>
        {
            env.Environment.FogEnabled = true;
            camera.Projection = Camera3D.ProjectionType.Perspective;
            camera.Fov = 55;
            camera.LookAtFromPosition(new Vector3(-settings.MapSize * 0.75f, 70, settings.MapSize * 0.75f), Vector3.Zero);
        });
        director.ShotAt(1.6f, $"{seed}_01_aerial");
        var spawn = Suburb.ToWorld(plan.Spawn, 1.6f);
        var forward = new Vector3(Mathf.Sin(plan.SpawnYaw), 0, Mathf.Cos(plan.SpawnYaw));
        director.At(1.7f, () => camera.LookAtFromPosition(spawn - forward * 3.5f + Vector3.Up * 0.3f, spawn + forward * 20f));
        director.ShotAt(2.2f, $"{seed}_02_spawn");
        // A look into a backyard from the first lot.
        var lot = plan.Lots.FirstOrDefault(l => l.HouseModel >= 0);
        if (lot != null)
        {
            var yard = Suburb.ToWorld(lot.LotToMap(0, lot.Bounds.HalfExtents.Y * 2f - 3f), 0);
            var outside = Suburb.ToWorld(lot.LotToMap(lot.Bounds.HalfExtents.X + 6f, lot.Bounds.HalfExtents.Y * 1.3f), 2.4f);
            director.At(2.3f, () => camera.LookAtFromPosition(outside, yard));
            director.ShotAt(2.8f, $"{seed}_03_backyard");
            var front = Suburb.ToWorld(lot.FrontCenter - lot.Bounds.AxisY * 7f + lot.Bounds.AxisX * 6f, 1.7f);
            director.At(2.9f, () => camera.LookAtFromPosition(front, Suburb.ToWorld(lot.House.Center, 2f)));
            director.ShotAt(3.4f, $"{seed}_04_front");
        }
        if (plan.Tanks.Count > 0)
        {
            var t = Suburb.ToWorld(plan.Tanks[0].Position, plan.Tanks[0].Elevation);
            director.At(3.5f, () => camera.LookAtFromPosition(t + new Vector3(2.5f, 1.8f, 2.5f), t));
            director.ShotAt(4.0f, $"{seed}_05_tank");
        }
    }

    private static int CountNodes(Node node)
    {
        var count = 1;
        foreach (var child in node.GetChildren())
        {
            count += CountNodes(child);
        }
        return count;
    }
}
