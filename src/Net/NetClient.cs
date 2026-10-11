using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.Net;

/// <summary>A lobby as the lobby browser lists it.</summary>
public sealed record LobbySummary(int Id, string CreatorName, int Players, LobbyServer.LobbyPhase Phase);

/// <summary>A player in a lobby or match.</summary>
public sealed record PlayerProfile(int Id, string Name, int Color);

/// <summary>The lobby the player is in, as the server last described it.</summary>
public sealed record LobbyInfo(int Id, int CreatorId, float MatchLength, LobbyServer.LobbyPhase Phase, IReadOnlyList<PlayerProfile> Members);

/// <summary>Everything a client needs to start a match.</summary>
public sealed record MatchSetup(int Id, byte[] Plan, int HostId, bool CreatorDebug, float Length, Godot.Collections.Dictionary Tuning, IReadOnlyList<(PlayerProfile Player, int Spawn)> Players);

/// <summary>A final standing, from the server.</summary>
public sealed record Standing(int Id, string Name, int Color, int Score, bool Present);

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
    private readonly IMonotonicClock clock;
    private ConnectionHandle? server;

    public NetClient() : this(PacketTransports.CreateClient()) { }
    public NetClient(IPacketTransport transport, IMonotonicClock? clock = null)
    {
        this.transport = transport;
        this.clock = clock ?? MonotonicClock.Instance;
    }
    private double connectStarted;
    private double nextPing;

    /// <summary>The version of the server that last turned this game away, when it said.</summary>
    public string? RejectedBy { get; private set; }

    public Status State { get; private set; } = Status.Offline;

    public int MyId { get; private set; }

    public string PlayerName { get; private set; } = "Player";

    public int Color { get; private set; }

    public IReadOnlyList<LobbySummary> Lobbies { get; private set; } = Array.Empty<LobbySummary>();

    public int LobbyCapacity { get; private set; } = 5;

    public LobbyInfo? Lobby { get; private set; }

    public bool IsCreator => Lobby != null && Lobby.CreatorId == MyId;

    /// <summary>Round trip to the server in milliseconds, measured with pings.</summary>
    public double PingMs { get; private set; }

    /// <summary>The server's clock minus ours, from the last ping.</summary>
    public double ServerClockOffset { get; private set; }

    /// <summary>Sampling time on the estimated server clock; zero until a ping establishes the offset.</summary>
    internal double BodySampleTime(double now) => PingMs > 0 ? now + ServerClockOffset : 0;

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

    /// <summary>Hands queued and subsequent messages to a fully initialized match.</summary>
    public bool AttachMatch(int id, Action<NetMessage> receiver) => inbox.Attach(id, receiver);

    public void DetachMatch(int id, Action<NetMessage> receiver) => inbox.Detach(id, receiver);

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
            Send(new NetWriter(Msg.Hello).Int((int)Dev.DevArgs.GetFloat("fake-protocol", Protocol.Version)).String(BuildInfo.GameVersion).String(BuildInfo.Build).String(PlayerName).Int(Color).Bool(OS.IsDebugBuild()));
        }
    }

    private void OnTransportDisconnected(ConnectionHandle connection, DisconnectReason reason)
    {
        if (connection == server) { Drop(reason); }
    }

    private void OnPacket(ConnectionHandle connection, TrafficClass traffic, ReadOnlyMemory<byte> data)
    {
        if (connection == server) { Handle(data.ToArray()); }
    }

    public override void _ExitTree()
    {
        transport.Connected -= OnTransportConnected;
        transport.Disconnected -= OnTransportDisconnected;
        transport.Packet -= OnPacket;
        server = null;
        inbox.Clear();
        transport.Close();
    }

    public void Connect(string address, int port, string name, int color)
    {
        Disconnect();
        PlayerName = Protocol.CleanName(name);
        Color = color;
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
        PingMs = 0;
        ServerClockOffset = 0;
        nextPing = 0;
        if (State == Status.Offline)
        {
            return;
        }
        LastDisconnect = DisconnectReason.LocalClose;
        server = null;
        transport.Close();
        inbox.Clear();
        Lobby = null;
        MyId = 0;
        SetState(Status.Offline, null);
    }

    public override void _Process(double delta)
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

    public SendResult Send(NetWriter message, TrafficClass traffic = TrafficClass.ReliableOrdered) => Send(message.ToArray(), traffic);

    public SendResult Send(byte[] message, TrafficClass traffic = TrafficClass.ReliableOrdered)
    {
        if (server == null) { return SendResult.Closed; }
        var result = transport.Send(server, traffic, message);
        if (traffic == TrafficClass.ReliableOrdered && result != SendResult.Success)
        {
            Drop(new DisconnectReason(DisconnectKind.TransportFailure, $"Could not send a required message ({result}). Please reconnect."));
        }
        return result;
    }

    // ------------------------------------------------------------------ Lobby requests

    public void CreateLobby() => Send(new NetWriter(Msg.CreateLobby));

    public void JoinLobby(int id) => Send(new NetWriter(Msg.JoinLobby).Int(id));

    public void LeaveLobby()
    {
        Send(new NetWriter(Msg.LeaveLobby));
        inbox.Clear();
        Lobby = null;
        LobbyChanged?.Invoke();
    }

    public void SetProfile(string name, int color)
    {
        PlayerName = Protocol.CleanName(name);
        Color = color;
        if (State == Status.Connected)
        {
            Send(new NetWriter(Msg.SetProfile).String(PlayerName).Int(Color));
        }
    }

    public void SetMatchLength(float seconds) => Send(new NetWriter(Msg.SetMatchLength).Float(seconds));

    /// <summary>
    /// Asks the server to start the lobby's match (creator only), on this player's tuning: the values baked into a
    /// release build, or the live ones in a debug build.
    /// </summary>
    public void StartMatch()
    {
        var tuning = OS.IsDebugBuild() ? Tuning.Current : Tuning.LoadBaked();
        Send(new NetWriter(Msg.StartMatch).Snapshot(tuning.Snapshot()));
    }

    // ------------------------------------------------------------------ Incoming

    internal void Handle(byte[] data)
    {
        try
        {
            var message = MessageCodec.Decode(data, fromServer: true);
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
                    var me = lobby.Members.FirstOrDefault(m => m.Id == MyId);
                    if (me != null) { Color = me.Color; PlayerName = me.Name; }
                    Lobby = me != null ? lobby : null;
                    if (Lobby == null) { inbox.Clear(); }
                    LobbyChanged?.Invoke();
                    break;
                case MatchSetup setup:
                    if (State != Status.Connected || Lobby == null || setup.Id <= lastMatchId || !setup.Players.Any(p => p.Player.Id == MyId)) { return; }
                    lastMatchId = setup.Id;
                    inbox.Begin(setup.Id);
                    MatchStarting?.Invoke(setup);
                    break;
                case PongMessage pong:
                    var now = clock.Seconds;
                    PingMs = (now - pong.Sent) * 1000;
                    ServerClockOffset = pong.ServerTime - (pong.Sent + now) / 2;
                    break;
                default:
                    inbox.Receive(message, data.Length);
                    break;
            }
        }
        catch (MatchInbox.OverflowException e)
        {
            Drop(new DisconnectReason(DisconnectKind.TransportFailure, e.Message));
        }
        catch (Exception e)
        {
            GD.PushWarning($"[net] bad message: {e.Message}");
        }
    }

    private void Drop(DisconnectReason reason)
    {
        LastDisconnect = reason;
        PingMs = 0;
        ServerClockOffset = 0;
        nextPing = 0;
        if (server != null) { transport.Disconnect(server, reason); }
        server = null;
        transport.Close();
        inbox.Clear();
        Lobby = null;
        MyId = 0;
        State = Status.Offline;
        StatusChanged?.Invoke(Status.Offline, reason.Detail);
    }

    private void SetState(Status status, string? reason)
    {
        State = status;
        StatusChanged?.Invoke(status, reason);
    }
}
