using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Propane.Core;

/// <summary>One pre-broken piece of the tank shell.</summary>
public sealed record DebrisPiece(string Name, Mesh Mesh, Shape3D Shape, Vector3 Centroid, float Mass);

/// <summary>Loads and caches the meshes, collision shapes and materials that many nodes share.</summary>
public static class GameAssets
{
    private const string TankScenePath = "res://assets/tank/tank.glb";
    private const string DebrisScenePath = "res://assets/tank/debris.glb";
    private const float SteelShellDensity = 900f;

    private static Mesh? tankMesh;
    private static Shape3D? tankShape;
    private static IReadOnlyList<DebrisPiece>? debrisPieces;
    private static ShaderMaterial? tankOuter;
    private static ShaderMaterial? tankInterior;
    private static readonly Dictionary<string, PackedScene> Scenes = new();

    public static Mesh TankMesh
    {
        get
        {
            LoadTank();
            return tankMesh!;
        }
    }

    public static Shape3D TankShape
    {
        get
        {
            LoadTank();
            return tankShape!;
        }
    }

    public static IReadOnlyList<DebrisPiece> Debris => debrisPieces ??= LoadDebris();

    public static ShaderMaterial TankOuterMaterial => tankOuter ??= CreateTankOuter();

    public static ShaderMaterial TankInteriorMaterial => tankInterior ??= new ShaderMaterial
    {
        Shader = GD.Load<Shader>("res://shaders/debris_interior.gdshader"),
    };

    /// <summary>Loads a model scene once and instantiates it on every call.</summary>
    public static Node3D Instantiate(string path)
    {
        if (!Scenes.TryGetValue(path, out var scene))
        {
            scene = GD.Load<PackedScene>(path);
            Scenes[path] = scene;
        }
        return scene.Instantiate<Node3D>();
    }

    /// <summary>All mesh instances under a node, depth first.</summary>
    public static IEnumerable<MeshInstance3D> MeshInstances(Node root)
    {
        if (root is MeshInstance3D mesh)
        {
            yield return mesh;
        }
        foreach (var child in root.GetChildren())
        {
            foreach (var nested in MeshInstances(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// A well-spread subset of points for a collision hull with a sensible vertex count. It starts from the extreme
    /// points along a fan of directions (weighted toward the base, so a prop's feet or wheels keep their true footprint
    /// and it stands where it was placed), then adds points by greedy farthest-point sampling.
    /// </summary>
    public static List<Vector3> FarthestPoints(IReadOnlyList<Vector3> points, int count)
    {
        var unique = points.Distinct().ToList();
        if (unique.Count <= count)
        {
            return unique;
        }
        var chosen = new List<Vector3>();
        foreach (var direction in HullDirections)
        {
            var extreme = unique.MaxBy(p => p.Dot(direction));
            if (!chosen.Contains(extreme))
            {
                chosen.Add(extreme);
            }
        }
        var distance = unique.Select(p => chosen.Min(c => p.DistanceSquaredTo(c))).ToArray();
        while (chosen.Count < count)
        {
            var best = 0;
            for (var i = 1; i < unique.Count; i++)
            {
                if (distance[i] > distance[best])
                {
                    best = i;
                }
            }
            chosen.Add(unique[best]);
            for (var i = 0; i < unique.Count; i++)
            {
                distance[i] = Mathf.Min(distance[i], unique[i].DistanceSquaredTo(unique[best]));
            }
        }
        return chosen;
    }

    /// <summary>The 26 box directions, plus steep downward ones that find the lowest points under each corner.</summary>
    private static readonly Vector3[] HullDirections = BuildHullDirections();

    private static Vector3[] BuildHullDirections()
    {
        var directions = new List<Vector3>();
        for (var x = -1; x <= 1; x++)
        {
            for (var z = -1; z <= 1; z++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if (x != 0 || y != 0 || z != 0)
                    {
                        directions.Add(new Vector3(x, y, z).Normalized());
                    }
                }
                if (x != 0 || z != 0)
                {
                    directions.Add(new Vector3(x, -6, z).Normalized());
                }
            }
        }
        return directions.ToArray();
    }

    /// <summary>A convex hull shape from a mesh's vertices.</summary>
    public static ConvexPolygonShape3D HullFromMesh(Mesh mesh)
    {
        var points = new List<Vector3>();
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var arrays = mesh.SurfaceGetArrays(surface);
            points.AddRange(arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array());
        }
        return new ConvexPolygonShape3D { Points = points.Distinct().ToArray() };
    }

    private static void LoadTank()
    {
        if (tankMesh != null)
        {
            return;
        }
        var root = Instantiate(TankScenePath);
        var meshes = MeshInstances(root).ToDictionary(m => m.Name.ToString(), m => m.Mesh);
        tankMesh = meshes["Tank"];
        tankShape = HullFromMesh(meshes["TankCollision"]);
        root.Free();
    }

    private static List<DebrisPiece> LoadDebris()
    {
        var root = Instantiate(DebrisScenePath);
        var meshes = MeshInstances(root).ToDictionary(m => m.Name.ToString(), m => m.Mesh);
        var pieces = new List<DebrisPiece>();
        foreach (var (name, mesh) in meshes)
        {
            if (name.EndsWith("_Hull"))
            {
                continue;
            }
            var hull = meshes[name + "_Hull"];
            var bounds = hull.GetAabb();
            var volume = Mathf.Max(bounds.Volume * 0.35f, 0.00015f);
            pieces.Add(new DebrisPiece(name, mesh, HullFromMesh(hull), bounds.GetCenter(),
                Mathf.Clamp(volume * SteelShellDensity, 0.15f, 2.5f)));
        }
        root.Free();
        return pieces;
    }

    private static ShaderMaterial CreateTankOuter()
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/tank.gdshader") };
        material.SetShaderParameter("albedo_texture", GD.Load<Texture2D>("res://assets/tank/T_PropaneTank_BaseColor.png"));
        material.SetShaderParameter("normal_texture", GD.Load<Texture2D>("res://assets/tank/T_PropaneTank_Normal.png"));
        material.SetShaderParameter("orm_texture", GD.Load<Texture2D>("res://assets/tank/T_PropaneTank_ORM.png"));
        return material;
    }

    /// <summary>Applies the tank materials to a mesh instance by surface name (outer shell versus interior).</summary>
    public static void ApplyTankMaterials(MeshInstance3D instance)
    {
        var mesh = instance.Mesh;
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var source = mesh.SurfaceGetMaterial(surface);
            var isInterior = source != null && source.ResourceName.Contains("Interior");
            instance.SetSurfaceOverrideMaterial(surface, isInterior ? TankInteriorMaterial : TankOuterMaterial);
        }
    }
}
