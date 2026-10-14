using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using Propane.Core;
using Propane.Fx;

namespace Propane.Tank;

/// <summary>Bound shell-fragment registration during chains, retaining spawned mess until its content retires.</summary>
public partial class DebrisQueue : Node
{
    private static readonly ConditionalWeakTable<WorldContext, DebrisQueue> worlds = new();
    private readonly record struct Fragment(Spawn Spawn, DebrisPiece Piece, Transform3D Pose, Vector3 Linear, Vector3 Angular, float Soot);
    private readonly Queue<Fragment> pending = new();
    public required WorldContext World { get; init; }
    internal int PendingCount => pending.Count;

    public static void Enqueue(WorldContext world, Spawn spawn, Transform3D pose, Vector3 inherited, Vector3 center)
    {
        var count = Math.Clamp(world.Tuning.DebrisPiecesPerTank, 0, GameAssets.Debris.Count);
        if (count == 0) return;
        world.EnsureAlive();
        var queue = worlds.GetValue(world, static owner =>
        {
            var node = new DebrisQueue { World = owner, Name = "DebrisQueue" };
            owner.EffectsRoot.AddChild(node);
            owner.OnStopping(node.pending.Clear);
            return node;
        });
        // Visual randomness must not depend on when the presentation queue drains.
        using var rng = new RandomNumberGenerator();
        for (var i = 0; i < count; i++)
        {
            var piece = GameAssets.Debris[PieceIndex(i, count, GameAssets.Debris.Count)];
            var outward = pose * piece.Centroid - center;
            outward = outward.LengthSquared() > 0.0001f ? outward.Normalized() : Vector3.Up;
            var speed = world.Tuning.DebrisSpeed * rng.RandfRange(0.45f, 1.15f);
            var linear = inherited + (outward + Vector3.Up * rng.RandfRange(0.3f, 1.1f)).Normalized() * speed;
            var angular = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)) * world.Tuning.DebrisSpin;
            queue.pending.Enqueue(new Fragment(spawn, piece, pose, linear, angular, rng.RandfRange(0.3f, 0.6f)));
        }
    }

    internal static int PieceIndex(int index, int count, int available) => count == 1 ? 0 : index * (available - 1) / (count - 1);

    public override void _Process(double delta)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.Debris);
        var remaining = World.Tuning.DebrisSpawnPerFrame;
        while (remaining > 0 && pending.TryDequeue(out var fragment))
        {
            var root = fragment.Spawn.ContentRoot;
            // Retiring a suburb also cancels work that has not yet reached its content tree.
            if (!World.IsAlive || !IsInstanceValid(root) || !root.CanProcess() || root.IsQueuedForDeletion()) continue;
            var body = DebrisBody.Create(World, fragment.Piece, fragment.Soot);
            fragment.Spawn.InWorld(body);
            body.GlobalTransform = fragment.Pose;
            body.LinearVelocity = fragment.Linear;
            body.AngularVelocity = fragment.Angular;
            remaining--;
        }
    }

    public override void _ExitTree()
    {
        pending.Clear();
        if (worlds.TryGetValue(World, out var current) && ReferenceEquals(current, this)) worlds.Remove(World);
    }
}
