using System;
using Godot;
using Propane.Assets;

namespace Propane.Dev;

/// <summary>Unsaved Dev-only primitive. No cosmetic art or item resource is written to disk or exported.</summary>
internal static class AssetFixture
{
    public static PackedScene Scene()
    {
        var root = new Node3D { Name = "DevPrimitive" };
        var mesh = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(.24f, .15f, .24f), Material = new StandardMaterial3D { ResourceName = "Tint_Player", AlbedoColor = Colors.White } },
            Position = new Vector3(0, .22f, 0),
        };
        root.AddChild(mesh);
        mesh.Owner = root;
        var scene = new PackedScene();
        try
        {
            if (scene.Pack(root) != Error.Ok) throw new InvalidOperationException("Cannot pack Dev primitive");
            return scene;
        }
        catch { scene.Dispose(); throw; }
        finally { root.Free(); }
    }

    public static HatCatalog Catalog(PackedScene scene)
    {
        using var library = new HatLibrary();
        library.Hats.Add(new HatDefinition { Id = "dev_probe", DisplayName = "Dev primitive", Scene = scene });
        return new HatCatalog(library);
    }
}
