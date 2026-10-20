using System.Linq;
using Godot;
using Propane.Assets;
using Propane.Core;

namespace Propane.Player;

public partial class PlayerCharacter
{
    /// <summary>Borrowed immutable catalog. Dev fixtures inject unsaved resources without changing the process cache.</summary>
    public HatCatalog CosmeticCatalog { get; init; } = HatCatalog.Shipped;
    private AppearanceRecord? appearance;
    private HeadAttachment? headAttachment;
    private HatCatalog.Item? shownHat;

    /// <summary>Accepted full record; unsupported IDs remain stored while rendering the canonical default.</summary>
    public AppearanceRecord Appearance
    {
        get => appearance ??= AppearanceRecord.FromColor(bodyColor);
        set
        {
            appearance = value;
            BodyColor = value.Color;
        }
    }

    /// <summary>Offline cosmetics preserve the existing unquantized body color.</summary>
    public void SetHat(string? id)
    {
        appearance = Appearance with { Hat = id };
        UpdateHat();
    }

    private void UpdateHat()
    {
        if (headAttachment == null) return;
        var next = CosmeticCatalog.Resolve(appearance?.Hat);
        var changed = !ReferenceEquals(shownHat, next);
        headAttachment.Apply(next, bodyColor, meshDetail);
        shownHat = next;
        if (changed)
        {
            // Remove freed meshes before any subsequent detail change; recover each imported original bias.
            detailMeshes = GameAssets.MeshInstances(model).Concat(GameAssets.MeshInstances(rifle))
                .Concat(headAttachment.Instance is { } hat ? GameAssets.MeshInstances(hat) : [])
                .Where(mesh => (mesh.Layers & RenderLayers.Actors) != 0)
                .Select(mesh => (mesh, mesh.LodBias / meshDetail)).ToArray();
        }
    }
}
