using System.Collections.Generic;
using System.Linq;

namespace Propane.Modes;

/// <summary>Copied actor facts; a projection never borrows a rule's mutable collections.</summary>
public readonly record struct RingOutActor(int ActorId, string Name, bool Alive, int? Place, int Credits);

/// <summary>
/// Ring Out at an explicit mode clock read. Owns copied actor/elimination arrays and borrows only the immutable
/// slab plan. It can outlive its runner without retaining rules, subscriptions or native resources.
/// </summary>
public sealed record RingOutState(RingOutPlan Plan, bool Over, int AliveCount, double Elapsed,
    IReadOnlyList<RingOutActor> Actors, IReadOnlyList<ActorEliminated> Eliminations) : IModeView
{
    public int? Place(int actor) => Actors.FirstOrDefault(a => a.ActorId == actor).Place;
    public int Credits(int actor) => Actors.FirstOrDefault(a => a.ActorId == actor).Credits;
    public string Name(int actor) => Actors.FirstOrDefault(a => a.ActorId == actor).Name ?? $"#{actor}";
}

/// <summary>Health and shield values are sampled at the projection's explicit mode clock, including regeneration.</summary>
public readonly record struct ShortFuseActor(int ActorId, string Name, bool Started, bool Alive, double Health,
    bool Shielded, int Wins, int Kills, int SpectateTarget);

/// <summary>Owned plain arrays for one Short Fuse read; retired by ordinary collection after its consumers release it.</summary>
public sealed record ShortFuseState(bool Over, uint Round, int WinsToMatch, double MaxHealth, int AliveCount,
    int? MatchWinner, IReadOnlyList<ShortFuseActor> Actors, IReadOnlyList<ActorDied> RoundDeaths,
    IReadOnlyList<RoundWon> Rounds) : IModeView
{
    public ShortFuseActor Actor(int actor) => Actors.FirstOrDefault(a => a.ActorId == actor);
    public int Wins(int actor) => Actor(actor).Wins;
    public int Kills(int actor) => Actor(actor).Kills;
    public string Name(int actor) => Actor(actor).Name ?? $"#{actor}";
}

/// <summary>Dissolve Run's copied values and closed chains at an explicit mode clock read.</summary>
public sealed record DissolveRunState(int Score, int Meters, int Tanks, int Bonus, int BestChain, double Gap,
    int OpenChain, bool Caught, bool Over, double Elapsed, IReadOnlyList<ChainClosed> Chains) : IModeView;

/// <summary>Hot observations are values, without snapshots, live rules or callbacks into them.</summary>
public readonly record struct RingOutBotView(RingOutPlan Plan, double Elapsed);
public readonly record struct DissolveRunBotView(bool Caught, double Front);
