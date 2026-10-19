using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>
/// Owns connections, lobby admission and staging, transport and diagnostics. Each match or roaming world is an
/// owned ServerRun, called at the existing receive/tick boundaries. Runs no physics, headless on a dedicated server
/// (<c>--server</c>) or inside a player's game.
/// </summary>
public partial class LobbyServer : Node
{
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
        public ServerRun? Match;
        public int Capacity;
        public ParticipantRoster Roster = null!;
        /// <summary>The mode and settings its creator chose, as every member sees them.</summary>
        public ModeSettings Settings = ModeSettings.Default;
        /// <summary>The mode its next match plays: the creator's choice, or a test's synthetic mode.</summary>
        public ServerMode Mode = null!;
        /// <summary>The world its members roam while it waits (and while the stage counts down); null otherwise.</summary>
        public ServerRun? World;
        /// <summary>While staging: the creator's start snapshot, and when the match begins.</summary>
        public Godot.Collections.Dictionary? Staged;
        public double StageEnds;
    }

    private readonly IPacketTransport transport;
    private readonly IMonotonicClock clock;
    private readonly ProtocolPeerMap peerIds;
    private bool subscribed;
    private double nextDiagnostics;

    private readonly PacketCodec codec;
    private readonly ServerModes modes;
    private readonly IServerSettings settings;
    private readonly Func<SuburbSettings, int, Func<int>, SuburbPlan> generate;

    // Parameterless script instantiation cannot supply application-owned mode inputs.
    public LobbyServer() => throw new InvalidOperationException("LobbyServer requires compiled server mode inputs");
    public LobbyServer(ServerModes modes, IServerSettings settings, Func<SuburbSettings, int, Func<int>, SuburbPlan> generate, ServerTransport services, PacketCodec codec, IMonotonicClock? clock = null)
    {
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        this.modes = modes ?? throw new ArgumentNullException(nameof(modes));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.generate = generate ?? throw new ArgumentNullException(nameof(generate));
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
    internal Action<ServerRun>? RunCreated { get; set; }
    internal Func<ParticipantRoster> RosterFactory { get; set; } = () => new();
    /// <summary>The roster revision of a peer's lobby (0 outside one), for request fixtures.</summary>
    internal uint RosterRevision(int peer) => clients.GetValueOrDefault(peer)?.Lobby?.Roster.Revision ?? 0;
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
        RetireRuns();
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
            nextDiagnostics = now + (LaunchArgs.Current.Has("net-throttle-trace") ? .25 : 10);
            if (LaunchArgs.Current.Has("capacity-metrics")) Log(TransportMetrics.Server(clients.Keys.Select(id => peerIds.Connection(id)).Where(c => c != null).Select(c => transport.Diagnostics(c!))));
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
            var lobby = client.Lobby;
            try { LeaveLobby(client); }
            catch (Exception error) { FailOwner(lobby, "departure", error); }
        }
    }

    internal void OnReceived(int from, byte[] data, TrafficClass? incoming = null)
    {
        if (!clients.TryGetValue(from, out var client)) { return; }
        Msg? type = data.Length > 0 ? (Msg)data[0] : null;
        NetMessage message;
        try { message = codec.Decode(data, fromServer: false); }
        catch (Exception error) when (PacketCodec.IsDecodeFailure(error))
        {
            Log($"bad {type?.ToString() ?? "empty"} message from {from}: {error.Message}");
            return;
        }
        catch (Exception error) { FailOwner(client.Lobby, "decode", error); return; }
        var affectedLobby = client.Lobby;
        try
        {
            if (!client.Welcomed && message.Type != Msg.Hello) { return; }
            // Match traffic belongs to the lobby's current match or its current world; anything else is stale.
            var run = Protocol.IsMatch(message.Type) ? RunFor(client, message.MatchId) : null;
            if (Protocol.IsMatch(message.Type) && run == null) { return; }
            if (Protocol.IsRelay(message.Type))
            {
                run!.Receive(client.Id, message, incoming);
                return;
            }
            if (message.Type == Msg.ModeEvent)
            {
                run!.Receive(client.Id, message, incoming);
                return;
            }
            switch (message.Type)
            {
                case Msg.Hello: Hello(client, (HelloMessage)message.Payload); break;
                case Msg.CreateLobby: CreateLobby(client, ref affectedLobby); break;
                case Msg.JoinLobby: JoinLobby(client, ((IntMessage)message.Payload).Value, ref affectedLobby); break;
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
                case Msg.SetMode:
                    // The codec admitted only lobby modes, valid mint steps and bounded tank supply.
                    if (client.Lobby is { Phase: LobbyPhase.Waiting } modeLobby && modeLobby.CreatorId == client.Id)
                    {
                        modeLobby.Settings = (ModeSettings)message.Payload;
                        modeLobby.Mode = modes.ForLobby(modeLobby.Settings.ModeId)!;
                        Log($"lobby {modeLobby.Id}: {modeLobby.Mode.Name}, {modeLobby.Settings.TankSupply}× tank supply, a tank every {modeLobby.Settings.MintSeconds:0.0} s in the zone");
                        SendLobbyState(modeLobby);
                        BroadcastLobbyList();
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
                case Msg.CancelStart: CancelStart(client); break;
                case Msg.Loaded:
                case Msg.ClaimPickup:
                    run!.Receive(client.Id, message, incoming);
                    break;
                case Msg.Ping:
                    Send(client.Id, new NetWriter(Msg.Pong).Double(((TimeMessage)message.Payload).Value).Double(clock.Seconds), traffic: TrafficClass.UnreliablePlayerPoses);
                    break;
            }
        }
        catch (Exception error) { FailOwner(affectedLobby, $"dispatch {message.Type}", error); }
    }

    /// <summary>
    /// A partially executed handler is never rolled back or retried. Detach its lobby's routes before normal run
    /// disposal; keep connections and other lobbies alive. No replacement run is constructed on the failure path.
    /// </summary>
    private void FailOwner(Lobby? lobby, string operation, Exception error)
    {
        Log($"owner failure in lobby {lobby?.Id.ToString() ?? "none"} during {operation}: {error}");
        if (lobby == null) return;
        lobby.Staged = null;
        lobby.StageEnds = 0;
        lobby.Phase = LobbyPhase.Waiting;
        // Departure can fail after membership removal but before its usual creator/empty-lobby tail.
        if (lobby.Members.Count == 0) lobbies.Remove(lobby.Id);
        else if (!lobby.Members.Contains(lobby.CreatorId)) lobby.CreatorId = lobby.Members[0];
        try { Retire(ref lobby.Match); }
        catch (Exception cleanup) { Log($"match cleanup failure in lobby {lobby.Id}: {cleanup}"); }
        try { Retire(ref lobby.World); }
        catch (Exception cleanup) { Log($"world cleanup failure in lobby {lobby.Id}: {cleanup}"); }
        // Keep peers connected and make the waiting lobby usable again, without re-entering the failed run.
        // A persistent delivery failure must not interrupt another lobby's tick or cleanup.
        try
        {
            foreach (var peer in lobby.Members) SetLoading(peer, false);
            foreach (var peer in lobby.Members)
                Notice(clients[peer], "The server stopped this run after an internal failure. You can start another match.");
            if (lobby.Members.Count > 0) SendLobbyState(lobby);
        }
        catch (Exception cleanup) { Log($"failure notification in lobby {lobby.Id}: {cleanup}"); }
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

    private void CreateLobby(Client client, ref Lobby? affectedLobby)
    {
        if (client.Lobby != null)
        {
            LeaveLobby(client);
        }
        // A completed departure must not remain the failure owner of a later construction.
        affectedLobby = null;
        var lobby = new Lobby
        {
            Id = lobbyIds.Allocate(),
            CreatorId = client.Id,
            MatchLength = settings.MatchLengthDefault,
            Capacity = Math.Clamp(settings.MaxPlayers, 2, Protocol.TestedActorCeiling),
            Roster = RosterFactory(),
            Settings = ModeSettings.Default with { ModeId = modes.ForLobby(modes.Default.Id)?.Id ?? modes.Lobby[0].Id },
            Mode = modes.Default,
        };
        affectedLobby = lobby;
        lobbies[lobby.Id] = lobby;
        Log($"{client.Name} created lobby {lobby.Id}");
        // With a world, its creator's first lobby state already names it.
        AddMember(lobby, client, announce: !Worlds);
        OpenWorld(lobby);
    }

    private void JoinLobby(Client client, int lobbyId, ref Lobby? affectedLobby)
    {
        if (!lobbies.TryGetValue(lobbyId, out var lobby))
        {
            Notice(client, "That lobby has closed.");
            SendLobbyList(client);
            return;
        }
        if (lobby.Phase != LobbyPhase.Waiting)
        {
            Notice(client, lobby.Phase == LobbyPhase.Staging ? "That lobby is starting a match. Join when it ends."
                : "That lobby is in a match. Join when it ends.");
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
        affectedLobby = lobby;
        AddMember(lobby, client);
    }

    private void AddMember(Lobby lobby, Client client, bool announce = true)
    {
        lobby.Roster.Add(client.Id, ActorKind.Human, client.Name, client.Appearance, ControllerConfig.Human);
        lobby.Members.Add(client.Id);
        client.Lobby = lobby;
        if (!announce) return;
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
        if (lobby.World is { } world) { world.Depart(client.Id); }
        if (lobby.Match is { } match && match.Depart(client.Id) && match.PeerCount == 0)
        {
            Retire(ref lobby.Match);
            lobby.Phase = LobbyPhase.Waiting;
        }
        if (lobby.Phase == LobbyPhase.Staging && lobby.CreatorId == client.Id)
        {
            // Only the creator's start was staged; whoever inherits the lobby starts again if they want to. The lobby
            // state below announces it, once the lobby has its next creator.
            CancelStage(lobby, "the creator left", announce: false);
        }
        if (lobby.Members.Count == 0)
        {
            lobbies.Remove(lobby.Id);
            Retire(ref lobby.World);
            Log($"lobby {lobby.Id} closed");
        }
        else
        {
            if (lobby.CreatorId == client.Id)
            {
                lobby.CreatorId = lobby.Members[0];
            }
            if (!OpenWorld(lobby)) SendLobbyState(lobby);
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
        {
            Peers = lobby.Members.ToArray(),
            Mode = lobby.Settings,
            StartsIn = lobby.Phase == LobbyPhase.Staging ? (float)Math.Clamp(lobby.StageEnds - clock.Seconds, 0, Protocol.MaxStage) : 0,
            WorldId = lobby.World?.Id ?? 0,
        };
        var bytes = codec.Encode(new NetMessage(Msg.LobbyState, 0, 0, state), true);
        var roster = codec.Encode(new NetMessage(Msg.RosterState, 0, 0,
            new RosterStateMessage(lobby.Id, lobby.Roster.Revision, actors)), true);
        foreach (var peer in lobby.Members) { Send(peer, bytes); Send(peer, roster); }
        SyncWorld(lobby);
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
                Send(client.Id, codec.Encode(new NetMessage(Msg.ActorRequestResult, 0, 0, client.ActorReply), true));
            return;
        }
        var accepted = false;
        var lobby = client.Lobby;
        var reason = lobby == null ? "Join a lobby first." : "The roster is frozen during a match.";
        if (lobby is { Phase: LobbyPhase.Waiting })
        {
            try { accepted = ChangeRoster(client, lobby, payload, out reason); }
            catch (InvalidOperationException) { reason = "The session identity space is exhausted. Create a new lobby."; }
            catch (OverflowException) { reason = "The session identity space is exhausted. Create a new lobby."; }
        }
        var result = new ActorRequestResultMessage(request, accepted, lobby?.Roster.Revision ?? 1, accepted ? "" : reason);
        client.LastActorRequest = request;
        client.ActorReply = result;
        Send(client.Id, codec.Encode(new NetMessage(Msg.ActorRequestResult, 0, 0, result), true));
        if (accepted) { SendLobbyState(lobby!); BroadcastLobbyList(); }
    }

    /// <summary>
    /// Validates one waiting-lobby roster request against the revision it was made on. Clients add only bots, change
    /// only actors they own, and name bots uniquely; appearance and controller ranges were checked by the codec.
    /// </summary>
    private static bool ChangeRoster(Client client, Lobby lobby, object payload, out string reason)
    {
        var roster = lobby.Roster;
        var revision = payload switch
        {
            AddActorMessage a => a.Revision,
            UpdateActorMessage a => a.Revision,
            RemoveActorMessage a => a.Revision,
            _ => throw new InvalidDataException("unknown roster request"),
        };
        reason = "The lobby changed. Check it and try again.";
        if (revision != roster.Revision) return false;
        reason = "You do not own that actor, or its controller is invalid.";
        switch (payload)
        {
            case AddActorMessage add:
                if (add.Kind != ActorKind.Bot) { reason = "Only bots can be added."; return false; }
                if (!NameAvailable(roster, add.Name, 0, out reason)) return false;
                if (roster.Present.Count >= lobby.Capacity) { reason = "That lobby is full."; return false; }
                roster.Add(client.Id, add.Kind, add.Name, add.Appearance, add.Config);
                return true;
            case UpdateActorMessage update:
                if (roster.Get(update.ActorId) is not { Present: true } actor || actor.OwnerPeerId != client.Id) return false;
                if (actor.Kind == ActorKind.Bot ? !NameAvailable(roster, update.Name, actor.ActorId, out reason)
                        : !Protocol.ValidActorName(update.Name)) { return false; }
                return roster.Update(client.Id, update.ActorId, update.Name, update.Appearance, update.Config);
            case RemoveActorMessage remove:
                return roster.Remove(client.Id, remove.ActorId);
        }
        return false;
    }

    private static bool NameAvailable(ParticipantRoster roster, string name, int actorId, out string reason)
    {
        reason = $"Names need 1 to {Protocol.MaxNameLength} letters, digits, spaces or symbols.";
        if (!Protocol.ValidActorName(name)) return false;
        reason = $"Someone in the lobby is already called {name}.";
        return !roster.Present.Any(a => a.ActorId != actorId && Protocol.SameName(a.Name, name));
    }

    private byte[] LobbyList()
    {
        var w = new NetWriter(Msg.LobbyList).Int(Protocol.TestedActorCeiling).Int(lobbies.Count);
        foreach (var lobby in lobbies.Values.OrderBy(l => l.Id))
        {
            var creator = clients.GetValueOrDefault(lobby.CreatorId);
            w.Int(lobby.Id).String(creator?.Name ?? "?").Int(lobby.Roster.Present.Count).Byte((byte)lobby.Phase).Int(lobby.Capacity).Byte(lobby.Settings.ModeId);
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
        if (client.Lobby is not { Phase: LobbyPhase.Waiting } lobby || lobby.CreatorId != client.Id || !CanStart(lobby, client))
        {
            return;
        }
        // A lobby with a world stages the start: everyone sees the lineup count down while still roaming.
        using var staged = settings.FromSnapshot(lobby.Mode, snapshot);
        if (lobby.World != null && staged.LobbyStageTime > 0)
        {
            lobby.Phase = LobbyPhase.Staging;
            lobby.Staged = snapshot;
            lobby.StageEnds = clock.Seconds + Math.Min(staged.LobbyStageTime, Protocol.MaxStage);
            Log($"lobby {lobby.Id}: starting in {staged.LobbyStageTime:0.#} s");
            SendLobbyState(lobby);
            BroadcastLobbyList();
            return;
        }
        BeginMatch(lobby, client, snapshot);
    }

    /// <summary>The creator still has an actor and the roster fits: otherwise it says why.</summary>
    private bool CanStart(Lobby lobby, Client client)
    {
        var actors = lobby.Roster.Present;
        if (actors.Count == 0 || actors.Count > lobby.Capacity || !actors.Any(a => a.OwnerPeerId == client.Id))
        { Notice(client, "The creator needs an actor to start."); return false; }
        return true;
    }

    /// <summary>The creator takes the start back while the stage counts down.</summary>
    private void CancelStart(Client client)
    {
        if (client.Lobby is { Phase: LobbyPhase.Staging } lobby && lobby.CreatorId == client.Id) CancelStage(lobby, "the creator canceled");
    }

    private void CancelStage(Lobby lobby, string why, bool announce = true)
    {
        lobby.Phase = LobbyPhase.Waiting;
        lobby.Staged = null;
        Log($"lobby {lobby.Id}: start canceled, {why}");
        if (!announce) return;
        SendLobbyState(lobby);
        BroadcastLobbyList();
    }

    /// <summary>The match itself: the lobby world (if any) retires, then the suburb is generated and sent as always.</summary>
    private void BeginMatch(Lobby lobby, Client client, Godot.Collections.Dictionary snapshot)
    {
        CloseWorld(lobby);
        var tuning = settings.FromSnapshot(lobby.Mode, snapshot);
        ServerRun? match = null;
        try
        {
            tuning.ConfigureLobby(lobby.Settings.MintSeconds, lobby.Settings.TankSupply);
            match = NewRun(lobby, client.Debug, lobby.MatchLength, false, tuning);
            RunCreated?.Invoke(match);
            match.InitializeMatch(lobby.Roster.Present, lobby.Roster.Revision, StartScore, random.Next);
            lobby.Match = match;
            match.StartMatch();
        }
        catch (Exception failure)
        {
            if (lobby.Match == match) lobby.Match = null;
            try
            {
                if (match != null) match.Dispose();
                else tuning.Dispose();
            }
            catch (Exception cleanup) { throw new AggregateException("Run construction and retirement failed", failure, cleanup); }
            throw;
        }
        SendLobbyState(lobby);
        BroadcastLobbyList();
    }

    internal void AdvanceMatches(double now)
    {
        foreach (var lobby in lobbies.Values.ToList())
        {
            try { Tick(lobby, now); }
            catch (Exception error) { FailOwner(lobby, "tick", error); }
        }
    }

    private void Tick(Lobby lobby, double now)
    {
        lobby.World?.Tick(now);
        if (lobby.Phase == LobbyPhase.Staging && now >= lobby.StageEnds)
        {
            var creator = clients.GetValueOrDefault(lobby.CreatorId);
            var snapshot = lobby.Staged!;
            lobby.Phase = LobbyPhase.Waiting;
            lobby.Staged = null;
            if (creator == null || !CanStart(lobby, creator)) { CancelStage(lobby, "the lineup changed"); }
            else { BeginMatch(lobby, creator, snapshot); }
            return;
        }
        lobby.Match?.Tick(now);
    }

    private void ReturnToLobby(Lobby lobby)
    {
        try { lobby.Roster.RespawnAll(); }
        catch (OverflowException)
        {
            RetireExhaustedLobby(lobby);
            return;
        }
        lobby.Phase = LobbyPhase.Waiting;
        Retire(ref lobby.Match);
        // Everyone still here goes back to roaming, in a new world.
        if (!OpenWorld(lobby)) SendLobbyState(lobby);
        BroadcastLobbyList();
    }

    private ServerRun NewRun(Lobby lobby, bool debug, float length, bool world, IServerRunSettings tuning)
    {
        var mode = world ? modes.Roaming : lobby.Mode;
        return new ServerRun(matchIds.Allocate(), lobby.Id, lobby.CreatorId, debug, length, world, tuning, mode,
            generate, clock,
            Send, world ? SetLoading : SetMatchLoading, phase => lobby.Phase = phase, () => ReturnToLobby(lobby), Log, TraceAuthority, codec);
    }

    // A frozen match roster must still name connected peers during construction. Preserve its strict lookup;
    // roaming admission uses the tolerant connection lookup below.
    private void SetMatchLoading(int peer, bool value)
    {
        if (clients[peer].Connection is { } connection) transport.SetLoading(connection, value);
    }

    private static void TraceAuthority(int run, AuthorityMessage result)
    {
        if (LaunchArgs.Current.Has("tank-authority-trace"))
        {
            var time = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
            var bodies = string.Join(" ", result.Bodies.Select(b => $"{b.Body.Id:x}:{b.Owner}:{b.Body.Epoch}:{b.Body.Sequence}:{b.State}:{b.Rest}"));
            Log(FormattableString.Invariant($"authority-trace match {run} time {time:0.000000} peer {result.Peer} request {result.Request} reason {result.Reason} bodies {bodies}"));
        }
    }

    private void SetLoading(int peer, bool value)
    {
        if (clients.GetValueOrDefault(peer)?.Connection is { } connection) transport.SetLoading(connection, value);
    }

    private static void Retire(ref ServerRun? routing)
    {
        var retiring = routing;
        routing = null;
        retiring?.Dispose();
    }

    private void RetireRuns()
    {
        foreach (var lobby in lobbies.Values)
        {
            Retire(ref lobby.Match);
            Retire(ref lobby.World);
        }
    }

    public override void _Notification(int what)
    {
        // Handler fixtures and failed startup can free a server that never entered the scene tree.
        if (what == NotificationPredelete) RetireRuns();
    }

    private void RetireExhaustedLobby(Lobby lobby)
    {
        // No roster mutation is possible at exhaustion. Detach peers before disconnect callbacks can
        // attempt to remove their actors, and retire the round once instead of throwing every frame.
        const string reason = "This lobby needs to close. Reconnect to create a new lobby.";
        lobbies.Remove(lobby.Id);
        Retire(ref lobby.Match);
        Retire(ref lobby.World);
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
