using System;
using System.Collections.Generic;
using System.Linq;

namespace Propane.Modes;

/// <summary>How many of each spot's own tanks still stand intact on it, and how many tanks are loose in the world.</summary>
public sealed record PilesStocked(IReadOnlyList<int> Intact, int LiveTanks) : AcceptedEvent;

/// <summary>A pile was registered at this spot, its first stock or a refill.</summary>
public sealed record PileFilled(int Spot) : AcceptedEvent;

/// <summary>The world authority found no clear ground for this spot's refill (or the request was abandoned).</summary>
public sealed record PileUnplaced(int Spot) : AcceptedEvent;

/// <summary>Stock this spot now: the world is new and empty, so the runner places the pile itself.</summary>
public sealed record StockPile(int Spot) : ModeCommand;

/// <summary>Refill this spot: the runner asks the world authority for clear ground beside it, as King does.</summary>
public sealed record RefillPile(int Spot) : ModeCommand;

/// <summary>
/// Where a lobby world's piles stand and where its actors arrive, from its dials: pure layout. Piles stand evenly on
/// one ring; actors arrive on an inner ring, each at its allocation slot's own angle (golden-angle steps, so any
/// number of arrivals spread out and the same slot always arrives at the same place on every peer), facing the middle.
/// </summary>
public static class LobbyLayout
{
    private const double GoldenAngle = 2.399963229728653;

    public static ModeSpot[] Spots(int piles, double ring) => Enumerable.Range(0, Math.Max(0, piles))
        .Select(i => i * Math.Tau / piles + Math.PI / 2)
        .Select(angle => new ModeSpot(Math.Cos(angle) * ring, 0, Math.Sin(angle) * ring)).ToArray();

    /// <summary>An actor's arrival point (on the floor) and the yaw that faces the middle, by allocation slot.</summary>
    public static (ModeSpot At, double Yaw) Arrival(int allocationSlot, double ring)
    {
        // Half a pile step off the pile ring's first spot, so nobody arrives looking straight down a gap.
        var angle = allocationSlot * GoldenAngle - Math.PI / 2 + 0.35;
        var at = new ModeSpot(Math.Cos(angle) * ring, 0, Math.Sin(angle) * ring);
        return (at, Math.Atan2(at.X, at.Z));
    }

    /// <summary>Half the side of the square the world is laid out in: the pile ring and room to roam around it.</summary>
    public static double Extent(double pileRing) => pileRing + 30;
}

/// <summary>
/// The lobby world: no score, no spills and no end. Everything that changes the world is open while it runs. It keeps
/// each pile spot stocked: at the start every spot is stocked at once, and when none of a spot's own tanks stands
/// intact on it any longer, a fresh pile is asked for after <c>LobbyRefillDelay</c>, unless its
/// <c>LobbyPileCount</c> tanks, with those of the piles already on their way, would take the world past
/// <c>LobbyTankLimit</c> live tanks (so venting strays cannot pile up without bound). Pure: the runner reports the stock.
/// </summary>
public sealed class LobbyRules : IModeRules
{
    public const string LobbyRefillDelay = nameof(LobbyRefillDelay);
    public const string LobbyTankLimit = nameof(LobbyTankLimit);
    public const string LobbyPileCount = nameof(LobbyPileCount);
    public static readonly IReadOnlyList<string> Tuners = new[] { LobbyRefillDelay, LobbyTankLimit, LobbyPileCount };

    private readonly List<ModeActor> roster = new();
    private readonly List<ModeSpot> spots = new();
    private double?[] emptySince = Array.Empty<double?>();
    private bool[] pending = Array.Empty<bool>();
    private ModeTuners tuners = ModeTuners.Empty;

    public IReadOnlyList<ModeSpot> Spots => spots;
    /// <summary>Whether a spot's stock or refill is on its way.</summary>
    public bool Pending(int spot) => pending[spot];

    public void Initialize(ModeSetup setup)
    {
        roster.Clear();
        roster.AddRange(setup.Roster);
        spots.Clear();
        spots.AddRange(setup.World.Spots ?? Array.Empty<ModeSpot>());
        emptySince = new double?[spots.Count];
        pending = new bool[spots.Count];
        tuners = setup.Tuners;
    }

    public IReadOnlyList<ModeCommand> Handle(AcceptedEvent accepted, ModeClock clock)
    {
        switch (accepted)
        {
            case ActorJoined joined:
                roster.RemoveAll(a => a.ActorId == joined.Actor.ActorId);
                roster.Add(joined.Actor);
                break;
            case ActorLeft left:
                var index = roster.FindIndex(a => a.ActorId == left.ActorId);
                if (index >= 0) roster[index] = roster[index] with { Present = false };
                break;
            case TunersChanged changed:
                tuners = changed.Tuners;
                break;
            case PhaseEntered { Phase: ModePhase.Round }:
                for (var i = 0; i < spots.Count; i++) pending[i] = true;
                return Enumerable.Range(0, spots.Count).Select(i => (ModeCommand)new StockPile(i)).ToArray();
            case PhaseEntered { Phase: ModePhase.RoundEnd }:
                return new ModeCommand[] { new EndMatch() };
            case PilesStocked stocked when clock.Phase == ModePhase.Round:
                return Refills(stocked, clock.Now);
            case PileFilled { Spot: var filled } when Valid(filled):
                pending[filled] = false;
                emptySince[filled] = null;
                break;
            case PileUnplaced { Spot: var unplaced } when Valid(unplaced):
                // Try again after another delay: whoever stood there may have moved on.
                pending[unplaced] = false;
                emptySince[unplaced] = clock.Now;
                break;
            case SpillRequested spill:
                return new ModeCommand[] { new SpillCanceled(spill.Ticket, SpillCancel.Closed) };
        }
        return Array.Empty<ModeCommand>();
    }

    private ModeCommand[] Refills(PilesStocked stocked, double now)
    {
        var refills = new List<ModeCommand>();
        var delay = tuners.Float(LobbyRefillDelay);
        // Every refill brings a whole pile, and piles on their way are not live yet: they count against the limit too.
        var pile = Math.Max(1, tuners.Int(LobbyPileCount));
        var room = tuners.Int(LobbyTankLimit) - stocked.LiveTanks - pending.Count(p => p) * pile;
        for (var i = 0; i < spots.Count && i < stocked.Intact.Count; i++)
        {
            if (stocked.Intact[i] > 0) { emptySince[i] = null; continue; }
            if (pending[i]) continue;
            emptySince[i] ??= now;
            if (room < pile || now - emptySince[i] < delay) continue;
            pending[i] = true;
            room -= pile;
            refills.Add(new RefillPile(i));
        }
        return refills.ToArray();
    }

    private bool Valid(int spot) => spot >= 0 && spot < spots.Count;

    public ModeState Snapshot() => new(1, roster.Select(a => new ModeStanding(a.ActorId, a.Name, 0, a.Present)).ToArray(), Array.Empty<ModeReport>());

    public bool Permits(ModeGate gate, ModeClock clock) => gate == ModeGate.World && clock.PlayOpen;
}

/// <summary>The lobby world's HUD: nothing of the mode's own. The lobby card and name tags are the world's.</summary>
public sealed class LobbyHud : IModeHudPresenter
{
    public ModeHud Present(ModeHudInput input) => ModeHud.Empty;
}
