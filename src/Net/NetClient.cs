using System;
using System.Collections.Generic;
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
public sealed record MatchSetup(byte[] Plan, int HostId, float Length, Godot.Collections.Dictionary Tuning, IReadOnlyList<(PlayerProfile Player, int Spawn)> Players);

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

    public Status State { get; private set; } = Status.Offline;

    public int MyId { get; private set; }

    public string Name { get; private set; } = "Player";

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

    /// <summary>Every message about the running match, with its type and the reader positioned after the type byte.</summary>
    public event Action<Msg, NetReader>? MatchMessage;

    public override void _Ready()
    {
        transport.PeerConnected += id =>
        {
            if (id == NetTransport.ServerId)
            {
                Send(new NetWriter(Msg.Hello).Int(Protocol.Version).String(BuildInfo.GameVersion).String(BuildInfo.Build).String(Name).Int(Color));
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
        Name = Protocol.CleanName(name);
        Color = color;
        lastError = null;
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
        Lobby = null;
        LobbyChanged?.Invoke();
    }

    public void SetProfile(string name, int color)
    {
        Name = Protocol.CleanName(name);
        Color = color;
        if (State == Status.Connected)
        {
            Send(new NetWriter(Msg.SetProfile).String(Name).Int(Color));
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
        Send(new NetWriter(Msg.StartMatch).Bytes(GD.VarToBytes(tuning.Snapshot())));
    }

    // ------------------------------------------------------------------ Incoming

    private void Handle(byte[] data)
    {
        var type = (Msg)data[0];
        var r = new NetReader(data);
        try
        {
            switch (type)
            {
                case Msg.Welcome:
                    MyId = r.Int();
                    SetState(Status.Connected, null);
                    break;
                case Msg.Rejected:
                    lastError = r.String();
                    Drop(lastError);
                    break;
                case Msg.Notice:
                    NoticeReceived?.Invoke(r.String());
                    break;
                case Msg.LobbyList:
                {
                    LobbyCapacity = r.Int();
                    var list = new List<LobbySummary>();
                    for (int i = 0, n = r.Int(); i < n; i++)
                    {
                        list.Add(new LobbySummary(r.Int(), r.String(), r.Int(), (LobbyServer.LobbyPhase)r.Byte()));
                    }
                    Lobbies = list;
                    LobbiesChanged?.Invoke();
                    break;
                }
                case Msg.LobbyState:
                {
                    var id = r.Int();
                    var creator = r.Int();
                    var length = r.Float();
                    var phase = (LobbyServer.LobbyPhase)r.Byte();
                    var members = new List<PlayerProfile>();
                    for (int i = 0, n = r.Int(); i < n; i++)
                    {
                        members.Add(new PlayerProfile(r.Int(), r.String(), r.Int()));
                    }
                    var me = members.Find(m => m.Id == MyId);
                    if (me != null)
                    {
                        // The server may have changed the colour if someone else had it.
                        Color = me.Color;
                        Name = me.Name;
                    }
                    Lobby = me != null ? new LobbyInfo(id, creator, length, phase, members) : null;
                    LobbyChanged?.Invoke();
                    break;
                }
                case Msg.MatchStart:
                {
                    var plan = r.Bytes();
                    var host = r.Int();
                    var length = r.Float();
                    var tuning = GD.BytesToVar(r.Bytes()).AsGodotDictionary();
                    var players = new List<(PlayerProfile, int)>();
                    for (int i = 0, n = r.Int(); i < n; i++)
                    {
                        players.Add((new PlayerProfile(r.Int(), r.String(), r.Int()), r.Int()));
                    }
                    MatchStarting?.Invoke(new MatchSetup(plan, host, length, tuning, players));
                    break;
                }
                case Msg.Pong:
                {
                    var sent = r.Double();
                    var serverTime = r.Double();
                    var now = NetTransport.Now;
                    PingMs = (now - sent) * 1000.0;
                    ServerClockOffset = serverTime - (sent + now) / 2;
                    break;
                }
                default:
                    MatchMessage?.Invoke(type, r);
                    break;
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"[net] bad {type} message: {e.Message}");
        }
    }

    private void Drop(string reason)
    {
        transport.Close();
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
