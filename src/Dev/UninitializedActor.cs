using Propane.Player;

namespace Propane.Dev;

/// <summary>Engine callbacks do not propagate managed Ready exceptions to AddChild. Probe that handshake explicitly.</summary>
public partial class UninitializedActor : PlayerCharacter
{
    public override void _Ready() { }
}
