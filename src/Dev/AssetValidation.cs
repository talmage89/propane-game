using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Propane.Assets;
using Propane.Core;

namespace Propane.Dev;

/// <summary>Load every allowed native resource, independently of Python's metadata/GLB reader.</summary>
public partial class AssetValidation : Node
{
    public override void _Ready()
    {
        try
        {
            using var inventory = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("res://tools/assets/inventory.json")));
            var count = 0;
            foreach (var file in inventory.RootElement.GetProperty("files").EnumerateObject())
            {
                var extension = Path.GetExtension(file.Name);
                if (extension is not (".glb" or ".png" or ".tres")) continue;
                var path = "res://assets/" + file.Name;
                var resource = ResourceLoader.Load(path) ?? throw new InvalidOperationException($"Cannot load {path}");
                if (resource is PackedScene scene)
                {
                    var root = scene.Instantiate();
                    root.Free();
                }
                count++;
            }
            var model = GameAssets.Instantiate(DefaultCharacter.ScenePath);
            try
            {
                DefaultCharacter.Check(model.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single(),
                    model.FindChildren("*", "AnimationPlayer", owned: false).OfType<AnimationPlayer>().Single());
            }
            finally { model.Free(); }
            if (DevArgs.Get("fixture-library") is { } fixturePath)
            {
                var library = ResourceLoader.Load<HatLibrary>(fixturePath) ?? throw new InvalidOperationException("Missing scaffold library");
                if (library.Hats.Count != 1 || library.Hats[0].Id != "dev_probe" || library.Hats[0].DisplayName != "Temporary existing prop" ||
                    library.DefaultAuthor != "Propane project" || library.Hats[0].Author != "" || library.Hats[0].Scene == null)
                {
                    throw new InvalidOperationException("Scaffold resource properties differ after native loading");
                }
                var root = library.Hats[0].Scene!.Instantiate();
                try { HatCatalog.ValidateScene(root); }
                finally { root.Free(); }
                GD.Print("[assets] PASS scaffold hats=1");
            }
            GD.Print($"[assets] PASS native resources={count} hats={HatCatalog.Shipped.Items.Length}");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[assets] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }
}
