using System.Linq;
using Godot;
using Propane.Core;
using Propane.World;

namespace Propane.Dev;

/// <summary>Lines up asset models next to the mannequin and a tank at chosen scales, for judging proportions.</summary>
public partial class WorldLab : Node3D
{
    public override void _Ready()
    {
        DevArgs.Setup();
        var env = new VoidEnvironment();
        AddChild(env);
        var set = DevArgs.Get("set") ?? "houses";
        var scale = DevArgs.GetFloat("scale", 7.5f);
        string[] models = set switch
        {
            "houses" => "abcdefghijklmnopqrstu".Select(c => $"res://assets/kenney/suburban/building-type-{c}.glb").ToArray(),
            "yard" => new[] { "fence", "fence-1x3", "fence-low", "tree-large", "tree-small", "planter", "driveway-long", "path-long" }.Select(n => $"res://assets/kenney/suburban/{n}.glb").ToArray(),
            "cars" => new[] { "sedan", "sedan-sports", "suv", "suv-luxury", "hatchback-sports", "van", "truck", "truck-flat", "delivery", "taxi", "police" }.Select(n => $"res://assets/kenney/cars/{n}.glb").ToArray(),
            "nature" => new[] { "tree_default", "tree_oak", "tree_detailed", "tree_fat", "tree_simple", "tree_small", "tree_tall", "tree_cone", "plant_bush", "plant_bushLarge", "plant_bushDetailed", "flower_redA", "rock_smallA" }.Select(n => $"res://assets/kenney/nature/{n}.glb").ToArray(),
            "props" => new[] { "gas_grill", "wheelie_bin", "mailbox", "street_lamp", "fire_hydrant", "patio_heater", "cooler", "kiddie_pool", "patio_umbrella", "shed", "stop_sign" }.Select(n => $"res://assets/props/{n}.glb").ToArray(),
            "furniture" => new[] { "bench", "chair", "chairCushion", "table", "tableRound", "loungeChair", "loungeChairRelax", "trashcan", "cardboardBoxClosed", "pottedPlant", "sideTable" }.Select(n => $"res://assets/kenney/furniture/{n}.glb").ToArray(),
            _ => System.Array.Empty<string>(),
        };
        if (set == "composite")
        {
            Place("res://assets/kenney/suburban/building-type-b.glb", new Vector3(0, 0, -6), 7.5f);
            Place("res://assets/kenney/suburban/building-type-e.glb", new Vector3(16, 0, -6), 7.5f);
            Place("res://assets/kenney/cars/sedan.glb", new Vector3(6, 0, 3), DevArgs.GetFloat("car", 1.85f), 90);
            Place("res://assets/kenney/cars/truck.glb", new Vector3(11, 0, 3), DevArgs.GetFloat("car", 1.85f), 0);
            for (var i = 0; i < 4; i++)
            {
                Place("res://assets/kenney/suburban/fence.glb", new Vector3(-8 + i * 0.475f * DevArgs.GetFloat("fence", 6.5f), 0, 1), DevArgs.GetFloat("fence", 6.5f));
            }
            Place("res://assets/kenney/nature/tree_oak.glb", new Vector3(20, 0, 2), DevArgs.GetFloat("tree", 4f));
            Place("res://assets/kenney/nature/tree_default.glb", new Vector3(23, 0, 3), DevArgs.GetFloat("tree", 4f));
            Place("res://assets/kenney/furniture/chair.glb", new Vector3(2, 0, 3), DevArgs.GetFloat("furn", 1.6f));
            Place("res://assets/kenney/furniture/table.glb", new Vector3(3, 0, 3), DevArgs.GetFloat("furn", 1.6f));
            Place("res://assets/kenney/furniture/trashcan.glb", new Vector3(4, 0, 3.5f), DevArgs.GetFloat("furn", 1.6f));
            models = System.Array.Empty<string>();
        }
        var x = 0f;
        foreach (var path in models)
        {
            var node = GameAssets.Instantiate(path);
            node.Scale = Vector3.One * scale;
            AddChild(node);
            var aabb = Bounds(node);
            node.Position = new Vector3(x - aabb.Position.X, 0, 0);
            GD.Print($"MODEL {path.GetFile(),-28} size {aabb.Size.X:0.00} x {aabb.Size.Y:0.00} x {aabb.Size.Z:0.00}");
            x += aabb.Size.X + 1.5f;
        }

        var dummy = GameAssets.Instantiate("res://assets/character/mannequin.glb");
        AddChild(dummy);
        dummy.Position = new Vector3(-1.5f, 0, 1f);
        var tank = Tank.PropaneTank.Create();
        AddChild(tank);
        tank.Position = new Vector3(-2.5f, 0, 1f);
        tank.Freeze = true;

        var camera = new Camera3D { Fov = DevArgs.GetFloat("fov", 40f), Current = true };
        AddChild(camera);
        var span = Mathf.Max(x, 8f);
        var center = new Vector3(span * 0.5f - 2f, 1.5f, 0);
        camera.LookAtFromPosition(center + new Vector3(0, span * 0.25f, span * 0.75f), center);
        if (set == "composite")
        {
            camera.LookAtFromPosition(new Vector3(-4, 2.2f, 14), new Vector3(6, 2.2f, 0));
        }
        env.Follow = camera;
        var director = new CaptureDirector { QuitAfter = 1.2f };
        AddChild(director);
        director.ShotAt(1.0f, $"{set}_{scale:0.0}");
    }

    private void Place(string path, Vector3 position, float scale, float yawDegrees = 0)
    {
        var node = GameAssets.Instantiate(path);
        node.Scale = Vector3.One * scale;
        node.Position = position;
        node.RotationDegrees = new Vector3(0, yawDegrees, 0);
        AddChild(node);
    }

    private static Aabb Bounds(Node3D root)
    {
        var first = true;
        var box = new Aabb();
        foreach (var mesh in GameAssets.MeshInstances(root))
        {
            var local = root.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
            var b = (local * mesh.GetAabb());
            b = new Aabb(b.Position * root.Scale, b.Size * root.Scale);
            box = first ? b : box.Merge(b);
            first = false;
        }
        return box;
    }
}
