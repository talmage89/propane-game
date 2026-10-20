using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.Assets;

/// <summary>Immutable main-thread catalog snapshot. Scenes/materials are borrowed for the process lifetime.</summary>
public sealed class HatCatalog
{
    public const string LibraryPath = "res://assets/library.tres";
    private static HatCatalog? shipped;
    public static HatCatalog Shipped => shipped ??= new HatCatalog(ResourceLoader.Load<HatLibrary>(LibraryPath)
        ?? throw new InvalidOperationException("Missing shipped hat library"));

    public sealed record Item(string Id, string DisplayName, string Author, string License, string Source, PackedScene Scene);
    public ImmutableArray<Item> Items { get; }

    public HatCatalog(HatLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = ImmutableArray.CreateBuilder<Item>(library.Hats.Count);
        if (string.IsNullOrWhiteSpace(library.DefaultAuthor) || string.IsNullOrWhiteSpace(library.DefaultLicense))
            throw new InvalidOperationException("Hat library needs authored defaults");
        foreach (var definition in library.Hats)
        {
            if (definition == null || !ValidId(definition.Id) || !ids.Add(definition.Id) || string.IsNullOrWhiteSpace(definition.DisplayName))
                throw new InvalidOperationException("Invalid or duplicate hat identity/name");
            if (definition.Scene == null) throw new InvalidOperationException($"Missing hat scene: {definition.Id}");
            var authored = definition.ResourcePath.Length == 0 || definition.ResourcePath.StartsWith("res://assets/authored/", StringComparison.Ordinal);
            var author = definition.Author.Length == 0 && authored ? library.DefaultAuthor : definition.Author;
            var license = definition.License.Length == 0 && authored ? library.DefaultLicense : definition.License;
            var source = definition.Source.Length == 0 && authored ? "owner export" : definition.Source;
            if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(license) || string.IsNullOrWhiteSpace(source))
                throw new InvalidOperationException($"Missing hat provenance: {definition.Id}");
            var root = definition.Scene.Instantiate<Node3D>();
            try { ValidateScene(root); }
            finally { root.Free(); }
            items.Add(new Item(definition.Id, definition.DisplayName, author, license, source, definition.Scene));
        }
        Items = items.OrderBy(item => item.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    public Item? Resolve(string? id) => id == null ? null : Items.FirstOrDefault(item => item.Id == id);

    public static bool ValidId(string? id) => id is { Length: >= 1 and <= AppearanceCodec.MaxSlotBytes } &&
        (id[0] is >= 'a' and <= 'z' or >= '0' and <= '9') &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    /// <summary>Native counterpart to the source GLB checks; no script, animation, skin or physical consequence.</summary>
    public static void ValidateScene(Node root)
    {
        var surfaces = 0;
        void Visit(Node node)
        {
            if (node is not Node3D spatial || node is not MeshInstance3D && node.GetType() != typeof(Node3D) ||
                node.GetScript().VariantType != Variant.Type.Nil || !spatial.Transform.IsFinite())
            {
                throw new InvalidOperationException($"Unsupported hat node: {node.Name}");
            }

            if (node is MeshInstance3D mesh)
            {
                if (mesh.Skin != null || mesh.Mesh == null) throw new InvalidOperationException("Hat must be an unskinned mesh");
                surfaces += mesh.Mesh.GetSurfaceCount();
                for (var i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
                {
                    if (mesh.Mesh.SurfaceGetMaterial(i) is not BaseMaterial3D material ||
                        material.Transparency is not (BaseMaterial3D.TransparencyEnum.Disabled or BaseMaterial3D.TransparencyEnum.AlphaScissor))
                    {
                        throw new InvalidOperationException("Hat requires opaque/cutout standard materials");
                    }
                }
            }
            foreach (var child in node.GetChildren()) Visit(child);
        }
        Visit(root);
        if (surfaces == 0) throw new InvalidOperationException("Hat scene has no mesh surfaces");
    }
}
