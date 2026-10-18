using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Modes;
using Propane.Player;
using Propane.Session;
using Propane.Tank;
using Propane.UI;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>
/// A multiplayer match on one peer: the generated suburb, its owned actors, everyone else's
/// remote copies, ammo pickups and local presentation. Every peer simulates the whole suburb; this class sends what
/// its actors do (movement, shots, punctures, explosions, drops, the bodies they own) and plays what the others
/// send. See docs/multiplayer.md for the rules and the networking model.
/// </summary>
public partial class Match : Node3D, IClientAuthorityReplica
{
    public SessionContext Session { get; private set; } = null!;
    public WorldContext World => Session.World;
    public bool CreateLocalView { get; init; } = true;

    /// <summary>Network time source; set before entering the tree for deterministic simulation.</summary>
    internal IMonotonicClock Clock { get; init; } = MonotonicClock.Instance;

    private enum Phase
    {
        Loading,
        Countdown,
        Playing,
        Ended,
    }

    [Flags]
    private enum Impact : byte
    {
        None = 0,
        Hit = 1,
        Metal = 2,
        Decal = 4,
    }

    private const float RevealMaxRadius = 320f;
    private const float MaterializeShare = 0.58f;
    private const float TagRefresh = 1f / 30f;

    private readonly Dictionary<int, PlayerProfile> profiles = new();
    private readonly Dictionary<int, int> scores = new();
    /// <summary>The leaderboard's roster, in match order (ties rank in it, as in the results).</summary>
    private readonly List<LeaderboardEntry> leaderboardRoster = new();
    private ModeDefinition mode = null!;
    private IModeHudPresenter hudPresenter = null!;
    private readonly ModeCommandOrder commandOrder = new();
    private uint round = RoundInfo.First.RoundId;
    private readonly uint worldRevision = RoundInfo.First.WorldRevision;
    private readonly Dictionary<uint, PropaneTank> tanks = new();
    private readonly HashSet<uint> exploded = new();
    private readonly HashSet<uint> predictedExplosions = new();
    private ClientAuthorityTransactions authority = null!;
    private readonly HashSet<uint> unconfirmedDrops = new();

    private readonly Dictionary<int, RigidBody3D> looseProps = new();
    private readonly List<AmmoPickup> pickups = new();

    private VoidEnvironment environment = null!;
    private Node3D effects = null!;
    private Suburb suburb = null!;
    private SuburbPlan plan = null!;
    // Refreshed synchronously at actor/profile changes; never search the roster from presentation.
    private PlayerCharacter? player;

    private BodySync bodies = null!;
    private BodyPublisher bodyPublisher = null!;
    private TankGather gather = null!;
    private Hud hud = null!;
    private MatchHud matchHud = null!;
    private MatchMenu menu = null!;
    private ResultsScreen results = null!;
    private TuningPanel? tuningPanel;

    private Phase phase = Phase.Loading;
    private int me;
    private int loadingFrames;
    private float revealTime;
    private bool revealing = true;
    private float countdownLeft;
    private float timeLeft = -1;
    private float endLeft;
    private float groupRefresh;
    private List<Vector3> tankGroups = new();
    private string noticeText = "";
    private float noticeLeft;

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

    public int MyScore => player == null ? 0 : scores.GetValueOrDefault(player.ActorId);

    public IReadOnlyDictionary<int, int> Scores => scores;

    public bool IsPlaying => phase == Phase.Playing;

    public IReadOnlyList<Standing> FinalStandings { get; private set; } = Array.Empty<Standing>();

    public bool IsOver => phase == Phase.Ended;

    public float TimeLeft => timeLeft;

    private System.Collections.Generic.IEnumerable<ActorRuntime> RemoteActors => Session.Actors.Where(actor => actor.Character.IsRemote);

    public IEnumerable<PlayerCharacter> RemoteBodies => RemoteActors.Select(actor => actor.Character);

    public IReadOnlyList<AmmoPickup> Pickups => pickups;

    public IEnumerable<PropaneTank> LiveTanks => tanks.Values.Where(t => IsInstanceValid(t) && t.State != PropaneTank.TankState.Exploded);

    public BodySync Bodies => bodies;

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
        SessionFactory.Build(this, tuning, new MatchSource(this), new TankBankMode(this), session => Session = session,
            networked: true, present: CreateLocalView);
    }

    public override void _ExitTree() => Session?.Dispose();

    private void RefreshPlayer()
    {
        player = null;
        if (!Session.IsAlive) return;
        foreach (var actor in Session.Actors)
        {
            if (actor.Character.IsRemote) continue;
            player ??= actor.Character;
            if (profiles[actor.Character.ActorId].Kind != ActorKind.Human) continue;
            player = actor.Character;
            break;
        }
    }

    private void StopReplication()
    {
        RefreshPlayer();
        foreach (var tank in tanks.Values.Where(IsInstanceValid))
        {
            tank.Punctured -= OnTankPunctured;
            tank.Detonated -= OnTankDetonated;
        }
        bodies?.Clear();
        tanks.Clear();
        looseProps.Clear();
        pickups.Clear();
        authority?.Dispose();
    }

    // ------------------------------------------------------------------ Frame

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
        var tuning = World.Tuning;
        if (revealing)
        {
            revealTime += dt;
            var t = Mathf.Clamp(revealTime / Mathf.Max(tuning.TransitionTime * MaterializeShare, 0.01f), 0, 1);
            World.Reveal.Radius = Mathf.Lerp(0f, RevealMaxRadius, t * t);
            if (t >= 1f)
            {
                revealing = false;
                suburb.SetRevealSide(0f);
                World.Reveal.Radius = 100000f;
            }
        }

        switch (phase)
        {
            case Phase.Countdown:
                countdownLeft -= dt;
                if (countdownLeft > 0)
                {
                    matchHud.ShowCountdown(Mathf.CeilToInt(countdownLeft).ToString());
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

        noticeLeft -= dt;
        matchHud.SetNotice(noticeLeft > 0 ? noticeText : "");
        UpdateHud(dt);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (World is not { IsAlive: true } || World.Paused) { return; }
        var dt = (float)delta;
        var now = Clock.Seconds;
        foreach (var remote in RemoteActors)
        {
            (remote.IntentSource as ReplicatedIntent)?.Apply(remote.Character, now);
        }
        bodies.Correct(now);
        if (IsLobby) RefillLobbyRifles();
        ThinkBots(dt);

        foreach (var owned in Session.OwnedActors)
        {
            owned.SendTimer -= dt;
            if (owned.SendTimer > 0) continue;
            owned.SendTimer = Mathf.Max(0, owned.SendTimer + 1f / Mathf.Max(World.Tuning.PlayerSendRate, 1f));
            SendPlayerState(owned.Character, now);
        }
        SendBodies(now);
        CheckPickups();
    }

    private void UpdateHud(float dt)
    {
        if (player?.View == null) return;
        var tuning = World.Tuning;
        var camera = player.View.CameraRig.Camera;
        var viewportHeight = GetViewport().GetVisibleRect().Size.Y;
        var radius = Mathf.Tan(Mathf.DegToRad(player.SpreadDegrees)) / Mathf.Tan(Mathf.DegToRad(camera.Fov / 2f)) * viewportHeight / 2f;
        // The lobby world's stage screen stands in for the HUD: no tags, notices or crosshair through its lineup.
        var menuOpen = menu.Visible || tuningPanel?.Visible == true || lobbyStaged;
        hud.SetCrosshair(radius, !player.IsRagdolled && Input.MouseMode == Input.MouseModeEnum.Captured && phase != Phase.Ended, player.AimAmount);

        groupRefresh -= dt;
        if (groupRefresh <= 0)
        {
            groupRefresh = 0.25f;
            tankGroups = phase is Phase.Playing or Phase.Countdown ? TankGroups() : new List<Vector3>();
        }
        var shown = PresentHud(tankGroups, tuning.TankArrows);
        hud.SetTankArrows(camera, shown.Objectives, shown.ObjectivesVisible);
        hud.Visible = !menuOpen;
        matchHud.Visible = !menuOpen;

        matchHud.Show(shown);
        matchHud.SetAmmo(player.Ammo);
        matchHud.SetView(camera, player.GlobalPosition);
        matchHud.SetTags(ScoreTags(camera));
        for (var i = 0; i < leaderboardRoster.Count; i++)
        {
            if (leaderboardRoster[i].Present && Actor(leaderboardRoster[i].Id) == null) leaderboardRoster[i] = leaderboardRoster[i] with { Present = false };
        }
        matchHud.SetStandings(leaderboardRoster, scores, player.ActorId, phase is Phase.Countdown or Phase.Playing);
    }

    private ModePhase ModePhase => phase switch
    {
        Phase.Loading => Modes.ModePhase.Loading,
        Phase.Countdown => Modes.ModePhase.Countdown,
        Phase.Playing => Modes.ModePhase.Round,
        _ => Modes.ModePhase.Results,
    };

    /// <summary>The mode's HUD data for this peer's view; the local view adds crosshair, ammo and feedback.</summary>
    private ModeHud PresentHud(IReadOnlyList<Vector3> objectives, bool showObjectives) =>
        hudPresenter.Present(new ModeHudInput(player?.ActorId ?? 0, ModePhase, timeLeft, scores, objectives, showObjectives, View: ModeView));

    /// <summary>Groups of live tanks (the plan's and dropped ones), for the arrows.</summary>
    private List<Vector3> TankGroups() => suburb.TankGroupCenters();

    /// <summary>Every other player's score, over their head, if this player can see them and they are near enough.</summary>
    private List<ScoreTag> ScoreTags(Camera3D camera)
    {
        var tags = new List<ScoreTag>();
        var tuning = World.Tuning;
        var space = World.Physics.DirectSpaceState;
        var from = camera.GlobalPosition;
        foreach (var remote in Session.Actors.Where(a => a.Character != player))
        {
            var at = remote.Character.TagPosition;
            var distance = from.DistanceTo(at);
            if (distance > tuning.ScoreTagRange || camera.IsPositionBehind(at))
            {
                continue;
            }
            var ray = PhysicsRayQueryParameters3D.Create(from, at, Layers.World | Layers.Props);
            if (space.IntersectRay(ray).Count > 0)
            {
                continue;
            }
            var scale = tuning.ScoreTagScale * Mathf.Lerp(1.15f, 0.6f, Mathf.Clamp(distance / Mathf.Max(tuning.ScoreTagRange, 1f), 0, 1));
            var profile = profiles[remote.Character.ActorId];
            // The lobby world has no scores: everyone's name floats over them instead.
            tags.Add(new ScoreTag(camera.UnprojectPosition(at), scores.GetValueOrDefault(remote.Character.ActorId), profile.Appearance.Color, scale,
                IsLobby ? profile.Name : null));
        }
        return tags;
    }

    // ------------------------------------------------------------------ Input

    public override void _UnhandledInput(InputEvent @event)
    {
        if (World is not { IsAlive: true }) { return; }
        if (LobbyInput(@event)) { return; }
        if (@event.IsActionPressed(InputSetup.Pause))
        {
            if (tuningPanel?.Visible == true)
            {
                tuningPanel.Toggle();
                CaptureMouse(true);
            }
            else if (menu.Visible)
            {
                CloseMenu();
            }
            else
            {
                menu.Visible = true;
                CaptureMouse(false);
            }
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed(InputSetup.ToggleTuning) && tuningPanel != null && !menu.Visible)
        {
            tuningPanel.Toggle();
            CaptureMouse(!tuningPanel.Visible);
        }
        else if (@event.IsActionPressed(InputSetup.ToggleFullscreen))
        {
            var fullscreen = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
            DisplayServer.WindowSetMode(fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
        }
        else if (@event is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured &&
                 !menu.Visible && tuningPanel?.Visible != true)
        {
            CaptureMouse(true);
        }
    }

    private void CloseMenu()
    {
        menu.Visible = false;
        CaptureMouse(true);
    }

    private static void CaptureMouse(bool captured) =>
        Input.MouseMode = captured ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;

    private void ShowNotice(string text, float seconds)
    {
        noticeText = text;
        noticeLeft = seconds;
    }

    // ------------------------------------------------------------------ Tanks

    /// <summary>Starts following a tank: its id, owner and what happens to it here.</summary>
    private void Track(PropaneTank tank, int owner, bool replace = false)
    {
        tanks[tank.NetId] = tank;
        if (replace) { bodies.ReplaceBody(tank.NetId, tank); }
        else { bodies.Register(tank.NetId, tank, isTank: true, owner); }
        tank.Punctured += OnTankPunctured;
        tank.Detonated += OnTankDetonated;
    }

    private void OnTankPunctured(PropaneTank tank)
    {
        var request = authority.NewRequest();
        var puncture = new PunctureMessage(tank.NetId, tank.GlobalTransform, tank.LinearVelocity, tank.AngularVelocity,
            tank.HoleLocal, tank.HoleNormalLocal, tank.SwirlSign, Request: request, Cause: tank.ChainCause);
        authority.Puncture(puncture, tank.CauseActorId, Actor(tank.CauseActorId)?.LifeId ?? 0);
    }

    private void OnTankDetonated(PropaneTank tank)
    {
        if (tank.DetonatedRemotely)
        {
            tanks.Remove(tank.NetId);
            bodies.Remove(tank.NetId);
            exploded.Add(tank.NetId);
            return;
        }
        // Keep the registry/epoch until the server resolves the prediction. A refusal can recreate this body.
        predictedExplosions.Add(tank.NetId);
        var request = authority.NewRequest();
        tank.BlastRequest = request;
        var explosion = new ExplodeMessage(tank.NetId, tank.GlobalTransform * PropaneTank.LocalCenter,
            Request: request, Cause: tank.ChainCause);
        authority.Explode(explosion, tank.CauseActorId, Actor(tank.CauseActorId)?.LifeId ?? 0);
    }

    internal void OnLocalBlast(Vector3 center, IReadOnlyList<RigidBody3D> pushed, uint cause)
    {
        if (!World.IsAlive) { return; }
        Claim(pushed, cause);
        // Child punctures are queued during ChainHit, before the blast applies its impulse/spin.
        // Flush even if every pushed body is already owned or pending and Claim has nothing to send.
        authority.Flush();
    }

    /// <summary>One atomic request for all disturbed bodies, with each body's expected epoch and proposed pose.</summary>
    internal uint Claim(IEnumerable<GodotObject> disturbed, uint cause = 0, bool atomic = false, bool predictMotion = true)
    {
        var claims = atomic ? bodies.ClaimGroup(disturbed, predictMotion) : bodies.Claim(disturbed);
        return authority.Claim(claims, cause);
    }

    private void GrantGather(IReadOnlyList<PropaneTank> members, Action<Func<PropaneTank, bool>?> done) =>
        authority.GrantGather(bodies.ClaimGroup(members, predictMotion: false),
            granted => done(granted == null ? null : tank => granted(tank.NetId)));

    private void PublishGather(PropaneTank tank, GatherPhase phase)
    {
        if (!IsPlaying || bodies.Host != me || bodies.Get(tank.NetId) is not { } entry || entry.Owner != me || entry.ClaimPending ||
            tank.State != PropaneTank.TankState.Intact) { return; }
        var state = BodySync.Capture(entry) with { Sequence = checked(++entry.Sequence) };
        Net.Send(MessageCodec.Encode(new NetMessage(Msg.Gather, Setup.Id, 0, new GatherMessage(phase, new[] { state })), fromServer: false));
    }

    private void ReceiveGather(int sender, GatherMessage message)
    {
        foreach (var state in message.Bodies)
        {
            if (bodies.Get(state.Id) is not { } entry || entry.Owner != sender || entry.Epoch != state.Epoch ||
                state.Sequence <= entry.LastGatherSequence || entry.Body is not PropaneTank tank || tank.State != PropaneTank.TankState.Intact) { continue; }
            entry.LastGatherSequence = state.Sequence;
            bodies.OnState(sender, state, message.Phase == GatherPhase.Settled, Clock.Seconds);
            if (message.Phase == GatherPhase.Glide) { tank.BeginGather(); }
            else
            {
                tank.EndGather();
                if (message.Phase == GatherPhase.Settled)
                {
                    if (state.Sequence >= entry.LastSequence)
                    {
                        BodySync.Snap(tank, state.Transform, Vector3.Zero, Vector3.Zero);
                        tank.Sleeping = true;
                    }
                    tank.MarkPile(state.Transform);
                }
            }
        }
    }

    BodySync.Entry? IClientAuthorityReplica.FindBody(uint id) => bodies.Get(id);

    BodyState? IClientAuthorityReplica.CurrentProposal(uint id) => bodies.Get(id) is { } entry && IsInstanceValid(entry.Body)
        ? BodySync.Capture(entry) : null;

    void IClientAuthorityReplica.SendAuthority(Msg type, object payload, int actorId, uint lifeId)
    {
        var actor = Actor(actorId);
        var action = actor != null ? checked(++actor.Runtime.ActionSequence) : 0;
        var bytes = MessageCodec.Encode(new NetMessage(type, Setup.Id, 0, payload, actorId, lifeId, action), fromServer: false);
        if (AuthoritySendOverride != null) { AuthoritySendOverride(bytes); }
        else { Net.Send(bytes); }
    }

    void IClientAuthorityReplica.CancelRoot(uint request)
    {
        // Already owned venting neighbors need no Claim, but can still have this root's fuse.
        foreach (var tank in LiveTanks.Where(t => t.ChainCause == request)) { tank.CancelChainFuse(); }
    }

    (BodyAuthority Authority, BodySync.Entry? Entry)? IClientAuthorityReplica.Adopt(BodyAuthority authority, bool gatherGrant)
    {
        var id = authority.Body.Id;
        if (exploded.Contains(id)) { return null; }
        var entry = bodies.Get(id);
        if (!gatherGrant && entry != null && authority.Body.Epoch > entry.Epoch &&
            entry.Body is PropaneTank changed && IsInstanceValid(changed)) { changed.Disturb(); }
        if (entry != null && !bodies.OnAuthority(authority, out authority, Clock.Seconds)) { return null; }
        return (authority, entry);
    }

    void IClientAuthorityReplica.Explode(BodyAuthority authority)
    {
        var id = authority.Body.Id;
        if (predictedExplosions.Remove(id))
        {
            tanks.Remove(id);
            bodies.Remove(id);
            exploded.Add(id);
        }
        else { PlayExplosion(id, authority.Center); }
    }

    void IClientAuthorityReplica.Restore(BodyAuthority authority, BodySync.Entry? entry, ClaimRefusal reason, bool finishedPuncture)
    {
        var id = authority.Body.Id;
        if (id >= BodySync.CarBase)
        {
            if (entry != null && reason != ClaimRefusal.None)
            {
                BodySync.Snap(entry.Body, authority.Body.Transform, authority.Body.Linear, authority.Body.Angular);
                entry.Body.Sleeping = authority.Rest;
            }
            return;
        }
        var recreated = !tanks.TryGetValue(id, out var tank) || !IsInstanceValid(tank) || tank.State == PropaneTank.TankState.Exploded;
        if (recreated)
        {
            var pilePose = tank?.PilePose;
            if (tank != null && IsInstanceValid(tank) && !tank.IsQueuedForDeletion()) { tank.QueueFree(); }
            tank = PropaneTank.Create(World);
            tank.NetId = id;
            suburb.AddTank(tank);
            if (pilePose is { } pose) { tank.MarkPile(pose); }
            Track(tank, authority.Owner, replace: entry != null);
            if (entry == null) { bodies.OnAuthority(authority, Clock.Seconds); }
        }
        if (reason != ClaimRefusal.None) { tank!.CancelChainFuse(); }
        var wasIntact = tank!.State == PropaneTank.TankState.Intact;
        if (authority.State == TankLedger.State.Intact) { tank.RestoreIntact(); }
        // A losing puncture changes the vent, not its current trajectory. The confirmed owner's stream
        // corrects motion normally; the later refusal must not rewind adoption to the cached claim pose.
        var adoptingVent = finishedPuncture && !recreated && !wasIntact && authority.State == TankLedger.State.Venting;
        // A remote root blast can already have pushed an intact copy before its canonical child puncture.
        // The grant's pose predates that push. Keep existing motion until the confirmed owner's stream arrives;
        // only a resting intact observer needs initialization from the puncture's pose.
        var initializeVent = wasIntact && authority.State == TankLedger.State.Venting && tank.Sleeping;
        if (recreated || initializeVent || reason != ClaimRefusal.None && !adoptingVent)
        {
            BodySync.Snap(tank, authority.Body.Transform, authority.Body.Linear, authority.Body.Angular);
            tank.Sleeping = authority.Rest;
        }
        if (authority.Vent is { } vent && authority.State == TankLedger.State.Venting) { tank.PunctureRemote(vent.Hole, vent.Normal, vent.Swirl); }
        predictedExplosions.Remove(id);
    }

    void IClientAuthorityReplica.RemoveUnknown(uint id)
    {
        bodies.Remove(id);
        if (tanks.Remove(id, out var unknown) && IsInstanceValid(unknown)) { unknown.QueueFree(); }
        predictedExplosions.Remove(id);
    }

    void IClientAuthorityReplica.Notify(AuthorityMessage result) => AuthorityResolved?.Invoke(result);

    private void PlayExplosion(uint id, Vector3 center)
    {
        if (tanks.TryGetValue(id, out var known) && IsInstanceValid(known) && known.State != PropaneTank.TankState.Exploded)
        {
            known.DetonateRemote(center);
        }
        else
        {
            tanks.Remove(id);
            bodies.Remove(id);
            exploded.Add(id);
            var ground = World.Physics.DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(center, center + Vector3.Down * 30f, Layers.World | Layers.Floor));
            ExplosionEffect.Create(World, center, ground.Count > 0 ? ground["position"].AsVector3().Y : 0f);
            Blast.Detonate(World, center, null, chain: false);
        }
    }

    // ------------------------------------------------------------------ This player's actions

    private void OnFired(PlayerCharacter actor, ShotReport shot)
    {
        var impact = Impact.None;
        var victim = 0;
        var propIndex = -1;
        if (shot.Hit)
        {
            impact |= Impact.Hit;
            switch (shot.Collider)
            {
                case PropaneTank:
                    impact |= Impact.Metal;
                    break;
                case PlayerCharacter or PhysicalBone3D:
                    if (PlayerCharacter.Owning(shot.Collider) is { } hitPlayer && hitPlayer != actor)
                    {
                        victim = hitPlayer.ActorId;
                        if (actor == player) matchHud.ShowHitMarker();
                    }
                    break;
                case RigidBody3D { Freeze: false } body:
                    if (body is DebrisBody)
                    {
                        impact |= Impact.Metal;
                    }
                    if (bodies.Find(body) != null)
                    {
                        Claim(new[] { body });
                    }
                    else if (body.HasMeta(Suburb.PlanIndexMeta))
                    {
                        propIndex = body.GetMeta(Suburb.PlanIndexMeta).AsInt32();
                    }
                    break;
                default:
                    impact |= Impact.Decal;
                    break;
            }
        }
        SendActor(actor, Msg.Shot, new ShotMessage(shot.End, shot.Normal, shot.Direction, (byte)impact, victim, propIndex,
            Actor(victim)?.LifeId ?? 1), predict: true);
    }

    private readonly Dictionary<int, MeleeStartMessage> remoteMelee = new();

    private void OnMeleeHit(PlayerCharacter actor, MeleeHit hit)
    {
        var victim = 0;
        var propIndex = -1;
        if (hit.Collider is PlayerCharacter target && target != actor)
        {
            victim = target.ActorId;
            if (actor == player) matchHud.ShowHitMarker();
        }
        else if (hit.Collider is RigidBody3D body && bodies.Find(body) == null && body.HasMeta(Suburb.PlanIndexMeta))
        {
            propIndex = body.GetMeta(Suburb.PlanIndexMeta).AsInt32();
        }
        // Tanks and synced cars already took the impulse and use the ordinary ownership/state path.
        if (victim != 0 || propIndex >= 0)
            SendActor(actor, Msg.MeleeContact, new MeleeContactMessage(hit.Swing, victim, propIndex, Actor(victim)?.LifeId ?? 1), predict: true);
    }

    private void OnRagdolled(PlayerCharacter actor, Vector3 at, Vector3 velocity, Vector3 spin)
    {
        Send(ActorWriter(Msg.Ragdoll, actor).Vec3(at).Vec3(velocity).Vec3(spin));
        // Thrown: the bank spills where the player stood, whoever's blast it was.
        DropTanks(actor, World.Tuning.RagdollDropCount, SpillReason.Thrown, at + Vector3.Up * 0.35f, Vector3.Zero);
    }

    /// <summary>Another player's shot hit this player.</summary>
    private void OnHitByShot(PlayerCharacter victim, Vector3 shooterPosition)
    {
        if (victim == player) matchHud.AddHitDirection(shooterPosition);
        // The victim owns this gameplay grace; wire timestamps and interpolation remain monotonic.
        var now = World.Clock.Now;
        var tuning = World.Tuning;
        var grace = victim.Runtime.GraceUntil;
        if (phase != Phase.Playing || !TankBankRules.TryOpenGrace(now, ref grace, tuning.HitGraceTime))
        {
            return;
        }
        victim.Runtime.GraceUntil = grace;
        Vector3 origin;
        Vector3 away;
        if (victim.IsRagdolled)
        {
            origin = victim.PelvisPosition + Vector3.Up * 0.3f;
            away = Vector3.Zero;
        }
        else
        {
            var back = -new Vector3(-Mathf.Sin(victim.FacingYaw), 0, -Mathf.Cos(victim.FacingYaw));
            origin = victim.GlobalPosition + Vector3.Up * 0.45f + back * tuning.HitDropDistance;
            away = back;
        }
        DropTanks(victim, tuning.HitDropCount, SpillReason.Hit, origin, away);
    }

    /// <summary>
    /// Spills up to <paramref name="wanted"/> banked tanks around a point (never more than this player has banked),
    /// owned by this player and shielded for a moment. This owner chooses the placements and predicts them at once;
    /// the spill ticket (actor, life, action id) asks the server's rules to debit the bank.
    /// </summary>
    private void DropTanks(PlayerCharacter actor, int wanted, SpillReason reason, Vector3 origin, Vector3 away)
    {
        if (phase != Phase.Playing)
        {
            return;
        }
        var count = TankBankRules.SpillCount(wanted, scores.GetValueOrDefault(actor.ActorId), actor.Runtime.DropPredictions.UnconfirmedCount);
        if (count <= 0)
        {
            return;
        }
        var tuning = World.Tuning;
        var rng = new RandomNumberGenerator();
        var ticket = checked(++actor.Runtime.ActionSequence);
        var placements = new List<BodyState>(count);
        var start = rng.RandfRange(0, Mathf.Tau);
        var safeFrom = actor.IsRagdolled ? actor.PelvisPosition + Vector3.Up * 0.2f : actor.GlobalPosition + Vector3.Up * 0.9f;
        for (var i = 0; i < count; i++)
        {
            // Spread on a sunflower spiral, so any number lands evenly within the scatter radius.
            var angle = start + i * 2.39996f;
            var spread = count == 1 ? 0.15f : Mathf.Sqrt((i + 0.5f) / count);
            var offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * tuning.DropScatter * spread;
            var position = SafeDropPoint(safeFrom, origin + offset);
            var outward = (offset.LengthSquared() > 0.0001f ? offset.Normalized() : Vector3.Zero) + away;
            outward = outward.LengthSquared() > 0.0001f ? outward.Normalized() : Vector3.Zero;
            var velocity = outward * tuning.DropToss + Vector3.Up * tuning.DropToss * 0.6f;
            var spin = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)) * 2f;
            var transform = new Transform3D(new Basis(Vector3.Up, rng.RandfRange(0, Mathf.Tau)), position);
            var id = actor.Runtime.AllocateDropId();
            actor.Runtime.DropPredictions.Add(id);
            SpawnDroppedTank(id, actor.ActorId, transform, velocity, spin);
            placements.Add(new BodyState(id, transform, velocity, spin, 1, 0));
        }
        if (World.IsAlive)
        {
            Net.Send(MessageCodec.Encode(new NetMessage(Msg.ModeEvent, Setup.Id, 0, new ModeEnvelope(round, worldRevision, mode.Id,
                mode.SchemaVersion, actor.ActorId, actor.LifeId, ticket, ModeSchemas.TankBankKinds.Spill, new TankBankSpill(reason, placements))), fromServer: false));
        }
    }

    /// <summary>Pulls a drop point back toward a known clear point if a wall or prop is in the way, and keeps it above the ground.</summary>
    private Vector3 SafeDropPoint(Vector3 clear, Vector3 wanted)
    {
        var space = World.Physics.DirectSpaceState;
        var mask = Layers.World | Layers.Props | Layers.Floor;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(clear, wanted, mask));
        if (hit.Count > 0)
        {
            var toward = (wanted - clear).Normalized();
            wanted = hit["position"].AsVector3() - toward * 0.3f;
        }
        var ground = space.IntersectRay(PhysicsRayQueryParameters3D.Create(wanted + Vector3.Up * 0.5f, wanted + Vector3.Down * 3f, mask));
        if (ground.Count > 0)
        {
            wanted.Y = Mathf.Max(wanted.Y, ground["position"].AsVector3().Y + 0.05f);
        }
        return wanted;
    }

    private void SpawnDroppedTank(uint id, int owner, Transform3D transform, Vector3 velocity, Vector3 spin)
    {
        if (tanks.ContainsKey(id) || exploded.Contains(id))
        {
            return;
        }
        var tank = PropaneTank.Create(World);
        tank.NetId = id;
        tank.Name = $"Dropped_{id:x}";
        suburb.AddTank(tank);
        tank.GlobalTransform = transform;
        tank.LinearVelocity = velocity;
        tank.AngularVelocity = spin;
        tank.MakeInvulnerable(World.Tuning.DropInvulnerableTime, profiles[owner].Appearance.Color);
        Track(tank, profiles[owner].OwnerPeerId);
    }

    private void CheckPickups()
    {
        foreach (var actor in Session.OwnedActors) CheckPickups(actor.Character);
    }
    private void CheckPickups(PlayerCharacter actor)
    {
        if (phase != Phase.Playing || actor.IsRagdolled || actor.Ammo == null)
        {
            return;
        }
        var position = actor.GlobalPosition;
        var near = false;
        foreach (var pickup in pickups)
        {
            if (!pickup.Available || pickup.ClaimPending)
            {
                continue;
            }
            var offset = pickup.GlobalPosition - position;
            if (new Vector2(offset.X, offset.Z).Length() > AmmoPickup.TakeRadius || Mathf.Abs(offset.Y) > 1.6f)
            {
                continue;
            }
            near = true;
            if (actor.Ammo.Reserve >= World.Tuning.MaxReserve)
            {
                if (!actor.Runtime.AmmoFullShown)
                {
                    actor.Runtime.AmmoFullShown = true;
                    if (actor == player) matchHud.AddAmmoPopup(0);
                }
                continue;
            }
            // It vanishes here at once; the server decides who gets the rounds if two players reach it together.
            pickup.ClaimPending = true;
            Net.Send(ActorWriter(Msg.ClaimPickup, actor).Int(pickup.Index));
        }
        if (!near)
        {
            actor.Runtime.AmmoFullShown = false;
        }
    }

    // ------------------------------------------------------------------ Sending

    private void Send(NetWriter message, TrafficClass traffic = TrafficClass.ReliableOrdered)
    {
        if (World.IsAlive) { Net.Send(message, traffic); }
    }

    private void SendPlayerState(PlayerCharacter actor, double now)
    {
        var sequence = checked(++actor.Runtime.ReplicationSequence);
        var message = new NetMessage(Msg.PlayerState, Setup.Id, 0, new PlayerStateMessage(now, actor.CaptureNetState()), actor.ActorId, actor.LifeId, sequence);
        Net.Send(MessageCodec.Encode(message, fromServer: false), TrafficClass.UnreliablePlayerPoses);
    }

    private void SendBodies(double now)
    {
        unconfirmedDrops.Clear();
        foreach (var owned in Session.OwnedActors) unconfirmedDrops.UnionWith(owned.DropPredictions.PendingIds);
        bodyPublisher.Publish(now, unconfirmedDrops);
    }

    // ------------------------------------------------------------------ Receiving

    internal void OnMessage(NetMessage message)
    {
        if (!World.IsAlive) { return; }
        if (Protocol.IsRelay(message.Type))
        {
            OnRelay(message);
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
                ShowNotice("", 0);
                break;
            case Msg.MatchGo:
                phase = Phase.Playing;
                timeLeft = ((FloatMessage)message.Payload).Value;
                foreach (var actor in Session.OwnedActors) actor.Character.InputLocked = false;
                // The lobby world greets arrivals with its own banner instead.
                if (!IsLobby) matchHud.ShowCountdown("GO");
                break;
            case Msg.Clock:
                if (phase == Phase.Playing) { timeLeft = ((FloatMessage)message.Payload).Value; }
                break;
            case Msg.ModeCommand or Msg.ModeSnapshot:
                OnMode((ModeEnvelope)message.Payload);
                break;
            case Msg.PickupTaken:
                var take = (PickupMessage)message.Payload;
                if (take.Index < pickups.Count) { pickups[take.Index].Take(); }
                if (Actor(take.Player) is { IsRemote: false, Ammo: { } ammo })
                {
                    var added = ammo.AddReserve(tuning.PickupAmount);
                    if (take.Player == player?.ActorId) matchHud.AddAmmoPopup(added);
                }
                break;
            case Msg.PickupRespawn:
                var index = ((IntMessage)message.Payload).Value;
                if (index < pickups.Count) { pickups[index].Reveal(tuning.PickupRevealTime); }
                break;
            case Msg.MatchEnd:
                var standings = (IReadOnlyList<Standing>)message.Payload;
                FinalStandings = standings;
                phase = Phase.Ended;
                foreach (var tank in LiveTanks) { tank.EndGather(); }
                timeLeft = 0;
                foreach (var actor in Session.OwnedActors) actor.Character.InputLocked = true;
                endLeft = tuning.ResultsTime;
                if (player != null) results.Show(standings, player.ActorId, tuning.ResultsTime, PresentHud(Array.Empty<Vector3>(), false).Columns);
                break;
            case Msg.PlayerLeft:
                var left = (PlayerLeftMessage)message.Payload;
                foreach (var departing in Session.Actors.Where(a => a.Character.OwnerPeerId == left.Player).ToArray())
                {
                    ShowNotice($"{profiles[departing.Character.ActorId].Name} left the {(IsLobby ? "lobby" : "match")}", 3f);
                    Session.RemoveActor(departing);
                }
                RefreshBotPeers();
                bodies.Host = left.Host;
                break;
        }
    }

    /// <summary>
    /// Mode commands and snapshots from the server, in command order: duplicates and overtaken ids are dropped, and
    /// only this run's schema and world revision apply. Round ids only move forward.
    /// </summary>
    private void OnMode(ModeEnvelope envelope)
    {
        if (envelope.ModeId != mode.Id || envelope.SchemaVersion != mode.SchemaVersion || envelope.WorldRevision != worldRevision ||
            envelope.RoundId < round || !commandOrder.Accept(envelope.EventId)) { return; }
        round = envelope.RoundId;
        switch (envelope.Payload)
        {
            case TankBankScore score:
                ApplyScore(envelope.ActorId, score.Score, score.Delta);
                break;
            case TankBankBanks banks:
                foreach (var (actor, bank, _) in banks.Banks) ApplyScore(actor, bank, 0);
                break;
            case TankBankSpill spill:
                // The committed spill reaches everyone, its owner included: confirmation clears pending ids without
                // spawning them twice.
                if (Actor(envelope.ActorId) is not { } source || source.LifeId != envelope.LifeId) break;
                if (!source.IsRemote) source.Runtime.DropPredictions.Accepted(spill.Tanks.Select(t => t.Id));
                foreach (var body in spill.Tanks) { SpawnDroppedTank(body.Id, envelope.ActorId, body.Transform, body.Linear, body.Angular); }
                break;
            case TankBankRefused refusedSpill:
                foreach (var id in Session.OwnedActors.SelectMany(a => a.DropPredictions.Refused(refusedSpill.Ids)))
                {
                    if (!tanks.Remove(id, out var refused)) { continue; }
                    bodies.Remove(id);
                    refused.Punctured -= OnTankPunctured;
                    refused.Detonated -= OnTankDetonated;
                    refused.QueueFree();
                }
                break;
            default:
                OnKing(envelope.Payload);
                break;
        }
    }

    private void ApplyScore(int actorId, int score, int delta)
    {
        scores[actorId] = score;
        if (actorId == player?.ActorId)
        {
            matchHud.Show(PresentHud(tankGroups, World.Tuning.TankArrows), delta > 0);
            if (delta > 0) { matchHud.AddScorePopup(delta); }
        }
        if (delta < 0 && Actor(actorId) is { IsRemote: false } scored)
            scored.Runtime.DropPredictions.ScoreConfirmed(delta);
    }

    private PlayerCharacter? Actor(int id) => Session.Actor(id)?.Character;
    private NetWriter ActorWriter(Msg type, PlayerCharacter actor) =>
        new(type, Setup.Id, actor.ActorId, actor.LifeId, checked(++actor.Runtime.ActionSequence));
    private readonly Dictionary<(int Actor, Msg Type), (uint Life, uint Action)> receivedActions = new();
    private void SendActor(PlayerCharacter actor, Msg type, object payload, bool predict = false)
    {
        var message = new NetMessage(type, Setup.Id, me, payload, actor.ActorId, actor.LifeId, checked(++actor.Runtime.ActionSequence));
        if (predict) OnRelay(message);
        Net.Send(MessageCodec.Encode(message, false));
    }
    private void OnRelay(NetMessage message)
    {
        var now = Clock.Seconds;
        var sender = message.Sender;
        var payload = message.Payload;
        var source = Actor(message.ActorId);
        if (Protocol.IsActorMessage(message.Type))
        {
            if (source == null || source.OwnerPeerId != sender || source.LifeId != message.LifeId) return;
            var key = (message.ActorId, message.Type);
            var previous = receivedActions.GetValueOrDefault(key);
            if (previous.Life == message.LifeId && message.ActionId <= previous.Action) return;
            receivedActions[key] = (message.LifeId, message.ActionId);
        }
        var remote = source is { IsRemote: true } ? source.Runtime : null;
        switch (payload)
        {
            case PlayerStateMessage state:
                remote?.Snapshots?.Add(state.Time, state.State, now);
                break;
            case MeleeStartMessage strike:
                remoteMelee[message.ActorId] = strike;
                break;
            case MeleeContactMessage contact:
                if (!remoteMelee.TryGetValue(message.ActorId, out var swing) || swing.Swing != contact.Swing) break;
                if (Actor(contact.Victim) is { IsRemote: false } victim && victim.LifeId == contact.VictimLifeId)
                {
                    victim.Runtime.LastHitTime = now;
                    victim.Runtime.LastHitBy = message.ActorId;
                    victim.ReceiveMelee(swing.Direction);
                    if (victim == player && source != null) matchHud.AddHitDirection(source.GlobalPosition);
                }
                else if (sender != me && contact.PropIndex >= 0 && looseProps.TryGetValue(contact.PropIndex, out var pushed) && IsInstanceValid(pushed) && !pushed.Freeze)
                {
                    pushed.ApplyCentralImpulse(swing.Direction * World.Tuning.MeleePropImpulse);
                }

                break;
            case ShotMessage shot:
                remote?.Character.RemoteShot(shot.End);
                var impact = (Impact)shot.Impact;
                if (sender != me && impact.HasFlag(Impact.Hit))
                {
                    ImpactEffect.Spawn(World, shot.End, shot.Normal, impact.HasFlag(Impact.Metal), impact.HasFlag(Impact.Decal));
                }
                if (sender != me && shot.PropIndex >= 0 && looseProps.TryGetValue(shot.PropIndex, out var prop) && IsInstanceValid(prop) && !prop.Freeze)
                {
                    prop.ApplyImpulse(shot.Direction * World.Tuning.BulletImpulse, shot.End - prop.GlobalPosition);
                }
                if (Actor(shot.Victim) is { IsRemote: false } hitVictim && hitVictim.LifeId == shot.VictimLifeId && source != null)
                {
                    hitVictim.Runtime.LastHitTime = now;
                    hitVictim.Runtime.LastHitBy = message.ActorId;
                    OnHitByShot(hitVictim, source.GlobalPosition + Vector3.Up * 1.4f);
                }
                break;
            case BodyStatesMessage states:
                var transit = Net.PingMs > 0 ? now + Net.ServerClockOffset - states.Time : 0;
                foreach (var body in states.Bodies) { bodies.OnState(sender, body, rest: false, now, transit); }
                break;
            case BodyRestMessage rest:
                bodies.OnState(sender, new BodyState(rest.Id, rest.Transform, Vector3.Zero, Vector3.Zero, rest.Epoch, rest.Sequence), rest: true, now);
                if (bodies.Get(rest.Id) is { } rested && rested.Owner == sender && rested.Epoch == rest.Epoch &&
                    rested.LastSequence == rest.Sequence && rested.Body is PropaneTank restingTank) { restingTank.EndGather(); }
                break;
            case GatherMessage gathering:
                ReceiveGather(sender, gathering);
                break;
            case RagdollMessage ragdoll:
                remote?.Character.RemoteRagdoll(ragdoll.Position, ragdoll.Velocity, ragdoll.Spin);
                break;
            case GetUpMessage getUp:
                remote?.Character.RemoteGetUp(getUp.Position, getUp.Yaw);
                break;
            case TuningMessage tune:
                World.Tuning.Apply(new Godot.Collections.Dictionary { [tune.Name] = tune.Value });
                tuningPanel?.Refresh();
                if (remote != null) { ShowNotice($"{profiles[remote.Character.ActorId].Name} changed {tune.Name}", 2.5f); }
                break;
        }
    }
}
