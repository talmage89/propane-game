using System.Collections.Generic;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// Actor-specific observations. A brain needs no connection, camera or Match facade. Play state, time and scores
/// come from the mode's state as this peer knows it; the rest are world facts. Lists are indexed, never enumerated
/// through their interface, so a brain's tick allocates nothing.
/// </summary>
public interface IBotWorld
{
    WorldContext World { get; }
    PlayerCharacter Player { get; }
    bool IsPlaying { get; }
    float TimeLeft { get; }
    int MyScore { get; }
    /// <summary>Primary scores keyed by actor id, including other actors owned by this peer.</summary>
    IReadOnlyDictionary<int, int> Scores { get; }
    double Now { get; }
    double LastHitTime { get; }
    /// <summary>Actor id of the last shooter, independent of its owning peer.</summary>
    int LastHitBy { get; }
    /// <summary>Every other actor in play.</summary>
    IReadOnlyList<PlayerCharacter> RemoteBodies { get; }
    IReadOnlyList<AmmoPickup> Pickups { get; }
    /// <summary>What every bot of this peer in this world shares this tick: tanks, hazards, navigation.</summary>
    BotServices Services { get; }
    /// <summary>The tanks in play this tick.</summary>
    IReadOnlyList<PropaneTank> LiveTanks => Services.Tanks;
    /// <summary>
    /// The mode's rules as this peer runs them, for policies that read mode-specific state (a boundary, a zone), or
    /// null where the peer does not run them. Only the Lab's prototype runner supplies them so far.
    /// </summary>
    IModeRules? Rules => null;
    /// <summary>The mode clock to read <see cref="Rules"/> with.</summary>
    ModeClock Clock => default;
}

/// <summary>
/// A mode's bot goal policy: it picks goals from mode state and world facts, and the shared navigation
/// (<see cref="Navigation.BotNavigator"/>) and aiming (<see cref="BotAim"/>) turn them into intent. Its owner calls
/// <see cref="Think"/> every physics tick, after starting the tick on the world's <see cref="BotServices"/>.
/// </summary>
public interface IBotGoalPolicy : IPlayerInputSource, System.IDisposable
{
    /// <summary>What the bot is doing, for logs. Formatted when read, so a tick costs no string.</summary>
    string Status { get; }
    string NavigationDiagnostics => "";
    float Skill { get; set; }
    /// <summary>Reserves the brain and navigation buffers before the first physics tick.</summary>
    void Prepare(IBotWorld world) { }
    void Think(IBotWorld world, float dt);
}
