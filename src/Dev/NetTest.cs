using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev;

/// <summary>
/// A multiplayer client driven by a bot, for testing matches without people. It connects, creates a lobby
/// (<c>--role=host</c>, starting once <c>--players=N</c> have joined) or joins the first one (<c>--role=join</c>),
/// then plays with <see cref="Bot"/>. It logs
/// scores and, every few seconds, where it has every tank, so runs on several machines can be compared.
/// Run: godot res://scenes/dev/net_test.tscn -- --server=127.0.0.1 --role=host --players=2 [--capture-dir=/tmp/n]
/// Add <c>--host-server</c> to run the server in the same process.
/// </summary>
public partial class NetTest : Node
{
    private IBotBrain bot = null!;
    private NetClient net = null!;
    private Match? match;
    private CaptureDirector director = null!;
    private string role = "host";
    private int players = 2;
    private bool started;
    private float clock;
    private float digestTimer;
    private float trajectoryTimer;
    private float shotTimer;
    private int matches;
    private int matchesWanted = 1;

    public override void _Ready()
    {
        DevArgs.Setup();
        // Several test windows share one screen; a window the compositor is not showing would otherwise block on vsync.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 60;
        DevLog.Enabled = DevArgs.Get("verbose-log") != null;
        role = DevArgs.Get("role") ?? "host";
        // --brain=simple plays the first, easy bot; the default is the stronger one.
        bot = DevArgs.Get("brain") == "simple" ? new SimpleBot() : new Bot { Skill = DevArgs.GetFloat("bot-skill", 1f) };
        players = (int)DevArgs.GetFloat("players", 2);
        matchesWanted = (int)DevArgs.GetFloat("matches", 1);
        var address = DevArgs.Get("server") ?? "127.0.0.1";
        var port = (int)DevArgs.GetFloat("port", Protocol.DefaultPort);
        if (DevArgs.Get("host-server") != null)
        {
            var server = new LobbyServer { Name = "Server", Port = port };
            AddChild(server);
            server.Start();
        }
        InputSetup.Register();
        net = new NetClient { Name = "Net" };
        AddChild(net);
        director = new CaptureDirector { Name = "Director", QuitAfter = DevArgs.GetFloat("quit", 400f) };
        AddChild(director);
        net.StatusChanged += (status, reason) =>
        {
            Log($"status {status} {reason}" + (net.RejectedBy != null ? $" (server {net.RejectedBy})" : ""));
            if (status == NetClient.Status.Connected && role == "host")
            {
                net.CreateLobby();
            }
            if (status == NetClient.Status.Offline && reason != null)
            {
                GetTree().Quit(2);
            }
        };
        net.LobbiesChanged += () =>
        {
            if (role == "join" && net.Lobby == null && net.Lobbies.FirstOrDefault(l => l.Phase == LobbyServer.LobbyPhase.Waiting) is { } lobby)
            {
                net.JoinLobby(lobby.Id);
            }
        };
        net.LobbyChanged += () =>
        {
            if (net.Lobby is { } lobby)
            {
                Log($"lobby {lobby.Id}: {string.Join(", ", lobby.Members.Select(m => $"{m.Name}/{Protocol.ColorNames[m.Color]}"))}, {lobby.Phase}");
                TryStartMatch();
            }
        };
        net.NoticeReceived += text => Log($"notice: {text}");
        net.MatchStarting += async setup =>
        {
            var delay = DevArgs.GetFloat("match-delay", 0);
            if (delay > 0)
            {
                Log($"delaying match {setup.Id} construction by {delay} s");
                await ToSignal(GetTree().CreateTimer(delay), SceneTreeTimer.SignalName.Timeout);
                if (net.ActiveMatchId != setup.Id || net.State != NetClient.Status.Connected) { return; }
            }
            if (match != null)
            {
                RemoveChild(match);
                match.QueueFree();
            }
            match = new Match { Name = "Match", Net = net, Setup = setup };
            match.Finished += () =>
            {
                if (DevArgs.Get("scenario") is "duel" or "pickup" or "drop-refusal" or "puncture-race" or "gather" or "melee")
                {
                    ScenarioAssert(scenarioPassed, "scenario ended before all assertions passed");
                }
                if (DevArgs.Get("scenario") == "movement" && role == "host" && !movementProved)
                {
                    GD.PushError("[net movement] FAIL match ended before movement proof completed");
                    GetTree().Quit(1);
                    return;
                }
                if (bot is Bot smart)
                {
                    Log($"navigation finished: stuck={smart.StuckEvents} blacklist={smart.BlacklistedTargets} replans={smart.Replans} jumps={smart.Jumps}");
                }
                Log($"finished match {match.Setup.Id}; final scores {ScoreLine()}");
                RemoveChild(match);
                match.QueueFree();
                match = null;
                matches++;
                started = matches >= matchesWanted;
                // Lobby Waiting may have arrived before this client's results timer finished.
                TryStartMatch();
                if (matches >= matchesWanted)
                {
                    GetTree().CreateTimer(1.0).Timeout += () => GetTree().Quit();
                }
            };
            raceTick = raceSamples = 0;
            raceVent = null;
            raceCamera = null;
            raceWorst = 0;
            match.AuthorityResolved += result =>
            {
                if (DevArgs.Get("scenario") != "puncture-race") { return; }
                var canonical = result.Bodies.FirstOrDefault(b => b.Body.Id == BodySync.MapTankId(0));
                if (canonical?.State != TankLedger.State.Venting) { return; }
                if (raceVent != null) { ScenarioAssert(raceVent == canonical.Vent, "race: canonical vent changed during reconciliation"); }
                raceVent = canonical.Vent;
            };
            AddChild(match);
            match.Player.InputOverride = bot;
            if (DevArgs.Get("melee-start") != null)
            {
                // Stage before countdown so snapshots settle before the ordinary brains choose any targets.
                var peers = setup.Players.OrderBy(p => p.Player.Id).ToList();
                var slot = peers.FindIndex(p => p.Player.Id == match.Player.PeerId);
                var anchorId = peers[slot / 2 * 2].Player.Id;
                var anchor = match.Player.PeerId == anchorId ? match.Player : match.RemoteBodies.First(p => p.PeerId == anchorId);
                var yaw = anchor.FacingYaw;
                var forward = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
                // Use a normal clear spawn so navigation still has useful routes after the initial knockdowns.
                match.Player.Teleport(anchor.GlobalPosition + (slot % 2 == 0 ? Vector3.Zero : forward * 1.8f),
                    yaw + (slot % 2 == 0 ? 0 : Mathf.Pi));
            }
            match.Player.MeleeStarted += (swing, _) => Log($"melee swing {swing}");
            match.Player.MeleeHit += hit => Log($"melee contact {hit.Swing} victim {PlayerCharacter.Owning(hit.Collider)?.PeerId ?? 0}");
            clock = 0;
            duelTime = -1;
            gatherPositions.Clear();
            meleeThrows = meleeGetups = 0;
            meleeFirstDown = meleeSecondDown = false;
            meleeCaptured.Clear();
            duelInput = new ScriptedInput();
            duelTank = null;
            duelBlastAt = duelThrowAt = -1;
            pickupTakenAt = -1;
            pickupMovedAway = false;
            scenarioPassed = scenarioFailed = false;
            // Movement proof is per match, just like the duel/pickup assertions on a reused connection.
            movementCamera = null;
            remoteFeet = null;
            remoteGrip = null;
            remoteMotion = null;
            sawRemoteReload = false;
            sawRemoteCreep = sawRemoteSprint = sawRemoteAir = sawRemoteRestoredAim = movementProved = false;
            capturedCreep = capturedSprint = capturedLanding = capturedRelease = false;
            remoteGait = PlayerGait.Default;
            remoteGaitTime = 0;
            Log($"match {setup.Id} ready with {setup.Players.Count} players; playing {match.IsPlaying}, over {match.IsOver}");
        };
        var name = DevArgs.Get("name") ?? (role == "host" ? "HostBot" : $"Bot{OS.GetProcessId() % 1000}");
        var color = (int)DevArgs.GetFloat("color", role == "host" ? 0 : 1);
        // Give a hosted server a moment to open its port.
        GetTree().CreateTimer(DevArgs.Get("host-server") != null ? 0.3 : 0.05).Timeout += () => net.Connect(address, port, name, color);
    }

    private void TryStartMatch()
    {
        if (role == "host" && net.IsCreator && !started && matches < matchesWanted &&
            net.Lobby is { Phase: LobbyServer.LobbyPhase.Waiting } lobby && lobby.Members.Count >= players)
        {
            started = true;
            net.SetMatchLength(DevArgs.GetFloat("length", 60));
            GetTree().CreateTimer(0.5).Timeout += net.StartMatch;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (match == null || !IsInstanceValid(match))
        {
            return;
        }
        var dt = (float)delta;
        clock += dt;
        // --send-tune=Name=value: the host changes a value as the debug panel would, a few seconds in.
        if (role == "host" && DevArgs.Get("send-tune") is { } tune && clock >= 3f && clock - dt < 3f)
        {
            var split = tune.IndexOf('=');
            var name = tune[..split];
            var value = float.Parse(tune[(split + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            Tuning.Current.Set(name, value);
            net.Send(new NetWriter(Msg.TuningSet, match.Setup.Id).String(name).Scalar(value));
            Log($"sent tuning {name} = {value}");
        }
        var leaveAt = DevArgs.GetFloat("leave-at", -1);
        if (leaveAt > 0 && clock >= leaveAt)
        {
            Log("leaving the match on purpose");
            GetTree().Quit();
            return;
        }
        if (DevArgs.Get("scenario") is "duel" or "drop-refusal" or "gather")
        {
            Duel(match, dt);
        }
        else if (DevArgs.Get("scenario") == "melee")
        {
            MeleeScenario(match, dt);
        }
        else if (DevArgs.Get("scenario") == "puncture-race")
        {
            PunctureRace(match);
        }
        else if (DevArgs.Get("scenario") == "movement")
        {
            MovementScenario(match, dt);
        }
        else if (DevArgs.Get("scenario") == "pickup")
        {
            PickupScenario(match, dt);
        }
        else if (DevArgs.Get("scenario") == "watch" && role == "host")
        {
            Watch(match, dt);
        }
        else
        {
            bot.Think(match, dt);
            if (DevArgs.Get("bot-log") != null && Mathf.PosMod(clock, 2f) < dt)
            {
                Log($"bot: {bot.Status}" + (bot is Bot smart ? $"; stuck={smart.StuckEvents} blacklist={smart.BlacklistedTargets} replans={smart.Replans} jumps={smart.Jumps}" : ""));
            }
        }

        digestTimer -= dt;
        trajectoryTimer -= dt;
        if (DevArgs.Get("tank-trajectories") != null && trajectoryTimer <= 0)
        {
            trajectoryTimer = 0.05f;
            LogTankTrajectory();
        }
        if (digestTimer <= 0)
        {
            digestTimer = DevArgs.GetFloat("digest-every", 5f);
            Digest();
        }
        shotTimer -= dt;
        var every = DevArgs.GetFloat("shot-every", 0f);
        if (every > 0 && shotTimer <= 0)
        {
            shotTimer = every;
            director.Screenshot($"{net.PlayerName}_{clock:000.0}");
        }
    }

    private int raceTick;
    private int raceSamples;
    private float raceWorst;
    private VentParameters? raceVent;
    private Camera3D? raceCamera;

    /// <summary>
    /// Injects two opposed bullet contacts on the same simulation tick, while both copies are intact. This isolates
    /// the authority race from rifle spread/aim. A third client, when present, observes without shooting.
    /// </summary>
    private void PunctureRace(Match match)
    {
        if (!match.IsPlaying) { return; }
        raceTick++;
        var id = BodySync.MapTankId(0);
        var tank = match.LiveTanks.SingleOrDefault(t => t.NetId == id);
        ScenarioAssert(tank != null, "race: test tank disappeared");
        if (tank == null) { return; }
        if (raceTick == 1)
        {
            match.Player.InputOverride = new ScriptedInput();
            var road = match.Suburb.Plan.Roads.OrderByDescending(r => r.Length).First();
            var spot = World.Suburb.ToWorld((road.A + road.B) / 2, 8);
            BodySync.Snap(tank, new Transform3D(Basis.Identity, spot), Vector3.Zero, Vector3.Zero);
            match.Player.Teleport(spot + new Vector3(20, -7.9f, 0), 0);
            raceCamera = new Camera3D { Current = true, Fov = 55 };
            match.AddChild(raceCamera);
        }
        if (raceTick == 180 && net.Color < 2)
        {
            ScenarioAssert(tank.State == PropaneTank.TankState.Intact && match.Bodies.Get(id)!.Epoch == 1, "race: contender received a puncture before its simultaneous contact");
            var normal = net.Color == 0 ? Vector3.Right : Vector3.Left;
            var hole = PropaneTank.LocalCenter + normal * 0.15f;
            tank.TakeShot(tank.ToGlobal(hole), tank.GlobalBasis * normal, -(tank.GlobalBasis * normal));
            Log($"race contact tick {raceTick} hole {Vector(tank.HoleLocal)} swirl {tank.SwirlSign}");
        }
        if (raceCamera != null)
        {
            raceCamera.GlobalPosition = tank.GlobalPosition + new Vector3(2, 1.4f, 3);
            raceCamera.LookAt(tank.GlobalTransform * PropaneTank.LocalCenter, Vector3.Up);
        }
        if (raceTick is 210 or 300 or 600 or 1200) { director.Screenshot($"{net.PlayerName}_race_{raceTick}"); }
        // At 120 physics ticks/s, allow lagged reliable delivery before checking convergence.
        if (raceTick < 720) { return; }
        ScenarioAssert(raceVent != null && tank.State == PropaneTank.TankState.Venting, "race: no canonical puncture after the race");
        if (raceVent == null) { return; }
        ScenarioAssert(tank.HoleLocal == raceVent.Hole && tank.HoleNormalLocal == raceVent.Normal && tank.SwirlSign == raceVent.Swirl,
            "race: contender retained different canonical vent parameters");
        var lean = raceVent.Normal.Rotated(Vector3.Up, Mathf.DegToRad(Tuning.Current.VentSwirlDegrees) * raceVent.Swirl).Normalized();
        ScenarioAssert(tank.JetDirectionLocal.IsEqualApprox(lean), "race: jet lean differs from canonical vent");
        var entry = match.Bodies.Get(id)!;
        if (entry.Owner != net.MyId && entry.HasTarget)
        {
            var lead = (float)Math.Clamp(MonotonicClock.Now - entry.TargetReceived, 0, 0.3);
            var drift = tank.GlobalPosition.DistanceTo(entry.Target.Origin + entry.TargetLinear * lead);
            raceWorst = Mathf.Max(raceWorst, drift);
            ScenarioAssert(drift <= Tuning.Current.SnapDistance + 0.5f, $"race: trajectory escaped owner corrections ({drift:0.000} m)");
        }
        if (raceTick % 6 == 0)
        {
            raceSamples++;
            Log($"race path time {MonotonicClock.Now + net.ServerClockOffset:0.000} pos {Vector(tank.GlobalPosition)}");
        }
        if (raceTick == 720)
        {
            Log($"race params owner {entry.Owner} epoch {entry.Epoch} hole {Vector(tank.HoleLocal)} normal {Vector(tank.HoleNormalLocal)} lean {Vector(tank.JetDirectionLocal)} swirl {tank.SwirlSign}");
        }
        if (raceTick == 1320)
        {
            ScenarioAssert(raceSamples >= 100 && !entry.ClaimPending, "race: insufficient trajectory samples or unresolved claim");
            scenarioPassed = !scenarioFailed;
            Log($"puncture-race: assertions passed, {raceSamples} path samples, max correction error {raceWorst:0.000} m");
        }
    }

    private static string Vector(Vector3 v) => FormattableString.Invariant($"{v.X:R},{v.Y:R},{v.Z:R}");

    private readonly Dictionary<uint, Vector3> gatherPositions = new();
    private float duelTime = -1;
    private ScriptedInput duelInput = new();
    private uint? duelTank;
    private float duelShotLeft;
    private float duelBlastAt = -1;
    private float duelThrowAt = -1;
    private bool scenarioPassed;
    private bool scenarioFailed;
    private int duelInitialScore;
    private float pickupTakenAt = -1;
    private bool pickupMovedAway;

    /// <summary>
    /// A scripted close-up of the tank bank: the host shoots the other player (dropping tanks behind them, with the
    /// grace period between hits), then shoots the dropped tanks, whose blast throws the victim, spilling more.
    /// Both take screenshots. Run with two bots, one windowed or both, and the server's --start-score.
    /// </summary>
    private void Duel(Match match, float dt)
    {
        var player = match.Player;
        if (!match.IsPlaying)
        {
            return;
        }
        if (duelTime < 0)
        {
            duelTime = 0;
            player.InputOverride = duelInput;
            player.Ragdolled += (_, _, _) => duelThrowAt = duelTime;
            if (role == "host")
            {
                // Shouldered, for a tight spread.
                duelInput.Hold(0, 999, new PlayerIntent { Aim = true });
            }
        }
        duelTime += dt;
        duelInput.Advance(dt);
        var road = match.Suburb.Plan.Roads.OrderByDescending(r => r.Length).First();
        var mid = (road.A + road.B) / 2f;
        var shooterSpot = World.Suburb.ToWorld(mid - road.Direction * 7f, 0.05f);
        var victimSpot = World.Suburb.ToWorld(mid + road.Direction * 7f, 0.05f);
        var host = role == "host";
        var refusal = DevArgs.Get("scenario") == "drop-refusal";
        var gathering = DevArgs.Get("scenario") == "gather";
        if (gathering && Once(3.7f, dt))
        {
            // Leave the spilled tanks alone; the creator must acquire the dropper's entire group.
            player.Teleport((host ? shooterSpot : victimSpot) + Vector3.Right * 8, 0);
        }
        if (gathering && Once(35, dt))
        {
            var drops = match.LiveTanks.Where(IsDrop).ToArray();
            ScenarioAssert(drops.Length == 2 && drops.All(t => !t.Loose && !t.Gathering && t.State == PropaneTank.TankState.Intact), "gather: dropped group never became an intact pile");
            foreach (var tank in drops)
            {
                gatherPositions[tank.NetId] = tank.GlobalPosition;
                ScenarioAssert(match.Bodies.Get(tank.NetId)?.Owner == match.Bodies.Host, "gather: dropper retained ownership");
                Log($"gather final {tank.NetId:x} {tank.GlobalPosition}");
            }
        }
        if (gathering && Once(40, dt))
        {
            ScenarioAssert(gatherPositions.Count == 2 && match.LiveTanks.Where(IsDrop).All(t =>
                gatherPositions.TryGetValue(t.NetId, out var position) && position.DistanceTo(t.GlobalPosition) <= 0.006f && t.AtRest), "gather: final pile unstable");
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed) { Log("gather: assertions passed; two drops acquired, settled and stable on this client"); }
            director.Screenshot($"gather_{role}_settled");
        }
        if (gathering && host && duelTime is > 18 and < 32 && Mathf.PosMod(duelTime, 1) < dt)
        {
            var drops = match.LiveTanks.Where(IsDrop).ToArray();
            if (drops.Length > 0) { AimAt(player, drops[0].GlobalTransform * PropaneTank.LocalCenter); }
            director.Screenshot($"gather_{role}_{duelTime:00}");
        }
        if (Once(0.8f, dt)) { duelInitialScore = match.MyScore; }
        if (refusal && host && Once(2.55f, dt))
        {
            // The second victim hit is already in flight. With 120 ms lag, this creator change reaches the server
            // before the victim's old-count Drop, while reliable order delivers Shot before TuningSet to the victim.
            Tuning.Current.HitDropCount = 0;
            net.Send(new NetWriter(Msg.TuningSet, match.Setup.Id).String(nameof(Tuning.HitDropCount)).Scalar(0));
        }
        if (Once(3.5f, dt))
        {
            var victimId = host ? match.RemoteBodies.First().PeerId : net.MyId;
            ScenarioAssert(match.Scores[victimId] == (refusal ? 7 : 6) && match.LiveTanks.Count(IsDrop) == (refusal ? 1 : 2),
                refusal ? "drop-refusal: rejected prediction changed the bank or left a phantom tank" : "duel: three hits must spill exactly two tanks (one hit inside grace)");
            if (refusal)
            {
                scenarioPassed = !scenarioFailed;
                director.Screenshot($"{(host ? "shooter" : "victim")}_drop_refused");
                if (scenarioPassed) { Log("drop-refusal: assertions passed"); }
            }
        }
        if (!refusal && !gathering && Once(14f, dt))
        {
            ScenarioAssert(host ? duelBlastAt >= 0 && match.MyScore > duelInitialScore : duelThrowAt >= 0 && match.MyScore <= 1,
                host ? "duel: dropped tank never detonated and scored" : "duel: blast never threw the victim and spilled five tanks");
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed) { Log("duel: assertions passed"); }
        }
        var eventAt = host ? duelBlastAt : duelThrowAt;
        if (eventAt >= 0)
        {
            foreach (var offset in new[] { 0.15f, 0.8f, 1.8f })
            {
                if (Once(eventAt + offset, dt)) { director.Screenshot($"{(host ? "shooter_blast" : "victim_throw")}_{offset:0.00}"); }
            }
        }
        if (Once(0.2f, dt))
        {
            var mine = host ? shooterSpot : victimSpot;
            var other = host ? victimSpot : shooterSpot;
            var to = other - mine;
            var yaw = Mathf.Atan2(-to.X, -to.Z);
            player.Teleport(mine, yaw);
            player.CameraRig.SetOrientation(Mathf.RadToDeg(yaw) + (host ? 0 : 160), -6f);
        }
        if (!host)
        {
            foreach (var t in new[] { 2.5f, 4f, 5.2f, 6.5f, 8f })
            {
                if (Once(t, dt))
                {
                    director.Screenshot($"victim_{t:00.0}");
                }
            }
            return;
        }
        var victim = match.RemoteBodies.FirstOrDefault();
        if (victim == null)
        {
            return;
        }
        // Hits at 1.5 and 1.7 (inside the grace period: only one drop), then 2.5 (another drop).
        foreach (var t in new[] { 1.5f, 1.7f, 2.5f })
        {
            if (Once(t - 0.3f, dt))
            {
                AimAt(player, victim.GlobalPosition + Vector3.Up * 1.2f);
            }
            if (Once(t, dt))
            {
                duelInput.Fire(duelInput.Now + 0.001f);
                Log($"duel: shot at victim at {t}");
            }
        }
        if (Once(2.0f, dt) || Once(3.2f, dt))
        {
            director.Screenshot($"shooter_{duelTime:00.0}");
        }
        if (refusal || gathering) { return; }
        // Drops keep moving under lag. Follow one tank every frame, retrying until its detonation is observed;
        // a fixed aim/fire gap used to miss the first shot and puncture a different tank with the second.
        if (duelTime >= 4f && duelBlastAt < 0)
        {
            if (duelTank == null)
            {
                var dropped = match.LiveTanks.Where(IsDrop).Where(k => !k.Invulnerable)
                    .OrderBy(k => k.GlobalPosition.DistanceTo(player.GlobalPosition)).FirstOrDefault();
                if (dropped != null)
                {
                    duelTank = dropped.NetId;
                    duelShotLeft = 0.3f; // Let the rifle align before the first shot.
                    dropped.Detonated += _ => duelBlastAt = duelTime;
                    Log($"duel: tracking dropped tank {dropped.NetId:x}");
                }
            }
            var target = match.LiveTanks.FirstOrDefault(k => k.NetId == duelTank);
            if (target != null)
            {
                AimAt(player, target.GlobalTransform * PropaneTank.LocalCenter);
                duelShotLeft -= dt;
                if (duelShotLeft <= 0 && !target.Invulnerable)
                {
                    duelShotLeft = 0.4f;
                    duelInput.Fire(duelInput.Now + 0.001f);
                    Log($"duel: shot at tracked tank {target.NetId:x} {target.State}");
                }
            }
        }
        foreach (var t in new[] { 4.3f, 4.75f, 5.2f, 6.5f })
        {
            if (Once(t, dt))
            {
                director.Screenshot($"shooter_{t:00.0}");
            }
        }
    }

    /// <summary>Walks into the nearest ammo pickup and films it vanishing and building back up.</summary>
    private void PickupScenario(Match match, float dt)
    {
        var player = match.Player;
        if (!match.IsPlaying || match.Pickups.Count == 0)
        {
            return;
        }
        if (duelTime < 0)
        {
            duelTime = 0;
            player.InputOverride = duelInput;
        }
        duelTime += dt;
        duelInput.Advance(dt);
        var pickup = match.Pickups[0];
        if (Once(0.2f, dt))
        {
            // Spend some rounds first so the pickup has room to add them.
            player.Ammo!.Reserve = 10;
            var at = pickup.GlobalPosition;
            var start = at + new Vector3(3.5f, 0.05f, 0);
            player.Teleport(start, Mathf.Atan2(-(at - start).X, -(at - start).Z));
            AimAt(player, at + Vector3.Up * 0.3f);
        }
        if (Once(0.6f, dt))
        {
            AimAt(player, pickup.GlobalPosition + Vector3.Up * 0.3f);
            director.Screenshot("pickup_0_before");
            duelInput.Hold(duelInput.Now, duelInput.Now + 5f, new PlayerIntent { Move = new Vector2(0, 1f) });
        }
        if (duelTime >= 0.6f && !pickupMovedAway)
        {
            AimAt(player, pickup.GlobalPosition + Vector3.Up * 0.3f);
            if (pickup.ClaimPending || !pickup.Available)
            {
                // Stop only after actually reaching the can, and move out before it can respawn. The old fixed
                // walking interval could end short of the take radius, yet still capture a "taken" frame.
                pickupMovedAway = true;
                duelInput = new ScriptedInput();
                player.InputOverride = duelInput;
                player.Teleport(pickup.GlobalPosition + new Vector3(3.5f, 0.05f, 0), player.FacingYaw);
                AimAt(player, pickup.GlobalPosition + Vector3.Up * 0.3f);
            }
        }
        if (!pickup.Available && pickupTakenAt < 0)
        {
            pickupTakenAt = duelTime;
            ScenarioAssert(player.Ammo!.Reserve == 10 + Tuning.Current.PickupAmount,
                "pickup: can did not add its rounds exactly once");
        }
        if (Once(5f, dt)) { ScenarioAssert(pickupTakenAt >= 0, "pickup: player never took the can"); }
        if (pickupTakenAt < 0) { return; }
        foreach (var offset in new[] { 0.15f, 1f })
        {
            if (Once(pickupTakenAt + offset, dt))
            {
                director.Screenshot($"pickup_1_taken_{offset:0.00}");
                Log($"pickup available {pickup.Available}, ammo {player.Ammo!.Magazine}/{player.Ammo.Reserve}");
                ScenarioAssert(!pickup.Available && player.Ammo.Reserve == 10 + Tuning.Current.PickupAmount,
                    "pickup: can returned early or changed ammo while absent");
            }
        }
        var respawn = Tuning.Current.PickupRespawnTime;
        var reveal = Tuning.Current.PickupRevealTime;
        foreach (var f in new[] { 0.15f, 0.4f, 0.65f, 0.9f, 1.2f })
        {
            var t = pickupTakenAt + respawn + reveal * f;
            if (Once(t, dt))
            {
                director.Screenshot($"pickup_2_reveal_{f:0.00}");
                if (f == 1.2f)
                {
                    ScenarioAssert(pickup.Available, "pickup: can did not respawn");
                    scenarioPassed = !scenarioFailed;
                    if (scenarioPassed) { Log("pickup: assertions passed"); }
                }
            }
        }
    }

    /// <summary>The host follows another (playing) bot at a few meters and films it: running, aiming, shooting, thrown.</summary>
    private void Watch(Match match, float dt)
    {
        var player = match.Player;
        var other = match.RemoteBodies.FirstOrDefault();
        if (!match.IsPlaying || other == null)
        {
            return;
        }
        if (duelTime < 0)
        {
            duelTime = 0;
            player.InputOverride = duelInput;
        }
        duelTime += dt;
        var target = other.IsRagdolled ? other.PelvisPosition : other.GlobalPosition + Vector3.Up;
        if (player.GlobalPosition.DistanceTo(other.GlobalPosition) > 9f)
        {
            // Keep up: drop in a few meters behind and to the side of them.
            var back = other.GlobalPosition + new Vector3(Mathf.Sin(other.FacingYaw), 0, Mathf.Cos(other.FacingYaw)) * 5f + new Vector3(1.5f, 0.3f, 0);
            player.Teleport(back, player.FacingYaw);
        }
        AimAt(player, target);
        if (Mathf.PosMod(duelTime, 1.5f) < dt)
        {
            director.Screenshot($"watch_{duelTime:000.0}");
        }
    }

    private int meleeThrows, meleeGetups;
    private bool meleeFirstDown, meleeSecondDown;
    private readonly HashSet<string> meleeCaptured = new();

    private void MeleeScenario(Match match, float dt)
    {
        if (!match.IsPlaying) return;
        var victim = role == "host";
        var player = match.Player;
        var other = match.RemoteBodies.FirstOrDefault();
        if (other == null) return;
        if (duelTime < 0)
        {
            duelTime = 0;
            player.InputOverride = duelInput;
            player.Ragdolled += (_, _, _) => meleeThrows++;
            player.GotUp += (_, _) => meleeGetups++;
            if (!victim) { duelInput.Melee(2); duelInput.Melee(12); }
            if (victim && DevArgs.Get("view") == "side")
            {
                movementCamera = new Camera3D { Fov = 40, Current = true };
                match.AddChild(movementCamera);
            }
        }
        duelTime += dt;
        duelInput.Advance(dt);
        var attacker = victim ? other : player;
        var target = victim ? player : other;
        var origin = new Vector3(Tuning.Current.MpMapSizeBase + Tuning.Current.MpMapSizePerPlayer * match.RemoteBodies.Count() + 30, 0.05f, 0);
        if (Once(.25f, dt) || Once(10.5f, dt))
        {
            player.Teleport(origin + (victim ? Vector3.Forward * 1.8f : Vector3.Zero), victim ? Mathf.Pi : 0);
            player.CameraRig.SetOrientation(victim ? 180 : 0, -6);
            if (remoteMotion != null) remoteMotion.SkipNextPose = true;
        }
        if (duelTime > 1 && remoteGrip == null && DevArgs.Get("assert-grip") != null) remoteGrip = GripProbe.Attach(attacker);
        if (duelTime > 1 && remoteMotion == null && DevArgs.Get("assert-motion") != null) remoteMotion = MotionProbe.Attach(attacker);
        if (movementCamera != null)
        {
            var focus = attacker.GlobalPosition + Vector3.Up;
            movementCamera.LookAtFromPosition(focus + new Vector3(4, .6f, -1.2f), focus);
        }
        if (duelTime < 8) meleeFirstDown |= target.IsRagdolled;
        if (duelTime > 12) meleeSecondDown |= target.IsRagdolled;
        if (attacker.ResolvedState.Weapon == WeaponAction.Melee)
        {
            var swing = duelTime < 8 ? 1 : 2;
            var frame = (int)(attacker.MeleeProgress * 12);
            var label = $"melee_{role}_{swing}_{frame:00}";
            if (meleeCaptured.Add(label)) CaptureMelee(label);
        }
        if (Once(4, dt))
        {
            ScenarioAssert(meleeFirstDown && match.Scores[target.PeerId] == 0 && match.LiveTanks.Count(IsDrop) == 5,
                "melee: first knockdown must spill exactly five tanks from a five-tank bank");
            if (victim) ScenarioAssert(meleeThrows == 1, "melee: repeated active contacts threw victim twice");
        }
        if (Once(10, dt))
        {
            ScenarioAssert(!target.IsRagdolled && meleeFirstDown, "melee: first victim never got up");
            if (victim) ScenarioAssert(meleeGetups == 1, "melee: missing first get-up");
        }
        if (Once(14, dt))
        {
            ScenarioAssert(meleeSecondDown && match.Scores[target.PeerId] == 0 && match.LiveTanks.Count(IsDrop) == 5,
                "melee: zero-bank victim must ragdoll without another drop");
            if (victim) ScenarioAssert(meleeThrows == 2, "melee: missing zero-bank throw");
        }
        if (Once(22, dt))
        {
            ScenarioAssert(!target.IsRagdolled && meleeSecondDown, "melee: zero-bank victim never got up");
            if (victim) ScenarioAssert(meleeGetups == 2, "melee: missing zero-bank get-up");
            remoteGrip?.Finish(); remoteMotion?.Finish();
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed) Log("melee: assertions passed; five tanks spilled, zero-bank stun, both get-ups");
        }
    }

    private async void CaptureMelee(string label)
    {
        if (DisplayServer.GetName() == "headless" || DevArgs.Get("capture-dir") == null) return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (IsInsideTree()) director.ScreenshotAsync(label);
    }

    private Camera3D? movementCamera;
    private GripProbe? remoteGrip;
    private LocomotionProbe? remoteFeet;
    private MotionProbe? remoteMotion;
    private bool sawRemoteReload;
    private bool sawRemoteCreep;
    private bool sawRemoteSprint;
    private bool sawRemoteAir;
    private bool sawRemoteRestoredAim;
    private bool movementProved;
    private PlayerGait remoteGait;
    private float remoteGaitTime;
    private bool capturedCreep;
    private bool capturedSprint;
    private bool capturedLanding;
    private bool capturedRelease;

    /// <summary>Two-player gait/stance proof on the unobstructed void floor, seen through real snapshots.</summary>
    private void MovementScenario(Match match, float dt)
    {
        if (!match.IsPlaying) return;
        if (duelTime < 0)
        {
            duelTime = 0;
            match.Player.InputOverride = duelInput;
            match.Player.Teleport(new Vector3(role == "host" ? 20 : 0, 0.05f, -200), 0);
            if (role != "host")
            {
                duelInput.Hold(0.5f, 8.5f, new PlayerIntent { Move = new Vector2(0, 1) });
                duelInput.Hold(1.5f, 8.5f, new PlayerIntent { Sprint = true });
                duelInput.Hold(1.5f, 3.0f, new PlayerIntent { Aim = true });
                duelInput.Hold(4.5f, 7.5f, new PlayerIntent { Aim = true });
                duelInput.Jump(5.0f);
                duelInput.Hold(8.6f, 9.3f, new PlayerIntent { FireHeld = true });
                duelInput.Hold(9.5f, 11.2f, new PlayerIntent { Aim = true });
                duelInput.Hold(10, 10.7f, new PlayerIntent { FireHeld = true });
                duelInput.Hold(11.4f, 13.8f, new PlayerIntent { Move = Vector2.Down * 0.3f });
                duelInput.Reload(11.6f);
            }
            else
            {
                movementCamera = new Camera3D { Fov = 40, Current = true };
                match.AddChild(movementCamera);
            }
        }
        duelTime += dt;
        duelInput.Advance(dt);
        if (role != "host") return;
        var other = match.RemoteBodies.FirstOrDefault();
        if (other == null) return;
        if (remoteGrip == null && (DevArgs.Get("grip-metrics") != null || DevArgs.Get("assert-grip") != null))
            remoteGrip = GripProbe.Attach(other);
        // The spawn-to-void relocation also replaces the aim target and body yaw. Measure the movement
        // sequence after that controlled staging has settled; the matrix measures ordinary corrections.
        if (remoteFeet == null && duelTime >= 1 && (DevArgs.Get("feet-metrics") != null || DevArgs.Get("assert-feet") != null))
            remoteFeet = LocomotionProbe.Attach(other);
        if (remoteMotion == null && duelTime >= 1 && DevArgs.Get("assert-motion") != null)
            remoteMotion = MotionProbe.Attach(other);
        sawRemoteReload |= other.ResolvedState.Weapon == WeaponAction.Reload;
        if (remoteFeet != null) remoteFeet.Label = other.ResolvedState.Gait.ToString();
        var focus = other.GlobalPosition + Vector3.Up;
        var offset = new Vector3(4, 0.5f, -1.5f);
        var view = DevArgs.Get("view");
        if (view is "rifleleft" or "rifleright")
        {
            var rifle = other.GetNode<Rifle>("Rifle");
            focus = rifle.GlobalTransform * new Vector3(0, 0, -0.12f);
            offset = new Basis(Vector3.Up, other.FacingYaw) * new Vector3(view == "rifleleft" ? -0.8f : 0.8f, 0.2f, -0.45f);
        }
        movementCamera!.LookAtFromPosition(focus + offset, focus);
        var state = other.ResolvedState;
        var speed = new Vector2(other.Velocity.X, other.Velocity.Z).Length();
        remoteGaitTime = remoteGait == state.Gait ? remoteGaitTime + dt : 0;
        remoteGait = state.Gait;
        if (!sawRemoteCreep && state.Gait == PlayerGait.Creep && state.EffectiveAim && Math.Abs(speed - Tuning.Current.CreepSpeed) < 0.15f)
        {
            sawRemoteCreep = true;
            CaptureRemoteFrame("remote_00_creep");
            Log($"remote creep: {state.Support}/{state.Weapon} speed={speed:0.00}");
        }
        if (!sawRemoteSprint && state.Gait == PlayerGait.Sprint && speed > Tuning.Current.SprintSpeed - 0.15f)
        {
            sawRemoteSprint = true;
            CaptureRemoteFrame("remote_01_sprint");
            Log($"remote sprint: {state.Support}/{state.Weapon} speed={speed:0.00}");
        }
        if (!sawRemoteAir && sawRemoteCreep && sawRemoteSprint &&
            state.Support == PlayerSupport.Airborne && !state.EffectiveAim && other.AimAmount == 0)
        {
            sawRemoteAir = true;
            CaptureRemoteFrame("remote_02_air");
            Log($"remote air: {state.Gait}/{state.Weapon}");
        }
        if (sawRemoteAir && state.Grounded && state.EffectiveAim && state.Gait == PlayerGait.Creep)
        {
            sawRemoteRestoredAim = true;
        }
        // Each client can start its script on a different frame. Label screenshots from observed remote states,
        // after the gait/rig blends settle, rather than the observer's script clock.
        if (remoteGaitTime > 0.6f && state.Gait == PlayerGait.Creep && other.AimAmount > 0.99f)
        {
            if (!capturedCreep)
            {
                capturedCreep = true;
                CaptureRemoteFrame("remote_03_creep_settled");
            }
            if (sawRemoteAir && !capturedLanding)
            {
                capturedLanding = true;
                CaptureRemoteFrame("remote_06_landed_creep");
            }
        }
        if (remoteGaitTime > 0.6f && state.Gait == PlayerGait.Sprint && state.Grounded)
        {
            if (!capturedSprint)
            {
                capturedSprint = true;
                CaptureRemoteFrame("remote_04_sprint_settled");
            }
            if (capturedLanding && !capturedRelease)
            {
                capturedRelease = true;
                CaptureRemoteFrame("remote_07_release_sprint");
            }
        }
        if (!movementProved && duelTime >= 14.5f)
        {
            movementProved = true;
            remoteGrip?.Finish();
            remoteFeet?.Finish();
            remoteMotion?.Finish();
            if (DevArgs.Get("assert-motion") != null && (!sawRemoteReload || remoteMotion?.RecoilFrames < 10))
            {
                GD.PushError("[net movement] FAIL missing remote reload or recoil");
                GetTree().Quit(1);
                return;
            }
            if (!sawRemoteCreep || !sawRemoteSprint || !sawRemoteAir || !sawRemoteRestoredAim ||
                !capturedCreep || !capturedSprint || !capturedLanding || !capturedRelease)
            {
                GD.PushError("[net movement] FAIL missing remote gait/support");
                GetTree().Quit(1);
                return;
            }
            Log("[net movement] PASS remote creep, sprint, air aim suppression, restored landing aim, bursts and walking reload");
        }
    }

    private async void CaptureRemoteFrame(string label)
    {
        if (DisplayServer.GetName() == "headless") return;
        // Physics runs before animation/render. Read the frame after drawing the state that named it.
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (IsInsideTree()) director.Screenshot(label);
    }

    private bool Once(float at, float dt) => duelTime >= at && duelTime - dt < at;

    private static bool IsDrop(PropaneTank tank) => tank.NetId is >= BodySync.DropTankBase and < BodySync.CarBase;

    private void ScenarioAssert(bool condition, string failure)
    {
        if (condition) { return; }
        scenarioFailed = true;
        GD.PushError(failure);
        GetTree().Quit(1);
    }

    private static void AimAt(PlayerCharacter player, Vector3 point)
    {
        var camera = player.CameraRig.Camera;
        var view = point - camera.GlobalPosition;
        player.CameraRig.SetOrientation(Mathf.RadToDeg(Mathf.Atan2(-view.X, -view.Z)), Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(view.Normalized().Y, -1, 1))));
    }

    /// <summary>Scores and where this game has each tank, keyed by match clock, for comparing players' views.</summary>
    private void Digest()
    {
        if (match == null)
        {
            return;
        }
        var tanks = new StringBuilder();
        foreach (var tank in match.LiveTanks.OrderBy(t => t.NetId))
        {
            var p = tank.GlobalPosition;
            tanks.Append($" {tank.NetId:x}:{(tank.State == PropaneTank.TankState.Venting ? "V" : "I")}:{p.X:0.00},{p.Y:0.00},{p.Z:0.00}");
        }
        var remotes = string.Join(" ", match.RemoteBodies.Select(b => $"{b.PeerId}@{b.GlobalPosition.X:0.0},{b.GlobalPosition.Z:0.0}{(b.IsRagdolled ? "R" : "")}"));
        foreach (var body in match.RemoteBodies)
        {
            var bones = body.FindChildren("Physical_*", "PhysicalBone3D", owned: false).OfType<PhysicalBone3D>().ToList();
            if (bones.Count > 0)
            {
                var far = bones.Max(b => b.GlobalPosition.DistanceTo(body.GlobalPosition));
                Log($"bones of {body.PeerId}: farthest {far:0.00} m from the body, ragdolled {body.IsRagdolled}, layer {bones[0].CollisionLayer}");
            }
        }
        var self = match.Player;
        Log($"tuning BlastRadius {Tuning.Current.BlastRadius} PickupRespawnTime {Tuning.Current.PickupRespawnTime}");
        Log($"perf fps {Engine.GetFramesPerSecond():0} process {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:0.0} ms physics {Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:0.0} ms bodies {Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects)}");
        Log($"digest clock {match.TimeLeft:0.0} me {self.GlobalPosition.X:0.0},{self.GlobalPosition.Z:0.0}{(self.IsRagdolled ? "R" : "")} ammo {self.Ammo?.Magazine}/{self.Ammo?.Reserve} scores {ScoreLine()} remotes {remotes}");
        Log($"tanks clock {match.TimeLeft:0.0} n {match.LiveTanks.Count()}{tanks}");
    }

    private string ScoreLine() => match == null ? "" : string.Join(" ", match.Scores.Select(s => $"{s.Key}={s.Value}"));

    /// <summary>Same-machine wall time avoids comparing independently corrected match countdowns.</summary>
    private void LogTankTrajectory()
    {
        if (match == null) { return; }
        var sampleTime = Time.GetUnixTimeFromSystem();
        var tanks = new StringBuilder();
        foreach (var tank in match.LiveTanks.OrderBy(t => t.NetId))
        {
            var p = tank.GlobalPosition;
            tanks.Append(FormattableString.Invariant($" {tank.NetId:x}:{(tank.State == PropaneTank.TankState.Venting ? "V" : "I")}:{p.X:0.000},{p.Y:0.000},{p.Z:0.000}"));
        }
        Log(FormattableString.Invariant($"tank-sample time {sampleTime:0.000000} playing {match.IsPlaying} clock {match.TimeLeft:0.000} n {match.LiveTanks.Count()}{tanks}"));
    }

    private void Log(string text) => GD.Print($"[nettest {net.PlayerName} {Time.GetTicksMsec() / 1000.0:0.00}] {text}");
}
