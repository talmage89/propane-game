using System.Collections.Generic;
using Propane.Core;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;
using Propane.World;

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
    /// <summary>Explicit mode clock for time-dependent observation reads; distinct from simulation Now.</summary>
    ModeClock Clock => default;
    /// <summary>Prototype facts are sampled synchronously at the caller's read, without constructing HUD snapshots.</summary>
    RingOutBotView? RingOut => null;
    double? ShortFuseMaxHealth => null;
    DissolveRunBotView? DissolveRun => null;
    bool RoundAlive(int actorId) => false;
    double ShortFuseHealth(int actorId, ModeClock clock) => 0;
    /// <summary>
    /// The mode's own state as this peer sees it now (King's zone), from the rules in the Lab or the mode's snapshot
    /// in a match; null when the mode has none or none has arrived yet.
    /// </summary>
    IModeView? View => null;
    /// <summary>King's zone and clocks without constructing a mode snapshot for each thought.</summary>
    KingBotView? King
    {
        get
        {
            return View is KingState state ? KingBotView.Read(state, state.Elapsed) : null;
        }
    }
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
    /// <summary>The status without its values (its format), for counting what bots do.</summary>
    string StatusKind => Status;
    string NavigationDiagnostics => "";
    float Skill { get; set; }
    /// <summary>Hard opts into score-swing decisions in modes that provide them; other policies ignore it.</summary>
    bool PointsDriven { get => false; set { } }
    /// <summary>Reserves the brain and navigation buffers before the first physics tick.</summary>
    void Prepare(IBotWorld world) { }
    /// <summary>Release cached references to a departing actor before native free, preserving decision clocks.</summary>
    void ForgetActor(PlayerCharacter actor) { }
    void Think(IBotWorld world, float dt);
}
