using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Player;
using Propane.Session;
using Propane.Tank;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>
/// Coordinates one peer's physical-session owners at Godot's existing callbacks and synchronous inbox boundaries.
/// Replica, actor attachments, bots, mode policy and view binding own their state and retire with their scopes.
/// See docs/architecture.md and docs/multiplayer.md for the ordering and lifetime contracts.
/// </summary>
public partial class Match : Node3D, IClientAuthorityReplica, IClientModeReplica, IClientLobbyReplica
{
    public SessionContext Session { get; private set; } = null!;
    public WorldContext World => Session.World;
    public bool CreateLocalView { get; init; } = true;

    /// <summary>Network time source; set before entering the tree for deterministic simulation.</summary>
    internal IMonotonicClock Clock { get; init; } = MonotonicClock.Instance;

    private enum Phase { Loading, Countdown, Playing, Ended }
    private readonly Dictionary<int, PlayerProfile> profiles = new();
    private static readonly IReadOnlyDictionary<int, int> NoScores = new Dictionary<int, int>();
    private IModeClient? clientMode;
    private IModeClient ClientMode => clientMode ?? throw new InvalidOperationException("mode client is not attached");
    private ModeDefinition mode = null!;
    private readonly ModeCommandOrder commandOrder = new();
    private uint round = RoundInfo.First.RoundId;
    private readonly uint worldRevision = RoundInfo.First.WorldRevision;
    private ClientAuthorityTransactions authority = null!;
    private ClientWorldReplica worldReplica = null!;
    private ClientActorSet actorReplication = null!;
    private OwnedBotSet ownedBots = null!;
    private MatchViewBinding viewBinding = null!;
    private Suburb suburb = null!;
    private SuburbPlan plan = null!;
    private PlayerCharacter? player => viewBinding?.Player;
    private Phase phase = Phase.Loading;
    private int me;
    private int loadingFrames;
    private float countdownLeft;
    private float timeLeft = -1;
    private float endLeft;

    /// <summary>The connection the match talks through. Set before adding to the tree.</summary>
    public NetClient Net { get; set; } = null!;

    /// <summary>What the server sent to start the match. Set before adding to the tree.</summary>
    public MatchSetup Setup { get; set; } = null!;

    /// <summary>The results have been shown; time to go back to the lobby.</summary>
    public event Action? Finished;

    /// <summary>The player chose to leave the match.</summary>
    public event Action? LeaveRequested;

    /// <summary>The selected actor while this match has an owned roster. Requires an active owned actor.</summary>
    public PlayerCharacter Player => player ?? throw new InvalidOperationException("match has no owned actor");

    public Suburb Suburb => suburb;

    public int MyScore => player == null ? 0 : Scores.GetValueOrDefault(player.ActorId);

    public IReadOnlyDictionary<int, int> Scores => viewBinding?.Scores ?? NoScores;

    public bool IsPlaying => phase == Phase.Playing;

    public IReadOnlyList<Standing> FinalStandings { get; private set; } = Array.Empty<Standing>();

    public bool IsOver => phase == Phase.Ended;

    public float TimeLeft => timeLeft;

    private System.Collections.Generic.IEnumerable<ActorRuntime> RemoteActors => Session.Actors.Where(actor => actor.Character.IsRemote);

    public IEnumerable<PlayerCharacter> RemoteBodies => RemoteActors.Select(actor => actor.Character);

    public IReadOnlyList<AmmoPickup> Pickups => worldReplica.Pickups;

    public IEnumerable<PropaneTank> LiveTanks => worldReplica.LiveTanks;

    public BodySync Bodies => worldReplica.Bodies;

    public ClaimRefusal LastAuthorityRefusal => authority?.LastRefusal ?? ClaimRefusal.None;

    public event Action<AuthorityMessage>? AuthorityResolved;

    // Packet seam for assertion suites; production sends through the connection.
    internal Action? BeforeInboxAttach { get; init; }
    internal Action<byte[]>? AuthoritySendOverride { get; set; }

    /// <summary>When (on <see cref="Clock"/>) another player's shot last hit this player, and whose it was.</summary>
    public double LastHitTime => player?.Runtime.LastHitTime ?? double.NegativeInfinity;

    public int LastHitBy => player?.Runtime.LastHitBy ?? 0;

    public override void _Ready()
    {
        using var tuning = Tuning.FromSnapshot(Setup.Tuning);
        tuning.CopyLocalTunersFrom(Net.Template);
        SessionFactory.Build(this, tuning, new MatchSource(this), new MatchMode(this), session => Session = session,
            networked: true, present: CreateLocalView);
    }

    public override void _ExitTree() => Session?.Dispose();

    private void StopReplication()
    {
        viewBinding.RefreshPlayer();
        // Modes without a bot factory registered no bot stop callback; release their empty binding here.
        if (mode.CreateBot == null) ownedBots?.Dispose();
        worldReplica?.Dispose();
        authority?.Dispose();
        clientMode?.Dispose();
        clientMode = null;
        viewBinding.Dispose();
    }

    public override void _Process(double delta)
    {
        if (World is not { IsAlive: true } || World.Paused) { return; }
        // The first complete frame includes physics setup and shader warm-up. Reporting from
        // construction's packet callback would end native loading grace before that work finishes.
        if (loadingFrames > 0 && --loadingFrames == 0)
        {
            Send(new NetWriter(Msg.Loaded, Setup.Id));
            if (!World.IsAlive) return; // A required-send failure can synchronously retire the session.
        }
        var dt = (float)delta;
        viewBinding.Reveal(dt);

        switch (phase)
        {
            case Phase.Countdown:
                countdownLeft -= dt;
                if (countdownLeft > 0)
                {
                    viewBinding.ShowCountdown(Mathf.CeilToInt(countdownLeft).ToString());
                }
                break;
            case Phase.Playing:
                timeLeft = Mathf.Max(0, timeLeft - dt);
                break;
            case Phase.Ended:
                endLeft -= dt;
                if (endLeft <= 0)
                {
                    endLeft = float.MaxValue;
                    World.Defer(() => Finished?.Invoke(), whilePaused: true);
                    // Navigation runs after this native node notification returns.
                    return;
                }
                break;
        }

        viewBinding.TickHud(dt, ModePhase, timeLeft);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (World is not { IsAlive: true } || World.Paused) { return; }
        var dt = (float)delta;
        var now = Clock.Seconds;
        actorReplication.ApplySnapshots(now);
        worldReplica.Correct(now);
        ClientMode.PhysicsTick();
        ownedBots.ThinkBots(dt);
        actorReplication.PublishActors(dt, now);
        SendBodies(now);
        actorReplication.CheckPickups();
    }
    public override void _UnhandledInput(InputEvent @event)
    {
        if (World is not { IsAlive: true }) return;
        if (LobbyInput(@event)) return;
        viewBinding.InputEvent(@event);
    }

    private ModePhase ModePhase => phase switch
    {
        Phase.Loading => Modes.ModePhase.Loading,
        Phase.Countdown => Modes.ModePhase.Countdown,
        Phase.Playing => Modes.ModePhase.Round,
        _ => Modes.ModePhase.Results,
    };

    internal void OnLocalBlast(Vector3 center, IReadOnlyList<RigidBody3D> pushed, uint cause) => worldReplica.OnLocalBlast(center, pushed, cause);
    internal uint Claim(IEnumerable<GodotObject> disturbed, uint cause = 0, bool atomic = false, bool predictMotion = true) =>
        worldReplica.Claim(disturbed, cause, atomic, predictMotion);
    private void SendBodies(double now)
    {
        worldReplica.PublishBodies(now, actorReplication.PendingDrops());
    }
    private void Send(NetWriter message, TrafficClass traffic = TrafficClass.ReliableOrdered)
    {
        if (World.IsAlive) Net.Send(message, traffic);
    }
    void IClientAuthorityReplica.SendAuthority(Msg type, object payload, int actorId, uint lifeId)
    {
        var actor = Actor(actorId);
        var action = actor != null ? checked(++actorReplication.Get(actor.Runtime).ActionSequence) : 0;
        var bytes = Net.Codec.Encode(new NetMessage(type, Setup.Id, 0, payload, actorId, lifeId, action), fromServer: false);
        if (AuthoritySendOverride != null) { AuthoritySendOverride(bytes); }
        else { Net.Send(bytes); }
    }

    internal void OnMessage(NetMessage message)
    {
        if (!World.IsAlive) { return; }
        if (Protocol.IsRelay(message.Type))
        {
            actorReplication.OnRelay(message);
            return;
        }
        var tuning = World.Tuning;
        switch (message.Type)
        {
            case Msg.Authority:
                authority.Resolve((AuthorityMessage)message.Payload);
                break;
            case Msg.Countdown:
                phase = Phase.Countdown;
                countdownLeft = ((FloatMessage)message.Payload).Value;
                viewBinding.ShowNotice("", 0);
                break;
            case Msg.MatchGo:
                phase = Phase.Playing;
                timeLeft = ((FloatMessage)message.Payload).Value;
                foreach (var actor in Session.OwnedActors) actor.Character.InputLocked = false;
                // The lobby world greets arrivals with its own banner instead.
                if (viewBinding.Display.ShowGo) viewBinding.ShowCountdown("GO");
                break;
            case Msg.Clock:
                if (phase == Phase.Playing) { timeLeft = ((FloatMessage)message.Payload).Value; }
                break;
            case Msg.ModeCommand or Msg.ModeSnapshot:
                OnMode((ModeEnvelope)message.Payload);
                break;
            case Msg.PickupTaken:
                var take = (PickupMessage)message.Payload;
                if (take.Index < Pickups.Count) { Pickups[take.Index].Take(); }
                if (Actor(take.Player) is { IsRemote: false, Ammo: { } ammo })
                {
                    var added = ammo.AddReserve(tuning.PickupAmount);
                    if (take.Player == player?.ActorId) viewBinding.AmmoPopup(Actor(take.Player)!, added);
                }
                break;
            case Msg.PickupRespawn:
                var index = ((IntMessage)message.Payload).Value;
                if (index < Pickups.Count) { Pickups[index].Reveal(tuning.PickupRevealTime); }
                break;
            case Msg.MatchEnd:
                var standings = (IReadOnlyList<Standing>)message.Payload;
                FinalStandings = standings;
                phase = Phase.Ended;
                worldReplica.EndGather();
                timeLeft = 0;
                foreach (var actor in Session.OwnedActors) actor.Character.InputLocked = true;
                endLeft = tuning.ResultsTime;
                viewBinding.ShowResults(standings, tuning.ResultsTime, timeLeft);
                break;
            case Msg.PlayerLeft:
                var left = (PlayerLeftMessage)message.Payload;
                foreach (var departing in Session.Actors.Where(a => a.Character.OwnerPeerId == left.Player).ToArray())
                {
                    viewBinding.ShowNotice($"{profiles[departing.Character.ActorId].Name} left the {viewBinding.Display.Location}", 3f);
                    Session.RemoveActor(departing);
                }
                ownedBots.RefreshBotPeers();
                worldReplica.Bodies.Host = left.Host;
                break;
        }
    }

    private void OnMode(ModeEnvelope envelope)
    {
        if (envelope.ModeId != mode.Id || envelope.SchemaVersion != mode.SchemaVersion || envelope.WorldRevision != worldRevision ||
            envelope.RoundId < round || !commandOrder.Accept(envelope.EventId)) { return; }
        round = envelope.RoundId;
        ClientMode.Apply(envelope);
    }

    private PlayerCharacter? Actor(int id) => Session.Actor(id)?.Character;
    public IModeView? ModeView => clientMode?.ViewAt(Math.Max(0, Setup.Length - timeLeft));
}
