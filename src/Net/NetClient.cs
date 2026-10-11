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
/// in, and hands everything about a running match to whoever listens to <see cref="MatchMessage"/>.
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

    private readonly NetTransport transport = new();
    private double connectStarted;
    private double nextPing;
    private string? lastError;

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

    public NetTransport Transport => transport;

    /// <summary>Connection, welcome, rejection or loss. The string is a reason to show, if any.</summary>
    public event Action<Status, string?>? StatusChanged;

    public event Action? LobbiesChanged;

    public event Action? LobbyChanged;

    /// <summary>A message the server wants shown (lobby full, closed...).</summary>
    public event Action<string>? NoticeReceived;

    public event Action<MatchSetup>? MatchStarting;

    /// <summary>Completely decoded messages about the current match.</summary>
    public event Action<NetMessage>? MatchMessage;

    public int ActiveMatchId { get; private set; }

    private int lastMatchId;

    public override void _Ready()
    {
        transport.PeerConnected += id =>
        {
            if (id == NetTransport.ServerId)
            {
                Send(new NetWriter(Msg.Hello).Int((int)Dev.DevArgs.GetFloat("fake-protocol", Protocol.Version)).String(BuildInfo.GameVersion).String(BuildInfo.Build).String(PlayerName).Int(Color).Bool(OS.IsDebugBuild()));
            }
        };
        transport.PeerDisconnected += id =>
        {
            if (id == NetTransport.ServerId)
            {
                Drop(lastError ?? "Lost the connection to the server.");
            }
        };
        transport.Received += (_, data) => Handle(data);
        var lag = Dev.DevArgs.GetFloat("net-lag", 0) / 1000f;
        transport.SimulatedLag = lag / 2;
        transport.SimulatedJitter = Dev.DevArgs.GetFloat("net-jitter", 0) / 1000f;
        transport.SimulatedLoss = Dev.DevArgs.GetFloat("net-loss", 0) / 100f;
    }

    public override void _ExitTree() => transport.Close();

    public void Connect(string address, int port, string name, int color)
    {
        Disconnect();
        PlayerName = Protocol.CleanName(name);
        Color = color;
        lastMatchId = 0;
        lastError = null;
        RejectedBy = null;
        var error = transport.Connect(address, port);
        if (error != Error.Ok)
        {
            Drop($"Could not connect to {address}:{port} ({error}).");
            return;
        }
        connectStarted = NetTransport.Now;
        SetState(Status.Connecting, null);
    }

    public void Disconnect()
    {
        if (State == Status.Offline)
        {
            return;
        }
        transport.Close();
        ActiveMatchId = 0;
        Lobby = null;
        MyId = 0;
        SetState(Status.Offline, null);
    }

    public override void _Process(double delta)
    {
        transport.Poll();
        var now = NetTransport.Now;
        if (State == Status.Connecting && now - connectStarted > ConnectTimeout)
        {
            Drop("The server did not answer. Check the address, the port and that the server is running.");
        }
        if (State == Status.Connected && now >= nextPing)
        {
            nextPing = now + PingInterval;
            Send(new NetWriter(Msg.Ping).Double(now), reliable: false);
        }
    }

    public void Send(NetWriter message, bool reliable = true) => transport.Send(NetTransport.ServerId, message, reliable);

    public void Send(byte[] message, bool reliable = true) => transport.Send(NetTransport.ServerId, message, reliable);

    // ------------------------------------------------------------------ Lobby requests

    public void CreateLobby() => Send(new NetWriter(Msg.CreateLobby));

    public void JoinLobby(int id) => Send(new NetWriter(Msg.JoinLobby).Int(id));

    public void LeaveLobby()
    {
        Send(new NetWriter(Msg.LeaveLobby));
        ActiveMatchId = 0;
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
                    lastError = rejected.Reason;
                    RejectedBy = rejected.Version;
                    Drop(lastError);
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
                    if (Lobby == null) { ActiveMatchId = 0; }
                    LobbyChanged?.Invoke();
                    break;
                case MatchSetup setup:
                    if (State != Status.Connected || Lobby == null || setup.Id <= lastMatchId || !setup.Players.Any(p => p.Player.Id == MyId)) { return; }
                    lastMatchId = ActiveMatchId = setup.Id;
                    MatchStarting?.Invoke(setup);
                    break;
                case PongMessage pong:
                    var now = NetTransport.Now;
                    PingMs = (now - pong.Sent) * 1000;
                    ServerClockOffset = pong.ServerTime - (pong.Sent + now) / 2;
                    break;
                default:
                    if (message.MatchId == ActiveMatchId && ActiveMatchId != 0) { MatchMessage?.Invoke(message); }
                    break;
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"[net] bad message: {e.Message}");
        }
    }

    private void Drop(string reason)
    {
        transport.Close();
        ActiveMatchId = 0;
        Lobby = null;
        MyId = 0;
        State = Status.Offline;
        StatusChanged?.Invoke(Status.Offline, reason);
    }

    private void SetState(Status status, string? reason)
    {
        State = status;
        StatusChanged?.Invoke(status, reason);
    }
}
