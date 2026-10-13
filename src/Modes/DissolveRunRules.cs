using System;
using System.Collections.Generic;
using System.Linq;

namespace Propane.Modes;

/// <summary>
/// A tank went up in Dissolve Run: who set it off, and the chain it belongs to (a direct shot starts a chain; every
/// tank its blasts set off carries the same id).
/// </summary>
public sealed record RunTankDetonated(int ActorId, uint Chain) : AcceptedEvent;

/// <summary>The front caught the runner, <paramref name="Meters"/> along the run, with the front at <paramref name="Front"/>.</summary>
public sealed record RunCaught(int ActorId, int Meters, double Front) : ModeCommand;

/// <summary>A chain has had no new tank for the chain window: it scored <paramref name="Bonus"/> (n squared, or nothing below the minimum).</summary>
public sealed record ChainClosed(uint Chain, int Tanks, int Bonus) : ModeCommand;

/// <summary>
/// Dissolve Run: a solo run down an endless street that dissolves behind you. Everything is along world +X from the
/// run's start (the world's first spot). The front starts <c>DissolveFrontStartGap</c> behind the start and moves at a
/// speed that ramps from <c>DissolveFrontSpeed</c> toward <c>DissolveFrontMaxSpeed</c> with time constant
/// <c>DissolveFrontRampSeconds</c>; each detonated tank pushes it back <c>DissolveFrontPush</c>. A sample behind the
/// front is caught: the runner falls for <c>DissolveFallSeconds</c>, then the round is over. Score is the meters run
/// (the furthest sample past the start), plus <c>DissolveTankPoints</c> per tank, plus n squared for each closed chain
/// of n at least <c>DissolveChainMin</c>. Pure: the front is integrated from the mode clock, so a runner never sends it.
/// </summary>
public sealed class DissolveRunRules : IModeRules
{
    public const string DissolveFrontStartGap = nameof(DissolveFrontStartGap);
    public const string DissolveFrontSpeed = nameof(DissolveFrontSpeed);
    public const string DissolveFrontMaxSpeed = nameof(DissolveFrontMaxSpeed);
    public const string DissolveFrontRampSeconds = nameof(DissolveFrontRampSeconds);
    public const string DissolveFrontPush = nameof(DissolveFrontPush);
    public const string DissolveFallSeconds = nameof(DissolveFallSeconds);
    public const string DissolveTankPoints = nameof(DissolveTankPoints);
    public const string DissolveChainMin = nameof(DissolveChainMin);
    public const string DissolveChainWindow = nameof(DissolveChainWindow);
    public static readonly IReadOnlyList<string> Tuners = new[]
    {
        DissolveFrontStartGap, DissolveFrontSpeed, DissolveFrontMaxSpeed, DissolveFrontRampSeconds, DissolveFrontPush,
        DissolveFallSeconds, DissolveTankPoints, DissolveChainMin, DissolveChainWindow,
    };

    private readonly List<ModeActor> actors = new();
    private readonly Dictionary<uint, (int Tanks, double Last)> open = new();
    private readonly List<ChainClosed> closed = new();
    private ModeTuners tuners = ModeTuners.Empty;
    private double? roundStart;
    private double lastTime;
    private uint round = 1;

    /// <summary>World X where the run starts; meters count from here.</summary>
    public double StartX { get; private set; }
    /// <summary>World X of the dissolve front: everything behind it is gone.</summary>
    public double Front { get; private set; }
    /// <summary>The furthest world X the runner has reached.</summary>
    public double Best { get; private set; }
    /// <summary>The runner's world X at the last sample.</summary>
    public double Position { get; private set; }
    public int Tanks { get; private set; }
    public int Bonus { get; private set; }
    public int BestChain { get; private set; }
    public int Pushes { get; private set; }
    public double? CaughtAt { get; private set; }
    public bool Caught => CaughtAt != null;
    public bool Over { get; private set; }
    public int Meters => (int)Math.Floor(Math.Max(0, Best - StartX));
    public int Score => Meters + tuners.Int(DissolveTankPoints) * Tanks + Bonus;
    /// <summary>How far ahead of the front the runner is.</summary>
    public double Gap => Position - Front;
    /// <summary>The largest chain still open, or zero.</summary>
    public int OpenChain => open.Count == 0 ? 0 : open.Values.Max(c => c.Tanks);
    public IReadOnlyList<ChainClosed> Chains => closed;
    public double Elapsed(ModeClock clock) => roundStart is { } start ? Math.Max(0, clock.Now - start) : 0;

    /// <summary>The front's speed at a round time: an exponential approach from the start speed to the maximum.</summary>
    public double Speed(double elapsed)
    {
        var start = tuners.Float(DissolveFrontSpeed);
        var max = tuners.Float(DissolveFrontMaxSpeed);
        var tau = Math.Max(1e-3, tuners.Float(DissolveFrontRampSeconds));
        return max + (start - max) * Math.Exp(-Math.Max(0, elapsed) / tau);
    }

    public void Initialize(ModeSetup setup)
    {
        actors.Clear(); open.Clear(); closed.Clear();
        actors.AddRange(setup.Roster);
        tuners = setup.Tuners;
        StartX = setup.World.Spots is { Count: > 0 } spots ? spots[0].X : 0;
        roundStart = null;
        Reset();
    }

    private void Reset()
    {
        open.Clear(); closed.Clear();
        Front = StartX - tuners.Float(DissolveFrontStartGap);
        Best = Position = StartX;
        Tanks = Bonus = BestChain = Pushes = 0;
        CaughtAt = null;
        Over = false;
    }

    public IReadOnlyList<ModeCommand> Handle(AcceptedEvent accepted, ModeClock clock) => accepted switch
    {
        ActorsSampled sampled => Sample(sampled, clock),
        RunTankDetonated tank => Detonated(tank, clock),
        TunersChanged changed => Retune(changed.Tuners),
        PhaseEntered phase => Enter(phase),
        ActorLeft left => Leave(left.ActorId, clock),
        SpillRequested spill => new ModeCommand[] { new SpillCanceled(spill.Ticket, SpillCancel.Closed) },
        _ => Array.Empty<ModeCommand>(),
    };

    public bool Permits(ModeGate gate, ModeClock clock) => gate == ModeGate.Pickup ? clock.Phase == ModePhase.Round && !Caught : clock.PlayOpen && !Caught;

    public ModeState Snapshot()
    {
        var runner = actors.FirstOrDefault();
        var standings = runner == null ? Array.Empty<ModeStanding>() : new[] { new ModeStanding(runner.ActorId, runner.Name, Score, runner.Present) };
        var report = new[]
        {
            new ModeReport(ModeReportKind.Audit, $"dissolve run meters={Meters} tanks={Tanks} chains={closed.Count} bonus={Bonus} " +
                $"best_chain={BestChain} pushes={Pushes} score={Score} caught={Caught} front={Front:0.0}"),
            new ModeReport(ModeReportKind.Standings, string.Join(" ", standings.Select(s => $"{s.ActorId}={s.Score}"))),
            new ModeReport(ModeReportKind.Summary, $"run over, {Meters} m, {Tanks} tanks, best chain {BestChain}, score {Score}"),
        };
        return new ModeState(round, standings, report);
    }

    // ------------------------------------------------------------------ Events

    /// <summary>Moves the front to this clock's time: the exact integral of the ramped speed since the last step.</summary>
    private void Advance(ModeClock clock)
    {
        if (roundStart is not { } start || clock.Now <= lastTime) return;
        var max = tuners.Float(DissolveFrontMaxSpeed);
        var initial = tuners.Float(DissolveFrontSpeed);
        var tau = Math.Max(1e-3, tuners.Float(DissolveFrontRampSeconds));
        var t0 = Math.Max(0, lastTime - start);
        var t1 = Math.Max(0, clock.Now - start);
        Front += max * (t1 - t0) + (initial - max) * tau * (Math.Exp(-t0 / tau) - Math.Exp(-t1 / tau));
        lastTime = clock.Now;
    }

    private ModeCommand[] Sample(ActorsSampled sampled, ModeClock clock)
    {
        if (!clock.PlayOpen || roundStart == null || Over) return Array.Empty<ModeCommand>();
        Advance(clock);
        var commands = new List<ModeCommand>();
        var runner = actors.FirstOrDefault()?.ActorId;
        foreach (var sample in sampled.Samples)
        {
            if (sample.ActorId != runner || Caught) continue;
            Position = sample.X;
            Best = Math.Max(Best, sample.X);
            if (sample.X < Front)
            {
                CaughtAt = clock.Now;
                commands.AddRange(CloseChains(clock, all: true));
                commands.Add(new RunCaught(sample.ActorId, Meters, Front));
            }
        }
        if (!Caught) commands.AddRange(CloseChains(clock, all: false));
        if (Caught && clock.Now - CaughtAt!.Value >= tuners.Float(DissolveFallSeconds))
        {
            Over = true;
            commands.Add(new RoundOver());
        }
        if (commands.Count > 0 && runner is { } id) commands.Add(new ScoreChanged(id, Score, 0));
        return commands.ToArray();
    }

    private IEnumerable<ModeCommand> CloseChains(ModeClock clock, bool all)
    {
        var window = tuners.Float(DissolveChainWindow);
        foreach (var (id, chain) in open.Where(c => all || clock.Now - c.Value.Last > window).OrderBy(c => c.Key).ToArray())
        {
            open.Remove(id);
            var bonus = chain.Tanks >= tuners.Int(DissolveChainMin) ? chain.Tanks * chain.Tanks : 0;
            Bonus += bonus;
            BestChain = Math.Max(BestChain, chain.Tanks);
            var done = new ChainClosed(id, chain.Tanks, bonus);
            closed.Add(done);
            yield return done;
        }
    }

    private ModeCommand[] Detonated(RunTankDetonated tank, ModeClock clock)
    {
        if (!clock.PlayOpen || roundStart == null || Caught || Over) return Array.Empty<ModeCommand>();
        Advance(clock);
        Tanks++;
        Pushes++;
        Front -= tuners.Float(DissolveFrontPush);
        var chain = open.TryGetValue(tank.Chain, out var known) ? known.Tanks : 0;
        open[tank.Chain] = (chain + 1, clock.Now);
        BestChain = Math.Max(BestChain, chain + 1);
        return actors.FirstOrDefault() is { } runner ? new ModeCommand[] { new ScoreChanged(runner.ActorId, Score, 0) } : Array.Empty<ModeCommand>();
    }

    private ModeCommand[] Leave(int actorId, ModeClock clock)
    {
        if (actors.FirstOrDefault()?.ActorId != actorId || Over) return Array.Empty<ModeCommand>();
        Over = true;
        return CloseChains(clock, all: true).Append(new RoundOver()).ToArray();
    }

    private ModeCommand[] Retune(ModeTuners changed)
    {
        tuners = changed;
        return Array.Empty<ModeCommand>();
    }

    private ModeCommand[] Enter(PhaseEntered phase)
    {
        round = phase.Round.RoundId;
        switch (phase.Phase)
        {
            case ModePhase.Round:
                roundStart = lastTime = phase.Round.StartsAt;
                Reset();
                return new ModeCommand[] { new PublishSnapshot() };
            case ModePhase.RoundEnd:
                Over = true;
                return new ModeCommand[] { new EndMatch() };
            default:
                return Array.Empty<ModeCommand>();
        }
    }
}
