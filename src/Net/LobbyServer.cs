using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.World;

namespace Propane.Net;

/// <summary>
/// The multiplayer server. It runs no physics: it hosts lobbies, generates each match's suburb, relays messages
/// between the players in a match, keeps the score and the match clock, and settles ties (the first claim on a tank
/// or a pickup wins). It runs headless on a home server (<c>--server</c>), or inside a player's game for testing.
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
        public string Name = "Player";
        public int Color;
        public bool Welcomed;
        public Lobby? Lobby;
        public double KickAt;
    }

    private sealed class Lobby
    {
        public int Id;
        public int CreatorId;
        public readonly List<int> Members = new();
        public float MatchLength;
        public LobbyPhase Phase;
        public MatchRun? Match;
    }

    private sealed class MatchRun
    {
        public readonly List<int> Players = new();
        public readonly Dictionary<int, int> Scores = new();
        public readonly Dictionary<int, (string Name, int Color)> Profiles = new();
        public readonly HashSet<uint> Detonated = new();
        public readonly HashSet<int> Loaded = new();
        public int HostId;
        public double PhaseEnds;
        public double NextClock;
        public float Length;
        public Tuning Tuning = null!;
        public bool[] PickupUp = Array.Empty<bool>();
        public double[] PickupBack = Array.Empty<double>();
    }

    private readonly NetTransport transport = new();
    private readonly Dictionary<int, Client> clients = new();
    private readonly Dictionary<int, Lobby> lobbies = new();
    private readonly Random random = new();
    private int nextLobbyId = 1;

    public int Port { get; set; } = Protocol.DefaultPort;

    /// <summary>Simulated lag (seconds, each way) for testing.</summary>
    public double SimulatedLag
    {
        get => transport.SimulatedLag;
        set => transport.SimulatedLag = value;
    }

    public bool Running { get; private set; }

    /// <summary>Tanks every player starts a match with. 0 normally; tests set it to have something to lose.</summary>
    public int StartScore { get; set; }

    public Error Start()
    {
        transport.PeerConnected += OnConnected;
        transport.PeerDisconnected += OnDisconnected;
        transport.Received += OnReceived;
        var error = transport.Host(Port, MaxClients);
        Running = error == Error.Ok;
        Log(Running ? $"listening on UDP {Port} (version {BuildInfo.GameVersion}, build {BuildInfo.Build}, protocol {Protocol.Version})"
            : $"could not listen on UDP {Port}: {error}");
        return error;
    }

    public override void _ExitTree() => transport.Close();

    public override void _Process(double delta)
    {
        if (!Running)
        {
            return;
        }
        transport.Poll();
        var now = NetTransport.Now;
        foreach (var client in clients.Values.Where(c => c.KickAt > 0 && now >= c.KickAt).ToList())
        {
            client.KickAt = 0;
            transport.Kick(client.Id);
        }
        foreach (var lobby in lobbies.Values.ToList())
        {
            Tick(lobby, now);
        }
    }

    // ------------------------------------------------------------------ Connections

    private void OnConnected(int id)
    {
        clients[id] = new Client { Id = id };
        Log($"peer {id} connected");
    }

    private void OnDisconnected(int id)
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

    private void OnReceived(int from, byte[] data)
    {
        if (!clients.TryGetValue(from, out var client))
        {
            return;
        }
        var type = (Msg)data[0];
        try
        {
            if (!client.Welcomed && type != Msg.Hello)
            {
                return;
            }
            if (Protocol.IsRelay(type))
            {
                Relay(client, type, data);
                return;
            }
            var r = new NetReader(data);
            switch (type)
            {
                case Msg.Hello:
                    Hello(client, r);
                    break;
                case Msg.CreateLobby:
                    CreateLobby(client);
                    break;
                case Msg.JoinLobby:
                    JoinLobby(client, r.Int());
                    break;
                case Msg.LeaveLobby:
                    if (client.Lobby != null)
                    {
                        LeaveLobby(client);
                    }
                    break;
                case Msg.SetProfile:
                    SetProfile(client, r.String(), r.Int());
                    break;
                case Msg.SetMatchLength:
                    if (client.Lobby is { Phase: LobbyPhase.Waiting } lobby && lobby.CreatorId == client.Id)
                    {
                        lobby.MatchLength = Mathf.Clamp(r.Float(), 10f, 3600f);
                        SendLobbyState(lobby);
                    }
                    break;
                case Msg.StartMatch:
                    StartMatch(client, r);
                    break;
                case Msg.Loaded:
                    if (client.Lobby?.Match is { } match && client.Lobby.Phase == LobbyPhase.Loading)
                    {
                        match.Loaded.Add(client.Id);
                    }
                    break;
                case Msg.ClaimPickup:
                    ClaimPickup(client, r.Int());
                    break;
                case Msg.Ping:
                    transport.Send(client.Id, new NetWriter(Msg.Pong).Double(r.Double()).Double(NetTransport.Now), reliable: false);
                    break;
            }
        }
        catch (Exception e)
        {
            Log($"bad {type} message from {from}: {e.Message}");
        }
    }

    private void Hello(Client client, NetReader r)
    {
        var protocol = r.Int();
        var version = r.String();
        var build = r.String();
        var mismatch = BuildInfo.Mismatch(protocol, version, build);
        if (mismatch != null)
        {
            transport.Send(client.Id, new NetWriter(Msg.Rejected).String(mismatch), reliable: true);
            client.KickAt = NetTransport.Now + 1;
            Log($"peer {client.Id} turned away: {mismatch}");
            return;
        }
        client.Name = Protocol.CleanName(r.String());
        client.Color = Mathf.PosMod(r.Int(), Protocol.PlayerColors.Length);
        client.Welcomed = true;
        transport.Send(client.Id, new NetWriter(Msg.Welcome).Int(client.Id).String(BuildInfo.GameVersion), reliable: true);
        SendLobbyList(client);
        Log($"peer {client.Id} is {client.Name}");
    }

    // ------------------------------------------------------------------ Lobbies

    private void CreateLobby(Client client)
    {
        if (client.Lobby != null)
        {
            LeaveLobby(client);
        }
        var lobby = new Lobby { Id = nextLobbyId++, CreatorId = client.Id, MatchLength = Tuning.Current.MatchLengthDefault };
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
        if (lobby.Members.Count >= Tuning.Current.MaxPlayers)
        {
            Notice(client, "That lobby is full.");
            return;
        }
        if (client.Lobby == lobby)
        {
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
        lobby.Members.Add(client.Id);
        client.Lobby = lobby;
        client.Color = FreeColor(lobby, client.Color, client.Id);
        SendLobbyState(lobby);
        BroadcastLobbyList();
    }

    private void LeaveLobby(Client client)
    {
        var lobby = client.Lobby!;
        client.Lobby = null;
        lobby.Members.Remove(client.Id);
        if (lobby.Match is { } match && match.Players.Remove(client.Id))
        {
            if (match.HostId == client.Id && match.Players.Count > 0)
            {
                match.HostId = match.Players[0];
            }
            foreach (var id in match.Players)
            {
                transport.Send(id, new NetWriter(Msg.PlayerLeft).Int(client.Id).Int(match.HostId), reliable: true);
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

    private void SetProfile(Client client, string name, int color)
    {
        client.Name = Protocol.CleanName(name);
        client.Color = client.Lobby != null ? FreeColor(client.Lobby, Mathf.PosMod(color, Protocol.PlayerColors.Length), client.Id)
            : Mathf.PosMod(color, Protocol.PlayerColors.Length);
        if (client.Lobby != null)
        {
            SendLobbyState(client.Lobby);
            BroadcastLobbyList();
        }
    }

    /// <summary>The wanted colour if nobody else in the lobby has it, otherwise the first free one.</summary>
    private int FreeColor(Lobby lobby, int wanted, int self)
    {
        var taken = lobby.Members.Where(id => id != self && clients.ContainsKey(id)).Select(id => clients[id].Color).ToHashSet();
        if (!taken.Contains(wanted))
        {
            return wanted;
        }
        for (var i = 0; i < Protocol.PlayerColors.Length; i++)
        {
            if (!taken.Contains(i))
            {
                return i;
            }
        }
        return wanted;
    }

    private void SendLobbyState(Lobby lobby)
    {
        var w = new NetWriter(Msg.LobbyState).Int(lobby.Id).Int(lobby.CreatorId).Float(lobby.MatchLength).Byte((byte)lobby.Phase)
            .Int(lobby.Members.Count);
        foreach (var id in lobby.Members)
        {
            var member = clients.GetValueOrDefault(id);
            w.Int(id).String(member?.Name ?? "?").Int(member?.Color ?? 0);
        }
        var bytes = w.ToArray();
        foreach (var id in lobby.Members)
        {
            transport.Send(id, bytes, reliable: true);
        }
    }

    private byte[] LobbyList()
    {
        var w = new NetWriter(Msg.LobbyList).Int(Tuning.Current.MaxPlayers).Int(lobbies.Count);
        foreach (var lobby in lobbies.Values.OrderBy(l => l.Id))
        {
            var creator = clients.GetValueOrDefault(lobby.CreatorId);
            w.Int(lobby.Id).String(creator?.Name ?? "?").Int(lobby.Members.Count).Byte((byte)lobby.Phase);
        }
        return w.ToArray();
    }

    private void SendLobbyList(Client client) => transport.Send(client.Id, LobbyList(), reliable: true);

    private void BroadcastLobbyList()
    {
        var bytes = LobbyList();
        foreach (var client in clients.Values.Where(c => c.Welcomed && c.Lobby == null))
        {
            transport.Send(client.Id, bytes, reliable: true);
        }
    }

    private void Notice(Client client, string text) => transport.Send(client.Id, new NetWriter(Msg.Notice).String(text), reliable: true);

    // ------------------------------------------------------------------ Match flow

    private void StartMatch(Client client, NetReader r)
    {
        if (client.Lobby is not { Phase: LobbyPhase.Waiting } lobby || lobby.CreatorId != client.Id)
        {
            return;
        }
        // Everyone plays on the creator's tuning: the baked values in a release build, the live ones in debug.
        var snapshot = GD.BytesToVar(r.Bytes()).AsGodotDictionary();
        var tuning = Tuning.FromSnapshot(snapshot);
        var match = new MatchRun { Tuning = tuning, Length = lobby.MatchLength, HostId = client.Id };
        foreach (var id in lobby.Members)
        {
            match.Players.Add(id);
            match.Scores[id] = StartScore;
            match.Profiles[id] = (clients[id].Name, clients[id].Color);
        }

        var started = Time.GetTicksMsec();
        var seed = random.Next();
        var settings = SuburbPlans.Match(tuning, Mathf.Max(match.Players.Count, tuning.MaxPlayers));
        var plan = SuburbPlans.Generate(settings, seed, () => random.Next());
        var planBytes = PlanCodec.Encode(plan);
        match.PickupUp = Enumerable.Repeat(true, plan.AmmoSpots.Count).ToArray();
        match.PickupBack = new double[plan.AmmoSpots.Count];
        Log($"lobby {lobby.Id}: match for {match.Players.Count} on seed {plan.Seed} ({plan.Lots.Count} lots, {plan.Tanks.Count} tanks, " +
            $"{plan.AmmoSpots.Count} pickups, {planBytes.Length / 1024f:0.0} KB plan, generated in {Time.GetTicksMsec() - started} ms)");

        var w = new NetWriter(Msg.MatchStart).Bytes(planBytes).Int(match.HostId).Float(match.Length).Bytes(GD.VarToBytes(snapshot))
            .Int(match.Players.Count);
        for (var i = 0; i < match.Players.Count; i++)
        {
            var id = match.Players[i];
            w.Int(id).String(match.Profiles[id].Name).Int(match.Profiles[id].Color).Int(i);
        }
        var bytes = w.ToArray();
        foreach (var id in match.Players)
        {
            transport.Send(id, bytes, reliable: true);
        }
        lobby.Match = match;
        lobby.Phase = LobbyPhase.Loading;
        match.PhaseEnds = NetTransport.Now + LoadTimeout;
        SendLobbyState(lobby);
        BroadcastLobbyList();
    }

    private void Tick(Lobby lobby, double now)
    {
        if (lobby.Match is not { } match)
        {
            return;
        }
        switch (lobby.Phase)
        {
            case LobbyPhase.Loading:
                if (match.Players.All(match.Loaded.Contains) || now >= match.PhaseEnds)
                {
                    lobby.Phase = LobbyPhase.Countdown;
                    match.PhaseEnds = now + match.Tuning.CountdownTime;
                    Broadcast(match, new NetWriter(Msg.Countdown).Float(match.Tuning.CountdownTime));
                }
                break;
            case LobbyPhase.Countdown:
                if (now >= match.PhaseEnds)
                {
                    lobby.Phase = LobbyPhase.Playing;
                    match.PhaseEnds = now + match.Length;
                    match.NextClock = now + 1;
                    Broadcast(match, new NetWriter(Msg.MatchGo).Float(match.Length));
                    foreach (var (id, score) in match.Scores.Where(s => s.Value != 0))
                    {
                        Broadcast(match, new NetWriter(Msg.Score).Int(id).Int(score).Int(0));
                    }
                    Log($"lobby {lobby.Id}: match on");
                }
                break;
            case LobbyPhase.Playing:
                for (var i = 0; i < match.PickupUp.Length; i++)
                {
                    if (!match.PickupUp[i] && now >= match.PickupBack[i])
                    {
                        match.PickupUp[i] = true;
                        Broadcast(match, new NetWriter(Msg.PickupRespawn).Int(i));
                    }
                }
                if (now >= match.NextClock)
                {
                    match.NextClock += 1;
                    Broadcast(match, new NetWriter(Msg.Clock).Float((float)Math.Max(0, match.PhaseEnds - now)), reliable: false);
                }
                if (now >= match.PhaseEnds)
                {
                    lobby.Phase = LobbyPhase.Results;
                    match.PhaseEnds = now + match.Tuning.ResultsTime;
                    var w = new NetWriter(Msg.MatchEnd).Int(match.Scores.Count);
                    foreach (var (id, score) in match.Scores.OrderByDescending(s => s.Value))
                    {
                        w.Int(id).String(match.Profiles[id].Name).Int(match.Profiles[id].Color).Int(score).Bool(match.Players.Contains(id));
                    }
                    Broadcast(match, w);
                    Log($"lobby {lobby.Id}: match over, " + string.Join(", ", match.Scores.OrderByDescending(s => s.Value).Select(s => $"{match.Profiles[s.Key].Name} {s.Value}")));
                }
                break;
            case LobbyPhase.Results:
                if (now >= match.PhaseEnds)
                {
                    lobby.Phase = LobbyPhase.Waiting;
                    lobby.Match = null;
                    SendLobbyState(lobby);
                    BroadcastLobbyList();
                }
                break;
        }
    }

    private void ClaimPickup(Client client, int index)
    {
        if (client.Lobby is not { Phase: LobbyPhase.Playing, Match: { } match } || index < 0 || index >= match.PickupUp.Length || !match.PickupUp[index])
        {
            return;
        }
        match.PickupUp[index] = false;
        match.PickupBack[index] = NetTransport.Now + match.Tuning.PickupRespawnTime;
        Broadcast(match, new NetWriter(Msg.PickupTaken).Int(index).Int(client.Id));
    }

    // ------------------------------------------------------------------ Relay

    /// <summary>
    /// Passes a match message on to the other players with the sender's id inserted. Claims go back to the sender
    /// too, so every client sees all claims in the server's order. Explosions and drops also change the score.
    /// </summary>
    private void Relay(Client client, Msg type, byte[] data)
    {
        if (client.Lobby is not { Match: { } match } lobby || !match.Players.Contains(client.Id) ||
            lobby.Phase is not (LobbyPhase.Countdown or LobbyPhase.Playing or LobbyPhase.Results))
        {
            return;
        }
        var includeSender = false;
        switch (type)
        {
            case Msg.Explode:
            {
                // The first claim on a tank wins its point; later claims on the same tank are dropped.
                var tank = new NetReader(data).UInt();
                if (!match.Detonated.Add(tank))
                {
                    return;
                }
                if (lobby.Phase == LobbyPhase.Playing)
                {
                    ChangeScore(match, client.Id, 1);
                }
                break;
            }
            case Msg.Drop:
            {
                var r = new NetReader(data);
                r.Byte();
                var count = r.Int();
                if (lobby.Phase == LobbyPhase.Playing)
                {
                    ChangeScore(match, client.Id, -count);
                }
                break;
            }
            case Msg.Claim:
                includeSender = true;
                break;
            case Msg.TuningSet:
            {
                var r = new NetReader(data);
                var name = r.String();
                var value = GD.BytesToVar(r.Bytes());
                match.Tuning.Apply(new Godot.Collections.Dictionary { [name] = value });
                break;
            }
        }
        var w = new byte[data.Length + 4];
        w[0] = data[0];
        BitConverter.TryWriteBytes(new Span<byte>(w, 1, 4), client.Id);
        Buffer.BlockCopy(data, 1, w, 5, data.Length - 1);
        var reliable = type is not (Msg.PlayerState or Msg.BodyStates);
        foreach (var id in match.Players)
        {
            if (id != client.Id || includeSender)
            {
                transport.Send(id, w, reliable);
            }
        }
    }

    private void ChangeScore(MatchRun match, int player, int delta)
    {
        if (!match.Scores.TryGetValue(player, out var score))
        {
            return;
        }
        var next = Math.Max(0, score + delta);
        match.Scores[player] = next;
        Broadcast(match, new NetWriter(Msg.Score).Int(player).Int(next).Int(next - score));
    }

    private void Broadcast(MatchRun match, NetWriter message, bool reliable = true)
    {
        var bytes = message.ToArray();
        foreach (var id in match.Players)
        {
            transport.Send(id, bytes, reliable);
        }
    }

    private static void Log(string text) => GD.Print($"[server {Time.GetDatetimeStringFromSystem()}] {text}");
}
