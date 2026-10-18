using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Propane.World.Plan;

/// <summary>
/// The catalog as the generator sees it, measured once on the main thread: a worker thread can generate plans from it
/// while the main thread keeps loading models, because nothing here touches the engine or a shared cache.
/// </summary>
public sealed class PlanCatalogSnapshot : IPlanCatalog
{
    /// <summary>Every model key the generator measures (prop, car and furniture placements).</summary>
    public static readonly IReadOnlyList<string> Keys = PlanModels.CarModels.Distinct().Select(m => $"car:{m}").Concat(new[]
    {
        "prop:mailbox", "prop:wheelie_bin", "prop:gas_grill", "prop:patio_umbrella", "prop:patio_heater", "prop:shed",
        "prop:kiddie_pool", "prop:cooler", "prop:street_lamp", "prop:fire_hydrant", "prop:stop_sign", "furniture:table",
        "furniture:tableRound", "furniture:chair", "furniture:chairCushion", "furniture:loungeChair", "furniture:loungeChairRelax",
    }).ToArray();

    private readonly Dictionary<string, Vector2> footprints;

    // Takes ownership of freshly captured arrays/dictionary; they live with this read-only snapshot.
    internal PlanCatalogSnapshot(Vector2[] houses, int palettes, Dictionary<string, Vector2> footprints)
    {
        HouseFootprints = houses;
        HousePaletteCount = palettes;
        this.footprints = footprints;
    }

    public IReadOnlyList<Vector2> HouseFootprints { get; }
    public int HousePaletteCount { get; }

    public Vector2 PropFootprint(string model, float scale) => footprints.TryGetValue(model, out var size) ? size * scale
        : throw new KeyNotFoundException($"{model} is not in the plan catalog snapshot; add it to {nameof(PlanCatalogSnapshot)}.{nameof(Keys)}.");
}
