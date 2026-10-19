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
/// One server match or roaming world's lifetime. Owns its frozen match actors or live roaming membership,
/// admission, ledger, mode, readiness, pickups and tuning. Delivery and lobby transitions are synchronous callbacks;
/// this owner has no transport, clients, lobbies or processing node. Detach routing before disposal.
/// </summary>
internal sealed class ServerRun : IModeHost, IDisposable
{
    private const double LoadTimeout = 20;
    private const double StockInterval = 0.5;
    private readonly int lobbyId;
    private readonly IMonotonicClock clock;
    private readonly Action<int, byte[], TrafficClass> deliver;
    private readonly Action<int, bool> loading;
    private readonly Action<LobbyPhase> phaseChanged;
    private readonly Action returnToLobby;
    private readonly Action<string> log;
    private readonly Action<int, AuthorityMessage> authorityPublished;
    private LobbyPhase phase = LobbyPhase.Loading;
    public bool IsWorld { get; }
    public bool IsRetired { get; private set; }
    public int PeerCount => peers.Count;
    // Lifetime observation for retirement fixtures, with no recurring work or transport coupling.
    internal event Action? Retired;
    private readonly List<int> peers = new(); // Participating peers: readiness and delivery, once per connection.
    private readonly Dictionary<int, PlayerProfile> actorsById = new();
    private readonly Dictionary<(int Actor, bool Pose), uint> actions = new();
    private readonly Dictionary<int, (string Name, AppearanceRecord Appearance)> profiles = new();
    public int Id { get; }
    private readonly int creatorId;
    private readonly bool creatorDebug;
    private TankLedger tanks = null!;
    private readonly HashSet<int> looseProps = new();
    private readonly Dictionary<int, int> slots = new();
    private readonly HashSet<int> loaded = new();
    private readonly Dictionary<int, MeleeValidation> meleeByActor = new();
    private int hostId;
    private ModeRunner modeRunner = null!;
    private double PhaseEnds => modeRunner.Phases.PhaseEnds;
    private double nextClock;
    private readonly float length;
    private readonly PacketCodec codec;
    private readonly IServerRunSettings tuning;
    internal IServerRunSettings Configuration => tuning; // Native lifetime observation; this run owns and disposes it.
    private readonly ServerMode mode;
    private readonly Func<SuburbSettings, int, Func<int>, SuburbPlan> generate;
    private bool[] pickupUp = Array.Empty<bool>();
    private double[] pickupBack = Array.Empty<double>();
    /// <summary>Each actor's latest reported position, for a mode whose rules read where bodies are.</summary>
    private readonly Dictionary<int, ActorSample> samples = new();
    private double nextSample;
    /// <summary>A lobby world's next stock report to its rules.</summary>
    private double nextStock;

    public ServerRun(int id, int lobbyId, int creator, bool creatorDebug, float length, bool isWorld, IServerRunSettings tuning, ServerMode mode,
        Func<SuburbSettings, int, Func<int>, SuburbPlan> generate, IMonotonicClock clock, Action<int, byte[], TrafficClass> deliver, Action<int, bool> loading,
        Action<LobbyPhase> phaseChanged, Action returnToLobby, Action<string> log, Action<int, AuthorityMessage> authorityPublished, PacketCodec codec)
    {
        this.codec = codec;
        Id = id;
        this.lobbyId = lobbyId;
        creatorId = hostId = creator;
        this.creatorDebug = creatorDebug;
        this.length = length;
        IsWorld = isWorld;
        this.tuning = tuning;
        this.mode = mode;
        this.generate = generate;
        this.clock = clock;
        this.deliver = deliver;
        this.loading = loading;
        this.phaseChanged = phaseChanged;
        this.returnToLobby = returnToLobby;
        this.log = log;
        this.authorityPublished = authorityPublished;
    }

    public void Dispose()
    {
        if (IsRetired) return;
        IsRetired = true;
        tuning.Dispose();
        try { Retired?.Invoke(); }
        finally { Retired = null; }
    }

    private PhaseTimes Times => new(LoadTimeout, tuning.CountdownTime, tuning.ResultsTime);
    private void SetPhase(LobbyPhase value) { phase = value; phaseChanged(value); }

    public void InitializeMatch(IReadOnlyList<ActorRecord> actors, uint revision, int startScore, Func<int> nextRandom)
    {
        peers.AddRange(actors.Select(a => a.OwnerPeerId).Distinct());
        foreach (var a in actors)
        {
            actorsById.Add(a.ActorId, PlayerProfile.From(a));
            slots[a.ActorId] = a.AllocationSlot;
            profiles[a.ActorId] = (a.Name, a.Appearance);
        }

        var started = Time.GetTicksMsec();
        var seed = OS.IsDebugBuild() && int.TryParse(LaunchArgs.Current.Get("match-seed"), out var requestedSeed)
            ? requestedSeed : nextRandom();
        var settings = tuning.Supply(actors.Count);
        var plan = generate(settings, seed, () => nextRandom());
        var planBytes = PlanCodec.Encode(plan);
        tanks = new TankLedger(plan, hostId);
        modeRunner = new ModeRunner(mode, new ModeSetup(actors.Select(a => new ModeActor(a.ActorId, a.Name, true, startScore)).ToArray(),
            new ModeWorld(mode.World, 1, plan.Tanks.Count, KingPlacement.SpotsFrom(plan)), tuning.ReadModeTuners(), unchecked((ulong)plan.Seed)));
        for (var i = 0; i < plan.Props.Count; i++)
        {
            if (plan.Props[i].Body != World.Plan.PropBody.Static && !plan.Props[i].Model.StartsWith("car:")) { looseProps.Add(i); }
        }
        pickupUp = Enumerable.Repeat(true, plan.AmmoSpots.Count).ToArray();
        pickupBack = new double[plan.AmmoSpots.Count];
        log($"lobby {lobbyId}: match for {actors.Count} on seed {plan.Seed} ({plan.Lots.Count} lots, {plan.Tanks.Count} tanks, " +
            $"{plan.AmmoSpots.Count} pickups, {planBytes.Length / 1024f:0.0} KB plan, generated in {Time.GetTicksMsec() - started} ms)");

        var setup = new MatchSetup(Id, planBytes, hostId, creatorDebug, length, tuning.Snapshot(),
            actors.Select((a, spawn) => (PlayerProfile.From(a), spawn)).ToArray())
        {
            RosterRevision = revision,
            CreatorId = creatorId,
            Peers = peers.Select(id => new PeerCapabilities(id, id == creatorId && creatorDebug)).ToArray(),
            ModeId = mode.Id,
            ModeSchema = mode.SchemaVersion,
        };
        var bytes = codec.Encode(new NetMessage(Msg.MatchStart, Id, 0, setup), true);
        foreach (var id in peers)
        {
            loading(id, true);
            Send(id, bytes, traffic: TrafficClass.ReliableOrdered);
        }
    }

    public void StartMatch()
    {
        SetPhase(LobbyPhase.Loading);
        modeRunner.Apply(this, new PhaseEntered(ModePhase.Loading,
            modeRunner.Phases.Load(clock.Seconds, Times, modeRunner.Definition.Timer, length).Round), clock.Seconds);
    }

    public void InitializeWorld()
    {
        tanks = new TankLedger(WorldPlan(), hostId);
        modeRunner = new ModeRunner(mode, new ModeSetup(Array.Empty<ModeActor>(),
            new ModeWorld(mode.World, 1, 0, LobbyLayout.Spots(tuning.LobbyPiles, tuning.LobbyPileRing)), tuning.ReadModeTuners(), (ulong)Id));
    }

    public void StartWorld(double now)
    {
        // Straight into its one untimed round, which stocks every spot; nobody waits for anyone to load.
        var times = new PhaseTimes(LoadTimeout, 0, tuning.ResultsTime);
        modeRunner.Apply(this, new PhaseEntered(ModePhase.Loading, modeRunner.Phases.Load(now, times, modeRunner.Definition.Timer, 0).Round), now);
        while (modeRunner.Phases.Phase != ModePhase.Round && modeRunner.Phases.Advance(now, times, true) is { } change)
            modeRunner.Apply(this, new PhaseEntered(change.To, change.Round), now);
        log($"lobby {lobbyId}: world {Id} open with {tanks.LiveTanks} tanks");
    }

    public void SyncWorld(IReadOnlyList<ActorRecord> roster, IReadOnlyList<int> members, uint revision, int creator, double now)
    {
        if (IsRetired) return;
        var present = roster.ToDictionary(a => a.ActorId);
        foreach (var gone in actorsById.Values.Where(a => a.Present && !present.ContainsKey(a.Id)).ToArray())
        {
            actorsById[gone.Id] = gone with { Present = false };
            modeRunner.Apply(this, new ActorLeft(gone.Id), now);
        }
        foreach (var record in present.Values)
        {
            var joined = !actorsById.TryGetValue(record.ActorId, out var prior) || !prior.Present;
            actorsById[record.ActorId] = PlayerProfile.From(record);
            slots[record.ActorId] = record.AllocationSlot;
            profiles[record.ActorId] = (record.Name, record.Appearance);
            if (joined) modeRunner.Apply(this, new ActorJoined(new ModeActor(record.ActorId, record.Name)), now);
        }
        foreach (var peer in members.Where(peer => !peers.Contains(peer)).ToArray()) Admit(peer, members, revision, creator);
    }

    /// <summary>
    /// One peer's world start: the void plan with a start for every present actor, the world's tuning, every live
    /// body as the ledger has it (so a late arrival sees the world as it is), and Go.
    /// </summary>
    private void Admit(int peer, IReadOnlyList<int> members, uint revision, int creator)
    {
        peers.Add(peer);
        var actors = actorsById.Values.Where(a => a.Present).OrderBy(a => a.Id).ToArray();
        var plan = WorldPlan();
        foreach (var actor in actors)
        {
            var (at, yaw) = LobbyLayout.Arrival(actor.AllocationSlot, tuning.LobbySpawnRing);
            // A plan start's yaw faces away from where its actor looks (matches turn it around on arrival).
            plan.Spawns.Add(new SpawnPoint(new Vector2((float)at.X, (float)at.Z), (float)yaw + Mathf.Pi));
        }
        var setup = new MatchSetup(Id, PlanCodec.Encode(plan), hostId, false, length, tuning.Snapshot(),
            actors.Select((a, spawn) => (a, spawn)).ToArray())
        {
            RosterRevision = revision,
            CreatorId = creator,
            Peers = members.Select(id => new PeerCapabilities(id, false)).ToArray(),
            ModeId = modeRunner.Definition.Id,
            ModeSchema = modeRunner.Definition.SchemaVersion,
        };
        loading(peer, true);
        Send(peer, codec.Encode(new NetMessage(Msg.MatchStart, Id, 0, setup), true));
        var live = tanks.Live();
        for (var start = 0; start < live.Count; start += Protocol.MaxCollection)
        {
            Send(peer, codec.Encode(new NetMessage(Msg.Authority, Id, 0, new AuthorityMessage(hostId, 0, ClaimRefusal.None,
                live.Skip(start).Take(Protocol.MaxCollection).ToArray())), fromServer: true));
        }
        Send(peer, new NetWriter(Msg.MatchGo, Id).Float(0));
    }

    private SuburbPlan WorldPlan()
    {
        var extent = (float)LobbyLayout.Extent(tuning.LobbyPileRing);
        return new SuburbPlan { Seed = Id, Bounds = new Rect2(-extent, -extent, extent * 2, extent * 2) };
    }

    private void TickWorld(double now)
    {
        foreach (var held in modeRunner.Gate.Expire(now))
            modeRunner.Wire.Failed(modeRunner, this, held.Peer, (ModeEnvelope)held.Message.Payload, now);
        if (now < nextStock || modeRunner.Wire is not LobbyWire wire) return;
        nextStock = now + StockInterval;
        modeRunner.Apply(this, wire.Stock(modeRunner, this), now);
    }

    public bool Depart(int peer)
    {
        if (IsRetired || !peers.Remove(peer)) return false;
        if (hostId == peer && peers.Count > 0)
        {
            hostId = actorsById.Values.Where(a => a.Present && a.OwnerPeerId != peer && a.Kind == ActorKind.Human)
                .Select(a => a.OwnerPeerId).FirstOrDefault();
            if (hostId == 0) hostId = peers[0];
        }
        foreach (var actor in actorsById.Values.Where(a => a.OwnerPeerId == peer && a.Present).ToArray())
        {
            actorsById[actor.Id] = actor with { Present = false };
            samples.Remove(actor.Id);
            modeRunner.Apply(this, new ActorLeft(actor.Id), clock.Seconds);
        }
        var transfers = peers.Count > 0 ? tanks.Transfer(peer, hostId) : Array.Empty<BodyAuthority>();
        foreach (var id in peers)
        {
            Send(id, codec.Encode(new NetMessage(Msg.Authority, Id, 0,
                new AuthorityMessage(hostId, 0, ClaimRefusal.None, transfers)), fromServer: true));
            Send(id, new NetWriter(Msg.PlayerLeft, Id).Int(peer).Int(hostId), traffic: TrafficClass.ReliableOrdered);
        }
        return true;
    }

    public void Receive(int peer, NetMessage message, TrafficClass? incoming)
    {
        if (IsRetired || !peers.Contains(peer)) return;
        if (Protocol.IsRelay(message.Type))
        {
            Relay(peer, message, incoming);
        }
        else if (message.Type == Msg.ModeEvent)
        {
            ModeEvent(peer, message);
        }
        else if (message.Type == Msg.Loaded)
        {
            loaded.Add(peer);
            loading(peer, false);
        }
        else if (message.Type == Msg.ClaimPickup && AcceptActor(peer, message))
        {
            ClaimPickup(message.ActorId, ((IntMessage)message.Payload).Value);
        }
    }

    public void Tick(double now)
    {
        if (IsRetired) return;
        if (IsWorld) { TickWorld(now); return; }
        foreach (var held in modeRunner.Gate.Expire(now))
            modeRunner.Wire.Failed(modeRunner, this, held.Peer, (ModeEnvelope)held.Message.Payload, now);
        if (phase == LobbyPhase.Playing)
        {
            for (var i = 0; i < pickupUp.Length; i++)
            {
                if (!pickupUp[i] && now >= pickupBack[i])
                {
                    pickupUp[i] = true;
                    Broadcast(new NetWriter(Msg.PickupRespawn, Id).Int(i));
                }
            }
            if (now >= nextClock)
            {
                nextClock += 1;
                Broadcast(new NetWriter(Msg.Clock, Id).Float((float)Math.Max(0, PhaseEnds - now)), traffic: TrafficClass.UnreliablePlayerPoses);
            }
            SampleActors(now);
        }
        // One timed step per tick; a round's end resolves through RoundEnd, MatchEnd and Results at once.
        if (modeRunner.Phases.Advance(now, Times, peers.All(loaded.Contains)) is { } change)
        {
            Enter(change, now);
        }
    }

    private void Enter(PhaseChange change, double now)
    {
        switch (change.To)
        {
            case ModePhase.Countdown:
                SetPhase(LobbyPhase.Countdown);
                Broadcast(new NetWriter(Msg.Countdown, Id).Float(tuning.CountdownTime));
                modeRunner.Apply(this, new PhaseEntered(ModePhase.Countdown, change.Round), now);
                break;
            case ModePhase.Round:
                SetPhase(LobbyPhase.Playing);
                nextClock = now + 1;
                nextSample = now;
                Broadcast(new NetWriter(Msg.MatchGo, Id).Float((float)change.Round.Length));
                modeRunner.Apply(this, new PhaseEntered(ModePhase.Round, change.Round), now);
                log($"lobby {lobbyId}: match on");
                break;
            case ModePhase.RoundEnd:
                foreach (var held in modeRunner.Gate.Clear())
                    modeRunner.Wire.Failed(modeRunner, this, held.Peer, (ModeEnvelope)held.Message.Payload, now);
                var decision = modeRunner.Apply(this, new PhaseEntered(ModePhase.RoundEnd, change.Round), now)
                    .LastOrDefault(c => c is EndMatch or NextRound) ?? new EndMatch();
                if (decision is NextRound { RebuildWorld: true })
                {
                    // Clients cannot rebuild a world inside a run yet (a segment world source will); end cleanly instead.
                    log($"match {Id}: the mode asked for a rebuilt world, which this server cannot deliver; ending the match");
                    decision = new EndMatch();
                }
                Enter(modeRunner.Phases.Decide(now, Times, decision), now);
                break;
            case ModePhase.MatchEnd:
                SetPhase(LobbyPhase.Results);
                var state = modeRunner.Rules.Snapshot();
                var w = new NetWriter(Msg.MatchEnd, Id).Int(state.Standings.Count);
                foreach (var standing in state.Standings)
                {
                    w.Int(standing.ActorId).String(profiles[standing.ActorId].Name).Appearance(profiles[standing.ActorId].Appearance)
                        .Int(standing.Score).Bool(standing.Present);
                }
                Broadcast(w);
                modeRunner.Apply(this, new PhaseEntered(ModePhase.MatchEnd, change.Round), now);
                foreach (var report in state.Report)
                {
                    log(report.Kind switch
                    {
                        ModeReportKind.Audit => $"match {Id}: {report.Text}",
                        ModeReportKind.Standings => $"standings match {Id}: {report.Text}",
                        _ => $"lobby {lobbyId}: {report.Text}",
                    });
                }
                if ((modeRunner.Rules as TankBankRules ?? (modeRunner.Rules as KingRules)?.Bank) is { } bank &&
                    (bank.MapDetonations != tanks.MapDetonations || bank.DropDetonations != tanks.DropDetonations))
                {
                    log($"match {Id}: ledger disagreement map={tanks.MapDetonations} drops={tanks.DropDetonations}");
                }
                Enter(modeRunner.Phases.Advance(now, Times, true)!.Value, now);
                break;
            case ModePhase.Results:
                SetPhase(LobbyPhase.Results);
                modeRunner.Apply(this, new PhaseEntered(ModePhase.Results, change.Round), now);
                break;
            case ModePhase.Lobby:
                returnToLobby();
                break;
            case ModePhase.Loading:
                SetPhase(LobbyPhase.Loading);
                modeRunner.Apply(this, new PhaseEntered(ModePhase.Loading, change.Round), now);
                break;
        }
    }

    /// <summary>
    /// Feeds the rules where every present actor is, at the rate players send their positions, from those positions:
    /// the feet while up, the pelvis while thrown or getting up.
    /// </summary>
    private void SampleActors(double now)
    {
        if (!modeRunner.Definition.SamplesActors || now < nextSample) return;
        nextSample = now + 1.0 / Math.Max(1f, tuning.PlayerSendRate);
        modeRunner.Apply(this, new ActorsSampled(samples.Values.ToArray()), now);
    }

    private static ActorSample Sample(int actorId, Player.PlayerNetState state)
    {
        var down = state.State != Player.PlayerNetState.Mode.Active;
        var at = down ? state.PelvisPosition : state.Position;
        return new ActorSample(actorId, at.X, at.Y, at.Z, down);
    }

    private void ClaimPickup(int actorId, int index)
    {
        if (!modeRunner.Permits(ModeGate.Pickup, clock.Seconds) ||
            index < 0 || index >= pickupUp.Length || !pickupUp[index])
        {
            return;
        }
        pickupUp[index] = false;
        pickupBack[index] = clock.Seconds + tuning.PickupRespawnTime;
        Broadcast(new NetWriter(Msg.PickupTaken, Id).Int(index).Int(actorId));
    }

    // ------------------------------------------------------------------ Relay

    /// <summary>
    /// Resolves authority requests into ordered canonical grants/refusals, and forwards other match messages with
    /// the sender inserted. Accepted explosions and drops also change the bank.
    /// </summary>
    private void Relay(int peer, NetMessage message, TrafficClass? incoming)
    {
        // A lobby world relays all the while it lives (waiting and staging); a match only once it is under way.
        if (!IsWorld && phase is not (LobbyPhase.Countdown or LobbyPhase.Playing or LobbyPhase.Results) &&
            !(phase == LobbyPhase.Loading && message.Type is Msg.BodyStates or Msg.BodyRest) &&
            message.Type is not (Msg.Claim or Msg.Puncture or Msg.Explode)) { return; }
        if (Protocol.IsActorMessage(message.Type) && !AcceptActor(peer, message)) return;
        var actorId = message.ActorId;
        var includeSender = message.Type is Msg.Shot or Msg.MeleeStart or Msg.MeleeContact;
        var playOpen = modeRunner.Permits(ModeGate.World, clock.Seconds);
        switch (message.Payload)
        {
            case PlayerStateMessage state:
                if (modeRunner.Definition.SamplesActors) samples[actorId] = Sample(actorId, state.State);
                break;
            case ExplodeMessage explosion:
                var detonation = tanks.Detonate(peer, explosion, actorId, playOpen);
                PublishAuthority(detonation);
                var dependency = new ModeDependency(peer, explosion.Request);
                if (detonation.Reason == ClaimRefusal.None)
                {
                    modeRunner.Apply(this, new TankDetonated(actorId, explosion.Id < BodyIds.DropTankBase ? TankOrigin.Map : TankOrigin.Drop), clock.Seconds);
                    foreach (var held in modeRunner.Gate.Release(dependency))
                        modeRunner.Wire.Event(modeRunner, this, held.Peer, (ModeEnvelope)held.Message.Payload, clock.Seconds);
                }
                else if (detonation.Reason != ClaimRefusal.DuplicateRequest)
                {
                    foreach (var held in modeRunner.Gate.Fail(dependency))
                        modeRunner.Wire.Failed(modeRunner, this, held.Peer, (ModeEnvelope)held.Message.Payload, clock.Seconds);
                }
                return;
            case PunctureMessage puncture:
                PublishAuthority(tanks.Puncture(peer, puncture, actorId, playOpen));
                return;
            case ClaimMessage claim:
                PublishAuthority(tanks.Claim(peer, claim, playOpen));
                return;
            case GatherMessage gather:
                if (!tanks.Gather(peer, hostId, gather, playOpen)) { return; }
                break;
            case BodyStatesMessage states:
                try { tanks.ValidateBodies(states.Bodies.Select(b => b.Id)); }
                catch (InvalidDataException error) { Reject(peer, message.Type, error.Message); return; }
                var accepted = states.Bodies.Where(b => tanks.Update(peer, b, rest: false)).ToArray();
                if (accepted.Length == 0) { return; }
                // Sampling uses the owner's estimate of our clock, only for bounded pose extrapolation, never ordering.
                // Zero means no ping yet: fall back to this relay's time instead of inventing a sampling age.
                var relayedAt = clock.Seconds;
                var sampledAt = states.Time == 0 ? relayedAt : System.Math.Clamp(states.Time,
                    System.Math.Max(0, relayedAt - BodyState.MaxExtrapolation), relayedAt);
                message = message with { Payload = states with { Time = sampledAt, Bodies = accepted } };
                break;
            case BodyRestMessage rest:
                try { tanks.ValidateBodies(new[] { rest.Id }); }
                catch (InvalidDataException error) { Reject(peer, message.Type, error.Message); return; }
                if (!tanks.Update(peer, new BodyState(rest.Id, rest.Transform, Vector3.Zero, Vector3.Zero, rest.Epoch, rest.Sequence), rest: true)) { return; }
                break;
            case MeleeStartMessage strike:
                if (!playOpen) return;
                if (!meleeByActor.TryGetValue(actorId, out var melee)) meleeByActor[actorId] = melee = new MeleeValidation();
                if (!melee.Start(strike, clock.Seconds, new MeleeTiming(tuning.MeleeWindup, tuning.MeleeActive, tuning.MeleeRecovery, tuning.MeleeCooldown, tuning.MeleeRelayGrace))) return;
                break;
            case MeleeContactMessage contact:
                if (!playOpen ||
                    contact.Victim != 0 && (!ValidTarget(contact.Victim, contact.VictimLifeId) || contact.Victim == actorId) ||
                    contact.PropIndex >= 0 && !looseProps.Contains(contact.PropIndex))
                {
                    return;
                }

                if (!meleeByActor.TryGetValue(actorId, out var swing) || !swing.Contact(contact, clock.Seconds, tuning.MeleeMaxContacts)) return;
                break;
            case ShotMessage shot:
                if (shot.Victim != 0 && (!ValidTarget(shot.Victim, shot.VictimLifeId) || shot.Victim == actorId) ||
                    shot.PropIndex >= 0 && !looseProps.Contains(shot.PropIndex))
                {
                    Reject(peer, message.Type, "unknown shot target");
                    return;
                }
                break;
            case TuningMessage tune:
                // The original creator's debug capability is captured at start, never transferred with hosting.
                if (peer != creatorId || !creatorDebug) { Reject(peer, message.Type, "tuning requires the debug creator"); return; }
                // Supply belongs to the locked lobby choice, not live debug edits or Defaults.
                if (tuning.IsLobbyLocked(tune.Name)) return;
                tuning.Apply(new Godot.Collections.Dictionary { [tune.Name] = tune.Value });
                modeRunner.Apply(this, new TunersChanged(tuning.ReadModeTuners()), clock.Seconds);
                break;
        }
        var bytes = codec.Encode(message with { Sender = peer }, fromServer: true);
        var traffic = message.Type switch
        {
            Msg.PlayerState => TrafficClass.UnreliablePlayerPoses,
            Msg.BodyStates => incoming is { } delivery && delivery.IsReliable() ? delivery : TrafficClass.UnreliableBodyStates,
            _ => TrafficClass.ReliableOrdered,
        };
        foreach (var id in peers)
        {
            if (id != peer || includeSender) { Send(id, bytes, traffic); }
        }
    }

    private void Reject(int peer, Msg type, string detail) => log($"bad {type} message from {peer}: {detail}");

    /// <summary>
    /// A mode event: the envelope codec already checked its schema, kind, direction and bounds. The reporter must own
    /// the actor with its current life and a newer action sequence; causal dependencies wait in the run's gate.
    /// </summary>
    private void ModeEvent(int peer, NetMessage message)
    {
        var envelope = (ModeEnvelope)message.Payload;
        if (envelope.ModeId != modeRunner.Definition.Id || envelope.SchemaVersion != modeRunner.Definition.SchemaVersion)
        {
            Reject(peer, message.Type, $"mode event for schema {envelope.ModeId}/{envelope.SchemaVersion} in a {modeRunner.Definition.Name} run");
            return;
        }
        var kind = modeRunner.Schema.Kind(envelope.Kind, ModeDirection.Event);
        if (kind.Reporter == ModeReporter.WorldAuthority && peer != hostId) return;
        if (kind.Reporter is ModeReporter.ActorOwner or ModeReporter.WorldAuthority && !AcceptActor(peer, message)) return;
        var now = clock.Seconds;
        if (kind.Dependency?.Invoke(envelope.Payload) is { } dependency && !tanks.Exploded(dependency.Peer, dependency.Request))
        {
            if (!modeRunner.Gate.Hold(peer, message, dependency, now)) modeRunner.Wire.Failed(modeRunner, this, peer, envelope, now);
            return;
        }
        modeRunner.Wire.Event(modeRunner, this, peer, envelope, now);
    }

    void IModeHost.Broadcast(Msg type, ModeEnvelope envelope)
    {
        var bytes = codec.Encode(new NetMessage(type, Id, 0, envelope), fromServer: true);
        foreach (var id in peers) Send(id, bytes);
    }

    void IModeHost.Send(int peer, Msg type, ModeEnvelope envelope) =>
        Send(peer, codec.Encode(new NetMessage(type, Id, 0, envelope), fromServer: true));

    void IModeHost.RegisterEntities(int actorId, int peer, IReadOnlyList<BodyState> bodies) =>
        tanks.RegisterDrops(slots[actorId], peer, bodies);

    IReadOnlyList<BodyState> IModeHost.SpawnWorldTanks(IReadOnlyList<Transform3D> poses) => tanks.SpawnWorldTanks(hostId, poses);
    uint IModeHost.Life(int actorId) => actorsById[actorId].LifeId;
    int IModeHost.WorldAuthority => hostId;
    ModeWireTuners IModeHost.WireTuners => new(tuning.KingPileCount, tuning.KingPileReach, tuning.KingPileClearance,
        tuning.LobbyPileCount, tuning.LobbyPileReach, tuning.LobbyPileClearance,
        new Vector3(tuning.PileSpacing, tuning.PileLayerHeight, tuning.PileDepthSpacing), tuning.PileUprightLayerHeight);
    void IModeHost.Log(string text) => log($"match {Id}: {text}");
    BodyAuthority? IModeHost.Body(uint id) => tanks.Get(id);
    int IModeHost.LiveTanks => tanks.LiveTanks;

    private bool ValidTarget(int actor, uint life) =>
        actorsById.TryGetValue(actor, out var target) && target.Present && target.LifeId == life;

    private bool AcceptActor(int peer, NetMessage message)
    {
        if (!actorsById.TryGetValue(message.ActorId, out var actor) || !actor.Present ||
            actor.OwnerPeerId != peer || actor.LifeId != message.LifeId || message.ActionId == 0)
        {
            return false;
        }

        var key = (message.ActorId, message.Type == Msg.PlayerState);
        if (message.ActionId <= actions.GetValueOrDefault(key)) return false;
        actions[key] = message.ActionId;
        return true;
    }

    private void PublishAuthority(AuthorityMessage result)
    {
        authorityPublished(Id, result);
        var bytes = codec.Encode(new NetMessage(Msg.Authority, Id, 0, result), fromServer: true);
        if (result.Reason == ClaimRefusal.None)
        {
            foreach (var id in peers) { Send(id, bytes); }
        }
        else
        {
            Send(result.Peer, bytes);
            log($"refused authority from {result.Peer} request {result.Request}: {result.Reason}");
        }
    }

    private void Broadcast(NetWriter message, TrafficClass traffic = TrafficClass.ReliableOrdered)
    {
        var bytes = message.ToArray();
        foreach (var id in peers)
        {
            Send(id, bytes, traffic);
        }
    }

    private void Send(int peer, NetWriter message, TrafficClass traffic = TrafficClass.ReliableOrdered) => Send(peer, message.ToArray(), traffic);
    private void Send(int peer, byte[] bytes, TrafficClass traffic = TrafficClass.ReliableOrdered) => deliver(peer, bytes, traffic);
}
