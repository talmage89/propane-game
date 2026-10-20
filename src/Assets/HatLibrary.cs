using Godot;

namespace Propane.Assets;

/// <summary>Explicit exported dependency graph. Defaults apply only to this project's authored items.</summary>
[GlobalClass]
public partial class HatLibrary : Resource
{
    [Export] public string DefaultAuthor { get; set; } = "Propane project";
    [Export] public string DefaultLicense { get; set; } = "LicenseRef-Project-All-Rights-Reserved";
    [Export] public Godot.Collections.Array<HatDefinition> Hats { get; set; } = new();
}
