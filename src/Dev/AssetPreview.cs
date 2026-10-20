using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Assets;
using Propane.Core;
using Propane.Player;
using Propane.ReleaseSmoke;
using Propane.World;

namespace Propane.Dev;

/// <summary>Owner preview: all registered hats, None with an empty library, or one unsaved raw GLB import.</summary>
public partial class AssetPreview : Node3D
{
    private WorldContext world = null!;
    private PlayerCharacter actor = null!;
    private Camera3D camera = null!;
    private readonly ScriptedInput input = new();
    private readonly List<GodotObject> imported = new();
    private readonly List<Dictionary<string, string>> shots = new();

    public override void _Ready()
    {
        try
        {
            DevArgs.MarkReady();
            DevArgs.Setup();
            InputSetup.Register();
            using var defaults = new Tuning();
            world = new WorldContext(this, defaults, present: true);
            world.Random.Seed = 1923;
            world.SetContentRoot(world.Root);
            world.Spawn.InWorld(new VoidEnvironment());
            var catalog = HatCatalog.Shipped;
            if (DevArgs.Get("asset") is { } file)
            {
                var rootPath = ProjectSettings.GlobalizePath("res://");
                var path = System.IO.Path.GetFullPath(file);
                if (!path.StartsWith(rootPath, StringComparison.Ordinal) || System.IO.Path.GetExtension(path) != ".glb")
                    throw new ArgumentException("Preview asset must be a GLB inside this worktree");
                using var document = new GltfDocument();
                using var state = new GltfState();
                if (document.AppendFromFile(path, state) != Error.Ok) throw new InvalidOperationException("Cannot import preview GLB");
                var raw = document.GenerateScene(state);
                try
                {
                    HatCatalog.ValidateScene(raw);
                    var scene = new PackedScene();
                    imported.Add(scene);
                    if (scene.Pack(raw) != Error.Ok) throw new InvalidOperationException("Cannot pack preview GLB");
                    using var library = new HatLibrary();
                    library.Hats.Add(new HatDefinition { Id = "file_preview", DisplayName = "File preview", Scene = scene });
                    catalog = new HatCatalog(library);
                }
                finally { raw.Free(); }
            }
            actor = world.Spawn.InWorld(new PlayerCharacter { World = world, CosmeticCatalog = catalog, InputOverride = input });
            camera = world.AddView(this, new Camera3D { Current = true, Fov = 40 });
            var director = new CaptureDirector { CollectFrameMetrics = false };
            AddChild(director);
            var entries = new string?[] { null }.Concat(catalog.Items.Select(item => (string?)item.Id)).ToArray();
            for (var i = 0; i < entries.Length; i++)
            {
                var id = entries[i];
                var start = i * 15f;
                director.At(start + .1f, () => { actor.Teleport(Vector3.Up * .05f, 0); actor.SetHat(id); actor.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 5, Reserve = 90 }; });
                var views = new[] { new Vector3(0, .4f, -4), new Vector3(4, .4f, 0), new Vector3(0, .4f, 4), new Vector3(-4, .4f, 0), new Vector3(2, 3, -3) };
                for (var angle = 0; angle < views.Length; angle++)
                {
                    var offset = views[angle];
                    var label = (id ?? "none") + "_view_" + angle;
                    director.At(start + .2f + angle * .3f, () => CameraOffset = offset);
                    director.ShotAt(start + .4f + angle * .3f, label);
                    shots.Add(new Dictionary<string, string> { ["id"] = id ?? "none", ["shot"] = label });
                }
                input.Hold(start + 2, start + 3, new PlayerIntent { Move = Vector2.Down * .25f });
                input.Hold(start + 3, start + 4, new PlayerIntent { Move = Vector2.Down });
                input.Hold(start + 4, start + 5, new PlayerIntent { Move = Vector2.Down, Sprint = true });
                input.Hold(start + 5, start + 6, new PlayerIntent { Move = Vector2.Down, Aim = true });
                input.Reload(start + 6); input.Melee(start + 7);
                director.At(start + 8.5f, () => actor.RemoteRagdoll(actor.GlobalPosition, new Vector3(0, 4, 0), new Vector3(1, 0, 1)));
                foreach (var moment in new[] { 2.5f, 3.5f, 4.5f, 5.5f, 6.5f, 7.3f, 9f, 10f, 12f, 14f })
                {
                    var label = (id ?? "none") + "_motion_" + moment.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                    director.ShotAt(start + moment, label);
                    shots.Add(new Dictionary<string, string> { ["id"] = id ?? "none", ["shot"] = label });
                }
            }
            director.At(entries.Length * 15 - .2f, () =>
            {
                var directory = DevArgs.Get("capture-dir") ?? throw new ArgumentException("--capture-dir is required");
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "preview.json"), System.Text.Json.JsonSerializer.Serialize(shots));
                GD.Print($"[assets preview] PASS hats={catalog.Items.Length}; None plus all items, five angles, motion and ragdoll/get-up");
            });
            director.QuitAfter = entries.Length * 15;
            ProcessPhysicsPriority = int.MinValue;
        }
        catch (Exception error) { GD.PushError($"[assets preview] FAIL {error}"); ShutdownGuard.Quit(GetTree(), 1); }
    }

    private Vector3 CameraOffset { get; set; } = new(3, 1, -4);
    public override void _PhysicsProcess(double delta) { input.Advance((float)delta); }
    public override void _Process(double delta)
    {
        if (actor == null) return;
        var at = actor.IsRagdolled ? actor.PelvisPosition : actor.GlobalPosition + Vector3.Up * 1.3f;
        camera.LookAtFromPosition(at + CameraOffset, at);
    }
    public override void _ExitTree()
    {
        world?.Dispose();
        foreach (var resource in imported) resource.Dispose();
    }
}
