using System;
using System.Collections.Generic;
using System.Linq;
using Propane.Bots;
using Propane.Core;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Modes;

/// <summary>
/// Supply and ammo policy: how a mode's suburb is stocked for a player count, whether rifles carry limited
/// ammunition, and whether ammo cans appear.
/// </summary>
public sealed record ModeSupply(Func<Tuning, int, SuburbSettings> Suburb, bool LimitedAmmo, bool Pickups);

/// <summary>
/// Everything that makes a mode: its rules, world source, supply, round timer, HUD presenter, bot goal policy, the
/// tuners its rules copy and its envelope schema version. A new mode adds a definition here and its payload codecs
/// in <c>ModeSchemas</c>; the outer messages, the runner and the factory do not change.
/// </summary>
public sealed record ModeDefinition(
    byte Id,
    string Name,
    ushort SchemaVersion,
    Func<IModeRules> CreateRules,
    ModeWorldSource World,
    ModeSupply Supply,
    RoundTimer Timer,
    Func<IModeHudPresenter> CreateHud,
    Func<ulong?, IBotGoalPolicy>? CreateBot,
    IReadOnlyList<string> Tuners)
{
    /// <summary>Copies the tuners the rules read from a live tuning instance; rules never see the resource.</summary>
    public ModeTuners CopyTuners(Tuning tuning)
    {
        var values = new Dictionary<string, double>();
        foreach (var name in Tuners)
        {
            var property = typeof(Tuning).GetProperty(name) ?? throw new InvalidOperationException($"no tuner {name}");
            values[name] = Convert.ToDouble(property.GetValue(tuning), System.Globalization.CultureInfo.InvariantCulture);
        }
        return new ModeTuners(values);
    }
}

public static class ModeRegistry
{
    public const byte SandboxId = 1;
    public const byte TankBankId = 2;

    public static readonly ModeDefinition Sandbox = new(SandboxId, "Sandbox", 1, () => new SandboxRules(), ModeWorldSource.Void,
        new ModeSupply((tuning, _) => SuburbPlans.SinglePlayer(tuning), LimitedAmmo: false, Pickups: false),
        RoundTimer.None, () => new SandboxHud(), null, Array.Empty<string>());

    public static readonly ModeDefinition TankBank = new(TankBankId, "Tank Bank", 1, () => new TankBankRules(), ModeWorldSource.SuburbPlan,
        new ModeSupply(SuburbPlans.Match, LimitedAmmo: true, Pickups: true), RoundTimer.Countdown, () => new TankBankHud(),
        seed => new BankingBot(seed), TankBankRules.Tuners);

    public const byte RingOutId = 3;
    public const byte KingId = 4;

    /// <summary>Lab prototype: sumo on a dissolving slab. Unlimited ammo, no cans; the round counts up until one stands.</summary>
    public static readonly ModeDefinition RingOut = new(RingOutId, "Ring Out", 1, () => new RingOutRules(), ModeWorldSource.Void,
        new ModeSupply((tuning, _) => SuburbPlans.SinglePlayer(tuning), LimitedAmmo: false, Pickups: false), RoundTimer.CountUp,
        () => new RingOutHud(), seed => new RingOutBot(seed), RingOutRules.Tuners);

    /// <summary>Lab prototype: hold a moving zone to mint banked tanks, with Tank Bank's bank and spills.</summary>
    public static readonly ModeDefinition King = new(KingId, "King of the Cul-de-sac", 1, () => new KingRules(), ModeWorldSource.Void,
        new ModeSupply((tuning, _) => SuburbPlans.SinglePlayer(tuning), LimitedAmmo: false, Pickups: false), RoundTimer.Countdown,
        () => new KingHud(), seed => new KingBot(seed), KingRules.Tuners);

    public const byte ShortFuseId = 5;

    /// <summary>
    /// Lab prototype: last one standing with health, a new suburb each round. Unlimited ammo, no cans; the suburb is
    /// stocked for the players (on a Lab-sized slab), who start around its rim rather than at its spawn points.
    /// </summary>
    public static readonly ModeDefinition ShortFuse = new(ShortFuseId, "Short Fuse", 1, () => new ShortFuseRules(), ModeWorldSource.SuburbPlan,
        new ModeSupply((tuning, players) => SuburbPlans.Match(tuning, players) with { MapSize = tuning.ShortFuseMapSize, PlayerSpawns = 0, AmmoSpots = 0 },
            LimitedAmmo: false, Pickups: false),
        RoundTimer.Countdown, () => new ShortFuseHud(), seed => new ShortFuseBot(seed), ShortFuseRules.Tuners);

    /// <summary>
    /// Modes tried only in the Lab. They are not in <see cref="All"/>: no lobby, MatchStart or envelope schema names
    /// them, so they change nothing a match shares.
    /// </summary>
    public static IReadOnlyList<ModeDefinition> Prototypes { get; } = new[] { RingOut, King, ShortFuse };

    private static readonly List<ModeDefinition> registered = new() { Sandbox, TankBank };
    public static IReadOnlyList<ModeDefinition> All => registered.AsReadOnly();

    public static ModeDefinition? Find(byte id, ushort schemaVersion) =>
        registered.FirstOrDefault(mode => mode.Id == id && mode.SchemaVersion == schemaVersion);

    /// <summary>Test seam: assertion suites register synthetic modes beside the shipped two.</summary>
    internal static void Register(ModeDefinition mode)
    {
        registered.RemoveAll(m => m.Id == mode.Id && m.SchemaVersion == mode.SchemaVersion);
        registered.Add(mode);
    }
}
