using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Per-character presentation cost: <c>--players=N</c> (default 8) scripted players walk, creep, sprint,
/// aim, fire, reload and strike on a void floor, apart and aiming up, so no shot or strike couples them. After a
/// warm-up it reports the mean and 95th percentile of the engine's process and physics time per frame (animation
/// trees, skeleton modifiers, scripts and the physics step) and of their sum. Run the same file on two builds to
/// compare what each character costs.</summary>
public partial class AnimationCost : Node3D
{
    private WorldContext world = null!;
    private readonly List<ScriptedInput> inputs = new();
    private readonly List<double> process = new(), physics = new();
    private int frames;
    private const int Warmup = 120, Measured = 600;

    public override void _ExitTree() => world.Dispose();

    public override void _Ready()
    {
        DevArgs.MarkReady();
        DevArgs.Setup();
        ProcessPhysicsPriority = -10;
        using var defaults = new Tuning();
        world = new WorldContext(this, defaults, present: true);
        var dynamic = new Node3D { Name = "Dynamic" };
        AddChild(dynamic);
        world.SetContentRoot(dynamic);
        var floor = new StaticBody3D { CollisionLayer = Layers.Floor, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        AddChild(floor);
        var count = (int)DevArgs.GetFloat("players", 8);
        var directions = new[] { Vector2.Down, Vector2.Up, Vector2.Left, Vector2.Right, new Vector2(1, -1).Normalized() };
        for (var i = 0; i < count; i++)
        {
            var input = new ScriptedInput();
            // A repeating 6-second routine, offset per player so the states overlap.
            for (var loop = 0; loop < 4; loop++)
            {
                var t = loop * 6 + i * 0.37f;
                input.Hold(t, t + 1.5f, new PlayerIntent { Move = directions[(i + loop) % directions.Length] });
                input.Hold(t + 1.5f, t + 3, new PlayerIntent { Move = directions[(i + loop + 2) % directions.Length], Aim = true });
                input.Hold(t + 2, t + 2.6f, new PlayerIntent { FireHeld = true });
                input.Hold(t + 3, t + 4, new PlayerIntent { Move = Vector2.Down, Sprint = true });
                input.Hold(t + 4, t + 4.6f, new PlayerIntent { Move = Vector2.Right, FireHeld = true });
                input.Reload(t + 4.7f);
                input.Melee(t + 5.4f);
            }
            // Aim 20 degrees up so no shot or ragdoll couples the players; one row, far apart.
            input.Look(0, 0.25f, new Vector2(0, -20 / Mathf.RadToDeg(world.Tuning.MouseSensitivity) / 0.25f));
            inputs.Add(input);
            var player = new PlayerCharacter
            {
                World = world,
                Name = $"Player{i}",
                InputOverride = input,
                Position = new Vector3(i * 15, 0, 0)
            };
            dynamic.AddChild(player);
            player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 30, Reserve = 900 };
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        foreach (var input in inputs) input.Advance((float)delta);
    }

    public override void _Process(double delta)
    {
        if (frames++ < Warmup) return;
        process.Add(Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000);
        physics.Add(Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000);
        if (process.Count < Measured) return;
        static string Stats(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return FormattableString.Invariant($"mean={values.Average():F3}ms p95={sorted[sorted.Length * 95 / 100]:F3}ms");
        }
        var total = process.Zip(physics, (a, b) => a + b).ToList();
        // Skeleton updates can land in either step depending on what first reads a pose, so compare the total.
        GD.Print($"[animation-cost] players={inputs.Count} frames={process.Count} total {Stats(total)} process {Stats(process)} physics {Stats(physics)}");
        ShutdownGuard.Quit(GetTree());
    }
}
