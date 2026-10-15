using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>
/// The lobby world: while a lobby waits, its members roam a void world together (the Lobby mode: no score, refilling
/// piles, rifles that never run dry). It is a run like a match, with its own id from the same sequence, its own ledger
/// and relays, so traffic for a world that has closed is stale by id. Unlike a match its membership follows the
/// lobby's roster live: whoever joins is admitted with a world start of their own (the void plan, every live body and
/// Go), and every peer reconciles its actors with the roster it is sent. The creator's start closes it.
/// </summary>
public partial class LobbyServer
{
    /// <summary>The longest stage a start may count down, in seconds (the tuner's ceiling; a wire bound on StartsIn).</summary>
    public const float MaxStage = 10;

    /// <summary>How often a world's rules hear how its piles stand, in seconds.</summary>
    private const double StockInterval = 0.5;

    /// <summary>
    /// Whether waiting lobbies run a world. Off, lobbies wait in the menu as before (and tests that count packets may
    /// turn it off).
    /// </summary>
    public bool Worlds { get; set; } = LobbyWorldDefault.Enabled;

    /// <summary>The run a match message names: this lobby's current match or its current world.</summary>
    private static MatchRun? RunFor(Client client, int id) => client.Lobby switch
    {
        { Match: { } match } when match.Id == id => match,
        { World: { } world } when world.Id == id => world,
        _ => null,
    };

    /// <summary>
    /// A waiting lobby without a world gets one, stocked; its lobby state (naming the world) goes to every member, who
    /// are then admitted. False when it has one already or runs none.
    /// </summary>
    private bool OpenWorld(Lobby lobby)
    {
        if (!Worlds || lobby.World != null || lobby.Phase != LobbyPhase.Waiting || lobby.Members.Count == 0) return false;
        var now = clock.Seconds;
        // The server's own dials build the world (in a release build, the baked ones); the lobby's choices apply too.
        var tuning = Tuning.SavedTemplate.Copy();
        lobby.Settings.Apply(tuning);
        var world = new MatchRun
        {
            Id = matchIds.Allocate(),
            CreatorId = lobby.CreatorId,
            Tuning = tuning,
            Length = Math.Clamp(lobby.MatchLength, 10, 3600),
            HostId = lobby.CreatorId,
        };
        var mode = ModeRegistry.LobbyWorld;
        world.Tanks = new TankLedger(Plan(world), world.HostId);
        world.Host = new MatchHost(this, world);
        world.Mode = new ModeRunner(mode, new ModeSetup(Array.Empty<ModeActor>(),
            new ModeWorld(mode.World, 1, 0, LobbyLayout.Spots(tuning.LobbyPiles, tuning.LobbyPileRing)), mode.CopyTuners(tuning), (ulong)world.Id));
        lobby.World = world;
        // Straight into its one untimed round, which stocks every spot; nobody waits for anyone to load.
        var times = new PhaseTimes(LoadTimeout, 0, tuning.ResultsTime);
        world.Mode.Apply(world.Host, new PhaseEntered(ModePhase.Loading, world.Mode.Phases.Load(now, times, mode.Timer, 0).Round), now);
        while (world.Mode.Phases.Phase != ModePhase.Round && world.Mode.Phases.Advance(now, times, true) is { } change)
            world.Mode.Apply(world.Host, new PhaseEntered(change.To, change.Round), now);
        Log($"lobby {lobby.Id}: world {world.Id} open with {world.Tanks.LiveTanks} tanks");
        SendLobbyState(lobby);
        BroadcastLobbyList();
        return true;
    }

    /// <summary>The creator started: the world retires, and anything still sent to it is stale.</summary>
    private void CloseWorld(Lobby lobby)
    {
        if (lobby.World is not { } world) return;
        lobby.World = null;
        Log($"lobby {lobby.Id}: world {world.Id} closed");
    }

    /// <summary>
    /// Mirrors the roster into the world: new actors join (with their peers, if new), removed ones leave, and changed
    /// names and colors follow. Every lobby state broadcast ends here, so the world never lags the roster.
    /// </summary>
    private void SyncWorld(Lobby lobby)
    {
        if (lobby.World is not { } world) return;
        var now = clock.Seconds;
        var present = lobby.Roster.Present.ToDictionary(a => a.ActorId);
        foreach (var gone in world.Actors.Values.Where(a => a.Present && !present.ContainsKey(a.Id)).ToArray())
        {
            world.Actors[gone.Id] = gone with { Present = false };
            world.Mode.Apply(world.Host, new ActorLeft(gone.Id), now);
        }
        foreach (var record in present.Values)
        {
            var joined = !world.Actors.TryGetValue(record.ActorId, out var prior) || !prior.Present;
            world.Actors[record.ActorId] = PlayerProfile.From(record);
            world.Slots[record.ActorId] = record.AllocationSlot;
            world.Profiles[record.ActorId] = (record.Name, record.Appearance);
            if (joined) world.Mode.Apply(world.Host, new ActorJoined(new ModeActor(record.ActorId, record.Name)), now);
        }
        foreach (var peer in lobby.Members.Where(peer => !world.Players.Contains(peer)).ToArray()) Admit(lobby, world, peer);
    }

    /// <summary>
    /// One peer's world start: the void plan with a start for every present actor, the world's tuning, every live
    /// body as the ledger has it (so a late arrival sees the world as it is), and Go.
    /// </summary>
    private void Admit(Lobby lobby, MatchRun world, int peer)
    {
        world.Players.Add(peer);
        var actors = world.Actors.Values.Where(a => a.Present).OrderBy(a => a.Id).ToArray();
        var plan = Plan(world);
        foreach (var actor in actors)
        {
            var (at, yaw) = LobbyLayout.Arrival(actor.AllocationSlot, world.Tuning.LobbySpawnRing);
            // A plan start's yaw faces away from where its actor looks (matches turn it around on arrival).
            plan.Spawns.Add(new SpawnPoint(new Vector2((float)at.X, (float)at.Z), (float)yaw + Mathf.Pi));
        }
        var setup = new MatchSetup(world.Id, PlanCodec.Encode(plan), world.HostId, false, world.Length, world.Tuning.Snapshot(),
            actors.Select((a, spawn) => (a, spawn)).ToArray())
        {
            RosterRevision = lobby.Roster.Revision,
            CreatorId = lobby.CreatorId,
            Peers = lobby.Members.Select(id => new PeerCapabilities(id, false)).ToArray(),
            ModeId = world.Mode.Definition.Id,
            ModeSchema = world.Mode.Definition.SchemaVersion,
        };
        if (clients.GetValueOrDefault(peer)?.Connection is { } connection) { transport.SetLoading(connection, true); }
        Send(peer, MessageCodec.Encode(new NetMessage(Msg.MatchStart, world.Id, 0, setup), true));
        var live = world.Tanks.Live();
        for (var start = 0; start < live.Count; start += Protocol.MaxCollection)
        {
            Send(peer, MessageCodec.Encode(new NetMessage(Msg.Authority, world.Id, 0, new AuthorityMessage(world.HostId, 0, ClaimRefusal.None,
                live.Skip(start).Take(Protocol.MaxCollection).ToArray())), fromServer: true));
        }
        Send(peer, new NetWriter(Msg.MatchGo, world.Id).Float(0));
    }

    /// <summary>The world's plan: the void, laid out around the pile ring, with no ground, houses or tanks of its own.</summary>
    private static SuburbPlan Plan(MatchRun world)
    {
        var extent = (float)LobbyLayout.Extent(world.Tuning.LobbyPileRing);
        return new SuburbPlan { Seed = world.Id, Bounds = new Rect2(-extent, -extent, extent * 2, extent * 2) };
    }

    private void TickWorld(MatchRun world, double now)
    {
        foreach (var held in world.Mode.Gate.Expire(now))
            world.Mode.Wire.Failed(world.Mode, world.Host, held.Peer, (ModeEnvelope)held.Message.Payload, now);
        if (now < world.NextStock || world.Mode.Wire is not LobbyWire wire) return;
        world.NextStock = now + StockInterval;
        world.Mode.Apply(world.Host, wire.Stock(world.Mode, world.Host), now);
    }
}

/// <summary>
/// Whether this process plays lobbies in a lobby world: the default. <c>--classic-lobby</c> keeps the lobby menu over
/// the backdrop instead (a client then ignores its lobby's world; a server started with it runs none), until the owner
/// has reviewed the world's look.
/// </summary>
public static class LobbyWorldDefault
{
    public static bool Enabled => Dev.DevArgs.Get("classic-lobby") == null;
}
