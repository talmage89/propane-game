using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Tank;

namespace Propane.Dev;

/// <summary>More than two full blast pages, duplicate body shapes and a real impulse on every body.</summary>
public partial class BlastQueryTests : Node3D
{
    private WorldContext world = null!;
    public override void _ExitTree() => world.Dispose();

    private void Require(bool condition, string message)
    {
        if (condition) { return; }
        GD.PushError($"[blast query] FAIL {message}");
        ShutdownGuard.Quit(GetTree(), 1);
        throw new InvalidOperationException(message);
    }

    public override void _Ready()
    {
        world = new WorldContext(this, Tuning.SavedTemplate, present: true);
        DevArgs.MarkReady();
        // More colliders than one blast page, including duplicate shapes on the same body.
        var bodies = new List<RigidBody3D>();
        for (var i = 0; i < 1100; i++)
        {
            var body = new RigidBody3D
            {
                CollisionLayer = Layers.Props,
                CollisionMask = 0,
                Mass = 17,
                Position = new Vector3((i % 11 - 5) * 0.05f, 1 + (i / 121) * 0.05f, (i / 11 % 11 - 5) * 0.05f)
            };
            body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.01f } });
            if (i % 10 == 0) { body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.02f } }); }
            AddChild(body); bodies.Add(body);
        }

        world.SetContentRoot(this);
        var director = new CaptureDirector { QuitAfter = 1 }; AddChild(director);
        director.At(0.2f, () =>
        {
            var query = new PhysicsShapeQueryParameters3D { Shape = new SphereShape3D { Radius = 4 }, Transform = new Transform3D(Basis.Identity, Vector3.Up), CollisionMask = Layers.Pushable };
            var seen = Blast.QueryAll(GetWorld3D().DirectSpaceState, query).Select(h => h["collider_id"].AsUInt64()).Distinct().Count();
            Require(seen == bodies.Count, $"truncated blast query {seen}/{bodies.Count}");
            Blast.Detonate(world, Vector3.Up, null, false);
        });
        director.At(0.3f, () =>
        {
            Require(bodies.All(b => b.LinearVelocity.Length() > 1), "dense blast failed to push every body");
            GD.Print($"[blast query] PASS dense blast: {bodies.Count} bodies, duplicate shapes, complete query and push");
        });
    }
}
