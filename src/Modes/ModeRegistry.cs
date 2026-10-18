using System;
using System.Collections.Generic;
using System.Linq;
using Propane.Bots;
using Propane.Core;
using Propane.Net;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Modes;

public static class ModeRegistry
{
    public const byte SandboxId = ModeIds.Sandbox;
    public const byte TankBankId = ModeIds.TankBank;

    public static readonly ModeDefinition Sandbox = new(SandboxId, "Sandbox", 1, () => new SandboxRules(), ModeWorldSource.Void,
        new ModeSupply((tuning, _) => SuburbPlans.SinglePlayer(tuning), LimitedAmmo: false, Pickups: false),
        RoundTimer.None, () => new SandboxHud(), null, Array.Empty<string>());

    public static readonly ModeDefinition TankBank = new(TankBankId, "Tank Bank", ModeIds.TankBankSchema, () => new TankBankRules(), ModeWorldSource.SuburbPlan,
        new ModeSupply(SuburbPlans.Match, LimitedAmmo: true, Pickups: true), RoundTimer.Countdown, () => new TankBankHud(),
        seed => new BankingBot(seed), Array.AsReadOnly(TankBankRules.Tuners.ToArray()));

    public const byte RingOutId = ModeIds.RingOut;
    public const byte KingId = ModeIds.King;

    /// <summary>Lab prototype: sumo on a dissolving slab. Unlimited ammo, no cans; the round counts up until one stands.</summary>
    public static readonly ModeDefinition RingOut = new(RingOutId, "Ring Out", 1, () => new RingOutRules(), ModeWorldSource.Void,
        new ModeSupply((tuning, _) => SuburbPlans.SinglePlayer(tuning), LimitedAmmo: false, Pickups: false), RoundTimer.CountUp,
        () => new RingOutHud(), seed => new RingOutBot(seed), Array.AsReadOnly(RingOutRules.Tuners.ToArray()));

    /// <summary>
    /// Hold a moving zone to mint banked tanks, with Tank Bank's bank and spills. A lobby can choose it; the Lab still
    /// tries it against its bots. It plays a match's suburb, stocked for the players, with unlimited ammo and no cans.
    /// </summary>
    public static readonly ModeDefinition King = new(KingId, "King of the Cul-de-sac", 1, () => new KingRules(), ModeWorldSource.SuburbPlan,
        new ModeSupply((tuning, players) => SuburbPlans.Match(tuning, players) with { AmmoSpots = 0 }, LimitedAmmo: false, Pickups: false),
        RoundTimer.Countdown, () => new KingHud(), seed => new KingBot(seed), Array.AsReadOnly(KingRules.Tuners.ToArray()))
    { SamplesActors = true };

    public const byte ShortFuseId = ModeIds.ShortFuse;

    /// <summary>
    /// Lab prototype: last one standing with health, a new suburb each round. Unlimited ammo, no cans; the suburb is
    /// stocked for the players (on a Lab-sized slab), who start around its rim rather than at its spawn points.
    /// </summary>
    public static readonly ModeDefinition ShortFuse = new(ShortFuseId, "Short Fuse", 1, () => new ShortFuseRules(), ModeWorldSource.SuburbPlan,
        new ModeSupply((tuning, players) => SuburbPlans.Match(tuning, players) with { MapSize = tuning.ShortFuseMapSize, PlayerSpawns = 0, AmmoSpots = 0 },
            LimitedAmmo: false, Pickups: false),
        RoundTimer.Countdown, () => new ShortFuseHud(), seed => new ShortFuseBot(seed), Array.AsReadOnly(ShortFuseRules.Tuners.ToArray()));

    public const byte DissolveRunId = ModeIds.DissolveRun;

    /// <summary>
    /// Lab prototype: a solo run down a street that builds itself ahead and dissolves behind. Its world is a segment
    /// stream (<c>SegmentWorldSource</c>), not one plan; rifles carry limited ammo and the segments hold ammo cans.
    /// </summary>
    public static readonly ModeDefinition DissolveRun = new(DissolveRunId, "Dissolve Run", 1, () => new DissolveRunRules(), ModeWorldSource.SuburbPlan,
        new ModeSupply((tuning, _) => SuburbPlans.SinglePlayer(tuning), LimitedAmmo: true, Pickups: true), RoundTimer.CountUp,
        () => new DissolveRunHud(), seed => new DissolveRunBot(seed), Array.AsReadOnly(DissolveRunRules.Tuners.ToArray()));

    public const byte LobbyWorldId = ModeIds.Lobby;

    /// <summary>
    /// The lobby world: everyone in a waiting lobby roams the void together, shooting and throwing tanks for nothing.
    /// No score and no end; its piles refill and rifles never run dry (both tuners). A server runs it while the lobby
    /// waits; it is not a lobby choice. Lobby bots roam it with Tank Bank's brain.
    /// </summary>
    public static readonly ModeDefinition LobbyWorld = new(LobbyWorldId, "Lobby", 1, () => new LobbyRules(), ModeWorldSource.Void,
        new ModeSupply((tuning, _) => SuburbPlans.SinglePlayer(tuning), LimitedAmmo: false, Pickups: false), RoundTimer.None,
        () => new LobbyHud(), seed => new BankingBot(seed), Array.AsReadOnly(LobbyRules.Tuners.ToArray()));

    /// <summary>
    /// Modes the Lab tries against its bots through its prototype runner. Ring Out and Short Fuse are only that: they
    /// are not in <see cref="All"/>, so no lobby, MatchStart or envelope schema names them. King is also networked.
    /// </summary>
    public static IReadOnlyList<ModeDefinition> Prototypes { get; } = Array.AsReadOnly(new[] { RingOut, King, ShortFuse, DissolveRun });

    /// <summary>The modes a lobby's creator can choose, the default first.</summary>
    public static IReadOnlyList<ModeDefinition> Lobby { get; } = Array.AsReadOnly(new[] { TankBank, King });

    public static ModeDefinition? ForLobby(byte id) => Lobby.FirstOrDefault(mode => mode.Id == id);

    /// <summary>Process-lifetime compiled network integration, in registration order. Never mutated by tests.</summary>
    public static ModeCatalog Shipped { get; } = new(new[]
    {
        new ModeBundle(Sandbox, ModeSchemas.Sandbox(), () => new SilentModeWire(), (replica, _) => new SilentClientAdapter(replica)),
        new ModeBundle(TankBank, ModeSchemas.TankBank(), () => new TankBankWire(), (replica, _) => new BankClientAdapter(replica)),
        new ModeBundle(King, ModeSchemas.King(), () => new KingWire(), (replica, _) => new KingClientAdapter(replica)),
        new ModeBundle(LobbyWorld, ModeSchemas.Lobby(), () => new LobbyWire(), (replica, lobby) => new LobbyClientAdapter(replica, lobby)),
    });

    private static ModeCatalog current = Shipped;
    public static IReadOnlyList<ModeDefinition> All => current.Definitions;
    /// <summary>Borrowed immutable wire bindings for the current compiled catalog; owners capture once.</summary>
    public static PacketCodec Codec => current.Codec;

    public static ModeDefinition? Find(byte id, ushort schemaVersion) => current.Find(id, schemaVersion)?.Definition;

    public static ModeSchema Schema(byte id, ushort version) => current.Find(id, version)?.Schema
        ?? throw new System.IO.InvalidDataException($"unknown mode schema {id}/{version}");

    /// <summary>
    /// Bind an existing definition override to its validated network integration. Rule initialization still precedes
    /// wire construction; a missing integration fails before either factory is invoked.
    /// </summary>
    public static ModeRunner CreateRunner(ModeDefinition definition, ModeSetup setup)
    {
        var bundle = current.Find(definition.Id, definition.SchemaVersion)
            ?? throw new InvalidOperationException($"no compiled mode bundle {definition.Id}/{definition.SchemaVersion} ({definition.Name})");
        return new ModeRunner(ServerMode(definition, bundle), setup);
    }

    /// <summary>Composition-only resolution for a client's physical session; MatchSetup remains a wire value.</summary>
    public static ModeDefinition Resolve(MatchSetup setup) => Find(setup.ModeId, setup.ModeSchema)
        ?? throw new InvalidOperationException($"unknown mode {setup.ModeId}/{setup.ModeSchema}");

    /// <summary>Construct a fresh session client from the validated bundle; never infer a fallback from its payloads.</summary>
    public static IModeClient CreateClient(MatchSetup setup, IClientModeReplica replica, IClientLobbyReplica lobby)
    {
        var bundle = current.Find(setup.ModeId, setup.ModeSchema)
            ?? throw new InvalidOperationException($"no compiled mode bundle {setup.ModeId}/{setup.ModeSchema}");
        return bundle.CreateClient!(replica, lobby)
            ?? throw new InvalidOperationException($"mode {setup.ModeId}/{setup.ModeSchema}: client adapter factory returned null");
    }

    public static bool ValidSettings(ModeSettings settings) => ForLobby(settings.ModeId) != null && settings.ValidValues;

    private static ServerMode ServerMode(ModeDefinition definition, ModeBundle bundle) => new(definition.Id, definition.Name,
        definition.World, definition.Timer, definition.SamplesActors, definition.Tuners, bundle.Schema!, definition.CreateRules, bundle.CreateServerWire!);

    /// <summary>
    /// Compile one server's mode inputs from the current catalog. Supply callbacks retain only the supply factory;
    /// tuner sampling uses plain names. Neither callback captures a definition or its HUD/bot factories. The server
    /// borrows these bindings until retirement; each run still owns and releases its native tuning.
    /// </summary>
    public static LobbyServer CreateServer(ServerTransport? transport = null, IMonotonicClock? clock = null, ModeDefinition? initialMode = null)
    {
        var catalog = current;
        var supplies = new Dictionary<ServerMode, Func<Tuning, int, SuburbSettings>>();
        ServerMode Compile(ModeDefinition definition)
        {
            var bundle = catalog.Find(definition.Id, definition.SchemaVersion)
                ?? throw new InvalidOperationException($"no compiled mode bundle {definition.Id}/{definition.SchemaVersion} ({definition.Name})");
            var mode = ServerMode(definition, bundle);
            supplies.Add(mode, definition.Supply.Suburb);
            return mode;
        }
        var modes = new ServerModes(Compile(initialMode ?? TankBank), Compile(LobbyWorld), Lobby.Select(Compile));
        return new LobbyServer(modes, BindSupply(supplies),
            (mode, tuning) => ModeDefinition.CopyTuners(tuning, mode.Tuners),
            SuburbPlans.Generate, transport ?? PacketTransports.CreateServer(), catalog.Codec, clock);
    }

    // Separate capture scope: CreateServer's local compiler also captures the full catalog. A callback created
    // in that scope would retain the catalog and its presentation factories even if it only uses supplies.
    private static Func<ServerMode, Tuning, int, SuburbSettings> BindSupply(
        IReadOnlyDictionary<ServerMode, Func<Tuning, int, SuburbSettings>> supplies) =>
        (mode, tuning, players) => supplies[mode](tuning, players);

    /// <summary>
    /// Main-thread test seam. Validated overlays are strictly nested; disposal restores the exact previous catalog,
    /// including entry identities/order, even during exception unwinding. No shipped collection is mutated.
    /// </summary>
    internal static IDisposable RegisterScoped(ModeBundle bundle)
    {
        var previous = current;
        var replacement = previous.With(bundle);
        current = replacement;
        return new Registration(previous, replacement);
    }

    private sealed class Registration(ModeCatalog previous, ModeCatalog replacement) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            if (!ReferenceEquals(current, replacement)) throw new InvalidOperationException("mode registrations must be disposed in reverse order");
            current = previous;
            disposed = true;
        }
    }
}
