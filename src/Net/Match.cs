using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Player;
using Propane.Tank;
using Propane.UI;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>
/// A multiplayer match on this player's machine: the suburb the server generated, this player, everyone else's
/// remote copy, the ammo pickups and the match HUD. Every player simulates the whole suburb; this class sends what
/// this player does (movement, shots, punctures, explosions, drops, the bodies they own) and plays what the others
/// send. See docs/multiplayer.md for the rules and the networking model.
/// </summary>
public partial class Match : Node3D
{
    private enum Phase
    {
        Loading,
        Countdown,
        Playing,
        Ended,
    }

    private enum DropReason : byte
    {
        Hit,
        Ragdoll,
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
    private const int StatesPerMessage = Protocol.MaxBodyStates;
    private const float TagRefresh = 1f / 30f;

    private sealed class Remote
    {
        public required PlayerProfile Profile;
        public required PlayerCharacter Body;
        public readonly SnapshotBuffer Buffer = new();
    }

    private sealed record AuthorityRequest(Msg Type, uint Request, uint Cause, uint[] Ids, object Payload)
    {
        public readonly Dictionary<uint, uint> Expected = new();
    }

    private readonly Dictionary<int, Remote> remotes = new();
    private readonly Dictionary<int, PlayerProfile> profiles = new();
    private readonly Dictionary<int, int> scores = new();
    private readonly Dictionary<uint, PropaneTank> tanks = new();
    private readonly HashSet<uint> exploded = new();
    private readonly HashSet<uint> predictedExplosions = new();
    private readonly List<AuthorityRequest> outgoingAuthority = new();
    private readonly Dictionary<uint, AuthorityRequest> pendingAuthority = new();
    private readonly Dictionary<uint, uint> pendingBodies = new();
    private readonly HashSet<uint> sentExplosions = new();
    private uint nextRequest;

    private readonly Dictionary<int, RigidBody3D> looseProps = new();
    private readonly List<AmmoPickup> pickups = new();

    private VoidEnvironment environment = null!;
    private Node3D effects = null!;
    private Suburb suburb = null!;
    private SuburbPlan plan = null!;
    private PlayerCharacter player = null!;
    private BodySync bodies = null!;
    private TankGather gather = null!;
    private readonly HashSet<uint> gatherRequests = new();
    private Hud hud = null!;
    private MatchHud matchHud = null!;
    private MatchMenu menu = null!;
    private ResultsScreen results = null!;
    private TuningPanel? tuningPanel;

    private Phase phase = Phase.Loading;
    private int me;
    private int mySlot;
    private float revealTime;
    private bool revealing = true;
    private float countdownLeft;
    private float timeLeft = -1;
    private float endLeft;
    private float sendTimer;
    private float groupRefresh;
    private List<Vector3> tankGroups = new();
    private double graceUntil;
    private int dropCounter;
    private readonly DropPredictions dropPredictions = new();
    private bool ammoFullShown;
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

    public PlayerCharacter Player => player;

    public Suburb Suburb => suburb;

    public int MyScore => scores.GetValueOrDefault(me);

    public IReadOnlyDictionary<int, int> Scores => scores;

    public bool IsPlaying => phase == Phase.Playing;

    public bool IsOver => phase == Phase.Ended;

    public float TimeLeft => timeLeft;

    public IEnumerable<PlayerCharacter> RemoteBodies => remotes.Values.Select(r => r.Body);

    public IReadOnlyList<AmmoPickup> Pickups => pickups;

    public IEnumerable<PropaneTank> LiveTanks => tanks.Values.Where(t => IsInstanceValid(t) && t.State != PropaneTank.TankState.Exploded);

    public BodySync Bodies => bodies;

    public ClaimRefusal LastAuthorityRefusal { get; private set; }

    public event Action<AuthorityMessage>? AuthorityResolved;

    // Packet seam for assertion suites; production sends through the connection.
    internal Action<byte[]>? AuthoritySendOverride { get; set; }

    /// <summary>When (on <see cref="MonotonicClock.Now"/>) another player's shot last hit this player, and whose it was.</summary>
    public double LastHitTime { get; private set; } = -100;

    public int LastHitBy { get; private set; }

    public override void _Ready()
    {
        Tuning.UseForMatch(Tuning.FromSnapshot(Setup.Tuning));
        InputSetup.Register();
        _ = GameAssets.Debris;
        ExplosionEffect.Preload();
        me = Net.MyId;

        environment = new VoidEnvironment { Name = "Void" };
        AddChild(environment);
        effects = new Node3D { Name = "Effects" };
        AddChild(effects);
        Spawn.SetEffectsRoot(effects);

        plan = PlanCodec.Decode(Setup.Plan);
        var settings = SuburbPlans.Match(Tuning.Current, Setup.Players.Count);
        suburb = Suburb.Build(plan, settings, networked: true);
        AddChild(suburb);
        Spawn.SetWorldRoot(suburb.DynamicRoot);
        bodies = new BodySync(me, Setup.HostId);

        var mapTanks = suburb.MapTanks;
        for (var i = 0; i < mapTanks.Count; i++)
        {
            Track(mapTanks[i], Setup.HostId);
        }
        foreach (var body in suburb.DynamicRoot.GetChildren().OfType<RigidBody3D>().Where(b => b.HasMeta(Suburb.PlanIndexMeta)))
        {
            var index = body.GetMeta(Suburb.PlanIndexMeta).AsInt32();
            if (plan.Props[index].Model.StartsWith("car:"))
            {
                bodies.Register(BodySync.CarId(index), body, isTank: false, Setup.HostId);
            }
            else
            {
                looseProps[index] = body;
            }
        }

        gather = new TankGather
        {
            Name = "Gather",
            Tanks = () => LiveTanks,
            Enabled = () => IsPlaying && bodies.Host == me,
            Grant = GrantGather,
            Publish = PublishGather
        };
        suburb.AddChild(gather);

        foreach (var (profile, slot) in Setup.Players)
        {
            profiles[profile.Id] = profile;
            scores[profile.Id] = 0;
            var spawn = plan.Spawns.Count > 0 ? plan.Spawns[slot % plan.Spawns.Count] : new SpawnPoint(plan.Spawn, plan.SpawnYaw);
            var position = Suburb.ToWorld(spawn.Position, 0.05f);
            if (profile.Id == me)
            {
                mySlot = slot;
                player = new PlayerCharacter
                {
                    Name = "Player",
                    PeerId = me,
                    BodyColor = Protocol.PlayerColor(profile.Color),
                    Ammo = AmmoState.Starting(),
                    InputLocked = true,
                };
                AddChild(player);
                player.Teleport(position, spawn.Yaw + Mathf.Pi);
            }
            else
            {
                var body = new PlayerCharacter
                {
                    Name = $"Remote_{profile.Id}",
                    IsRemote = true,
                    PeerId = profile.Id,
                    BodyColor = Protocol.PlayerColor(profile.Color),
                };
                AddChild(body);
                body.Teleport(position, spawn.Yaw + Mathf.Pi);
                remotes[profile.Id] = new Remote { Profile = profile, Body = body };
            }
        }
        if (player == null)
        {
            throw new InvalidOperationException("this player is not in the match");
        }
        environment.Follow = player.CameraRig.Camera;
        player.Fired += OnFired;
        player.Ragdolled += OnRagdolled;
        player.GotUp += (root, yaw) => Send(new NetWriter(Msg.GetUp, Setup.Id).Vec3(root).Float(yaw));
        player.MeleeStarted += (swing, direction) => Send(new NetWriter(Msg.MeleeStart, Setup.Id).UInt(swing).Vec3(direction));
        player.MeleeHit += OnMeleeHit;
        player.PushedBody += body => Claim(new[] { body });

        for (var i = 0; i < plan.AmmoSpots.Count; i++)
        {
            var pickup = new AmmoPickup { Name = $"Ammo_{i}", Index = i };
            suburb.DynamicRoot.AddChild(pickup);
            pickup.Position = Suburb.ToWorld(plan.AmmoSpots[i]);
            pickups.Add(pickup);
        }

        hud = new Hud { Name = "Hud" };
        AddChild(hud);
        hud.ShowTankCount(false);
        matchHud = new MatchHud { Name = "MatchHud" };
        AddChild(matchHud);
        results = new ResultsScreen { Name = "Results" };
        AddChild(results);
        menu = new MatchMenu { Name = "Menu" };
        menu.ResumeRequested += CloseMenu;
        menu.LeaveRequested += () => LeaveRequested?.Invoke();
        AddChild(menu);
        if (OS.IsDebugBuild() && Setup.CreatorDebug && Setup.HostId == me)
        {
            tuningPanel = new TuningPanel { Name = "Tuning", Note = "Changes apply to everyone in the match", UseDeclaredRanges = true };
            tuningPanel.ValueChanged += (name, value) => Send(new NetWriter(Msg.TuningSet, Setup.Id).String(name).Scalar(value));
            AddChild(tuningPanel);
        }

        // The suburb materializes around the player, as in single player.
        suburb.SetRevealSide(1f);
        RenderingServer.GlobalShaderParameterSet("reveal_center", player.GlobalPosition);
        RenderingServer.GlobalShaderParameterSet("reveal_radius", 0f);
        Warmup.Run(this, new Vector3(0, -400f, 0));

        Blast.LocalBlast += OnLocalBlast;
        CaptureMouse(true);
        ShowNotice("Waiting for everyone to arrive", 30f);
        // Loaded is sent after the handoff: Countdown, Go or even End may already be waiting in the inbox.
        Net.AttachMatch(Setup.Id, OnMessage);
        Send(new NetWriter(Msg.Loaded, Setup.Id));
        GD.Print($"[match] seed {plan.Seed}: {plan.Lots.Count} lots, {plan.Tanks.Count} tanks, {pickups.Count} pickups, {Setup.Players.Count} players, host {Setup.HostId}, me {me}");
    }

    public override void _ExitTree()
    {
        Blast.LocalBlast -= OnLocalBlast;
        Net.DetachMatch(Setup.Id, OnMessage);
        Tuning.EndMatch();
        RenderingServer.GlobalShaderParameterSet("reveal_radius", 100000f);
        Engine.TimeScale = 1.0;
    }

    // ------------------------------------------------------------------ Frame

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        var tuning = Tuning.Current;
        if (revealing)
        {
            revealTime += dt;
            var t = Mathf.Clamp(revealTime / Mathf.Max(tuning.TransitionTime * MaterializeShare, 0.01f), 0, 1);
            RenderingServer.GlobalShaderParameterSet("reveal_radius", Mathf.Lerp(0f, RevealMaxRadius, t * t));
            if (t >= 1f)
            {
                revealing = false;
                suburb.SetRevealSide(0f);
                RenderingServer.GlobalShaderParameterSet("reveal_radius", 100000f);
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
                    Finished?.Invoke();
                    // Finished may remove this scene immediately; its viewport and world are then unavailable.
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
        var dt = (float)delta;
        var now = MonotonicClock.Now;
        foreach (var remote in remotes.Values)
        {
            if (remote.Buffer.HasData)
            {
                remote.Body.SetRemoteState(remote.Buffer.Sample(now));
            }
        }
        bodies.Correct(now);

        sendTimer -= dt;
        if (sendTimer <= 0)
        {
            sendTimer += 1f / Mathf.Max(Tuning.Current.PlayerSendRate, 1f);
            sendTimer = Mathf.Max(sendTimer, 0);
            SendPlayerState(now);
        }
        SendBodies(dt, now);
        CheckPickups();
    }

    private void UpdateHud(float dt)
    {
        var tuning = Tuning.Current;
        var camera = player.CameraRig.Camera;
        var viewportHeight = GetViewport().GetVisibleRect().Size.Y;
        var radius = Mathf.Tan(Mathf.DegToRad(player.SpreadDegrees)) / Mathf.Tan(Mathf.DegToRad(camera.Fov / 2f)) * viewportHeight / 2f;
        var menuOpen = menu.Visible || tuningPanel?.Visible == true;
        hud.SetCrosshair(radius, !player.IsRagdolled && Input.MouseMode == Input.MouseModeEnum.Captured && phase != Phase.Ended, player.AimAmount);

        groupRefresh -= dt;
        if (groupRefresh <= 0)
        {
            groupRefresh = 0.25f;
            tankGroups = phase is Phase.Playing or Phase.Countdown ? TankGroups() : new List<Vector3>();
        }
        hud.SetTankArrows(camera, tankGroups, tuning.TankArrows && phase is Phase.Playing or Phase.Countdown);
        hud.Visible = !menuOpen;
        matchHud.Visible = !menuOpen;

        matchHud.SetTimer(phase == Phase.Playing || phase == Phase.Ended ? timeLeft : -1);
        matchHud.SetAmmo(player.Ammo);
        matchHud.SetView(camera, player.GlobalPosition);
        matchHud.SetTags(ScoreTags(camera));
    }

    /// <summary>Groups of live tanks (the plan's and dropped ones), for the arrows.</summary>
    private List<Vector3> TankGroups() => suburb.TankGroupCenters();

    /// <summary>Every other player's score, over their head, if this player can see them and they are near enough.</summary>
    private List<ScoreTag> ScoreTags(Camera3D camera)
    {
        var tags = new List<ScoreTag>();
        var tuning = Tuning.Current;
        var space = GetWorld3D().DirectSpaceState;
        var from = camera.GlobalPosition;
        foreach (var remote in remotes.Values)
        {
            var at = remote.Body.TagPosition;
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
            tags.Add(new ScoreTag(camera.UnprojectPosition(at), scores.GetValueOrDefault(remote.Profile.Id), Protocol.PlayerColor(remote.Profile.Color), scale));
        }
        return tags;
    }

    // ------------------------------------------------------------------ Input

    public override void _UnhandledInput(InputEvent @event)
    {
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

    private uint NewRequest() => checked(++nextRequest);

    private void OnTankPunctured(PropaneTank tank)
    {
        var request = NewRequest();
        var puncture = new PunctureMessage(tank.NetId, tank.GlobalTransform, tank.LinearVelocity, tank.AngularVelocity,
            tank.HoleLocal, tank.HoleNormalLocal, tank.SwirlSign, Request: request, Cause: tank.ChainCause);
        QueueAuthority(new AuthorityRequest(Msg.Puncture, request, tank.ChainCause, new[] { tank.NetId },
            puncture));
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
        var request = NewRequest();
        tank.BlastRequest = request;
        var explosion = new ExplodeMessage(tank.NetId, tank.GlobalTransform * PropaneTank.LocalCenter,
            Request: request, Cause: tank.ChainCause);
        QueueAuthority(new AuthorityRequest(Msg.Explode, request, tank.ChainCause, new[] { tank.NetId },
            explosion));
    }

    internal void OnLocalBlast(Vector3 center, IReadOnlyList<RigidBody3D> pushed, uint cause)
    {
        Claim(pushed, cause);
        // Child punctures are queued during ChainHit, before the blast applies its impulse/spin.
        // Flush even if every pushed body is already owned or pending and Claim has nothing to send.
        FlushAuthority();
    }

    /// <summary>One atomic request for all disturbed bodies, with each body's expected epoch and proposed pose.</summary>
    internal uint Claim(IEnumerable<GodotObject> disturbed, uint cause = 0, bool atomic = false)
    {
        var claims = atomic ? bodies.ClaimGroup(disturbed) : bodies.Claim(disturbed);
        if (claims.Count == 0) { return 0; }
        var request = NewRequest();
        QueueAuthority(new AuthorityRequest(Msg.Claim, request, cause, claims.Select(c => c.Id).ToArray(),
            new ClaimMessage(request, claims, cause)));
        return request;
    }

    private void GrantGather(IReadOnlyList<PropaneTank> members, Action<Func<PropaneTank, bool>?> done)
    {
        var request = Claim(members, atomic: true);
        if (request == 0) { done(null); return; }
        gatherRequests.Add(request);
        void Resolve(AuthorityMessage result)
        {
            if (result.Peer != me || result.Request != request) { return; }
            AuthorityResolved -= Resolve;
            if (result.Reason != ClaimRefusal.None) { done(null); return; }
            var epochs = result.Bodies.ToDictionary(b => b.Body.Id, b => b.Body.Epoch);
            done(t => bodies.Get(t.NetId) is { } e && e.Owner == me && !e.ClaimPending && epochs.GetValueOrDefault(t.NetId) == e.Epoch);
        }
        AuthorityResolved += Resolve;
    }

    private void PublishGather(PropaneTank tank, GatherPhase phase)
    {
        if (!IsPlaying || bodies.Host != me || bodies.Get(tank.NetId) is not { } entry || entry.Owner != me || entry.ClaimPending ||
            tank.State != PropaneTank.TankState.Intact) { return; }
        var state = BodySync.Capture(entry) with { Sequence = ++entry.Sequence };
        Net.Send(MessageCodec.Encode(new NetMessage(Msg.Gather, Setup.Id, 0, new GatherMessage(phase, new[] { state })), fromServer: false));
    }

    private void ReceiveGather(int sender, GatherMessage message)
    {
        foreach (var state in message.Bodies)
        {
            if (bodies.Get(state.Id) is not { } entry || entry.Owner != sender || entry.Epoch != state.Epoch ||
                state.Sequence <= entry.LastGatherSequence || entry.Body is not PropaneTank tank || tank.State != PropaneTank.TankState.Intact) { continue; }
            entry.LastGatherSequence = state.Sequence;
            bodies.OnState(sender, state, message.Phase == GatherPhase.Settled, MonotonicClock.Now);
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

    private void QueueAuthority(AuthorityRequest request)
    {
        foreach (var id in request.Ids)
        {
            if (bodies.Get(id) is { } entry) { request.Expected[id] = entry.Epoch; entry.ClaimPending = true; entry.HasTarget = false; }
        }
        outgoingAuthority.Add(request);
        if (request.Type != Msg.Puncture || request.Cause == 0) { FlushAuthority(); }
    }

    private BodyState? CurrentProposal(uint id) => bodies.Get(id) is { } entry && IsInstanceValid(entry.Body)
        ? BodySync.Capture(entry) : null;

    private PunctureMessage PunctureProposal(PunctureMessage puncture, uint epoch)
    {
        var pose = CurrentProposal(puncture.Id);
        return puncture with
        {
            Epoch = epoch,
            Transform = pose?.Transform ?? puncture.Transform,
            Linear = pose?.Linear ?? puncture.Linear,
            Angular = pose?.Angular ?? puncture.Angular
        };
    }

    private void FlushAuthority()
    {
        for (var i = 0; i < outgoingAuthority.Count;)
        {
            var request = outgoingAuthority[i];
            if (request.Ids.Any(pendingBodies.ContainsKey) || request.Cause != 0 && !sentExplosions.Contains(request.Cause)) { i++; continue; }
            outgoingAuthority.RemoveAt(i);
            pendingAuthority.Add(request.Request, request);
            foreach (var id in request.Ids) { pendingBodies[id] = request.Request; if (bodies.Get(id) is { } entry) { entry.ClaimPending = true; } }
            // A chain's root is sent before its children, even when a puncture/ownership grant held it back.
            if (request.Type == Msg.Explode) { sentExplosions.Add(request.Request); }
            var payload = request.Payload switch
            {
                PunctureMessage p => (object)PunctureProposal(p, request.Expected[p.Id]),
                ExplodeMessage p => p with { Epoch = request.Expected[p.Id] },
                ClaimMessage p => p with { Bodies = p.Bodies.Select(c => (CurrentProposal(c.Id) ?? c) with { Epoch = request.Expected[c.Id] }).ToArray() },
                _ => throw new InvalidOperationException("unknown authority request"),
            };
            var bytes = MessageCodec.Encode(new NetMessage(request.Type, Setup.Id, 0, payload), fromServer: false);
            if (AuthoritySendOverride != null) { AuthoritySendOverride(bytes); }
            else { Net.Send(bytes); }
        }
    }

    private void OnAuthority(AuthorityMessage result)
    {
        AuthorityRequest? finished = null;
        if (result.Peer == me && pendingAuthority.Remove(result.Request, out finished))
        {
            foreach (var id in finished.Ids) { pendingBodies.Remove(id); if (bodies.Get(id) is { } body) { body.ClaimPending = false; } }
            if (result.Reason == ClaimRefusal.None)
            {
                foreach (var next in outgoingAuthority)
                {
                    foreach (var grant in result.Bodies.Where(b => next.Expected.ContainsKey(b.Body.Id)))
                    {
                        next.Expected[grant.Body.Id] = grant.Body.Epoch;
                    }
                }
            }
            if (result.Reason != ClaimRefusal.None)
            {
                LastAuthorityRefusal = result.Reason;
                if (finished.Type == Msg.Explode)
                {
                    // Already owned venting neighbors need no Claim, but can still have this root's fuse.
                    foreach (var tank in LiveTanks.Where(t => t.ChainCause == finished.Request)) { tank.CancelChainFuse(); }
                }
                // Keep dependent predictions' original epochs. They also need explicit refusals to restore
                // their bodies; silently canceling a queued group could strand its other members.
            }
        }
        foreach (var reported in result.Bodies)
        {
            var authority = reported;
            var id = authority.Body.Id;
            if (exploded.Contains(id)) { continue; }
            var entry = bodies.Get(id);
            var gatherGrant = result.Peer == me && result.Reason == ClaimRefusal.None && gatherRequests.Contains(result.Request);
            if (!gatherGrant && entry != null && authority.Body.Epoch > entry.Epoch &&
                entry.Body is PropaneTank changed && IsInstanceValid(changed)) { changed.Disturb(); }
            if (entry != null && !bodies.OnAuthority(authority, out authority)) { continue; }
            if (authority.State == TankLedger.State.Exploded)
            {
                foreach (var canceled in outgoingAuthority.Where(r => r.Type != Msg.Claim && r.Ids.Contains(id)).ToArray())
                {
                    outgoingAuthority.Remove(canceled);
                    if (canceled.Type == Msg.Explode) { sentExplosions.Add(canceled.Request); }
                }
                if (predictedExplosions.Remove(id))
                {
                    tanks.Remove(id);
                    bodies.Remove(id);
                    exploded.Add(id);
                }
                else { PlayExplosion(id, authority.Center); }
                continue;
            }
            // A predicted second shot can already have freed the node before our first-shot grant arrives.
            // Resolve that shot after this grant; don't resurrect the winning prediction between the two echoes.
            if (result.Peer == me && result.Reason == ClaimRefusal.None && outgoingAuthority.Any(r => r.Ids.Contains(id))) { continue; }
            if (id >= BodySync.CarBase)
            {
                if (entry != null && result.Reason != ClaimRefusal.None)
                {
                    BodySync.Snap(entry.Body, authority.Body.Transform, authority.Body.Linear, authority.Body.Angular);
                    entry.Body.Sleeping = authority.Rest;
                }
                continue;
            }
            var recreated = !tanks.TryGetValue(id, out var tank) || !IsInstanceValid(tank) || tank.State == PropaneTank.TankState.Exploded;
            if (recreated)
            {
                var pilePose = tank?.PilePose;
                if (tank != null && IsInstanceValid(tank) && !tank.IsQueuedForDeletion()) { tank.QueueFree(); }
                tank = PropaneTank.Create();
                tank.NetId = id;
                suburb.AddTank(tank);
                if (pilePose is { } pose) { tank.MarkPile(pose); }
                Track(tank, authority.Owner, replace: entry != null);
                if (entry == null) { bodies.OnAuthority(authority); }
            }
            if (result.Reason != ClaimRefusal.None) { tank!.CancelChainFuse(); }
            var wasIntact = tank!.State == PropaneTank.TankState.Intact;
            if (authority.State == TankLedger.State.Intact) { tank.RestoreIntact(); }
            // A losing puncture changes the vent, not its current trajectory. The confirmed owner's stream
            // corrects motion normally; the later refusal must not rewind adoption to the cached claim pose.
            var adoptingVent = finished?.Type == Msg.Puncture && !recreated && !wasIntact && authority.State == TankLedger.State.Venting;
            // A remote root blast can already have pushed an intact copy before its canonical child puncture.
            // The grant's pose predates that push. Keep existing motion until the confirmed owner's stream arrives;
            // only a resting intact observer needs initialization from the puncture's pose.
            var initializeVent = wasIntact && authority.State == TankLedger.State.Venting && tank.Sleeping;
            if (recreated || initializeVent || result.Reason != ClaimRefusal.None && !adoptingVent)
            {
                BodySync.Snap(tank, authority.Body.Transform, authority.Body.Linear, authority.Body.Angular);
                tank.Sleeping = authority.Rest;
            }
            if (authority.Vent is { } vent && authority.State == TankLedger.State.Venting) { tank.PunctureRemote(vent.Hole, vent.Normal, vent.Swirl); }
            predictedExplosions.Remove(id);
        }
        if (finished != null && result.Reason == ClaimRefusal.UnknownBody)
        {
            foreach (var id in finished.Ids.Except(result.Bodies.Select(b => b.Body.Id)))
            {
                bodies.Remove(id);
                if (tanks.Remove(id, out var unknown) && IsInstanceValid(unknown)) { unknown.QueueFree(); }
                predictedExplosions.Remove(id);
            }
        }
        FlushAuthority();
        AuthorityResolved?.Invoke(result);
        if (result.Peer == me) { gatherRequests.Remove(result.Request); }
    }

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
            var ground = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(center, center + Vector3.Down * 30f, Layers.World | Layers.Floor));
            ExplosionEffect.Create(center, ground.Count > 0 ? ground["position"].AsVector3().Y : 0f);
            Blast.Detonate(this, center, null, chain: false);
        }
    }

    // ------------------------------------------------------------------ This player's actions

    private void OnFired(ShotReport shot)
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
                    if (PlayerCharacter.Owning(shot.Collider) is { IsRemote: true } hitPlayer)
                    {
                        victim = hitPlayer.PeerId;
                        matchHud.ShowHitMarker();
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
        Send(new NetWriter(Msg.Shot, Setup.Id).Vec3(shot.End).Vec3(shot.Normal).Vec3(shot.Direction).Byte((byte)impact).Int(victim).Int(propIndex));
    }

    private readonly Dictionary<int, MeleeStartMessage> remoteMelee = new();

    private void OnMeleeHit(MeleeHit hit)
    {
        var victim = 0;
        var propIndex = -1;
        if (hit.Collider is PlayerCharacter { IsRemote: true } target)
        {
            victim = target.PeerId;
            matchHud.ShowHitMarker();
        }
        else if (hit.Collider is RigidBody3D body && bodies.Find(body) == null && body.HasMeta(Suburb.PlanIndexMeta))
        {
            propIndex = body.GetMeta(Suburb.PlanIndexMeta).AsInt32();
        }
        // Tanks and synced cars already took the impulse and use the ordinary ownership/state path.
        if (victim != 0 || propIndex >= 0)
            Send(new NetWriter(Msg.MeleeContact, Setup.Id).UInt(hit.Swing).Int(victim).Int(propIndex));
    }

    private void OnRagdolled(Vector3 at, Vector3 velocity, Vector3 spin)
    {
        Send(new NetWriter(Msg.Ragdoll, Setup.Id).Vec3(at).Vec3(velocity).Vec3(spin));
        // Thrown: the bank spills where the player stood, whoever's blast it was.
        DropTanks(Tuning.Current.RagdollDropCount, DropReason.Ragdoll, at + Vector3.Up * 0.35f, Vector3.Zero);
    }

    /// <summary>Another player's shot hit this player.</summary>
    private void OnHitByShot(Vector3 shooterPosition)
    {
        matchHud.AddHitDirection(shooterPosition);
        var now = MonotonicClock.Now;
        if (phase != Phase.Playing || now < graceUntil)
        {
            return;
        }
        var tuning = Tuning.Current;
        graceUntil = now + tuning.HitGraceTime;
        Vector3 origin;
        Vector3 away;
        if (player.IsRagdolled)
        {
            origin = player.PelvisPosition + Vector3.Up * 0.3f;
            away = Vector3.Zero;
        }
        else
        {
            var back = -new Vector3(-Mathf.Sin(player.FacingYaw), 0, -Mathf.Cos(player.FacingYaw));
            origin = player.GlobalPosition + Vector3.Up * 0.45f + back * tuning.HitDropDistance;
            away = back;
        }
        DropTanks(tuning.HitDropCount, DropReason.Hit, origin, away);
    }

    /// <summary>
    /// Spills up to <paramref name="wanted"/> banked tanks around a point (never more than this player has banked),
    /// owned by this player and shielded for a moment, and tells everyone.
    /// </summary>
    private void DropTanks(int wanted, DropReason reason, Vector3 origin, Vector3 away)
    {
        if (phase != Phase.Playing)
        {
            return;
        }
        var count = Mathf.Min(wanted, Mathf.Max(0, MyScore - dropPredictions.UnconfirmedCount));
        if (count <= 0)
        {
            return;
        }
        var tuning = Tuning.Current;
        var color = profiles[me].Color;
        var rng = new RandomNumberGenerator();
        var w = new NetWriter(Msg.Drop, Setup.Id).Byte((byte)reason).Int(count).Int(color);
        var start = rng.RandfRange(0, Mathf.Tau);
        var safeFrom = player.IsRagdolled ? player.PelvisPosition + Vector3.Up * 0.2f : player.GlobalPosition + Vector3.Up * 0.9f;
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
            var id = BodySync.DropTankId(mySlot, dropCounter++);
            dropPredictions.Add(id);
            SpawnDroppedTank(id, me, color, transform, velocity, spin);
            w.UInt(id).Transform(transform).Vec3(velocity).Vec3(spin).UInt(1).UInt(0);
        }
        Send(w);
    }

    /// <summary>Pulls a drop point back toward a known clear point if a wall or prop is in the way, and keeps it above the ground.</summary>
    private Vector3 SafeDropPoint(Vector3 clear, Vector3 wanted)
    {
        var space = GetWorld3D().DirectSpaceState;
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

    private void SpawnDroppedTank(uint id, int owner, int color, Transform3D transform, Vector3 velocity, Vector3 spin)
    {
        if (tanks.ContainsKey(id) || exploded.Contains(id))
        {
            return;
        }
        var tank = PropaneTank.Create();
        tank.NetId = id;
        tank.Name = $"Dropped_{id:x}";
        suburb.AddTank(tank);
        tank.GlobalTransform = transform;
        tank.LinearVelocity = velocity;
        tank.AngularVelocity = spin;
        tank.MakeInvulnerable(Tuning.Current.DropInvulnerableTime, Protocol.PlayerColor(color));
        Track(tank, owner);
    }

    private void CheckPickups()
    {
        if (phase != Phase.Playing || player.IsRagdolled || player.Ammo == null)
        {
            return;
        }
        var position = player.GlobalPosition;
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
            if (player.Ammo.Reserve >= Tuning.Current.MaxReserve)
            {
                if (!ammoFullShown)
                {
                    ammoFullShown = true;
                    matchHud.AddAmmoPopup(0);
                }
                continue;
            }
            // It vanishes here at once; the server decides who gets the rounds if two players reach it together.
            pickup.ClaimPending = true;
            Net.Send(new NetWriter(Msg.ClaimPickup, Setup.Id).Int(pickup.Index));
        }
        if (!near)
        {
            ammoFullShown = false;
        }
    }

    // ------------------------------------------------------------------ Sending

    private void Send(NetWriter message, TrafficClass traffic = TrafficClass.ReliableOrdered) => Net.Send(message, traffic);

    private void SendPlayerState(double now)
    {
        var message = new NetMessage(Msg.PlayerState, Setup.Id, 0, new PlayerStateMessage(now, player.CaptureNetState()));
        Net.Send(MessageCodec.Encode(message, fromServer: false), TrafficClass.UnreliablePlayerPoses);
    }

    private void SendBodies(float dt, double now)
    {
        var (moving, rested) = bodies.Collect(dt);
        for (var start = 0; start < moving.Count; start += StatesPerMessage)
        {
            var batch = moving.Skip(start).Take(StatesPerMessage).ToList();
            var w = new NetWriter(Msg.BodyStates, Setup.Id).Double(Net.BodySampleTime(now)).UShort((ushort)batch.Count);
            foreach (var entry in batch)
            {
                w.UInt(entry.Id).Transform(entry.Body.GlobalTransform).Vec3(entry.Body.LinearVelocity).Vec3(entry.Body.AngularVelocity)
                    .UInt(entry.Epoch).UInt(checked(++entry.Sequence));
            }
            Send(w, TrafficClass.UnreliableBodyStates);
        }
        foreach (var entry in rested)
        {
            Send(new NetWriter(Msg.BodyRest, Setup.Id).UInt(entry.Id).Transform(entry.Body.GlobalTransform).Double(now).UInt(entry.Epoch).UInt(checked(++entry.Sequence)));
        }
    }

    // ------------------------------------------------------------------ Receiving

    internal void OnMessage(NetMessage message)
    {
        if (Protocol.IsRelay(message.Type))
        {
            OnRelay(message.Sender, message.Payload);
            return;
        }
        var tuning = Tuning.Current;
        switch (message.Type)
        {
            case Msg.Authority:
                OnAuthority((AuthorityMessage)message.Payload);
                break;
            case Msg.Countdown:
                phase = Phase.Countdown;
                countdownLeft = ((FloatMessage)message.Payload).Value;
                ShowNotice("", 0);
                break;
            case Msg.MatchGo:
                phase = Phase.Playing;
                timeLeft = ((FloatMessage)message.Payload).Value;
                player.InputLocked = false;
                matchHud.ShowCountdown("GO");
                break;
            case Msg.Clock:
                if (phase == Phase.Playing) { timeLeft = ((FloatMessage)message.Payload).Value; }
                break;
            case Msg.Score:
                var update = (ScoreMessage)message.Payload;
                scores[update.Player] = update.Score;
                if (update.Player == me)
                {
                    matchHud.SetScore(update.Score, update.Delta > 0);
                    if (update.Delta > 0) { matchHud.AddScorePopup(update.Delta); }
                    else if (update.Delta < 0) { dropPredictions.ScoreConfirmed(update.Delta); }
                }
                break;
            case Msg.DropRefused:
                foreach (var id in dropPredictions.Refused(((DropRefusedMessage)message.Payload).Ids))
                {
                    if (!tanks.Remove(id, out var refused)) { continue; }
                    bodies.Remove(id);
                    refused.Punctured -= OnTankPunctured;
                    refused.Detonated -= OnTankDetonated;
                    refused.QueueFree();
                }
                break;
            case Msg.PickupTaken:
                var take = (PickupMessage)message.Payload;
                if (take.Index < pickups.Count) { pickups[take.Index].Take(); }
                if (take.Player == me && player.Ammo != null) { matchHud.AddAmmoPopup(player.Ammo.AddReserve(tuning.PickupAmount)); }
                break;
            case Msg.PickupRespawn:
                var index = ((IntMessage)message.Payload).Value;
                if (index < pickups.Count) { pickups[index].Reveal(tuning.PickupRevealTime); }
                break;
            case Msg.MatchEnd:
                var standings = (IReadOnlyList<Standing>)message.Payload;
                phase = Phase.Ended;
                foreach (var tank in LiveTanks) { tank.EndGather(); }
                timeLeft = 0;
                player.InputLocked = true;
                endLeft = tuning.ResultsTime;
                results.Show(standings, me, tuning.ResultsTime);
                break;
            case Msg.PlayerLeft:
                var left = (PlayerLeftMessage)message.Payload;
                if (remotes.Remove(left.Player, out var remote))
                {
                    remote.Body.QueueFree();
                    ShowNotice($"{remote.Profile.Name} left the match", 3f);
                }
                bodies.Host = left.Host;
                break;
        }
    }

    private void OnRelay(int sender, object payload)
    {
        var now = MonotonicClock.Now;
        var remote = remotes.GetValueOrDefault(sender);
        switch (payload)
        {
            case PlayerStateMessage state:
                remote?.Buffer.Add(state.Time, state.State, now);
                break;
            case MeleeStartMessage strike:
                remoteMelee[sender] = strike;
                break;
            case MeleeContactMessage contact:
                if (!remoteMelee.TryGetValue(sender, out var swing) || swing.Swing != contact.Swing) break;
                if (contact.Victim == me)
                {
                    LastHitTime = now;
                    LastHitBy = sender;
                    player.ReceiveMelee(swing.Direction);
                    if (remote != null) matchHud.AddHitDirection(remote.Body.GlobalPosition);
                }
                else if (contact.PropIndex >= 0 && looseProps.TryGetValue(contact.PropIndex, out var pushed) && IsInstanceValid(pushed) && !pushed.Freeze)
                {
                    pushed.ApplyCentralImpulse(swing.Direction * Tuning.Current.MeleePropImpulse);
                }

                break;
            case ShotMessage shot:
                remote?.Body.RemoteShot(shot.End);
                var impact = (Impact)shot.Impact;
                if (impact.HasFlag(Impact.Hit))
                {
                    ImpactEffect.Spawn(shot.End, shot.Normal, impact.HasFlag(Impact.Metal), impact.HasFlag(Impact.Decal));
                }
                if (shot.PropIndex >= 0 && looseProps.TryGetValue(shot.PropIndex, out var prop) && IsInstanceValid(prop) && !prop.Freeze)
                {
                    prop.ApplyImpulse(shot.Direction * Tuning.Current.BulletImpulse, shot.End - prop.GlobalPosition);
                }
                if (shot.Victim == me && remote != null)
                {
                    LastHitTime = now;
                    LastHitBy = sender;
                    OnHitByShot(remote.Body.GlobalPosition + Vector3.Up * 1.4f);
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
            case DropMessage drop:
                if (sender == me) { dropPredictions.Accepted(drop.Tanks.Select(t => t.Id)); }
                foreach (var body in drop.Tanks) { SpawnDroppedTank(body.Id, sender, drop.Color, body.Transform, body.Linear, body.Angular); }
                break;
            case RagdollMessage ragdoll:
                remote?.Body.RemoteRagdoll(ragdoll.Position, ragdoll.Velocity, ragdoll.Spin);
                break;
            case GetUpMessage getUp:
                remote?.Body.RemoteGetUp(getUp.Position, getUp.Yaw);
                break;
            case TuningMessage tune:
                Tuning.Current.Apply(new Godot.Collections.Dictionary { [tune.Name] = tune.Value });
                tuningPanel?.Refresh();
                if (remote != null) { ShowNotice($"{remote.Profile.Name} changed {tune.Name}", 2.5f); }
                break;
        }
    }
}
