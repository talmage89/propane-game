using System.Collections.Generic;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// Actor-specific observations. A brain needs no connection, camera or Match facade. Play state, time and scores
/// come from the mode's state as this peer knows it; the rest are world facts.
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
    IEnumerable<PlayerCharacter> RemoteBodies { get; }
    IReadOnlyList<AmmoPickup> Pickups { get; }
    IEnumerable<PropaneTank> LiveTanks { get; }
    /// <summary>
    /// The mode's rules as this peer runs them, for policies that read mode-specific state (a boundary), or null
    /// where the peer does not run them. Only the Lab's prototype runner supplies them.
    /// </summary>
    IModeRules? Rules => null;
    /// <summary>The mode clock to read <see cref="Rules"/> with.</summary>
    ModeClock Clock => default;
    /// <summary>
    /// The mode's own state as this peer sees it now (King's zone), from the rules in the Lab or the mode's snapshot
    /// in a match; null when the mode has none or none has arrived yet.
    /// </summary>
    IModeView? View => null;
}

/// <summary>
/// A mode's bot goal policy: it picks goals from mode state and world facts, and the shared navigation
/// (<see cref="Navigation.BotNavigator"/>) and aiming (<see cref="BotAim"/>) turn them into intent.
/// </summary>
public interface IBotGoalPolicy : IPlayerInputSource, System.IDisposable
{
    string Status { get; }
    float Skill { get; set; }
    void Think(IBotWorld world, float dt);
}
