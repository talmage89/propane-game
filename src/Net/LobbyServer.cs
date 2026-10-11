using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.World;

namespace Propane.Net;

/// <summary>
/// The multiplayer server. It runs no physics: it hosts lobbies, generates each match's suburb, relays messages
/// between the players in a match, keeps the score and the match clock, and settles ties (the first claim on a tank
/// or a pickup wins). It runs headless on a dedicated server (<c>--server</c>), or inside a player's game for testing.
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
        public bool Debug;
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
        public int Id;
        public int CreatorId;
        public bool CreatorDebug;
        public TankLedger Tanks = null!;
        public readonly HashSet<int> LooseProps = new();
        public readonly Dictionary<int, int> Slots = new();
        public readonly Dictionary<int, (int Gained, int LostToHits, int LostToThrows)> Stats = new();
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
    private int nextMatchId = 1;

    // Injectable packet sink for headless tests; production always uses ENet.
    internal Action<int, byte[], bool>? SendOverride { get; set; }

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
        AdvanceMatches(now);
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

    internal void OnReceived(int from, byte[] data)
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
                Relay(client, message);
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
                    if (client.Lobby == null || client.Lobby.Phase == LobbyPhase.Waiting) { SetProfile(client, profile.Name, profile.Color); }
                    break;
                case Msg.SetMatchLength:
                    if (client.Lobby is { Phase: LobbyPhase.Waiting } lobby && lobby.CreatorId == client.Id)
                    {
                        lobby.MatchLength = ((FloatMessage)message.Payload).Value;
                        SendLobbyState(lobby);
                    }
                    break;
                case Msg.StartMatch: StartMatch(client, (Godot.Collections.Dictionary)message.Payload); break;
                case Msg.Loaded:
                    if (client.Lobby is { Phase: LobbyPhase.Loading, Match: { } match }) { match.Loaded.Add(client.Id); }
                    break;
                case Msg.ClaimPickup: ClaimPickup(client, ((IntMessage)message.Payload).Value); break;
                case Msg.Ping:
                    Send(client.Id, new NetWriter(Msg.Pong).Double(((TimeMessage)message.Payload).Value).Double(NetTransport.Now), reliable: false);
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
            Send(client.Id, new NetWriter(Msg.Rejected).String(mismatch).String(BuildInfo.GameVersion), reliable: true);
            client.KickAt = NetTransport.Now + 1;
            Log($"peer {client.Id} ({hello.Version}, build {hello.Build}) turned away: {mismatch}");
            return;
        }
        client.Name = Protocol.CleanName(hello.Name);
        client.Color = hello.Color;
        client.Debug = hello.Debug;
        client.Welcomed = true;
        Send(client.Id, new NetWriter(Msg.Welcome).Int(client.Id).String(BuildInfo.GameVersion), reliable: true);
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
            var transfers = match.Players.Count > 0 ? match.Tanks.Transfer(client.Id, match.HostId) : Array.Empty<BodyAuthority>();
            foreach (var id in match.Players)
            {
                Send(id, MessageCodec.Encode(new NetMessage(Msg.Authority, match.Id, 0,
                    new AuthorityMessage(match.HostId, 0, ClaimRefusal.None, transfers)), fromServer: true));
                Send(id, new NetWriter(Msg.PlayerLeft, match.Id).Int(client.Id).Int(match.HostId), reliable: true);
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

    /// <summary>The wanted color if nobody else in the lobby has it, otherwise the first free one.</summary>
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
            Send(id, bytes, reliable: true);
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

    private void SendLobbyList(Client client) => Send(client.Id, LobbyList(), reliable: true);

    private void BroadcastLobbyList()
    {
        var bytes = LobbyList();
        foreach (var client in clients.Values.Where(c => c.Welcomed && c.Lobby == null))
        {
            Send(client.Id, bytes, reliable: true);
        }
    }

    private void Notice(Client client, string text) => Send(client.Id, new NetWriter(Msg.Notice).String(text), reliable: true);

    // ------------------------------------------------------------------ Match flow

    private void StartMatch(Client client, Godot.Collections.Dictionary snapshot)
    {
        if (client.Lobby is not { Phase: LobbyPhase.Waiting } lobby || lobby.CreatorId != client.Id)
        {
            return;
        }
        // Everyone plays on the creator's tuning: the baked values in a release build, the live ones in debug.
        var tuning = Tuning.FromSnapshot(snapshot);
        var match = new MatchRun
        {
            Id = nextMatchId++,
            CreatorId = client.Id,
            CreatorDebug = client.Debug,
            Tuning = tuning,
            Length = lobby.MatchLength,
            HostId = client.Id
        };
        foreach (var id in lobby.Members)
        {
            match.Slots[id] = match.Players.Count;
            match.Players.Add(id);
            match.Scores[id] = StartScore;
            match.Profiles[id] = (clients[id].Name, clients[id].Color);
        }

        var started = Time.GetTicksMsec();
        var seed = random.Next();
        var settings = SuburbPlans.Match(tuning, Mathf.Max(match.Players.Count, tuning.MaxPlayers));
        var plan = SuburbPlans.Generate(settings, seed, () => random.Next());
        var planBytes = PlanCodec.Encode(plan);
        match.Tanks = new TankLedger(plan, match.HostId);
        for (var i = 0; i < plan.Props.Count; i++)
        {
            if (plan.Props[i].Body != World.Plan.PropBody.Static && !plan.Props[i].Model.StartsWith("car:")) { match.LooseProps.Add(i); }
        }
        match.PickupUp = Enumerable.Repeat(true, plan.AmmoSpots.Count).ToArray();
        match.PickupBack = new double[plan.AmmoSpots.Count];
        Log($"lobby {lobby.Id}: match for {match.Players.Count} on seed {plan.Seed} ({plan.Lots.Count} lots, {plan.Tanks.Count} tanks, " +
            $"{plan.AmmoSpots.Count} pickups, {planBytes.Length / 1024f:0.0} KB plan, generated in {Time.GetTicksMsec() - started} ms)");

        var w = new NetWriter(Msg.MatchStart, match.Id).Bytes(planBytes).Int(match.HostId).Bool(match.CreatorDebug).Float(match.Length).Snapshot(snapshot)
            .Int(match.Players.Count);
        for (var i = 0; i < match.Players.Count; i++)
        {
            var id = match.Players[i];
            w.Int(id).String(match.Profiles[id].Name).Int(match.Profiles[id].Color).Int(i);
        }
        var bytes = w.ToArray();
        foreach (var id in match.Players)
        {
            Send(id, bytes, reliable: true);
        }
        lobby.Match = match;
        lobby.Phase = LobbyPhase.Loading;
        match.PhaseEnds = NetTransport.Now + LoadTimeout;
        SendLobbyState(lobby);
        BroadcastLobbyList();
    }

    internal void AdvanceMatches(double now)
    {
        foreach (var lobby in lobbies.Values.ToList()) { Tick(lobby, now); }
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
                    Broadcast(match, new NetWriter(Msg.Countdown, match.Id).Float(match.Tuning.CountdownTime));
                }
                break;
            case LobbyPhase.Countdown:
                if (now >= match.PhaseEnds)
                {
                    lobby.Phase = LobbyPhase.Playing;
                    match.PhaseEnds = now + match.Length;
                    match.NextClock = now + 1;
                    Broadcast(match, new NetWriter(Msg.MatchGo, match.Id).Float(match.Length));
                    foreach (var (id, score) in match.Scores.Where(s => s.Value != 0))
                    {
                        Broadcast(match, new NetWriter(Msg.Score, match.Id).Int(id).Int(score).Int(0));
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
                        Broadcast(match, new NetWriter(Msg.PickupRespawn, match.Id).Int(i));
                    }
                }
                if (now >= match.NextClock)
                {
                    match.NextClock += 1;
                    Broadcast(match, new NetWriter(Msg.Clock, match.Id).Float((float)Math.Max(0, match.PhaseEnds - now)), reliable: false);
                }
                if (now >= match.PhaseEnds)
                {
                    lobby.Phase = LobbyPhase.Results;
                    match.PhaseEnds = now + match.Tuning.ResultsTime;
                    var w = new NetWriter(Msg.MatchEnd, match.Id).Int(match.Scores.Count);
                    foreach (var (id, score) in match.Scores.OrderByDescending(s => s.Value))
                    {
                        w.Int(id).String(match.Profiles[id].Name).Int(match.Profiles[id].Color).Int(score).Bool(match.Players.Contains(id));
                    }
                    Broadcast(match, w);
                    var spills = match.Stats.Values.Sum(st => st.LostToHits + st.LostToThrows);
                    var banked = match.Scores.Values.Sum();
                    var initial = StartScore * match.Scores.Count;
                    var expected = initial + match.Tanks.MapDetonations + match.Tanks.DropDetonations - spills;
                    Log($"match {match.Id}: bank audit initial={initial} map={match.Tanks.MapDetonations} drops={match.Tanks.DropDetonations} " +
                        $"spills={spills} banked={banked} expected={expected} ok={banked == expected}");
                    Log($"lobby {lobby.Id}: match over, " + string.Join(", ", match.Scores.OrderByDescending(s => s.Value).Select(s => $"{match.Profiles[s.Key].Name} {s.Value}")));
                    Log($"lobby {lobby.Id}: tanks gained / lost to hits / lost to throws: " + string.Join(", ", match.Profiles.Keys.Select(id =>
                    {
                        var st = match.Stats.GetValueOrDefault(id);
                        return $"{match.Profiles[id].Name} {st.Gained}/{st.LostToHits}/{st.LostToThrows}";
                    })));
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
        Broadcast(match, new NetWriter(Msg.PickupTaken, match.Id).Int(index).Int(client.Id));
    }

    // ------------------------------------------------------------------ Relay

    /// <summary>
    /// Resolves authority requests into ordered canonical grants/refusals, and forwards other match messages with
    /// the sender inserted. Accepted explosions and drops also change the bank.
    /// </summary>
    private void Relay(Client client, NetMessage message)
    {
        if (client.Lobby is not { Match: { } match } lobby || !match.Players.Contains(client.Id)) { return; }
        if (lobby.Phase is not (LobbyPhase.Countdown or LobbyPhase.Playing or LobbyPhase.Results) &&
            message.Type is not (Msg.Claim or Msg.Puncture or Msg.Explode or Msg.Drop)) { return; }
        var includeSender = false;
        switch (message.Payload)
        {
            case ExplodeMessage explosion:
                var detonation = match.Tanks.Detonate(client.Id, explosion, lobby.Phase == LobbyPhase.Playing && NetTransport.Now < match.PhaseEnds);
                PublishAuthority(match, detonation);
                if (detonation.Reason == ClaimRefusal.None)
                {
                    ChangeScore(match, client.Id, 1);
                    var stats = match.Stats.GetValueOrDefault(client.Id);
                    match.Stats[client.Id] = stats with { Gained = stats.Gained + 1 };
                }
                return;
            case PunctureMessage puncture:
                PublishAuthority(match, match.Tanks.Puncture(client.Id, puncture, lobby.Phase == LobbyPhase.Playing && NetTransport.Now < match.PhaseEnds));
                return;
            case DropMessage drop:
                try
                {
                    var limit = drop.Reason == 1 ? match.Tuning.RagdollDropCount : match.Tuning.HitDropCount;
                    if (lobby.Phase != LobbyPhase.Playing || NetTransport.Now >= match.PhaseEnds || drop.Tanks.Count > limit || drop.Tanks.Count > match.Scores[client.Id] || drop.Color != match.Profiles[client.Id].Color)
                    {
                        throw new InvalidDataException("drop outside play, exceeds bank/tuner or has wrong color");
                    }
                    match.Tanks.RegisterDrops(match.Slots[client.Id], client.Id, drop.Tanks);
                }
                catch (InvalidDataException e)
                {
                    // A fully decoded prediction can legitimately become invalid in flight (clock/tuning changes).
                    // Tell only its sender which ids to remove; the ledger and bank remain untouched.
                    Send(client.Id, MessageCodec.Encode(new NetMessage(Msg.DropRefused, match.Id, 0,
                        new DropRefusedMessage(drop.Tanks.Select(t => t.Id).ToArray())), fromServer: true));
                    Log($"refused Drop from {client.Id}: {e.Message}");
                    return;
                }
                includeSender = true; // Confirmation clears the sender's pending ids without spawning them twice.
                ChangeScore(match, client.Id, -drop.Tanks.Count);
                var losses = match.Stats.GetValueOrDefault(client.Id);
                match.Stats[client.Id] = drop.Reason == 1 ? losses with { LostToThrows = losses.LostToThrows + drop.Tanks.Count }
                    : losses with { LostToHits = losses.LostToHits + drop.Tanks.Count };
                break;
            case ClaimMessage claim:
                PublishAuthority(match, match.Tanks.Claim(client.Id, claim, lobby.Phase == LobbyPhase.Playing && NetTransport.Now < match.PhaseEnds));
                return;
            case BodyStatesMessage states:
                match.Tanks.ValidateBodies(states.Bodies.Select(b => b.Id));
                var accepted = states.Bodies.Where(b => match.Tanks.Update(client.Id, b, rest: false)).ToArray();
                if (accepted.Length == 0) { return; }
                // Sampling uses the owner's estimate of our clock, only for bounded pose extrapolation, never ordering.
                // Zero means no ping yet: fall back to this relay's time instead of inventing a sampling age.
                var relayedAt = NetTransport.Now;
                var sampledAt = states.Time == 0 ? relayedAt : System.Math.Clamp(states.Time,
                    System.Math.Max(0, relayedAt - BodySync.MaxExtrapolation), relayedAt);
                message = message with { Payload = states with { Time = sampledAt, Bodies = accepted } };
                break;
            case BodyRestMessage rest:
                match.Tanks.ValidateBodies(new[] { rest.Id });
                if (!match.Tanks.Update(client.Id, new BodyState(rest.Id, rest.Transform, Vector3.Zero, Vector3.Zero, rest.Epoch, rest.Sequence), rest: true)) { return; }
                break;
            case ShotMessage shot:
                if (shot.Victim != 0 && (!match.Players.Contains(shot.Victim) || shot.Victim == client.Id) ||
                    shot.PropIndex >= 0 && !match.LooseProps.Contains(shot.PropIndex))
                {
                    throw new InvalidDataException("unknown shot target");
                }
                break;
            case TuningMessage tune:
                // The original creator's debug capability is captured at start, never transferred with hosting.
                if (client.Id != match.CreatorId || !match.CreatorDebug) { throw new InvalidDataException("tuning requires the debug creator"); }
                match.Tuning.Apply(new Godot.Collections.Dictionary { [tune.Name] = tune.Value });
                break;
        }
        var bytes = MessageCodec.Encode(message with { Sender = client.Id }, fromServer: true);
        var reliable = message.Type is not (Msg.PlayerState or Msg.BodyStates);
        foreach (var id in match.Players)
        {
            if (id != client.Id || includeSender) { Send(id, bytes, reliable); }
        }
    }

    private void PublishAuthority(MatchRun match, AuthorityMessage result)
    {
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

    private void ChangeScore(MatchRun match, int player, int delta)
    {
        if (!match.Scores.TryGetValue(player, out var score))
        {
            return;
        }
        var next = Math.Max(0, score + delta);
        match.Scores[player] = next;
        Broadcast(match, new NetWriter(Msg.Score, match.Id).Int(player).Int(next).Int(next - score));
    }

    private void Broadcast(MatchRun match, NetWriter message, bool reliable = true)
    {
        var bytes = message.ToArray();
        foreach (var id in match.Players)
        {
            Send(id, bytes, reliable);
        }
    }

    private void Send(int peer, NetWriter message, bool reliable = true) => Send(peer, message.ToArray(), reliable);

    private void Send(int peer, byte[] bytes, bool reliable = true)
    {
        if (SendOverride != null) { SendOverride(peer, bytes, reliable); }
        else { transport.Send(peer, bytes, reliable); }
    }

    private static void Log(string text) => GD.Print($"[server {Time.GetDatetimeStringFromSystem()}] {text}");
}
