using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.World;

/// <summary>A placeable model with its world scale and measured, scaled footprint.</summary>
public sealed class ModelInfo
{
    public required string Path { get; init; }

    public required float Scale { get; init; }

    /// <summary>Scaled bounds in the model's own frame (front is +Z).</summary>
    public Aabb Bounds { get; set; }

    /// <summary>
    /// Where the model sits under its prop's origin, scaled: zero for models authored centered on their base, and for
    /// corner-pivoted kits (Kenney furniture) the shift that centers the footprint and puts the base on the ground.
    /// </summary>
    public Vector3 PivotOffset { get; set; }

    public Vector2 Footprint => new(Bounds.Size.X, Bounds.Size.Z);

    public float Height => Bounds.End.Y;
}

/// <summary>Every model the generator places, at real-world scale.</summary>
public static class Catalog
{
    public const float HouseScale = 7.5f;
    public const float CarScale = 1.85f;
    public const float FenceScale = 6f;
    public const float FenceThicknessScale = 3f;
    public const float FurnitureScale = 2f;

    private const string Suburban = "res://assets/kenney/suburban/";
    private const string Cars = "res://assets/kenney/cars/";
    private const string Nature = "res://assets/kenney/nature/";
    private const string Furniture = "res://assets/kenney/furniture/";
    private const string Props = "res://assets/props/";

    private static readonly Dictionary<string, ModelInfo> Models = new();
    private static List<ModelInfo>? houses;
    private static Texture2D[]? housePalettes;

    public static IReadOnlyList<ModelInfo> Houses => houses ??= "abcdefghijklmnopqrstu"
        .Select(c => Get($"{Suburban}building-type-{c}.glb", HouseScale)).ToList();

    /// <summary>Alternate colormaps for the house kit: green, blue, orange and charcoal roofs.</summary>
    public static Texture2D[] HousePalettes => housePalettes ??= new[]
    {
        GD.Load<Texture2D>($"{Suburban}Textures/colormap.png"),
        GD.Load<Texture2D>($"{Suburban}Textures/variation-a.png"),
        GD.Load<Texture2D>($"{Suburban}Textures/variation-b.png"),
        GD.Load<Texture2D>($"{Suburban}Textures/variation-c.png"),
    };

    public static readonly string[] CarModels =
    {
        "sedan", "sedan-sports", "suv", "suv-luxury", "hatchback-sports", "van", "truck", "sedan", "suv",
    };

    public const string PickupModel = "truck";

    // The pickup's bed, measured from the Kenney truck model (units before CarScale).
    public const float PickupBedFloorModel = 0.6f;
    public const float PickupBedWallTopModel = 0.8f;
    public const float PickupBedFrontModel = -0.3f;
    public const float PickupBedBackModel = -1.45f;
    public const float PickupBedHalfWidthModel = 0.72f;
    public const float PickupBedWallModel = 0.2f;

    /// <summary>Height of the bed floor above the ground, in meters.</summary>
    public const float PickupBedFloor = PickupBedFloorModel * CarScale;

    /// <summary>Distance from the truck's origin back to the middle of its bed, in meters.</summary>
    public const float PickupBedCenterBehind = -(PickupBedFrontModel + PickupBedBackModel) * 0.5f * CarScale;

    public static readonly string[] TreeModels =
    {
        "tree_default", "tree_oak", "tree_detailed", "tree_fat", "tree_simple", "tree_tall", "tree_default_dark",
        "tree_oak_dark",
    };

    public static readonly string[] BushModels = { "plant_bush", "plant_bushDetailed", "plant_bushLarge", "plant_bushSmall", "plant_bushTriangle" };

    public static readonly string[] FlowerModels = { "flower_purpleA", "flower_redA", "flower_yellowA", "flower_purpleB", "flower_redB", "flower_yellowB" };

    public static ModelInfo Fence => Get($"{Suburban}fence.glb", FenceScale);

    public static ModelInfo Car(string name) => Get($"{Cars}{name}.glb", CarScale);

    public static ModelInfo Tree(string name) => Get($"{Nature}{name}.glb", 1f);

    public static ModelInfo NatureModel(string name) => Get($"{Nature}{name}.glb", 1f);

    public static ModelInfo FurnitureModel(string name) => Get($"{Furniture}{name}.glb", FurnitureScale, recenter: true);

    public static ModelInfo Prop(string name) => Get($"{Props}{name}.glb", 1f);

    /// <summary>Loads (once) and measures a model at a scale, optionally recentring it on its footprint.</summary>
    public static ModelInfo Get(string path, float scale, bool recenter = false)
    {
        var key = $"{path}@{scale}{(recenter ? ":centered" : "")}";
        if (Models.TryGetValue(key, out var info))
        {
            return info;
        }
        info = new ModelInfo { Path = path, Scale = scale };
        var node = GameAssets.Instantiate(path);
        info.Bounds = MeasureBounds(node, scale);
        node.Free();
        if (recenter)
        {
            var center = info.Bounds.GetCenter();
            info.PivotOffset = new Vector3(-center.X, -info.Bounds.Position.Y, -center.Z);
            info.Bounds = new Aabb(info.Bounds.Position + info.PivotOffset, info.Bounds.Size);
        }
        Models[key] = info;
        return info;
    }

    private static Aabb MeasureBounds(Node3D root, float scale)
    {
        var first = true;
        var box = new Aabb();
        foreach (var mesh in GameAssets.MeshInstances(root))
        {
            var transform = Transform3D.Identity;
            Node? n = mesh;
            while (n != null && n != root)
            {
                if (n is Node3D n3)
                {
                    transform = n3.Transform * transform;
                }
                n = n.GetParent();
            }
            var b = transform * mesh.Mesh.GetAabb();
            box = first ? b : box.Merge(b);
            first = false;
        }
        return new Aabb(box.Position * scale, box.Size * scale);
    }
}
