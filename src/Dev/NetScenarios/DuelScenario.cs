using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;
using Propane.Tank;
using Propane.World.Plan;
using ProcessModeEnum = Godot.Node.ProcessModeEnum;

namespace Propane.Dev.NetScenarios;

internal sealed class DuelScenario(INetScenarioHost host) : NetScenario(host)
{
    private readonly Dictionary<uint, Vector3> gatherPositions = new();
    private readonly HashSet<uint> gatherShields = new();
    private readonly HashSet<uint> gatherGlides = new();
    private float duelTime = -1;
    private ScriptedInput duelInput = new();
    private uint? duelTank;
    private float duelShotLeft;
    private float duelBlastAt = -1;
    private float duelThrowAt = -1;
    private int duelInitialScore;

    protected override void Assembled(Match match)
    {
        duelTime = -1;
        gatherPositions.Clear(); gatherShields.Clear(); gatherGlides.Clear();
        duelInput = new ScriptedInput();
        duelTank = null;
        duelBlastAt = duelThrowAt = -1;
    }

    protected override void Run(Match match, float dt) => Duel(match, dt);
    private bool Once(float at, float dt) => duelTime >= at && duelTime - dt < at;
    private static Color ReadShieldColor(Variant value) => NetScenarioTools.ReadShieldColor(value);

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
            var captured = Epoch;
            void Thrown(Vector3 position, Vector3 direction, Vector3 impulse) { if (IsCurrent(captured)) duelThrowAt = duelTime; }
            player.Ragdolled += Thrown;
            DetachOnRetirement(() => player.Ragdolled -= Thrown);
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
        var shooterSpot = PlanCoordinates.ToWorld(mid - road.Direction * 7f, 0.05f);
        var victimSpot = PlanCoordinates.ToWorld(mid + road.Direction * 7f, 0.05f);
        var host = role == "host";
        var refusal = DevArgs.Get("scenario") == "drop-refusal";
        var gathering = DevArgs.Get("scenario") == "gather";
        if (gathering)
        {
            var dropper = match.Setup.Players.Single(p => p.Player.OwnerPeerId != match.Bodies.Host).Player;
            foreach (var tank in match.LiveTanks.Where(IsDrop))
            {
                var mesh = tank.GetNode<MeshInstance3D>("Mesh");
                var glow = ReadShieldColor(mesh.GetInstanceShaderParameter("shield_color"));
                if (tank.Invulnerable)
                {
                    ScenarioAssert(!tank.Gathering, "gather: shielded drop gathered");
                    // The dummy renderer cannot read back shader instance uniforms.
                    if (DisplayServer.GetName() == "headless") { gatherShields.Add(tank.NetId); }
                    else if (glow.A > 0)
                    {
                        ScenarioAssert(new Color(glow, 1) == dropper.Appearance.Color,
                            FormattableString.Invariant($"gather: shielded drop lost accepted appearance: tank {tank.NetId:x} glow={glow.R:R},{glow.G:R},{glow.B:R},{glow.A:R} expected={dropper.Appearance.Color.R:R},{dropper.Appearance.Color.G:R},{dropper.Appearance.Color.B:R}"));
                        gatherShields.Add(tank.NetId);
                    }
                }
                if (!tank.Gathering) { continue; }
                ScenarioAssert(!tank.Invulnerable, "gather: invulnerable drop entered glide");
                if (DisplayServer.GetName() != "headless")
                {
                    ScenarioAssert(glow.A == 0 && mesh.GetInstanceShaderParameter("shield_contrast").AsColor().A == 0 &&
                        mesh.GetInstanceShaderParameter("gather_shimmer").AsSingle() == match.World.Tuning.GatherShimmer,
                        "gather: expired shield glow/contrast remained or shimmer missing");
                }
                if (host && player.View != null)
                {
                    var camera = player.View.CameraRig.Camera;
                    player.CameraRig.ProcessMode = ProcessModeEnum.Disabled;
                    var center = tank.GlobalTransform * PropaneTank.LocalCenter;
                    camera.LookAtFromPosition(center + new Vector3(1.5f, 1, 2), center);
                    if (gatherGlides.Count == 0)
                    {
                        GetTree().CreateTimer(0.2).Timeout += Guard(() => director.ScreenshotAsync("gather_host_shimmer"));
                    }
                }
                gatherGlides.Add(tank.NetId);
            }
        }
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
                if (DisplayServer.GetName() != "headless")
                {
                    ScenarioAssert(tank.GetNode<MeshInstance3D>("Mesh").GetInstanceShaderParameter("gather_shimmer").AsSingle() == 0,
                        "gather: settled drop retained shimmer");
                }
                Log($"gather final {tank.NetId:x} {tank.GlobalPosition}");
            }
        }
        if (gathering && Once(40, dt))
        {
            ScenarioAssert(gatherShields.Count == 2 && gatherGlides.Count == 2, $"gather: did not observe both drop shields and glides (shields {gatherShields.Count}, glides {gatherGlides.Count})");
            ScenarioAssert(gatherPositions.Count == 2 && match.LiveTanks.Where(IsDrop).All(t =>
                gatherPositions.TryGetValue(t.NetId, out var position) && position.DistanceTo(t.GlobalPosition) <= 0.006f && t.AtRest), "gather: final pile unstable");
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed)
            {
                Log(DisplayServer.GetName() == "headless"
                    ? "gather: gameplay assertions passed; both shields expired and drops acquired, settled and stable"
                    : "gather: assertions passed; two accepted-color shields expired; both drops shimmered, acquired, settled and stable on this client");
            }
            director.ScreenshotAsync($"gather_{role}_settled");
        }
        if (gathering && host && duelTime is > 18 and < 32 && Mathf.PosMod(duelTime, 1) < dt)
        {
            var drops = match.LiveTanks.Where(IsDrop).ToArray();
            if (drops.Length > 0) { AimAt(player, drops[0].GlobalTransform * PropaneTank.LocalCenter); }
            director.ScreenshotAsync($"gather_{role}_{duelTime:00}");
        }
        if (Once(0.8f, dt)) { duelInitialScore = match.MyScore; }
        if (refusal && host && Once(2.55f, dt))
        {
            // The second victim hit is already in flight. With 120 ms lag, this creator change reaches the server
            // before the victim's old-count Drop, while reliable order delivers Shot before TuningSet to the victim.
            match.World.Tuning.HitDropCount = 0;
            net.Send(new NetWriter(Msg.TuningSet, match.Setup.Id).String(nameof(Tuning.HitDropCount)).Scalar(0));
        }
        if (Once(3.5f, dt))
        {
            var victimId = host ? match.RemoteBodies.First().ActorId : match.Player.ActorId;
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
            player.Aim.SetOrientation(Mathf.RadToDeg(yaw) + (host ? 0 : 160), -6f);
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
        if (DevArgs.Get("duel-blocked") != null && Once(3.7f, dt))
        {
            var dropped = match.LiveTanks.Where(IsDrop).OrderBy(k => k.NetId).First();
            var away = dropped.GlobalPosition - victim.GlobalPosition;
            away.Y = 0;
            ScenarioAssert(away.Length() > .2f, "duel: blocked-lane fixture has no drop offset");
            player.Teleport(victim.GlobalPosition - away.Normalized() * 14f, player.FacingYaw);
            var hit = DuelRay(match, dropped, player.GlobalPosition);
            ScenarioAssert(PlayerCharacter.Owning(hit) == victim, "duel: fixture did not put the victim in the firing lane");
            Log("duel: victim-blocked firing lane staged");
        }
        // Drops keep moving under lag and can land behind the victim. Keep a clear firing lane on the road;
        // blindly chasing the nearest drop can shoot the victim until their bank is empty instead.
        if (duelTime >= 4f && duelBlastAt < 0)
        {
            var target = match.LiveTanks.FirstOrDefault(k => k.NetId == duelTank);
            if (target != null && DuelRay(match, target, player.GlobalPosition) != target)
            {
                duelTank = null;
                target = null;
            }
            if (duelTank == null)
            {
                var lanes = new[] { shooterSpot, PlanCoordinates.ToWorld(mid + road.Direction * 21f, 0.05f) };
                foreach (var dropped in match.LiveTanks.Where(IsDrop).Where(k => !k.Invulnerable)
                             .OrderBy(k => k.GlobalPosition.DistanceTo(player.GlobalPosition)))
                {
                    var lane = lanes.FirstOrDefault(at => DuelRay(match, dropped, at) == dropped, Vector3.Inf);
                    if (!lane.IsFinite()) continue;
                    if (player.GlobalPosition.DistanceTo(lane) > .1f) player.Teleport(lane, player.FacingYaw);
                    target = dropped;
                    duelTank = target.NetId;
                    duelShotLeft = 0.3f; // Let the rifle align after changing the lane or target.
                    var captured = Epoch;
                    void Detonated(PropaneTank tank) { if (IsCurrent(captured)) duelBlastAt = duelTime; }
                    target.Detonated += Detonated;
                    var observed = target;
                    DetachOnRetirement(() => observed.Detonated -= Detonated);
                    Log($"duel: tracking dropped tank {target.NetId:x} from a clear firing lane");
                    break;
                }
            }
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

    private static GodotObject? DuelRay(Match match, PropaneTank target, Vector3 standing)
    {
        using var ray = PhysicsRayQueryParameters3D.Create(standing + Vector3.Up * 1.4f,
            target.GlobalTransform * PropaneTank.LocalCenter, Layers.ShotMask,
            new Godot.Collections.Array<Rid> { match.Player.GetRid() });
        var hit = match.World.Physics.DirectSpaceState.IntersectRay(ray);
        return hit.Count > 0 ? hit["collider"].As<GodotObject>() : null;
    }

}
