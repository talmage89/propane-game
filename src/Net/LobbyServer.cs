using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.World;

namespace Propane.Net;

/// <summary>
/// The multiplayer server. It runs no physics: it hosts lobbies, generates each match's suburb, relays messages
/// between the players in a match, runs the match clock and its mode (see <see cref="ModeRunner"/>), and settles ties
/// (the first claim on a tank or a pickup wins). It runs headless on a dedicated server (<c>--server</c>), or inside
/// a player's game for testing.
/// </summary>
public partial class LobbyServer : Node
{
    public enum LobbyPhase : byte
    {
        Waiting,
        Loading,
        Countdown,
        Playing,
        Results,
    }

    /// <summary>Longest the server waits for every player to build the suburb before starting the countdown anyway.</summary>
    private const double LoadTimeout = 20;

    private const int MaxClients = 64;

    private sealed class Client
    {
        public int Id;
        public ConnectionHandle? Connection;
        public string Name = "Player";
        public AppearanceRecord Appearance = AppearanceRecord.FromPalette(0);
        public bool Welcomed;
        public bool Debug;
        public Lobby? Lobby;
        public double KickAt;
        public DisconnectReason? Rejection;
        public uint LastActorRequest;
        public ActorRequestResultMessage? ActorReply;
    }

    private sealed class Lobby
    {
        public int Id;
        public int CreatorId;
        public readonly List<int> Members = new();
        public float MatchLength;
        public LobbyPhase Phase;
        public MatchRun? Match;
        public int Capacity;
        public ParticipantRoster Roster = null!;
        public ModeDefinition Mode = ModeRegistry.TankBank;
    }

    private sealed class MatchRun
    {
        public readonly List<int> Players = new(); // Participating peers: readiness and delivery, once per connection.
        public readonly Dictionary<int, PlayerProfile> Actors = new();
        public readonly Dictionary<(int Actor, bool Pose), uint> Actions = new();
        public readonly Dictionary<int, (string Name, AppearanceRecord Appearance)> Profiles = new();
        public int Id;
        public int CreatorId;
        public bool CreatorDebug;
        public TankLedger Tanks = null!;
        public readonly HashSet<int> LooseProps = new();
        public readonly Dictionary<int, int> Slots = new();
        public readonly HashSet<int> Loaded = new();
        public readonly Dictionary<int, MeleeValidation> Melee = new();
        public int HostId;
        public ModeRunner Mode = null!;
        public IModeHost Host = null!;
        public double PhaseEnds => Mode.Phases.PhaseEnds;
        public double NextClock;
        public float Length;
        public Tuning Tuning = null!;
        public bool[] PickupUp = Array.Empty<bool>();
        public double[] PickupBack = Array.Empty<double>();
    }

    private readonly IPacketTransport transport;
    private readonly IMonotonicClock clock;
    private readonly ProtocolPeerMap peerIds;
    private bool subscribed;
    private double nextDiagnostics;

    public LobbyServer() : this(PacketTransports.CreateServer()) { }
    public LobbyServer(ServerTransport services, IMonotonicClock? clock = null)
    {
        transport = services.Transport;
        peerIds = services.PeerIds;
        this.clock = clock ?? MonotonicClock.Instance;
    }
    private readonly Dictionary<int, Client> clients = new();
    private readonly Dictionary<int, Lobby> lobbies = new();
    private readonly Random random = new();
    private readonly IdentitySequence lobbyIds = new();
    private readonly IdentitySequence matchIds = new();

    // Legacy handler tests bypass the connection layer; transport tests use the injected interface.
    internal Action<int, byte[], TrafficClass>? SendOverride { get; set; }
    internal Func<ParticipantRoster> RosterFactory { get; set; } = () => new();
    /// <summary>The mode new lobbies play. Tank Bank is the only networked mode; tests run synthetic ones.</summary>
    internal ModeDefinition LobbyMode { get; set; } = ModeRegistry.TankBank;

    public int Port { get; set; } = Protocol.DefaultPort;

    public bool Running { get; private set; }

    /// <summary>Tanks every player starts a match with. 0 normally; tests set it to have something to lose.</summary>
    public int StartScore { get; set; }

    public OpenResult Start()
    {
        if (Running) { return new OpenResult(true); }
        if (!subscribed)
        {
            transport.Connected += OnTransportConnected;
            transport.Disconnected += OnTransportDisconnected;
            transport.Packet += OnPacket;
            subscribed = true;
        }
        var result = transport.Listen(new PacketEndpoint("", Port), MaxClients);
        Running = result.Success;
        Log(Running ? $"listening on UDP {Port} (version {BuildInfo.GameVersion}, build {BuildInfo.Build}, protocol {Protocol.Version})"
            : $"could not listen on UDP {Port}: {result.Detail}");
        return result;
    }

    public override void _ExitTree()
    {
        transport.Connected -= OnTransportConnected;
        transport.Disconnected -= OnTransportDisconnected;
        transport.Packet -= OnPacket;
        subscribed = false;
        Running = false;
        transport.Close();
    }

    private void OnTransportConnected(ConnectionHandle connection)
    {
        var id = peerIds.PeerId(connection);
        OnConnected(id);
        clients[id].Connection = connection;
    }

    private void OnTransportDisconnected(ConnectionHandle connection, DisconnectReason reason)
    {
        var id = peerIds.PeerId(connection);
        Log($"peer {id} disconnect reason {reason.Kind}: {reason.Detail} (backend {reason.BackendCode?.ToString() ?? "unavailable"})");
        if (clients.TryGetValue(id, out var client) && client.Connection == connection) { OnDisconnected(id); }
    }

    private void OnPacket(ConnectionHandle connection, TrafficClass traffic, ReadOnlyMemory<byte> bytes) =>
        OnReceived(peerIds.PeerId(connection), bytes.ToArray(), traffic);

    public override void _Process(double delta)
    {
        if (!Running)
        {
            return;
        }
        transport.Poll();
        var now = clock.Seconds;
        foreach (var client in clients.Values.Where(c => c.KickAt > 0 && now >= c.KickAt).ToList())
        {
            client.KickAt = 0;
            if (peerIds.Connection(client.Id) is { } connection) { transport.Disconnect(connection, client.Rejection ?? DisconnectReason.LocalClose); }
        }
        AdvanceMatches(now);
        if (now >= nextDiagnostics)
        {
            nextDiagnostics = now + (Dev.DevArgs.Get("net-throttle-trace") != null ? .25 : 10);
            if (Dev.DevArgs.Get("capacity-metrics") != null) Log(Dev.CapacityMetrics.Server(clients.Keys.Select(id => peerIds.Connection(id)).Where(c => c != null).Select(c => transport.Diagnostics(c!))));
            foreach (var id in clients.Keys)
            {
                if (peerIds.Connection(id) is not { } connection) { continue; }
                var d = transport.Diagnostics(connection);
                Log($"net peer {id} in {d.PacketsIn}/{d.BytesIn}B out {d.PacketsOut}/{d.BytesOut}B rtt {d.RoundTripMilliseconds:0.0}ms queue {d.QueuedPackets}/{d.QueuedBytes}B drops {d.Drops} poll {d.PollMilliseconds:0.000}ms native-queue {d.NativeQueueBytes?.ToString() ?? "unavailable"} loss {d.NativeLoss?.ToString("0.000") ?? "unavailable"} throttle {d.NativeThrottle?.ToString("0.000") ?? "unavailable"}/{d.NativeThrottleLimit?.ToString("0.000") ?? "unavailable"} variance {d.NativeRttVariance?.ToString("0.0") ?? "unavailable"}ms");
            }
        }
    }

    // ------------------------------------------------------------------ Connections

    internal void OnConnected(int id)
    {
        clients[id] = new Client { Id = id };
        Log($"peer {id} connected");
    }

    internal void OnDisconnected(int id)
    {
        if (!clients.Remove(id, out var client))
        {
            return;
        }
        Log($"peer {id} ({client.Name}) disconnected");
        if (client.Lobby != null)
        {
            LeaveLobby(client);
        }
    }

    internal void OnReceived(int from, byte[] data, TrafficClass? incoming = null)
    {
        if (!clients.TryGetValue(from, out var client)) { return; }
        Msg? type = data.Length > 0 ? (Msg)data[0] : null;
        try
        {
            var message = MessageCodec.Decode(data, fromServer: false);
            if (!client.Welcomed && message.Type != Msg.Hello) { return; }
            if (Protocol.IsMatch(message.Type) && client.Lobby?.Match?.Id != message.MatchId) { return; }
            if (Protocol.IsRelay(message.Type))
            {
                Relay(client, message, incoming);
                return;
            }
            if (message.Type == Msg.ModeEvent)
            {
                ModeEvent(client, message);
                return;
            }
            switch (message.Type)
            {
                case Msg.Hello: Hello(client, (HelloMessage)message.Payload); break;
                case Msg.CreateLobby: CreateLobby(client); break;
                case Msg.JoinLobby: JoinLobby(client, ((IntMessage)message.Payload).Value); break;
                case Msg.LeaveLobby:
                    if (client.Lobby != null) { LeaveLobby(client); }
                    break;
                case Msg.SetProfile:
                    var profile = (ProfileMessage)message.Payload;
                    if (client.Lobby == null || client.Lobby.Phase == LobbyPhase.Waiting) { SetProfile(client, profile.Name, profile.Appearance); }
                    break;
                case Msg.SetMatchLength:
                    if (client.Lobby is { Phase: LobbyPhase.Waiting } lobby && lobby.CreatorId == client.Id)
                    {
                        lobby.MatchLength = ((FloatMessage)message.Payload).Value;
                        SendLobbyState(lobby);
                    }
                    break;
                case Msg.SetCapacity:
                    if (client.Lobby is { Phase: LobbyPhase.Waiting } capacityLobby && capacityLobby.CreatorId == client.Id &&
                        ((IntMessage)message.Payload).Value >= capacityLobby.Roster.Present.Count)
                    {
                        capacityLobby.Capacity = ((IntMessage)message.Payload).Value;
                        SendLobbyState(capacityLobby);
                        BroadcastLobbyList();
                    }
                    break;
                case Msg.AddActor or Msg.UpdateActor or Msg.RemoveActor: ChangeActor(client, message.Payload); break;
                case Msg.StartMatch: StartMatch(client, (Godot.Collections.Dictionary)message.Payload); break;
                case Msg.Loaded:
                    if (client.Lobby?.Match is { } match)
                    {
                        match.Loaded.Add(client.Id);
                        if (client.Connection != null) { transport.SetLoading(client.Connection, false); }
                    }
                    break;
                case Msg.ClaimPickup:
                    if (client.Lobby?.Match is { } pickupMatch && AcceptActor(client, pickupMatch, message))
                        ClaimPickup(client, message.ActorId, ((IntMessage)message.Payload).Value); break;
                case Msg.Ping:
                    Send(client.Id, new NetWriter(Msg.Pong).Double(((TimeMessage)message.Payload).Value).Double(clock.Seconds), traffic: TrafficClass.UnreliablePlayerPoses);
                    break;
            }
        }
        catch (Exception e)
        {
            Log($"bad {type?.ToString() ?? "empty"} message from {from}: {e.Message}");
        }
    }

    private void Hello(Client client, HelloMessage hello)
    {
        if (client.Welcomed || client.KickAt > 0) { return; }
        var mismatch = BuildInfo.Mismatch(hello.Protocol, hello.Version);
        if (mismatch != null)
        {
            Send(client.Id, new NetWriter(Msg.Rejected).String(mismatch).String(BuildInfo.GameVersion), traffic: TrafficClass.ReliableOrdered);
            client.KickAt = clock.Seconds + 1;
            client.Rejection = new DisconnectReason(DisconnectKind.ApplicationRejected, mismatch);
            Log($"peer {client.Id} ({hello.Version}, build {hello.Build}) turned away: {mismatch}");
            return;
        }
        client.Name = Protocol.CleanName(hello.Name);
        client.Appearance = hello.Appearance;
        client.Debug = hello.Debug;
        client.Welcomed = true;
        Send(client.Id, new NetWriter(Msg.Welcome).Int(client.Id).String(BuildInfo.GameVersion), traffic: TrafficClass.ReliableOrdered);
        SendLobbyList(client);
        Log($"peer {client.Id} is {client.Name} ({hello.Version}, build {hello.Build}, debug {hello.Debug})");
    }

    // ------------------------------------------------------------------ Lobbies

    private void CreateLobby(Client client)
    {
        if (client.Lobby != null)
        {
            LeaveLobby(client);
        }
        var lobby = new Lobby
        {
            Id = lobbyIds.Allocate(),
            CreatorId = client.Id,
            MatchLength = Tuning.SavedTemplate.MatchLengthDefault,
            Capacity = Math.Clamp(Tuning.SavedTemplate.MaxPlayers, 2, Protocol.TestedActorCeiling),
            Roster = RosterFactory(),
            Mode = LobbyMode,
        };
        lobbies[lobby.Id] = lobby;
        Log($"{client.Name} created lobby {lobby.Id}");
        AddMember(lobby, client);
    }

    private void JoinLobby(Client client, int lobbyId)
    {
        if (!lobbies.TryGetValue(lobbyId, out var lobby))
        {
            Notice(client, "That lobby has closed.");
            SendLobbyList(client);
            return;
        }
        if (lobby.Phase != LobbyPhase.Waiting)
        {
            Notice(client, "That lobby is in a match. Join when it ends.");
            return;
        }
        if (lobby.Roster.Present.Count >= lobby.Capacity)
        {
            Notice(client, "That lobby is full.");
            return;
        }
        if (client.Lobby == lobby)
        {
            return;
        }
        if (!lobby.Roster.CanAdd)
        {
            Notice(client, "The session identity space is exhausted. Create a new lobby.");
            return;
        }
        if (client.Lobby != null)
        {
            LeaveLobby(client);
        }
        AddMember(lobby, client);
    }

    private void AddMember(Lobby lobby, Client client)
    {
        lobby.Roster.Add(client.Id, ActorKind.Human, client.Name, client.Appearance, ControllerConfig.Human);
        lobby.Members.Add(client.Id);
        client.Lobby = lobby;
        SendLobbyState(lobby);
        BroadcastLobbyList();
    }

    private void LeaveLobby(Client client)
    {
        var lobby = client.Lobby!;
        client.Lobby = null;
        if (client.Connection != null) { transport.SetLoading(client.Connection, false); }
        lobby.Members.Remove(client.Id);
        try { lobby.Roster.RemovePeer(client.Id); }
        catch (OverflowException)
        {
            RetireExhaustedLobby(lobby);
            return;
        }
        if (lobby.Match is { } match && match.Players.Remove(client.Id))
        {
            if (match.HostId == client.Id && match.Players.Count > 0)
            {
                match.HostId = match.Actors.Values.Where(a => a.Present && a.OwnerPeerId != client.Id && a.Kind == ActorKind.Human)
                    .Select(a => a.OwnerPeerId).FirstOrDefault();
                if (match.HostId == 0) match.HostId = match.Players[0];
            }
            foreach (var actor in match.Actors.Values.Where(a => a.OwnerPeerId == client.Id).ToArray())
            {
                match.Actors[actor.Id] = actor with { Present = false };
                match.Mode.Apply(match.Host, new ActorLeft(actor.Id), clock.Seconds);
            }
            var transfers = match.Players.Count > 0 ? match.Tanks.Transfer(client.Id, match.HostId) : Array.Empty<BodyAuthority>();
            foreach (var id in match.Players)
            {
                Send(id, MessageCodec.Encode(new NetMessage(Msg.Authority, match.Id, 0,
                    new AuthorityMessage(match.HostId, 0, ClaimRefusal.None, transfers)), fromServer: true));
                Send(id, new NetWriter(Msg.PlayerLeft, match.Id).Int(client.Id).Int(match.HostId), traffic: TrafficClass.ReliableOrdered);
            }
            if (match.Players.Count == 0)
            {
                lobby.Match = null;
                lobby.Phase = LobbyPhase.Waiting;
            }
        }
        if (lobby.Members.Count == 0)
        {
            lobbies.Remove(lobby.Id);
            Log($"lobby {lobby.Id} closed");
        }
        else
        {
            if (lobby.CreatorId == client.Id)
            {
                lobby.CreatorId = lobby.Members[0];
            }
            SendLobbyState(lobby);
        }
        if (clients.ContainsKey(client.Id))
        {
            SendLobbyList(client);
        }
        BroadcastLobbyList();
    }

    private void SetProfile(Client client, string name, AppearanceRecord appearance)
    {
        if (client.Lobby is { Phase: not LobbyPhase.Waiting }) return;
        client.Name = Protocol.CleanName(name);
        client.Appearance = appearance;
        if (client.Lobby != null)
        {
            foreach (var actor in client.Lobby.Roster.Present.Where(a => a.OwnerPeerId == client.Id && a.Kind == ActorKind.Human).Take(1))
                client.Lobby.Roster.Update(client.Id, actor.ActorId, client.Name, client.Appearance, actor.ControllerConfig);
            SendLobbyState(client.Lobby);
            BroadcastLobbyList();
        }
    }

    private void SendLobbyState(Lobby lobby)
    {
        var actors = lobby.Roster.Present.Select(PlayerProfile.From).ToArray();
        var state = new LobbyInfo(lobby.Id, lobby.CreatorId, lobby.MatchLength, lobby.Phase, actors, lobby.Capacity, lobby.Roster.Revision)
        { Peers = lobby.Members.ToArray() };
        var bytes = MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, state), true);
        var roster = MessageCodec.Encode(new NetMessage(Msg.RosterState, 0, 0,
            new RosterStateMessage(lobby.Id, lobby.Roster.Revision, actors)), true);
        foreach (var peer in lobby.Members) { Send(peer, bytes); Send(peer, roster); }
    }

    private void ChangeActor(Client client, object payload)
    {
        var request = payload switch
        {
            AddActorMessage a => a.RequestId,
            UpdateActorMessage a => a.RequestId,
            RemoveActorMessage a => a.RequestId,
            _ => throw new InvalidDataException("unknown roster request"),
        };
        if (request <= client.LastActorRequest)
        {
            if (request == client.LastActorRequest && client.ActorReply != null)
                Send(client.Id, MessageCodec.Encode(new NetMessage(Msg.ActorRequestResult, 0, 0, client.ActorReply), true));
            return;
        }
        var accepted = false;
        var reason = "The roster is frozen during a match.";
        var lobby = client.Lobby;
        if (lobby is { Phase: LobbyPhase.Waiting })
        {
            reason = "You do not own that actor, or its controller is invalid.";
            try
            {
                switch (payload)
                {
                    case AddActorMessage add when lobby.Roster.Present.Count < lobby.Capacity:
                        lobby.Roster.Add(client.Id, add.Kind, add.Name, add.Appearance, add.Config);
                        accepted = true;
                        break;
                    case AddActorMessage: reason = "That lobby is full."; break;
                    case UpdateActorMessage update:
                        accepted = lobby.Roster.Update(client.Id, update.ActorId, update.Name, update.Appearance, update.Config);
                        break;
                    case RemoveActorMessage remove:
                        accepted = lobby.Roster.Remove(client.Id, remove.ActorId);
                        break;
                }
            }
            catch (InvalidOperationException) { reason = "The session identity space is exhausted. Create a new lobby."; }
            catch (OverflowException) { reason = "The session identity space is exhausted. Create a new lobby."; }
        }
        var result = new ActorRequestResultMessage(request, accepted, lobby?.Roster.Revision ?? 1, accepted ? "" : reason);
        client.LastActorRequest = request;
        client.ActorReply = result;
        Send(client.Id, MessageCodec.Encode(new NetMessage(Msg.ActorRequestResult, 0, 0, result), true));
        if (accepted) { SendLobbyState(lobby!); BroadcastLobbyList(); }
    }

    private byte[] LobbyList()
    {
        var w = new NetWriter(Msg.LobbyList).Int(Protocol.TestedActorCeiling).Int(lobbies.Count);
        foreach (var lobby in lobbies.Values.OrderBy(l => l.Id))
        {
            var creator = clients.GetValueOrDefault(lobby.CreatorId);
            w.Int(lobby.Id).String(creator?.Name ?? "?").Int(lobby.Roster.Present.Count).Byte((byte)lobby.Phase).Int(lobby.Capacity);
        }
        return w.ToArray();
    }

    private void SendLobbyList(Client client) => Send(client.Id, LobbyList(), traffic: TrafficClass.ReliableOrdered);

    private void BroadcastLobbyList()
    {
        var bytes = LobbyList();
        foreach (var client in clients.Values.Where(c => c.Welcomed && c.Lobby == null))
        {
            Send(client.Id, bytes, traffic: TrafficClass.ReliableOrdered);
        }
    }

    private void Notice(Client client, string text) => Send(client.Id, new NetWriter(Msg.Notice).String(text), traffic: TrafficClass.ReliableOrdered);

    // ------------------------------------------------------------------ Match flow

    private void StartMatch(Client client, Godot.Collections.Dictionary snapshot)
    {
        if (client.Lobby is not { Phase: LobbyPhase.Waiting } lobby || lobby.CreatorId != client.Id)
        {
            return;
        }
        var actors = lobby.Roster.Present;
        if (actors.Count == 0 || actors.Count > lobby.Capacity || !actors.Any(a => a.OwnerPeerId == client.Id))
        { Notice(client, "The creator needs an actor to start."); return; }
        // Everyone plays on the creator's tuning: the baked values in a release build, the live ones in debug.
        var tuning = Tuning.FromSnapshot(snapshot);
        var match = new MatchRun
        {
            Id = matchIds.Allocate(),
            CreatorId = client.Id,
            CreatorDebug = client.Debug,
            Tuning = tuning,
            Length = lobby.MatchLength,
            HostId = client.Id
        };
        match.Players.AddRange(actors.Select(a => a.OwnerPeerId).Distinct());
        foreach (var a in actors)
        {
            match.Actors.Add(a.ActorId, PlayerProfile.From(a));
            match.Slots[a.ActorId] = a.AllocationSlot;
            match.Profiles[a.ActorId] = (a.Name, a.Appearance);
        }

        var started = Time.GetTicksMsec();
        var seed = OS.IsDebugBuild() && int.TryParse(Dev.DevArgs.Get("match-seed"), out var requestedSeed)
            ? requestedSeed : random.Next();
        var mode = lobby.Mode;
        var settings = mode.Supply.Suburb(tuning, actors.Count);
        var plan = SuburbPlans.Generate(settings, seed, () => random.Next());
        var planBytes = PlanCodec.Encode(plan);
        match.Tanks = new TankLedger(plan, match.HostId);
        match.Host = new MatchHost(this, match);
        match.Mode = new ModeRunner(mode, new ModeSetup(actors.Select(a => new ModeActor(a.ActorId, a.Name, true, StartScore)).ToArray(),
            new ModeWorld(mode.World, 1, plan.Tanks.Count), mode.CopyTuners(tuning), unchecked((ulong)plan.Seed)));
        for (var i = 0; i < plan.Props.Count; i++)
        {
            if (plan.Props[i].Body != World.Plan.PropBody.Static && !plan.Props[i].Model.StartsWith("car:")) { match.LooseProps.Add(i); }
        }
        match.PickupUp = Enumerable.Repeat(true, plan.AmmoSpots.Count).ToArray();
        match.PickupBack = new double[plan.AmmoSpots.Count];
        Log($"lobby {lobby.Id}: match for {actors.Count} on seed {plan.Seed} ({plan.Lots.Count} lots, {plan.Tanks.Count} tanks, " +
            $"{plan.AmmoSpots.Count} pickups, {planBytes.Length / 1024f:0.0} KB plan, generated in {Time.GetTicksMsec() - started} ms)");

        var setup = new MatchSetup(match.Id, planBytes, match.HostId, match.CreatorDebug, match.Length, snapshot,
            actors.Select((a, spawn) => (PlayerProfile.From(a), spawn)).ToArray())
        {
            RosterRevision = lobby.Roster.Revision,
            CreatorId = match.CreatorId,
            Peers = match.Players.Select(id => new PeerCapabilities(id, id == match.CreatorId && match.CreatorDebug)).ToArray(),
            ModeId = mode.Id,
            ModeSchema = mode.SchemaVersion,
        };
        var bytes = MessageCodec.Encode(new NetMessage(Msg.MatchStart, match.Id, 0, setup), true);
        foreach (var id in match.Players)
        {
            if (clients[id].Connection is { } connection) { transport.SetLoading(connection, true); }
            Send(id, bytes, traffic: TrafficClass.ReliableOrdered);
        }
        lobby.Match = match;
        lobby.Phase = LobbyPhase.Loading;
        match.Mode.Apply(match.Host, new PhaseEntered(ModePhase.Loading,
            match.Mode.Phases.Load(clock.Seconds, Times(match), mode.Timer, match.Length).Round), clock.Seconds);
        SendLobbyState(lobby);
        BroadcastLobbyList();
    }

    internal void AdvanceMatches(double now)
    {
        foreach (var lobby in lobbies.Values.ToList()) { Tick(lobby, now); }
    }

    private static PhaseTimes Times(MatchRun match) => new(LoadTimeout, match.Tuning.CountdownTime, match.Tuning.ResultsTime);

    private void Tick(Lobby lobby, double now)
    {
        if (lobby.Match is not { } match)
        {
            return;
        }
        foreach (var held in match.Mode.Gate.Expire(now))
            match.Mode.Wire.Failed(match.Mode, match.Host, held.Peer, (ModeEnvelope)held.Message.Payload, now);
        if (lobby.Phase == LobbyPhase.Playing)
        {
            for (var i = 0; i < match.PickupUp.Length; i++)
            {
                if (!match.PickupUp[i] && now >= match.PickupBack[i])
                {
                    match.PickupUp[i] = true;
                    Broadcast(match, new NetWriter(Msg.PickupRespawn, match.Id).Int(i));
                }
            }
            if (now >= match.NextClock)
            {
                match.NextClock += 1;
                Broadcast(match, new NetWriter(Msg.Clock, match.Id).Float((float)Math.Max(0, match.PhaseEnds - now)), traffic: TrafficClass.UnreliablePlayerPoses);
            }
        }
        // One timed step per tick; a round's end resolves through RoundEnd, MatchEnd and Results at once.
        if (match.Mode.Phases.Advance(now, Times(match), match.Players.All(match.Loaded.Contains)) is { } change)
        {
            Enter(lobby, match, change, now);
        }
    }

    private void Enter(Lobby lobby, MatchRun match, PhaseChange change, double now)
    {
        switch (change.To)
        {
            case ModePhase.Countdown:
                lobby.Phase = LobbyPhase.Countdown;
                Broadcast(match, new NetWriter(Msg.Countdown, match.Id).Float(match.Tuning.CountdownTime));
                match.Mode.Apply(match.Host, new PhaseEntered(ModePhase.Countdown, change.Round), now);
                break;
            case ModePhase.Round:
                lobby.Phase = LobbyPhase.Playing;
                match.NextClock = now + 1;
                Broadcast(match, new NetWriter(Msg.MatchGo, match.Id).Float((float)change.Round.Length));
                match.Mode.Apply(match.Host, new PhaseEntered(ModePhase.Round, change.Round), now);
                Log($"lobby {lobby.Id}: match on");
                break;
            case ModePhase.RoundEnd:
                foreach (var held in match.Mode.Gate.Clear())
                    match.Mode.Wire.Failed(match.Mode, match.Host, held.Peer, (ModeEnvelope)held.Message.Payload, now);
                var decision = match.Mode.Apply(match.Host, new PhaseEntered(ModePhase.RoundEnd, change.Round), now)
                    .LastOrDefault(c => c is EndMatch or NextRound) ?? new EndMatch();
                if (decision is NextRound { RebuildWorld: true })
                {
                    // Clients cannot rebuild a world inside a run yet (a segment world source will); end cleanly instead.
                    Log($"match {match.Id}: the mode asked for a rebuilt world, which this server cannot deliver; ending the match");
                    decision = new EndMatch();
                }
                Enter(lobby, match, match.Mode.Phases.Decide(now, Times(match), decision), now);
                break;
            case ModePhase.MatchEnd:
                lobby.Phase = LobbyPhase.Results;
                var state = match.Mode.Rules.Snapshot();
                var w = new NetWriter(Msg.MatchEnd, match.Id).Int(state.Standings.Count);
                foreach (var standing in state.Standings)
                {
                    w.Int(standing.ActorId).String(match.Profiles[standing.ActorId].Name).Appearance(match.Profiles[standing.ActorId].Appearance)
                        .Int(standing.Score).Bool(standing.Present);
                }
                Broadcast(match, w);
                match.Mode.Apply(match.Host, new PhaseEntered(ModePhase.MatchEnd, change.Round), now);
                foreach (var report in state.Report)
                {
                    Log(report.Kind switch
                    {
                        ModeReportKind.Audit => $"match {match.Id}: {report.Text}",
                        ModeReportKind.Standings => $"standings match {match.Id}: {report.Text}",
                        _ => $"lobby {lobby.Id}: {report.Text}",
                    });
                }
                if (match.Mode.Rules is TankBankRules bank && (bank.MapDetonations != match.Tanks.MapDetonations || bank.DropDetonations != match.Tanks.DropDetonations))
                {
                    Log($"match {match.Id}: ledger disagreement map={match.Tanks.MapDetonations} drops={match.Tanks.DropDetonations}");
                }
                Enter(lobby, match, match.Mode.Phases.Advance(now, Times(match), true)!.Value, now);
                break;
            case ModePhase.Results:
                lobby.Phase = LobbyPhase.Results;
                match.Mode.Apply(match.Host, new PhaseEntered(ModePhase.Results, change.Round), now);
                break;
            case ModePhase.Lobby:
                try { lobby.Roster.RespawnAll(); }
                catch (OverflowException)
                {
                    RetireExhaustedLobby(lobby);
                    return;
                }
                lobby.Phase = LobbyPhase.Waiting;
                lobby.Match = null;
                SendLobbyState(lobby);
                BroadcastLobbyList();
                break;
            case ModePhase.Loading:
                lobby.Phase = LobbyPhase.Loading;
                match.Mode.Apply(match.Host, new PhaseEntered(ModePhase.Loading, change.Round), now);
                break;
        }
    }

    private void RetireExhaustedLobby(Lobby lobby)
    {
        // No roster mutation is possible at exhaustion. Detach peers before disconnect callbacks can
        // attempt to remove their actors, and retire the round once instead of throwing every frame.
        const string reason = "This lobby needs to close. Reconnect to create a new lobby.";
        lobbies.Remove(lobby.Id);
        lobby.Match = null;
        var members = clients.Values.Where(c => c.Lobby == lobby).ToArray();
        foreach (var client in members) client.Lobby = null;
        lobby.Members.Clear();
        foreach (var client in members)
        {
            if (client.Connection != null) transport.SetLoading(client.Connection, false);
            Send(client.Id, new NetWriter(Msg.Rejected).String(reason).String(BuildInfo.GameVersion), traffic: TrafficClass.ReliableOrdered);
            client.KickAt = clock.Seconds + 1;
            client.Rejection = new DisconnectReason(DisconnectKind.ApplicationRejected, reason);
        }
        Log($"lobby {lobby.Id} closed: participant identity space exhausted");
        BroadcastLobbyList();
    }

    private void ClaimPickup(Client client, int actorId, int index)
    {
        if (client.Lobby is not { Match: { } match } || !match.Mode.Permits(ModeGate.Pickup, clock.Seconds) ||
            index < 0 || index >= match.PickupUp.Length || !match.PickupUp[index])
        {
            return;
        }
        match.PickupUp[index] = false;
        match.PickupBack[index] = clock.Seconds + match.Tuning.PickupRespawnTime;
        Broadcast(match, new NetWriter(Msg.PickupTaken, match.Id).Int(index).Int(actorId));
    }

    // ------------------------------------------------------------------ Relay

    /// <summary>
    /// Resolves authority requests into ordered canonical grants/refusals, and forwards other match messages with
    /// the sender inserted. Accepted explosions and drops also change the bank.
    /// </summary>
    private void Relay(Client client, NetMessage message, TrafficClass? incoming)
    {
        if (client.Lobby is not { Match: { } match } lobby || !match.Players.Contains(client.Id)) { return; }
        if (lobby.Phase is not (LobbyPhase.Countdown or LobbyPhase.Playing or LobbyPhase.Results) &&
            !(lobby.Phase == LobbyPhase.Loading && message.Type is Msg.BodyStates or Msg.BodyRest) &&
            message.Type is not (Msg.Claim or Msg.Puncture or Msg.Explode)) { return; }
        if (Protocol.IsActorMessage(message.Type) && !AcceptActor(client, match, message)) return;
        var actorId = message.ActorId;
        var includeSender = message.Type is Msg.Shot or Msg.MeleeStart or Msg.MeleeContact;
        var playOpen = match.Mode.Permits(ModeGate.World, clock.Seconds);
        switch (message.Payload)
        {
            case ExplodeMessage explosion:
                var detonation = match.Tanks.Detonate(client.Id, explosion, actorId, playOpen);
                PublishAuthority(match, detonation);
                var dependency = new ModeDependency(client.Id, explosion.Request);
                if (detonation.Reason == ClaimRefusal.None)
                {
                    match.Mode.Apply(match.Host, new TankDetonated(actorId, explosion.Id < BodySync.DropTankBase ? TankOrigin.Map : TankOrigin.Drop), clock.Seconds);
                    foreach (var held in match.Mode.Gate.Release(dependency))
                        match.Mode.Wire.Event(match.Mode, match.Host, held.Peer, (ModeEnvelope)held.Message.Payload, clock.Seconds);
                }
                else if (detonation.Reason != ClaimRefusal.DuplicateRequest)
                {
                    foreach (var held in match.Mode.Gate.Fail(dependency))
                        match.Mode.Wire.Failed(match.Mode, match.Host, held.Peer, (ModeEnvelope)held.Message.Payload, clock.Seconds);
                }
                return;
            case PunctureMessage puncture:
                PublishAuthority(match, match.Tanks.Puncture(client.Id, puncture, actorId, playOpen));
                return;
            case ClaimMessage claim:
                PublishAuthority(match, match.Tanks.Claim(client.Id, claim, playOpen));
                return;
            case GatherMessage gather:
                if (!match.Tanks.Gather(client.Id, match.HostId, gather, playOpen)) { return; }
                break;
            case BodyStatesMessage states:
                match.Tanks.ValidateBodies(states.Bodies.Select(b => b.Id));
                var accepted = states.Bodies.Where(b => match.Tanks.Update(client.Id, b, rest: false)).ToArray();
                if (accepted.Length == 0) { return; }
                // Sampling uses the owner's estimate of our clock, only for bounded pose extrapolation, never ordering.
                // Zero means no ping yet: fall back to this relay's time instead of inventing a sampling age.
                var relayedAt = clock.Seconds;
                var sampledAt = states.Time == 0 ? relayedAt : System.Math.Clamp(states.Time,
                    System.Math.Max(0, relayedAt - BodySync.MaxExtrapolation), relayedAt);
                message = message with { Payload = states with { Time = sampledAt, Bodies = accepted } };
                break;
            case BodyRestMessage rest:
                match.Tanks.ValidateBodies(new[] { rest.Id });
                if (!match.Tanks.Update(client.Id, new BodyState(rest.Id, rest.Transform, Vector3.Zero, Vector3.Zero, rest.Epoch, rest.Sequence), rest: true)) { return; }
                break;
            case MeleeStartMessage strike:
                if (!playOpen) return;
                if (!match.Melee.TryGetValue(actorId, out var melee)) match.Melee[actorId] = melee = new MeleeValidation();
                if (!melee.Start(strike, clock.Seconds, match.Tuning)) return;
                break;
            case MeleeContactMessage contact:
                if (!playOpen ||
                    contact.Victim != 0 && (!ValidTarget(match, contact.Victim, contact.VictimLifeId) || contact.Victim == actorId) ||
                    contact.PropIndex >= 0 && !match.LooseProps.Contains(contact.PropIndex))
                {
                    return;
                }

                if (!match.Melee.TryGetValue(actorId, out var swing) || !swing.Contact(contact, clock.Seconds, match.Tuning)) return;
                break;
            case ShotMessage shot:
                if (shot.Victim != 0 && (!ValidTarget(match, shot.Victim, shot.VictimLifeId) || shot.Victim == actorId) ||
                    shot.PropIndex >= 0 && !match.LooseProps.Contains(shot.PropIndex))
                {
                    throw new InvalidDataException("unknown shot target");
                }
                break;
            case TuningMessage tune:
                // The original creator's debug capability is captured at start, never transferred with hosting.
                if (client.Id != match.CreatorId || !match.CreatorDebug) { throw new InvalidDataException("tuning requires the debug creator"); }
                match.Tuning.Apply(new Godot.Collections.Dictionary { [tune.Name] = tune.Value });
                match.Mode.Apply(match.Host, new TunersChanged(match.Mode.Definition.CopyTuners(match.Tuning)), clock.Seconds);
                break;
        }
        var bytes = MessageCodec.Encode(message with { Sender = client.Id }, fromServer: true);
        var traffic = message.Type switch
        {
            Msg.PlayerState => TrafficClass.UnreliablePlayerPoses,
            Msg.BodyStates => incoming is { } delivery && delivery.IsReliable() ? delivery : TrafficClass.UnreliableBodyStates,
            _ => TrafficClass.ReliableOrdered,
        };
        foreach (var id in match.Players)
        {
            if (id != client.Id || includeSender) { Send(id, bytes, traffic); }
        }
    }

    /// <summary>
    /// A mode event: the envelope codec already checked its schema, kind, direction and bounds. The reporter must own
    /// the actor with its current life and a newer action sequence; causal dependencies wait in the run's gate.
    /// </summary>
    private void ModeEvent(Client client, NetMessage message)
    {
        if (client.Lobby is not { Match: { } match } || !match.Players.Contains(client.Id)) return;
        var envelope = (ModeEnvelope)message.Payload;
        if (envelope.ModeId != match.Mode.Definition.Id || envelope.SchemaVersion != match.Mode.Definition.SchemaVersion)
            throw new InvalidDataException($"mode event for schema {envelope.ModeId}/{envelope.SchemaVersion} in a {match.Mode.Definition.Name} run");
        var kind = ModeSchemas.Find(envelope.ModeId, envelope.SchemaVersion).Kind(envelope.Kind, ModeDirection.Event);
        if (kind.Reporter == ModeReporter.ActorOwner && !AcceptActor(client, match, message)) return;
        var now = clock.Seconds;
        if (kind.Dependency?.Invoke(envelope.Payload) is { } dependency && !match.Tanks.Exploded(dependency.Peer, dependency.Request))
        {
            if (!match.Mode.Gate.Hold(client.Id, message, dependency, now)) match.Mode.Wire.Failed(match.Mode, match.Host, client.Id, envelope, now);
            return;
        }
        match.Mode.Wire.Event(match.Mode, match.Host, client.Id, envelope, now);
    }

    private sealed class MatchHost(LobbyServer server, MatchRun match) : IModeHost
    {
        public void Broadcast(Msg type, ModeEnvelope envelope)
        {
            var bytes = MessageCodec.Encode(new NetMessage(type, match.Id, 0, envelope), fromServer: true);
            foreach (var id in match.Players) server.Send(id, bytes);
        }

        public void Send(int peer, Msg type, ModeEnvelope envelope) =>
            server.Send(peer, MessageCodec.Encode(new NetMessage(type, match.Id, 0, envelope), fromServer: true));

        public void RegisterEntities(int actorId, int peer, IReadOnlyList<BodyState> bodies) =>
            match.Tanks.RegisterDrops(match.Slots[actorId], peer, bodies);

        public uint Life(int actorId) => match.Actors[actorId].LifeId;
        public void Log(string text) => LobbyServer.Log(text);
    }

    private static bool ValidTarget(MatchRun match, int actor, uint life) =>
        match.Actors.TryGetValue(actor, out var target) && target.Present && target.LifeId == life;

    private static bool AcceptActor(Client client, MatchRun match, NetMessage message)
    {
        if (!match.Actors.TryGetValue(message.ActorId, out var actor) || !actor.Present ||
            actor.OwnerPeerId != client.Id || actor.LifeId != message.LifeId || message.ActionId == 0)
        {
            return false;
        }

        var key = (message.ActorId, message.Type == Msg.PlayerState);
        if (message.ActionId <= match.Actions.GetValueOrDefault(key)) return false;
        match.Actions[key] = message.ActionId;
        return true;
    }

    private void PublishAuthority(MatchRun match, AuthorityMessage result)
    {
        if (Dev.DevArgs.Get("tank-authority-trace") != null)
        {
            var time = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
            var bodies = string.Join(" ", result.Bodies.Select(b => $"{b.Body.Id:x}:{b.Owner}:{b.Body.Epoch}:{b.Body.Sequence}:{b.State}:{b.Rest}"));
            Log(FormattableString.Invariant($"authority-trace match {match.Id} time {time:0.000000} peer {result.Peer} request {result.Request} reason {result.Reason} bodies {bodies}"));
        }
        var bytes = MessageCodec.Encode(new NetMessage(Msg.Authority, match.Id, 0, result), fromServer: true);
        if (result.Reason == ClaimRefusal.None)
        {
            foreach (var id in match.Players) { Send(id, bytes); }
        }
        else
        {
            Send(result.Peer, bytes);
            Log($"refused authority from {result.Peer} request {result.Request}: {result.Reason}");
        }
    }

    private void Broadcast(MatchRun match, NetWriter message, TrafficClass traffic = TrafficClass.ReliableOrdered)
    {
        var bytes = message.ToArray();
        foreach (var id in match.Players)
        {
            Send(id, bytes, traffic);
        }
    }

    private void Send(int peer, NetWriter message, TrafficClass traffic = TrafficClass.ReliableOrdered) => Send(peer, message.ToArray(), traffic);

    private void Send(int peer, byte[] bytes, TrafficClass traffic = TrafficClass.ReliableOrdered)
    {
        if (SendOverride != null) { SendOverride(peer, bytes, traffic); }
        else if (peerIds.Connection(peer) is { } connection)
        {
            var result = transport.Send(connection, traffic, bytes);
            if (traffic.IsReliable() && result is SendResult.Backpressure or SendResult.Oversized)
            {
                transport.Disconnect(connection, new DisconnectReason(DisconnectKind.TransportFailure,
                    $"Could not send a required message ({result}). Please reconnect."));
            }
        }
    }

    private static void Log(string text) => GD.Print($"[server {Time.GetDatetimeStringFromSystem()}] {text}");
}
