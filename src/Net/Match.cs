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
/// send. See MULTIPLAYER.md for the rules and the networking model.
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
    private const int StatesPerMessage = 20;
    private const float TagRefresh = 1f / 30f;

    private sealed class Remote
    {
        public required PlayerProfile Profile;
        public required PlayerCharacter Body;
        public readonly SnapshotBuffer Buffer = new();
    }

    private readonly Dictionary<int, Remote> remotes = new();
    private readonly Dictionary<int, PlayerProfile> profiles = new();
    private readonly Dictionary<int, int> scores = new();
    private readonly Dictionary<uint, PropaneTank> tanks = new();
    private readonly HashSet<uint> exploded = new();
    private readonly Dictionary<int, RigidBody3D> looseProps = new();
    private readonly List<AmmoPickup> pickups = new();

    private VoidEnvironment environment = null!;
    private Node3D effects = null!;
    private Suburb suburb = null!;
    private SuburbPlan plan = null!;
    private PlayerCharacter player = null!;
    private BodySync bodies = null!;
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
    private int unconfirmedDrops;
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

    /// <summary>When (on <see cref="NetTransport.Now"/>) another player's shot last hit this player, and whose it was.</summary>
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
        player.GotUp += (root, yaw) => Send(new NetWriter(Msg.GetUp).Vec3(root).Float(yaw));
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
        if (OS.IsDebugBuild())
        {
            tuningPanel = new TuningPanel { Name = "Tuning", Note = "Changes apply to everyone in the match" };
            tuningPanel.ValueChanged += (name, value) => Send(new NetWriter(Msg.TuningSet).String(name).Bytes(GD.VarToBytes(value)));
            AddChild(tuningPanel);
        }

        // The suburb materializes around the player, as in single player.
        suburb.SetRevealSide(1f);
        RenderingServer.GlobalShaderParameterSet("reveal_center", player.GlobalPosition);
        RenderingServer.GlobalShaderParameterSet("reveal_radius", 0f);
        Warmup.Run(this, new Vector3(0, -400f, 0));

        Blast.LocalBlast += OnLocalBlast;
        Net.MatchMessage += OnMessage;
        CaptureMouse(true);
        ShowNotice("Waiting for everyone to arrive", 30f);
        Send(new NetWriter(Msg.Loaded));
        GD.Print($"[match] seed {plan.Seed}: {plan.Lots.Count} lots, {plan.Tanks.Count} tanks, {pickups.Count} pickups, {Setup.Players.Count} players, host {Setup.HostId}, me {me}");
    }

    public override void _ExitTree()
    {
        Blast.LocalBlast -= OnLocalBlast;
        Net.MatchMessage -= OnMessage;
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
        var now = NetTransport.Now;
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
    private void Track(PropaneTank tank, int owner)
    {
        tanks[tank.NetId] = tank;
        bodies.Register(tank.NetId, tank, isTank: true, owner);
        tank.Punctured += OnTankPunctured;
        tank.Detonated += OnTankDetonated;
    }

    private void OnTankPunctured(PropaneTank tank)
    {
        // This player punctured it, so it is theirs: everyone starts the same vent from this exact state.
        Claim(new[] { tank }, force: true);
        Send(new NetWriter(Msg.Puncture).UInt(tank.NetId).Transform(tank.GlobalTransform).Vec3(tank.LinearVelocity).Vec3(tank.AngularVelocity)
            .Vec3(tank.HoleLocal).Vec3(tank.HoleNormalLocal).Float(tank.SwirlSign));
    }

    private void OnTankDetonated(PropaneTank tank)
    {
        tanks.Remove(tank.NetId);
        bodies.Remove(tank.NetId);
        exploded.Add(tank.NetId);
        if (!tank.DetonatedRemotely)
        {
            // This player set it off (a shot, or a chain from one): the point is claimed with the explosion.
            Send(new NetWriter(Msg.Explode).UInt(tank.NetId).Vec3(tank.GlobalTransform * PropaneTank.LocalCenter));
        }
    }

    private void OnLocalBlast(Vector3 center, IReadOnlyList<RigidBody3D> pushed) => Claim(pushed);

    /// <summary>Takes charge of bodies this player disturbed, telling the others.</summary>
    private void Claim(IEnumerable<GodotObject> disturbed, bool force = false)
    {
        var ids = bodies.Claim(disturbed);
        if (force)
        {
            // A puncture always restates the claim, even on a body already owned, so its order is clear.
            foreach (var body in disturbed)
            {
                if (bodies.Find(body) is { } entry && !ids.Contains(entry.Id))
                {
                    entry.ClaimPending = true;
                    ids.Add(entry.Id);
                }
            }
        }
        if (ids.Count == 0)
        {
            return;
        }
        var w = new NetWriter(Msg.Claim).UShort((ushort)ids.Count);
        foreach (var id in ids)
        {
            w.UInt(id);
        }
        Send(w);
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
        Send(new NetWriter(Msg.Shot).Vec3(shot.End).Vec3(shot.Normal).Vec3(shot.Direction).Byte((byte)impact).Int(victim).Int(propIndex));
    }

    private void OnRagdolled(Vector3 at, Vector3 velocity, Vector3 spin)
    {
        Send(new NetWriter(Msg.Ragdoll).Vec3(at).Vec3(velocity).Vec3(spin));
        // Thrown: the bank spills where the player stood, whoever's blast it was.
        DropTanks(Tuning.Current.RagdollDropCount, DropReason.Ragdoll, at + Vector3.Up * 0.35f, Vector3.Zero);
    }

    /// <summary>Another player's shot hit this player.</summary>
    private void OnHitByShot(Vector3 shooterPosition)
    {
        matchHud.AddHitDirection(shooterPosition);
        var now = NetTransport.Now;
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
        var count = Mathf.Min(wanted, Mathf.Max(0, MyScore - unconfirmedDrops));
        if (count <= 0)
        {
            return;
        }
        unconfirmedDrops += count;
        var tuning = Tuning.Current;
        var color = profiles[me].Color;
        var rng = new RandomNumberGenerator();
        var w = new NetWriter(Msg.Drop).Byte((byte)reason).Int(count).Int(color);
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
            SpawnDroppedTank(id, me, color, transform, velocity, spin);
            w.UInt(id).Transform(transform).Vec3(velocity).Vec3(spin);
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
            Net.Send(new NetWriter(Msg.ClaimPickup).Int(pickup.Index));
        }
        if (!near)
        {
            ammoFullShown = false;
        }
    }

    // ------------------------------------------------------------------ Sending

    private void Send(NetWriter message, bool reliable = true) => Net.Send(message, reliable);

    private void SendPlayerState(double now)
    {
        var s = player.CaptureNetState();
        var flags = (byte)((s.Grounded ? 1 : 0) | ((byte)s.State << 1));
        Send(new NetWriter(Msg.PlayerState).Double(now).Vec3(s.Position).Vec3(s.Velocity).Float(s.Yaw).Vec3(s.AimTarget)
            .Byte(ToByte(s.Aim)).Byte(ToByte(s.Sprint)).Byte(ToByte(s.Ready)).Byte(ToByte(s.Reload)).Byte(flags)
            .Vec3(s.PelvisPosition).Vec3(s.PelvisVelocity).Byte(s.Staggers), reliable: false);
    }

    private void SendBodies(float dt, double now)
    {
        var (moving, rested) = bodies.Collect(dt);
        for (var start = 0; start < moving.Count; start += StatesPerMessage)
        {
            var batch = moving.Skip(start).Take(StatesPerMessage).ToList();
            var w = new NetWriter(Msg.BodyStates).Double(now).UShort((ushort)batch.Count);
            foreach (var entry in batch)
            {
                w.UInt(entry.Id).Transform(entry.Body.GlobalTransform).Vec3(entry.Body.LinearVelocity).Vec3(entry.Body.AngularVelocity);
            }
            Send(w, reliable: false);
        }
        foreach (var entry in rested)
        {
            Send(new NetWriter(Msg.BodyRest).UInt(entry.Id).Transform(entry.Body.GlobalTransform));
        }
    }

    private static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

    private static float FromByte(byte b) => b / 255f;

    // ------------------------------------------------------------------ Receiving

    private void OnMessage(Msg type, NetReader r)
    {
        if (Protocol.IsRelay(type))
        {
            OnRelay(type, r.Int(), r);
            return;
        }
        var tuning = Tuning.Current;
        switch (type)
        {
            case Msg.Countdown:
                phase = Phase.Countdown;
                countdownLeft = r.Float();
                ShowNotice("", 0);
                break;
            case Msg.MatchGo:
                phase = Phase.Playing;
                timeLeft = r.Float();
                player.InputLocked = false;
                matchHud.ShowCountdown("GO");
                break;
            case Msg.Clock:
                if (phase == Phase.Playing)
                {
                    timeLeft = r.Float();
                }
                break;
            case Msg.Score:
            {
                var id = r.Int();
                var score = r.Int();
                var delta = r.Int();
                scores[id] = score;
                if (id == me)
                {
                    matchHud.SetScore(score, delta > 0);
                    if (delta > 0)
                    {
                        matchHud.AddScorePopup(delta);
                    }
                    else if (delta < 0)
                    {
                        unconfirmedDrops = Mathf.Max(0, unconfirmedDrops + delta);
                    }
                }
                break;
            }
            case Msg.PickupTaken:
            {
                var index = r.Int();
                var by = r.Int();
                if (index >= 0 && index < pickups.Count)
                {
                    pickups[index].Take();
                }
                if (by == me && player.Ammo != null)
                {
                    matchHud.AddAmmoPopup(player.Ammo.AddReserve(tuning.PickupAmount));
                }
                break;
            }
            case Msg.PickupRespawn:
            {
                var index = r.Int();
                if (index >= 0 && index < pickups.Count)
                {
                    pickups[index].Reveal(tuning.PickupRevealTime);
                }
                break;
            }
            case Msg.MatchEnd:
            {
                var standings = new List<Standing>();
                for (int i = 0, n = r.Int(); i < n; i++)
                {
                    standings.Add(new Standing(r.Int(), r.String(), r.Int(), r.Int(), r.Bool()));
                }
                phase = Phase.Ended;
                timeLeft = 0;
                player.InputLocked = true;
                endLeft = tuning.ResultsTime;
                results.Show(standings, me, tuning.ResultsTime);
                break;
            }
            case Msg.PlayerLeft:
            {
                var id = r.Int();
                var host = r.Int();
                if (remotes.Remove(id, out var remote))
                {
                    remote.Body.QueueFree();
                    ShowNotice($"{remote.Profile.Name} left the match", 3f);
                }
                bodies.OwnerLeft(id, host);
                break;
            }
        }
    }

    private void OnRelay(Msg type, int sender, NetReader r)
    {
        var now = NetTransport.Now;
        var remote = remotes.GetValueOrDefault(sender);
        switch (type)
        {
            case Msg.PlayerState:
            {
                var time = r.Double();
                var s = new PlayerNetState
                {
                    Position = r.Vec3(),
                    Velocity = r.Vec3(),
                    Yaw = r.Float(),
                    AimTarget = r.Vec3(),
                    Aim = FromByte(r.Byte()),
                    Sprint = FromByte(r.Byte()),
                    Ready = FromByte(r.Byte()),
                    Reload = FromByte(r.Byte()),
                };
                var flags = r.Byte();
                s.Grounded = (flags & 1) != 0;
                s.State = (PlayerNetState.Mode)((flags >> 1) & 3);
                s.PelvisPosition = r.Vec3();
                s.PelvisVelocity = r.Vec3();
                s.Staggers = r.Byte();
                remote?.Buffer.Add(time, s, now);
                break;
            }
            case Msg.Shot:
            {
                var end = r.Vec3();
                var normal = r.Vec3();
                var direction = r.Vec3();
                var impact = (Impact)r.Byte();
                var victim = r.Int();
                var propIndex = r.Int();
                remote?.Body.RemoteShot(end);
                if (impact.HasFlag(Impact.Hit))
                {
                    ImpactEffect.Spawn(end, normal, impact.HasFlag(Impact.Metal), impact.HasFlag(Impact.Decal));
                }
                if (propIndex >= 0 && looseProps.TryGetValue(propIndex, out var prop) && IsInstanceValid(prop) && !prop.Freeze)
                {
                    prop.ApplyImpulse(direction * Tuning.Current.BulletImpulse, end - prop.GlobalPosition);
                }
                if (victim == me && remote != null)
                {
                    LastHitTime = NetTransport.Now;
                    LastHitBy = sender;
                    OnHitByShot(remote.Body.GlobalPosition + Vector3.Up * 1.4f);
                }
                break;
            }
            case Msg.Puncture:
            {
                var id = r.UInt();
                var transform = r.Transform();
                var linear = r.Vec3();
                var angular = r.Vec3();
                var hole = r.Vec3();
                var holeNormal = r.Vec3();
                var swirl = r.Float();
                if (tanks.TryGetValue(id, out var tank) && IsInstanceValid(tank) && tank.State == PropaneTank.TankState.Intact)
                {
                    BodySync.Snap(tank, transform, linear, angular);
                    tank.PunctureRemote(hole, holeNormal, swirl);
                }
                break;
            }
            case Msg.Explode:
            {
                var id = r.UInt();
                var center = r.Vec3();
                if (exploded.Contains(id))
                {
                    break;
                }
                if (tanks.TryGetValue(id, out var tank) && IsInstanceValid(tank))
                {
                    tank.DetonateRemote(center);
                }
                else
                {
                    // A tank this game never had (or lost track of): the explosion still happens here.
                    exploded.Add(id);
                    var ground = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(center, center + Vector3.Down * 30f, Layers.World | Layers.Floor));
                    ExplosionEffect.Create(center, ground.Count > 0 ? ground["position"].AsVector3().Y : 0f);
                    Blast.Detonate(this, center, null, chain: false);
                }
                break;
            }
            case Msg.Claim:
            {
                var ids = new List<uint>();
                for (int i = 0, n = r.UShort(); i < n; i++)
                {
                    ids.Add(r.UInt());
                }
                bodies.OnClaim(sender, ids);
                break;
            }
            case Msg.BodyStates:
            {
                var time = r.Double();
                for (int i = 0, n = r.UShort(); i < n; i++)
                {
                    bodies.OnState(sender, time, r.UInt(), r.Transform(), r.Vec3(), r.Vec3(), now);
                }
                break;
            }
            case Msg.BodyRest:
                bodies.OnRest(sender, r.UInt(), r.Transform(), now);
                break;
            case Msg.Drop:
            {
                r.Byte();
                var count = r.Int();
                var color = r.Int();
                for (var i = 0; i < count; i++)
                {
                    SpawnDroppedTank(r.UInt(), sender, color, r.Transform(), r.Vec3(), r.Vec3());
                }
                break;
            }
            case Msg.Ragdoll:
                remote?.Body.RemoteRagdoll(r.Vec3(), r.Vec3(), r.Vec3());
                break;
            case Msg.GetUp:
                remote?.Body.RemoteGetUp(r.Vec3(), r.Float());
                break;
            case Msg.TuningSet:
            {
                var name = r.String();
                var value = GD.BytesToVar(r.Bytes());
                Tuning.Current.Apply(new Godot.Collections.Dictionary { [name] = value });
                tuningPanel?.Refresh();
                if (remote != null)
                {
                    ShowNotice($"{remote.Profile.Name} changed {name}", 2.5f);
                }
                break;
            }
        }
    }
}
