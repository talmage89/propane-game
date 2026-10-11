using Propane.Core;
using Propane.Net;

namespace Propane.Player;

/// <summary>This machine presents snapshots for a peer-owned actor instead of simulating device intent.</summary>
public sealed class ReplicatedIntent(Tuning tuning) : IPlayerInputSource
{
    public SnapshotBuffer Snapshots { get; } = new(tuning);
    public PlayerIntent Read() => default;
    public void Apply(PlayerCharacter character, double now)
    {
        if (Snapshots.HasData) character.SetRemoteState(Snapshots.Sample(now));
    }
}
