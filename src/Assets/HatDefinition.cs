using Godot;

namespace Propane.Assets;

/// <summary>Owner-authored item metadata. HatCatalog snapshots it; runtime never mutates imported resources.</summary>
[GlobalClass]
public partial class HatDefinition : Resource
{
    [Export] public string Id { get; set; } = "";
    [Export] public string DisplayName { get; set; } = "";
    [Export] public string Author { get; set; } = "";
    [Export] public string License { get; set; } = "";
    [Export] public string Source { get; set; } = "";
    [Export] public PackedScene? Scene { get; set; }
}
