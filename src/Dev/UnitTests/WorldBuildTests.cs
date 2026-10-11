using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Dev.UnitTests;

/// <summary>Hashes the constructed world before its first physics tick, including fences, meshes and collision.</summary>
public static class WorldBuildTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("world/repeated builds include fences", () =>
        {
            var settings = SuburbPlans.Match(new Tuning(), 4);
            var plan = new SuburbGenerator(settings, new GameCatalog(), 23).Generate();
            TestRunner.Assert(plan.Fences.Count > 0, "fixture has no fences");
            var first = BuildHash(runner, plan, settings);
            var second = BuildHash(runner, PlanCodec.Decode(PlanCodec.Encode(plan)), settings);
            TestRunner.Assert(first == second, "world changed when rebuilt from the encoded plan");
        });
    }

    public static bool RunProcessMode(Node root)
    {
        if (DevArgs.Get("write-world-plan") is { } output)
        {
            var plan = new SuburbGenerator(SuburbPlans.Match(new Tuning(), 4), new GameCatalog(), 23).Generate();
            File.WriteAllBytes(output, PlanCodec.Encode(plan));
            return true;
        }
        if (DevArgs.Get("hash-world-plan") is { } input)
        {
            var plan = PlanCodec.Decode(File.ReadAllBytes(input));
            GD.Print($"[world] hash {BuildHash(root, plan, SuburbPlans.Match(new Tuning(), 4))} fences {plan.Fences.Count}");
            return true;
        }
        return false;
    }

    private static string BuildHash(Node root, SuburbPlan plan, SuburbSettings settings)
    {
        var suburb = Suburb.Build(plan, settings, networked: true);
        root.AddChild(suburb);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteNode(writer, suburb);
        var hash = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        root.RemoveChild(suburb);
        suburb.Free();
        return hash;
    }

    private static void WriteNode(BinaryWriter w, Node node)
    {
        // Auto-generated names and resource/instance ids are process-local, so never include them.
        w.Write(node.GetType().FullName!);
        if (node is Node3D spatial)
        {
            Write(w, spatial.Transform);
        }
        if (node is CollisionObject3D collision)
        {
            w.Write(collision.CollisionLayer);
            w.Write(collision.CollisionMask);
        }
        if (node is RigidBody3D body)
        {
            w.Write(body.Mass);
            w.Write(body.Freeze);
            w.Write(body.LinearDamp);
            w.Write(body.AngularDamp);
            Write(w, body.CenterOfMass);
            w.Write(body.PhysicsMaterialOverride?.Friction ?? 1);
            w.Write(body.PhysicsMaterialOverride?.Bounce ?? 0);
        }
        if (node is Tank.PropaneTank tank)
        {
            w.Write(tank.NetId);
        }
        if (node is CollisionShape3D shape)
        {
            w.Write(shape.Disabled);
            w.Write(shape.Shape.GetType().FullName!);
            switch (shape.Shape)
            {
                case BoxShape3D box: Write(w, box.Size); break;
                case CylinderShape3D cylinder: w.Write(cylinder.Radius); w.Write(cylinder.Height); break;
                case ConvexPolygonShape3D convex: Write(w, convex.Points); break;
                case ConcavePolygonShape3D concave: Write(w, concave.Data); w.Write(concave.BackfaceCollision); break;
                default: throw new InvalidOperationException($"unhashed collision shape {shape.Shape.GetType().Name}");
            }
        }
        if (node is MeshInstance3D mesh && mesh.Mesh != null)
        {
            w.Write(mesh.Layers);
            var tint = mesh.GetInstanceShaderParameter("instance_tint");
            // The headless renderer may return nil for an unset instance uniform; white is its shader default.
            var color = tint.VariantType == Variant.Type.Nil ? Colors.White : tint.AsColor();
            w.Write(color.R); w.Write(color.G); w.Write(color.B); w.Write(color.A);
            w.Write(mesh.Mesh.GetSurfaceCount());
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                // Variant arrays contain the actual geometry and attributes, rather than resource identity.
                var arrays = GD.VarToBytes(mesh.Mesh.SurfaceGetArrays(surface));
                w.Write(arrays.Length);
                w.Write(arrays);
            }
        }
        var children = node.GetChildren();
        w.Write(children.Count);
        foreach (var child in children)
        {
            WriteNode(w, child);
        }
    }

    private static void Write(BinaryWriter w, Transform3D t)
    {
        Write(w, t.Basis.X); Write(w, t.Basis.Y); Write(w, t.Basis.Z); Write(w, t.Origin);
    }

    private static void Write(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }

    private static void Write(BinaryWriter w, Vector3[] points)
    {
        w.Write(points.Length);
        foreach (var point in points) { Write(w, point); }
    }
}
