using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// A magazine dropped by a reload. Only for show, and local to each game: it falls and settles on the world, but
/// nothing shoots, pushes or syncs it. Once there are too many, the oldest go.
/// </summary>
public partial class DroppedMagazine : RigidBody3D
{
    private const int MaxDropped = 24;
    private const int HullPoints = 24;
    private const float MagazineMass = 0.13f;

    private static readonly List<DroppedMagazine> Dropped = new();
    private static Mesh? mesh;
    private static Shape3D? shape;

    /// <summary>Loads the mesh and builds its hull ahead of the first reload, which would otherwise hitch.</summary>
    public static void Preload()
    {
        if (mesh != null)
        {
            return;
        }
        var root = GameAssets.Instantiate("res://assets/rifle/magazine.glb");
        mesh = GameAssets.MeshInstances(root).First().Mesh;
        shape = new ConvexPolygonShape3D { Points = GameAssets.FarthestPoints(mesh.GetFaces(), HullPoints).ToArray() };
        root.Free();
    }

    /// <summary>Drops a magazine from <paramref name="at"/> (its top, oriented as on the rifle).</summary>
    public static void Drop(Transform3D at, Vector3 velocity)
    {
        Preload();
        var body = new DroppedMagazine
        {
            Name = "DroppedMagazine",
            Mass = MagazineMass,
            // No layer of its own: blasts, shots and the network never see it, but it lands on everything solid.
            CollisionLayer = 0,
            CollisionMask = Layers.World | Layers.Props | Layers.Tanks | Layers.Debris | Layers.Floor,
            ContinuousCd = true,
            PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.8f, Bounce = 0.2f },
            AngularDamp = 1f,
        };
        body.AddChild(new CollisionShape3D { Shape = shape });
        body.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = Rifle.BodyMaterial, Layers = RenderLayers.Actors });
        Spawn.InWorld(body);
        body.GlobalTransform = at;
        body.LinearVelocity = velocity;
        // Tumbles mostly end over end, as a magazine falls.
        body.AngularVelocity = at.Basis * new Vector3((float)GD.RandRange(-4, -1), (float)GD.RandRange(-1, 1), (float)GD.RandRange(-1.5, 1.5));

        Dropped.RemoveAll(magazine => !IsInstanceValid(magazine));
        Dropped.Add(body);
        if (Dropped.Count > MaxDropped)
        {
            Dropped[0].QueueFree();
            Dropped.RemoveAt(0);
        }
    }
}
