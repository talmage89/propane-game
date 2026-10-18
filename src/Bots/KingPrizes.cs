using System.Collections.Generic;
using Godot;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// One King brain's prize/points selection, scan buffers and blocked-shot retries. Retry deadlines use
/// IBotWorld.Now, scan/aim timers use physics seconds. Discarded with the brain.
/// </summary>
internal sealed class KingPrizes
{
    private readonly List<(PropaneTank Tank, float Distance)> candidates = new(256);
    private readonly List<Vector3> prizeBodies = new(32);
    private PropaneTank? prize;
    private float prizeTimer;
    private PropaneTank? blocker;
    private readonly BotPoints points = new();
    private float pointsTimer;
    private Node3D? pointsTarget;
    private bool retiredPointsTarget;
    private bool protectingPoints;
    private int protectZone = -1;
    private readonly Dictionary<ulong, double> pointsBlocked = new(1024);
    private readonly List<ulong> pointsExpired = new(1024);
    private PropaneTank? pointsShot;
    private float pointsShotWait;
    public Node3D? PointsTarget => pointsTarget;
    public bool ProtectingPoints => protectingPoints;

    public void Waiting()
    {
        pointsTarget = pointsShot = null;
        retiredPointsTarget = false;
        protectingPoints = false;
        pointsTimer = pointsShotWait = 0;
    }

    public void Down() { pointsTimer = pointsShotWait = 0; protectingPoints = false; }
    public void NavigationFailed() { pointsTarget = null; retiredPointsTarget = false; pointsTimer = 0; }
    public void RetryPoints() => pointsTimer = 0;
    public void ForgetActor(PlayerCharacter actor)
    {
        if (!ReferenceEquals(pointsTarget, actor)) return;
        pointsTarget = null;
        retiredPointsTarget = true;
    }

    public bool SelectPoints(IBotWorld world, KingBotView state, Vector3 spot, PlayerCharacter player, Vector3 here, Core.Tuning tuning, float dt, out Vector3? protectionGoal)
    {
        protectionGoal = null;
        var changed = false;
        pointsTimer -= dt;
        if (retiredPointsTarget || pointsTarget != null && (!GodotObject.IsInstanceValid(pointsTarget) ||
            pointsTarget is PropaneTank { State: PropaneTank.TankState.Exploded } or PlayerCharacter { IsRagdolled: true }))
        {
            pointsTimer = 0;
        }

        if (pointsTimer <= 0)
        {
            pointsTimer = tuning.BotPointsInterval;
            points.Begin(world);
            var zoneRate = points.Zone(world, state, spot);
            var protect = zoneRate < 0 && world.MyScore > 0;
            var best = Mathf.Max(0, zoneRate);
            if (pointsTarget == null && !retiredPointsTarget) best *= 1 + tuning.BotPointsHysteresis;
            pointsExpired.Clear();
            foreach (var (id, until) in pointsBlocked) if (until <= world.Now) pointsExpired.Add(id);
            foreach (var id in pointsExpired) pointsBlocked.Remove(id);
            Node3D? chosen = null;
            var bodies = world.RemoteBodies;
            for (var i = 0; i < bodies.Count; i++)
            {
                var enemy = bodies[i];
                var distance = enemy.GlobalPosition.DistanceTo(here);
                if (distance > tuning.BotPointsShotRange) continue;
                var rate = points.Fight(world, enemy, distance);
                if (enemy == pointsTarget) rate *= 1 + tuning.BotPointsHysteresis;
                if (rate > best && BotMotor.CanSee(world, player, BotMotor.AimPoint(enemy))) { best = rate; chosen = enemy; }
            }
            var tanks = world.LiveTanks;
            for (var i = 0; i < tanks.Count; i++)
            {
                var tank = tanks[i];
                var distance = tank.GlobalPosition.DistanceTo(here);
                // Bank from here, without crossing a pile or navigating to a changing tank. The zone policy still
                // takes us across the map when no immediate point opportunity pays better.
                if (pointsBlocked.ContainsKey(tank.GetInstanceId()) || tank.Invulnerable || distance < tuning.RagdollRadius + world.Services.PileSpread(i) + tuning.BotVentClearance ||
                    distance > tuning.BotPointsShotRange)
                {
                    continue;
                }

                var rate = points.Tank(world, tank, world.Services.PileCount(i), distance);
                if (tank == pointsTarget) rate *= 1 + tuning.BotPointsHysteresis;
                if (rate > best && world.Services.Ray(player.Aim.EyeOrigin, tank.GlobalTransform * PropaneTank.LocalCenter,
                    Core.Layers.ShotMask, out var hit) && hit.Collider == tank.GetRid()) { best = rate; chosen = tank; }
            }
            if (chosen != pointsTarget || retiredPointsTarget) { changed = true; pointsShotWait = 0; pointsShot = null; }
            retiredPointsTarget = false;
            pointsTarget = chosen;
            protect &= chosen == null;
            if (protect && (!protectingPoints || protectZone != state.Zone))
            {
                var away = BotMotor.Flat(here - spot).Normalized();
                if (away == Vector3.Zero) away = Vector3.Right;
                protectionGoal = spot + away * ((float)state.Radius + tuning.BotPointsProtectDistance + tuning.BotTankClearance * 2);
                protectZone = state.Zone;
            }
            protectingPoints = protect;
        }
        return changed;
    }

    public bool Shot(IBotWorld world, PlayerCharacter player, PropaneTank tank, Core.Tuning tuning, float dt, out bool giveUp)
    {
        if (pointsShot != tank) { pointsShot = tank; pointsShotWait = 0; }
        var onTarget = BotMotor.OnTarget(world, player, tank);
        pointsShotWait = onTarget ? 0 : pointsShotWait + dt;
        giveUp = pointsShotWait >= tuning.BotStuckAimTime;
        if (giveUp)
        {
            // The observation ray may see past a body or loose fence that obstructs the rifle.
            pointsBlocked[tank.GetInstanceId()] = world.Now + tuning.BotTargetRetryTime;
            pointsTarget = pointsShot = null;
            pointsShotWait = pointsTimer = 0;
        }
        return onTarget;
    }

    /// <summary>The tank to bank, chosen a few times a second: the nearest in sight, nearest first, so few rays.</summary>
    public PropaneTank? Prize(IBotWorld world, PlayerCharacter player, Core.Tuning tuning, float dt)
    {
        var here = player.GlobalPosition;
        var safe = tuning.RagdollRadius + 2.5f;
        prizeTimer -= dt;
        if (prize != null && (!GodotObject.IsInstanceValid(prize) || prize.State == PropaneTank.TankState.Exploded ||
            prize.GlobalPosition.DistanceTo(here) <= safe))
        {
            prize = null;
        }

        if (prizeTimer > 0) return prize;
        prizeTimer = tuning.BotPrizeInterval;
        candidates.Clear();
        var tanks = world.LiveTanks;
        var bodies = world.RemoteBodies;
        // These transforms cannot change during this synchronous search. Read each native position once,
        // rather than once per tank/body pair and again for every candidate-ranking comparison.
        prizeBodies.Clear();
        for (var i = 0; i < bodies.Count; i++) prizeBodies.Add(bodies[i].GlobalPosition);
        for (var i = 0; i < tanks.Count; i++)
        {
            var t = tanks[i];
            var position = t.GlobalPosition;
            var d = position.DistanceTo(here);
            if (t.Invulnerable || d <= safe || d >= 25f) continue;
            var crowded = false;
            for (var j = 0; j < prizeBodies.Count && !crowded; j++) crowded = prizeBodies[j].DistanceTo(position) <= 1f;
            if (!crowded) candidates.Add((t, d));
        }
        prize = null;
        while (prize == null && candidates.Count > 0)
        {
            var nearest = 0;
            for (var i = 1; i < candidates.Count; i++)
            {
                if (candidates[i].Distance < candidates[nearest].Distance) nearest = i;
            }
            var t = candidates[nearest].Tank;
            candidates.RemoveAt(nearest);
            if (BotMotor.CanSee(world, player, t.GlobalTransform * PropaneTank.LocalCenter)) prize = t;
        }
        return prize;
    }

    /// <summary>A venting tank in the zone this bot can see and set off from beyond its throw; chosen when looking.</summary>
    public PropaneTank? Blocker(IBotWorld world, PlayerCharacter player, Vector3 spot, float radius, Core.Tuning tuning, bool look)
    {
        var here = player.GlobalPosition;
        bool Fits(PropaneTank t) => GodotObject.IsInstanceValid(t) && t.State == PropaneTank.TankState.Venting && !t.Invulnerable &&
            BotMotor.Flat(t.GlobalPosition - spot).Length() < radius + tuning.RagdollRadius &&
            t.GlobalPosition.DistanceTo(here) > tuning.RagdollRadius + 1f && t.GlobalPosition.DistanceTo(here) < 30f;
        if (!look) return blocker != null && Fits(blocker) ? blocker : null;
        blocker = null;
        var venting = world.Services.Venting;
        for (var i = 0; i < venting.Count && blocker == null; i++)
        {
            if (Fits(venting[i]) && BotMotor.CanSee(world, player, venting[i].GlobalTransform * PropaneTank.LocalCenter)) blocker = venting[i];
        }
        return blocker;
    }
}
