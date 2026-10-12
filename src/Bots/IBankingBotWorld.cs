using System.Collections.Generic;
using Propane.Core;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>Actor-specific observations. A brain needs no connection, camera or Match facade.</summary>
public interface IBankingBotWorld
{
    WorldContext World { get; }
    PlayerCharacter Player { get; }
    bool IsPlaying { get; }
    float TimeLeft { get; }
    int MyScore { get; }
    IReadOnlyDictionary<int, int> Scores { get; }
    double Now { get; }
    double LastHitTime { get; }
    int LastHitBy { get; }
    IEnumerable<PlayerCharacter> RemoteBodies { get; }
    IReadOnlyList<AmmoPickup> Pickups { get; }
    IEnumerable<PropaneTank> LiveTanks { get; }
}
