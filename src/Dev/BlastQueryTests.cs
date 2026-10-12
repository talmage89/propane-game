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

        var wall = new StaticBody3D { CollisionLayer = Layers.World, Position = new Vector3(1, 1, 0) };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(.3f, 2, 2) } });
        AddChild(wall);
        var blocked = new Receiver(new Vector3(2, 1, 0));
        var clear = new Receiver(new Vector3(-2, 1, 0));
        world.Register(blocked); world.Register(clear);

        world.SetContentRoot(this);
        var director = new CaptureDirector { QuitAfter = 1 }; AddChild(director);
        director.At(0.2f, () =>
        {
            var query = new PhysicsShapeQueryParameters3D { Shape = new SphereShape3D { Radius = 4 }, Transform = new Transform3D(Basis.Identity, Vector3.Up), CollisionMask = Layers.Pushable };
            var seen = Blast.QueryAll(GetWorld3D().DirectSpaceState, query).Select(h => h["collider_id"].AsUInt64()).Distinct().Count();
            Require(seen == bodies.Count, $"truncated blast query {seen}/{bodies.Count}");
            var excluded = new PhysicsShapeQueryParameters3D
            {
                Shape = query.Shape,
                Transform = query.Transform,
                CollisionMask = query.CollisionMask,
                Exclude = new Godot.Collections.Array<Rid> { bodies[0].GetRid(), bodies[500].GetRid() }
            };
            var remaining = Blast.QueryAll(GetWorld3D().DirectSpaceState, excluded).Select(h => h["collider_id"].AsUInt64()).Distinct().ToArray();
            Require(remaining.Length == bodies.Count - 2 && !remaining.Contains(bodies[0].GetInstanceId()) &&
                !remaining.Contains(bodies[500].GetInstanceId()), "paging lost the caller's initial exclusions");
            var shortPage = new PhysicsShapeQueryParameters3D
            {
                Shape = new SphereShape3D { Radius = .04f },
                Transform = new Transform3D(Basis.Identity, bodies[0].GlobalPosition),
                CollisionMask = Layers.Pushable
            };
            var direct = GetWorld3D().DirectSpaceState.IntersectShape(shortPage, 512).Select(h => h["rid"].AsRid()).ToArray();
            Require(direct.Length is > 0 and < 512, "short-page fixture must stay below the query limit");
            var paged = Blast.QueryAll(GetWorld3D().DirectSpaceState, shortPage).Select(h => h["rid"].AsRid()).ToArray();
            Require(paged.SequenceEqual(direct), "short-page query changed the engine's hit order or duplicate shapes");
            Blast.Detonate(world, Vector3.Up, null, false);
            Require(blocked.Hits.Count == 1 && clear.Hits.Count == 1 &&
                Mathf.IsEqualApprox(blocked.Hits[0].Falloff, clear.Hits[0].Falloff * world.Tuning.BlastOcclusion),
                "reused sight query lost the receiver's occlusion or target");
            var opposite = new Vector3(10, 1, 0);
            Blast.Detonate(world, opposite, null, false);
            var reach = Mathf.Max(world.Tuning.BlastRadius, Mathf.Max(world.Tuning.RagdollRadius, world.Tuning.StaggerRadius));
            var nearFalloff = Mathf.Pow(1 - opposite.DistanceTo(blocked.BlastTargetPosition) / reach, world.Tuning.BlastFalloff);
            var farFalloff = Mathf.Pow(1 - opposite.DistanceTo(clear.BlastTargetPosition) / reach, world.Tuning.BlastFalloff);
            Require(blocked.Hits.Count == 2 && clear.Hits.Count == 2 && Mathf.IsEqualApprox(blocked.Hits[1].Falloff, nearFalloff) &&
                Mathf.IsEqualApprox(clear.Hits[1].Falloff, farFalloff * world.Tuning.BlastOcclusion),
                "the next blast retained the previous sight query origin or target");
        });
        director.At(0.3f, () =>
        {
            Require(bodies.All(b => b.LinearVelocity.Length() > 1), "dense blast failed to push every body");
            GD.Print($"[blast query] PASS dense blast: {bodies.Count} bodies, duplicate shapes, complete query and push");
        });
    }

    private sealed class Receiver(Vector3 position) : IBlastReceiver
    {
        public Vector3 BlastTargetPosition => position;
        public List<BlastInfo> Hits { get; } = new();
        public void ReceiveBlast(BlastInfo blast) => Hits.Add(blast);
    }
}
