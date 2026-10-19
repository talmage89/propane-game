using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.Net;

/// <summary>
/// The client's connection to the server: connects and introduces itself, follows the lobby list and the lobby it is
/// in, and hands everything about a running match to whoever listens to <see cref="AttachMatch"/>.
/// </summary>
public partial class NetClient : Node
{
    public enum Status
    {
        Offline,
        Connecting,
        Connected,
    }

    private const double PingInterval = 1.0;
    private const double ConnectTimeout = 8.0;

    private readonly IPacketTransport transport;
    private readonly PacketCodec codec;
    private readonly IMonotonicClock clock;
    private ConnectionHandle? server;
    // A send origin may still be iterating session state. Retire only after that stack has returned, at the next
    // poll boundary or deferred call in this frame. Disconnect/exit cancel this connection-scoped pending failure.
    private DisconnectReason? failedSend;

    // Godot script instantiation cannot select application-owned transport or codec inputs.
    public NetClient() => throw new InvalidOperationException("NetClient requires transport and codec inputs");
    public NetClient(IPacketTransport transport, PacketCodec codec, IMonotonicClock? clock = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        this.clock = clock ?? MonotonicClock.Instance;
    }
    private double connectStarted;
    private double nextPing;

    /// <summary>The version of the server that last turned this game away, when it said.</summary>
    public string? RejectedBy { get; private set; }

    public Status State { get; private set; } = Status.Offline;

    public int MyId { get; private set; }

    public string PlayerName { get; private set; } = "Player";

    public AppearanceRecord Appearance { get; private set; } = AppearanceRecord.FromPalette(0);

    public IReadOnlyList<LobbySummary> Lobbies { get; private set; } = Array.Empty<LobbySummary>();

    public int LobbyCapacity { get; private set; } = 5;

    public LobbyInfo? Lobby { get; private set; }

    public uint RosterRevision { get; private set; }
    public IReadOnlyList<PlayerProfile> Roster { get; private set; } = Array.Empty<PlayerProfile>();
    private uint nextActorRequest;
    public event Action<ActorRequestResultMessage>? ActorRequestCompleted;

    public bool IsCreator => Lobby != null && Lobby.CreatorId == MyId;

    /// <summary>
    /// Whether this game enters its lobby's world. Off (the classic lobby menu), world starts are acknowledged and
    /// ignored: this player's character stands still at its arrival point for the others.
    /// </summary>
    public bool LobbyWorlds { get; set; } = !LaunchArgs.Current.Has("classic-lobby");

    /// <summary>While the lobby stages a start: when (on this game's clock) the match begins.</summary>
    public double StageEnds { get; private set; }

    /// <summary>Seconds left on the stage's countdown, or zero when the lobby is not staging.</summary>
    public double StageLeft => Lobby is { Phase: LobbyPhase.Staging } ? Math.Max(0, StageEnds - clock.Seconds) : 0;

    /// <summary>Round trip to the server in milliseconds, measured with pings.</summary>
    public double PingMs { get; private set; }

    /// <summary>The server's clock minus ours, from the last ping.</summary>
    public double ServerClockOffset { get; private set; }

    /// <summary>Sampling time on the estimated server clock; zero until a ping establishes the offset.</summary>
    internal double BodySampleTime(double now) => PingMs > 0 ? now + ServerClockOffset : 0;

    public PacketCodec Codec => codec;
    public IPacketTransport Transport => transport;
    public TransportDiagnostics Diagnostics => server != null ? transport.Diagnostics(server) : default;

    public DisconnectReason? LastDisconnect { get; private set; }

    /// <summary>Connection, welcome, rejection or loss. The string is a reason to show, if any.</summary>
    public event Action<Status, string?>? StatusChanged;

    public event Action? LobbiesChanged;

    public event Action? LobbyChanged;

    /// <summary>A message the server wants shown (lobby full, closed...).</summary>
    public event Action<string>? NoticeReceived;

    public event Action<MatchSetup>? MatchStarting;

    private readonly MatchInbox inbox = new();

    public int ActiveMatchId => inbox.MatchId;
    public (int Packets, int Bytes, int Overflows) InboxPressure => (inbox.PeakPackets, inbox.PeakBytes, inbox.Overflows);

    /// <summary>Hands queued and subsequent messages to a fully initialized match.</summary>
    public bool AttachMatch(int id, Action<NetMessage> receiver)
    {
        try { return inbox.Attach(id, receiver); }
        catch (Exception error) { Fail("inbox replay", error); return false; }
    }

    public void DetachMatch(int id, Action<NetMessage> receiver) => inbox.Detach(id, receiver);

    /// <summary>Composition abandons an active or still-building failed run; an old owner cannot clear a newer inbox.</summary>
    internal void RetireMatchInbox(int id)
    {
        if (inbox.MatchId == id) inbox.Clear();
    }

    private int lastMatchId;

    public override void _Ready()
    {
        transport.Connected += OnTransportConnected;
        transport.Disconnected += OnTransportDisconnected;
        transport.Packet += OnPacket;
    }

    private void OnTransportConnected(ConnectionHandle connection)
    {
        if (connection == server)
        {
            Send(new NetWriter(Msg.Hello).Int((int)LaunchArgs.Current.GetFloat("fake-protocol", Protocol.Version)).String(BuildInfo.GameVersion).String(BuildInfo.Build).String(PlayerName).Int(Appearance.LegacyIndex).Bool(OS.IsDebugBuild()).Appearance(Appearance));
        }
    }

    private void OnTransportDisconnected(ConnectionHandle connection, DisconnectReason reason)
    {
        if (connection == server) { Drop(reason); }
    }

    private void OnPacket(ConnectionHandle connection, TrafficClass traffic, ReadOnlyMemory<byte> data)
    {
        if (connection == server && failedSend == null) { Handle(data.ToArray()); }
    }

    public override void _ExitTree()
    {
        transport.Connected -= OnTransportConnected;
        transport.Disconnected -= OnTransportDisconnected;
        transport.Packet -= OnPacket;
        server = null;
        failedSend = null;
        inbox.Clear();
        transport.Close();
    }

    public void Connect(string address, int port, string name, int color) => Connect(address, port, name, AppearanceRecord.FromPalette(color));

    public void Connect(string address, int port, string name, AppearanceRecord appearance)
    {
        Disconnect();
        PlayerName = Protocol.CleanName(name);
        Appearance = appearance;
        lastMatchId = 0;
        LastDisconnect = null;
        RejectedBy = null;
        var result = transport.Connect(new PacketEndpoint(address, port), out server);
        if (!result.Success)
        {
            Drop(new DisconnectReason(DisconnectKind.TransportFailure, $"Could not connect to {address}:{port} ({result.Detail}).", result.BackendCode));
            return;
        }
        connectStarted = clock.Seconds;
        SetState(Status.Connecting, null);
    }

    public void Disconnect()
    {
        failedSend = null;
        PingMs = 0;
        ServerClockOffset = 0;
        nextPing = 0;
        if (State == Status.Offline && server == null)
        {
            return;
        }
        LastDisconnect = DisconnectReason.LocalClose;
        server = null;
        transport.Close();
        inbox.Clear();
        Lobby = null;
        Roster = Array.Empty<PlayerProfile>();
        RosterRevision = 0;
        MyId = 0;
        SetState(Status.Offline, null);
    }

    public override void _Process(double delta) => PollNetwork();

    // Catch-up physics can keep running between scarce process frames. Service packet delivery
    // and ENet ACKs there too; otherwise real-time publications still queue behind a slow frame.
    public override void _PhysicsProcess(double delta) => PollNetwork();

    private void PollNetwork()
    {
        if (failedSend != null) { RetireFailedSend(); return; }
        try
        {
            transport.Poll();
            var now = clock.Seconds;
            if (State == Status.Connecting && now - connectStarted > ConnectTimeout)
            {
                Drop(new DisconnectReason(DisconnectKind.Timeout, "The server did not answer. Check the address, the port and that the server is running."));
            }
            if (State == Status.Connected && now >= nextPing)
            {
                nextPing = now + PingInterval;
                Send(new NetWriter(Msg.Ping).Double(now), TrafficClass.UnreliablePlayerPoses);
            }
        }
        catch (Exception error) { Fail("transport poll", error); }
        if (failedSend != null) RetireFailedSend();
    }

    public SendResult Send(NetWriter message, TrafficClass traffic = TrafficClass.ReliableOrdered) => Send(message.ToArray(), traffic);

    public SendResult Send(byte[] message, TrafficClass traffic = TrafficClass.ReliableOrdered)
    {
        if (server == null || failedSend != null) { return SendResult.Closed; }
        try
        {
            var result = transport.Send(server, traffic, message);
            if (result == SendResult.Success && message.Length > 0 && (Msg)message[0] is Msg.Loaded or Msg.LeaveLobby)
            {
                transport.SetLoading(server, false);
            }
            if (traffic.IsReliable() && result != SendResult.Success)
            {
                QueueSendFailure($"Could not send a required message ({result}). Please reconnect.");
            }
            return result;
        }
        catch (Exception error)
        {
            GD.Print($"[net] transport send failure ({traffic}): {error}");
            QueueSendFailure("The connection could not send a message. Please reconnect.");
            return SendResult.Closed;
        }
    }

    private void QueueSendFailure(string detail)
    {
        if (failedSend != null) return;
        GD.Print($"[net] {detail}");
        failedSend = new DisconnectReason(DisconnectKind.TransportFailure, detail);
        // Preserve immediate connection-failure observation; only the cleanup notification retires borrowed scenes.
        LastDisconnect = failedSend;
        State = Status.Offline;
        Callable.From(RetireFailedSend).CallDeferred();
    }

    private void RetireFailedSend()
    {
        if (failedSend is not { } reason) return;
        failedSend = null;
        Drop(reason);
    }

    // ------------------------------------------------------------------ Lobby requests

    public void CreateLobby() => Send(new NetWriter(Msg.CreateLobby));

    public void JoinLobby(int id) => Send(new NetWriter(Msg.JoinLobby).Int(id));

    public void LeaveLobby()
    {
        Send(new NetWriter(Msg.LeaveLobby));
        inbox.Clear();
        Lobby = null;
        Roster = Array.Empty<PlayerProfile>();
        RosterRevision = 0;
        LobbyChanged?.Invoke();
    }

    public void SetProfile(string name, AppearanceRecord appearance)
    {
        PlayerName = Protocol.CleanName(name);
        Appearance = appearance;
        if (State == Status.Connected)
        {
            Send(new NetWriter(Msg.SetProfile).String(PlayerName).Appearance(Appearance));
        }
    }

    public void SetCapacity(int capacity) => Send(new NetWriter(Msg.SetCapacity).Int(capacity));
    public uint AddActor(ActorKind kind, string name, AppearanceRecord appearance, ControllerConfig config)
    {
        var request = checked(++nextActorRequest);
        Send(codec.Encode(new NetMessage(Msg.AddActor, 0, 0, new AddActorMessage(request, kind, name, appearance, config, RosterRevision)), false));
        return request;
    }
    public uint UpdateActor(int actorId, string name, AppearanceRecord appearance, ControllerConfig config)
    {
        var request = checked(++nextActorRequest);
        Send(codec.Encode(new NetMessage(Msg.UpdateActor, 0, 0, new UpdateActorMessage(request, actorId, name, appearance, config, RosterRevision)), false));
        return request;
    }
    public uint RemoveActor(int actorId)
    {
        var request = checked(++nextActorRequest);
        Send(codec.Encode(new NetMessage(Msg.RemoveActor, 0, 0, new RemoveActorMessage(request, actorId, RosterRevision)), false));
        return request;
    }

    public void SetMatchLength(float seconds) => Send(new NetWriter(Msg.SetMatchLength).Float(seconds));

    /// <summary>Asks the server to set the lobby's mode and its settings (creator only, while waiting).</summary>
    public void SetMode(ModeSettings settings) => Send(codec.Encode(new NetMessage(Msg.SetMode, 0, 0, settings), false));

    /// <summary>
    /// Asks the server to start the lobby's match (creator only), on this player's tuning: the values baked into a
    /// release build, or the live ones in a debug build.
    /// </summary>
    public Tuning Template { get; init; } = Tuning.SavedTemplate;

    /// <summary>Takes back a staged start (creator only, while the stage counts down).</summary>
    public void CancelStart() => Send(new NetWriter(Msg.CancelStart));

    public void StartMatch()
    {
        using var tuning = OS.IsDebugBuild() ? Template.Copy() : Tuning.LoadBaked();
        if (Lobby is { } lobby)
        {
            tuning.KingMintSeconds = lobby.Mode.MintSeconds;
            tuning.TankCountMultiplier = lobby.Mode.TankSupply;
        }
        Send(new NetWriter(Msg.StartMatch).Snapshot(tuning.Snapshot()));
    }

    // ------------------------------------------------------------------ Incoming

    internal void Handle(byte[] data)
    {
        NetMessage message;
        try { message = codec.Decode(data, fromServer: true); }
        catch (Exception error) when (PacketCodec.IsDecodeFailure(error))
        {
            GD.PushWarning($"[net] bad message: {error.Message}");
            return;
        }
        catch (Exception error) { Fail("decode", error); return; }
        try
        {
            switch (message.Payload)
            {
                case WelcomeMessage welcome:
                    MyId = welcome.Id;
                    SetState(Status.Connected, null);
                    break;
                case RejectedMessage rejected:
                    RejectedBy = rejected.Version;
                    Drop(new DisconnectReason(DisconnectKind.ApplicationRejected, rejected.Reason));
                    break;
                case TextMessage notice:
                    NoticeReceived?.Invoke(notice.Value);
                    break;
                case LobbyListMessage list:
                    LobbyCapacity = list.Capacity;
                    Lobbies = list.Lobbies;
                    LobbiesChanged?.Invoke();
                    break;
                case LobbyInfo lobby:
                    if (Lobby?.Id == lobby.Id && lobby.RosterRevision < RosterRevision) break;
                    var me = lobby.Members.FirstOrDefault(m => m.OwnerPeerId == MyId && m.Kind == ActorKind.Human);
                    if (me != null) { Appearance = me.Appearance; PlayerName = me.Name; }
                    Lobby = lobby.Peers.Contains(MyId) ? lobby : null;
                    if (lobby.Phase == LobbyPhase.Staging) { StageEnds = clock.Seconds + lobby.StartsIn; }
                    if (Lobby != null) { Roster = lobby.Members; RosterRevision = lobby.RosterRevision; }
                    if (Lobby == null) { inbox.Clear(); }
                    LobbyChanged?.Invoke();
                    break;
                case RosterStateMessage roster:
                    if (Lobby?.Id != roster.LobbyId || roster.Revision <= RosterRevision) break;
                    Roster = roster.Actors;
                    RosterRevision = roster.Revision;
                    Lobby = Lobby with { Members = Roster, RosterRevision = RosterRevision };
                    LobbyChanged?.Invoke();
                    break;
                case ActorRequestResultMessage result:
                    ActorRequestCompleted?.Invoke(result);
                    break;
                case MatchSetup setup:
                    if (State != Status.Connected || Lobby == null || setup.Id <= lastMatchId || !setup.Players.Any(p => p.Player.OwnerPeerId == MyId)) { return; }
                    lastMatchId = setup.Id;
                    if (setup.ModeId == ModeIds.Lobby && !LobbyWorlds)
                    {
                        // The classic menu stays put; nothing of this world is buffered, and its loading grace ends now.
                        inbox.Clear();
                        Send(new NetWriter(Msg.Loaded, setup.Id));
                        return;
                    }
                    inbox.Begin(setup.Id);
                    if (server != null) { transport.SetLoading(server, true); }
                    MatchStarting?.Invoke(setup);
                    break;
                case PongMessage pong:
                    var now = clock.Seconds;
                    PingMs = (now - pong.Sent) * 1000;
                    ServerClockOffset = pong.ServerTime - (pong.Sent + now) / 2;
                    break;
                default:
                    if (!inbox.TryReceive(message, data.Length))
                        Drop(new DisconnectReason(DisconnectKind.TransportFailure, new MatchInbox.OverflowException().Message));
                    break;
            }
        }
        catch (Exception error) { Fail($"dispatch {message.Type}", error); }
    }

    private void Fail(string operation, Exception error)
    {
        GD.Print($"[net] owner failure during {operation}: {error}");
        Drop(new DisconnectReason(DisconnectKind.TransportFailure, "The local network session failed. Please reconnect."));
    }

    private void Drop(DisconnectReason reason)
    {
        failedSend = null;
        LastDisconnect = reason;
        PingMs = 0;
        ServerClockOffset = 0;
        nextPing = 0;
        if (server != null)
        {
            try { transport.Disconnect(server, reason); }
            catch (Exception error) { GD.Print($"[net] disconnect cleanup failure: {error}"); }
        }
        server = null;
        try { transport.Close(); }
        catch (Exception error) { GD.Print($"[net] transport cleanup failure: {error}"); }
        inbox.Clear();
        Lobby = null;
        Roster = Array.Empty<PlayerProfile>();
        RosterRevision = 0;
        MyId = 0;
        State = Status.Offline;
        // One broken observer must not suppress the application's normal session retirement callback.
        foreach (var callback in StatusChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<Status, string?>)callback)(Status.Offline, reason.Detail); }
            catch (Exception error) { GD.Print($"[net] offline notification failure: {error}"); }
        }
    }

    private void SetState(Status status, string? reason)
    {
        State = status;
        StatusChanged?.Invoke(status, reason);
    }
}
