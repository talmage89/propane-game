using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.World.Plan;

namespace Propane.World;

/// <summary>The real asset catalog, as the generator sees it.</summary>
public sealed class GameCatalog : IPlanCatalog
{
    private List<Vector2>? houseFootprints;

    public IReadOnlyList<Vector2> HouseFootprints => houseFootprints ??= Catalog.Houses.Select(h => h.Footprint).ToList();

    public int HousePaletteCount => Catalog.HousePalettes.Length;

    public Vector2 PropFootprint(string model, float scale) => Resolve(model).Footprint * scale;

    /// <summary>Capture native catalog measurements on the main thread before worker generation.</summary>
    public static PlanCatalogSnapshot Capture(IPlanCatalog source)
    {
        var footprints = new Dictionary<string, Vector2>();
        var houses = source.HouseFootprints.ToArray();
        var palettes = source.HousePaletteCount;
        foreach (var key in PlanCatalogSnapshot.Keys) footprints[key] = source.PropFootprint(key, 1f);
        return new PlanCatalogSnapshot(houses, palettes, footprints);
    }

    /// <summary>Maps a plan model key ("prop:x", "car:x", "nature:x", "furniture:x") to its catalog entry.</summary>
    public static ModelInfo Resolve(string model)
    {
        var split = model.IndexOf(':');
        var kind = model[..split];
        var name = model[(split + 1)..];
        return kind switch
        {
            "prop" => Catalog.Prop(name),
            "car" => Catalog.Car(name),
            "nature" => Catalog.NatureModel(name),
            "furniture" => Catalog.FurnitureModel(name),
            _ => throw new System.ArgumentException($"Unknown model kind in {model}"),
        };
    }
}
