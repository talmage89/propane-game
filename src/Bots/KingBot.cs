using System.Collections.Generic;
using Godot;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// King of the Cul-de-sac's goal policy: hold alone, shove or shoot intruders, and break off prolonged contests
/// to fight from the perimeter before independently timed raids. With nobody to fight, shoot
/// tanks in sight from a safe distance to bank them; head for the next spot
/// when the zone is about to move and it could not otherwise make it in time. It reads the zone from the mode's
/// <see cref="KingState"/>: the Lab's rules, or the server's snapshot in a match.
/// Hard compares nearby banks/spills with zone income and the cost of risking its bank in a crowd.
/// Prize searches are periodic; combat visibility stays live by default for brief sight gaps near the zone.
/// </summary>
public sealed class KingBot : IBotGoalPolicy
{

    private readonly RandomNumberGenerator rng = new();
    private readonly BotMotor motor = new();
    private readonly BotStatus status = new();
    private readonly List<(PropaneTank Tank, float Distance)> candidates = new(256);
    private readonly List<Vector3> prizeBodies = new(32);
    private int standZone = -1;
    private Vector3 standOffset;
    private float standCheck;
    private float fightTime;
    private float burstClock;
    private PropaneTank? prize;
    private float prizeTimer;
    private PlayerCharacter? rival;
    private PlayerCharacter? watched;
    private bool watchedVisible;
    private PropaneTank? blocker;
    private float sightTimer;
    private bool sightStarted;
    private bool wasDown;
    private float regroupTime;
    private Vector3 regroupGoal;
    private float regroupTurn = 1;
    private float crowdTimer;
    private int crowdRivals;
    private bool firstToZone;
    private float contestedTime;
    private float contestPatience;
    private float raidTime;
    private bool raidCommitted;
    private readonly BotPoints points = new();
    private float pointsTimer;
    private Node3D? pointsTarget;
    private bool protectingPoints;
    private int protectZone = -1;
    private readonly Dictionary<ulong, double> pointsBlocked = new(1024);
    private readonly List<ulong> pointsExpired = new(1024);
    private PropaneTank? pointsShot;
    private float pointsShotWait;
    public bool PointsDriven { get; set; }

    public string Status => status.ToString();
    public string StatusKind => status.Format;
    public string NavigationDiagnostics => motor.NavigationDiagnostics;
    public float Skill { get => motor.Skill; set => motor.Skill = value; }

    public KingBot(ulong? seed = null)
    {
        if (seed is { } value) rng.Seed = value;
        // Stagger sight without consuming the goal policy's seeded random stream: stand choices must not change
        // merely because observations moved to a periodic cadence.
        sightTimer = (unchecked((seed ?? 0) * 2654435761UL) & 1023) / 1024f;
    }

    public void Dispose() => rng.Dispose();

    public void Prepare(IBotWorld world) => motor.Prepare(world);

    public PlayerIntent Read() => motor.Read();

    public void Think(IBotWorld world, float dt)
    {
        motor.Begin(dt);
        var player = world.Player;
        if (!world.IsPlaying || world.King is not KingBotView state)
        {
            motor.Reset();
            wasDown = false;
            regroupTime = raidTime = contestedTime = contestPatience = 0;
            crowdTimer = 0;
            raidCommitted = false;
            pointsTarget = pointsShot = null;
            protectingPoints = false;
            pointsTimer = pointsShotWait = 0;
            status.Set("waiting");
            return;
        }
        if (player.IsRagdolled)
        {
            wasDown = true;
            pointsTimer = pointsShotWait = 0;
            protectingPoints = false;
            motor.Reset();
            status.Set("down");
            return;
        }
        var tuning = world.World.Tuning;
        var here = player.GlobalPosition;
        var zone = state.Zone;
        var spot = ToVector(state.Spot);
        if (wasDown)
        {
            wasDown = false;
            if (world.RemoteBodies.Count + 1 >= tuning.BotKingRegroupPlayers && tuning.BotKingRegroupMax > 0)
            {
                regroupTime = rng.RandfRange(Mathf.Max(0, tuning.BotKingRegroupMin),
                    Mathf.Max(tuning.BotKingRegroupMin, tuning.BotKingRegroupMax));
                var away = BotMotor.Flat(here - spot).Normalized();
                if (away == Vector3.Zero) away = Vector3.Right.Rotated(Vector3.Up, rng.RandfRange(0, Mathf.Tau));
                regroupGoal = spot + away * ((float)state.Radius + tuning.BotTankClearance * 2);
                regroupTurn = rng.Randf() < 0.5f ? -1 : 1;
                motor.Reset();
            }
        }
        // Leave early for the next spot only when the walk would otherwise outlast the warning.
        var next = ToVector(state.Next);
        if (state.Warning && state.UntilMove < BotMotor.Flat(next - here).Length() / tuning.SprintSpeed + 0.5)
        {
            zone++;
            spot = next;
        }
        if (zone != standZone)
        {
            // A spot sits on its pile, which nobody can stand in: hold the zone from beside it.
            standZone = zone;
            contestedTime = contestPatience = raidTime = crowdTimer = 0;
            raidCommitted = false;
            PickStand(state, world, spot);
        }
        standCheck -= dt;
        if (standCheck <= 0)
        {
            // Piles move and new ones materialize: a stand point a tank has rolled onto is no longer reachable.
            standCheck = 0.5f;
            if (!Clear(world, spot + standOffset)) PickStand(state, world, spot);
        }
        var radius = (float)state.Radius;

        if (BotMotor.Danger(world, player) is { } danger)
        {
            var away = here + BotMotor.Flat(here - danger.GlobalPosition).Normalized() * 6f;
            motor.Face(player, away + Vector3.Up * 1.4f, dt);
            motor.MoveTo(away, run: true);
            status.Set("clearing a venting tank");
            motor.Navigate(world, dt);
            return;
        }

        if (!sightStarted) { sightTimer *= tuning.BotKingSightInterval; sightStarted = true; }
        sightTimer -= dt;
        var look = sightTimer <= 0;
        if (look) sightTimer = tuning.BotKingSightInterval;

        if (regroupTime > 0)
        {
            // Charging straight back after every knockdown keeps a large roster permanently contested. Recover
            // outside for independently seeded intervals, then resume the same competitive zone policy. Meanwhile
            // keep playing from the edge: shoot a rival with a bank to spill, or bank a tank in sight, and circle.
            regroupTime -= dt;
            Regroup(world, player, spot, radius, tuning, dt, look);
            motor.Navigate(world, dt);
            return;
        }

        if (PointsDriven && ChoosePoints(world, state, spot, player, tuning, dt))
        {
            motor.Navigate(world, dt);
            if (motor.Failed) { pointsTarget = null; pointsTimer = 0; }
            return;
        }

        if (BreakCrowd(world, state, player, spot, radius, tuning, dt, look))
        {
            motor.Navigate(world, dt);
            return;
        }
        var inZone = BotMotor.Flat(here - spot).Length() <= radius * 0.85f;
        var reach = BotMotor.MeleeReach(tuning);
        var close = NearestIntruder(world, spot, radius, here, reach + 0.4f);
        if (!Intruding(rival, spot, radius)) rival = null;
        if (close == null && inZone && look) rival = VisibleIntruder(world, player, spot, radius);
        if (close != null)
        {
            motor.Face(player, BotMotor.AimPoint(close), dt);
            motor.Melee(player, close.GlobalPosition + Vector3.Up * tuning.MeleeContactHeight);
            motor.Intent.Move = new Vector2(0, 0.6f); // Into them: their own spot is never a standable goal.
            status.Set("shoving {0} out of the zone", close.ActorId);
        }
        else if (inZone && rival != null)
        {
            Shoot(player, rival, tuning, dt);
            var gap = BotMotor.Flat(rival.GlobalPosition - here);
            if (gap.Length() > 2.5f) motor.MoveTo(rival.GlobalPosition - gap.Normalized() * 1.5f);
            status.Set("contesting {0}", rival.ActorId);
        }
        else if (!inZone && Blocker(world, player, spot, radius, tuning, look) is { } venting)
        {
            // A venting tank in the zone keeps everyone out until it goes up: set it off from beyond its throw.
            var center = venting.GlobalTransform * PropaneTank.LocalCenter;
            motor.Face(player, center, dt);
            motor.Intent.Aim = true;
            if (BotMotor.OnTarget(world, player, venting)) motor.Tap();
            status.Set("clearing a venting tank from the zone");
        }
        else if (!inZone)
        {
            fightTime = 0;
            motor.Face(player, spot + standOffset + Vector3.Up * 1.4f, dt);
            motor.MoveTo(spot + standOffset, run: true);
            status.Set("heading for zone {0} {1:0.0} m away", zone, BotMotor.Flat(spot - here).Length());
        }
        else
        {
            // Holding: watch the nearest challenger and shoot one that comes close with a bank to spill.
            var watch = Nearest(world, here);
            var threat = watch != null && watch.GlobalPosition.DistanceTo(here) < 22f && world.Scores.GetValueOrDefault(watch.ActorId) > 0;
            if (watch != watched || look)
            {
                watched = watch;
                watchedVisible = threat && BotMotor.CanSee(world, player, BotMotor.AimPoint(watch!));
            }
            if (threat && watchedVisible)
            {
                Shoot(player, watch!, tuning, dt);
                status.Set("holding, shooting {0}", watch!.ActorId);
            }
            else if (Prize(world, player, tuning, dt) is { } target)
            {
                // Nothing to fight: bank the nearest tank in sight from where it stands, never one close enough to throw it.
                var center = target.GlobalTransform * PropaneTank.LocalCenter;
                motor.Face(player, center, dt);
                motor.Intent.Aim = true;
                if (BotMotor.OnTarget(world, player, target)) motor.Tap();
                status.Set("holding, shooting a {4} tank", word: BotStatus.Name(target.State));
            }
            else
            {
                fightTime = 0;
                if (watch != null) motor.Face(player, BotMotor.AimPoint(watch), dt);
                if (BotMotor.Flat(spot + standOffset - here).Length() > 1f) motor.MoveTo(spot + standOffset);
                status.Set(state.Holder == player.ActorId ? "holding the zone" : "in the zone");
            }
        }
        motor.Navigate(world, dt);
        if (motor.Failed) PickStand(state, world, spot);
    }

    /// <summary>Stop buying a permanent contest: fight from the perimeter, then make independently timed raids.</summary>
    private bool BreakCrowd(IBotWorld world, KingBotView state, PlayerCharacter player, Vector3 spot, float radius,
        Core.Tuning tuning, float dt, bool look)
    {
        if (world.RemoteBodies.Count + 1 < tuning.BotKingCrowdPlayers || tuning.BotKingRaidMax <= 0) return false;
        var here = player.GlobalPosition;
        crowdTimer -= dt;
        if (crowdTimer <= 0)
        {
            crowdTimer = tuning.BotKingCrowdInterval;
            crowdRivals = 0;
            firstToZone = true;
            var distance = BotMotor.Flat(here - spot).LengthSquared();
            var bodies = world.RemoteBodies;
            for (var i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                if (body.IsRagdolled) continue;
                var otherDistance = BotMotor.Flat(body.GlobalPosition - spot).LengthSquared();
                if (otherDistance <= radius * radius && Mathf.Abs(body.GlobalPosition.Y - spot.Y) <= tuning.KingZoneHeight) crowdRivals++;
                if (otherDistance < distance || otherDistance == distance && body.ActorId < player.ActorId) firstToZone = false;
            }
        }
        var inZone = BotMotor.Flat(here - spot).LengthSquared() <= radius * radius;
        if (contestPatience <= 0)
            contestPatience = rng.RandfRange(tuning.BotKingContestMin, Mathf.Max(tuning.BotKingContestMin, tuning.BotKingContestMax));
        // A prolonged shove in the middle buys no income. Break off on an independent clock, fight/bank from
        // outside, then raid again. The first challenger to an empty zone pounces instead of waiting for a timer.
        if (raidTime > 0)
        {
            raidTime -= dt;
            if (crowdRivals == 0 && firstToZone) raidTime = 0;
            if (raidTime > 0)
            {
                Regroup(world, player, spot, radius, tuning, dt, look, siege: true);
                return true;
            }
            raidCommitted = true;
        }
        contestedTime = inZone && state.Holder != player.ActorId && crowdRivals > 0 ? contestedTime + dt : 0;
        var edge = radius + tuning.BotTankClearance * 2;
        var waitAtEdge = !inZone && !raidCommitted && crowdRivals > 0 && BotMotor.Flat(here - spot).LengthSquared() <= edge * edge;
        if (waitAtEdge || contestedTime > 0 && contestedTime >= contestPatience)
        {
            raidTime = rng.RandfRange(tuning.BotKingRaidMin, Mathf.Max(tuning.BotKingRaidMin, tuning.BotKingRaidMax)) *
                (1 + Mathf.Max(0, world.RemoteBodies.Count + 1 - tuning.BotKingCrowdPlayers) * tuning.BotKingCrowdScale);
            contestedTime = contestPatience = 0;
            raidCommitted = false;
            var away = BotMotor.Flat(here - spot).Normalized();
            if (away == Vector3.Zero) away = Vector3.Right.Rotated(Vector3.Up, rng.RandfRange(0, Mathf.Tau));
            regroupGoal = spot + away * (radius + tuning.BotTankClearance * 2);
            regroupTurn = rng.Randf() < 0.5f ? -1 : 1;
            Regroup(world, player, spot, radius, tuning, dt, look, siege: true);
            return true;
        }

        return false;
    }

    /// <summary>Hard compares banking and spilling in sight against the zone's time-limited mint/denial rate.</summary>
    private bool ChoosePoints(IBotWorld world, KingBotView state, Vector3 spot, PlayerCharacter player, Core.Tuning tuning, float dt)
    {
        pointsTimer -= dt;
        if (pointsTarget != null && (!GodotObject.IsInstanceValid(pointsTarget) ||
            pointsTarget is PropaneTank { State: PropaneTank.TankState.Exploded } or PlayerCharacter { IsRagdolled: true }))
        {
            pointsTimer = 0;
        }

        var here = player.GlobalPosition;
        if (pointsTimer <= 0)
        {
            pointsTimer = tuning.BotPointsInterval;
            points.Begin(world);
            var zoneRate = points.Zone(world, state, spot);
            var protect = zoneRate < 0 && world.MyScore > 0;
            var best = Mathf.Max(0, zoneRate);
            if (pointsTarget == null) best *= 1 + tuning.BotPointsHysteresis;
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
            if (chosen != pointsTarget) { fightTime = pointsShotWait = 0; pointsShot = null; }
            pointsTarget = chosen;
            protect &= chosen == null;
            if (protect && (!protectingPoints || protectZone != state.Zone))
            {
                var away = BotMotor.Flat(here - spot).Normalized();
                if (away == Vector3.Zero) away = Vector3.Right;
                regroupGoal = spot + away * ((float)state.Radius + tuning.BotPointsProtectDistance + tuning.BotTankClearance * 2);
                protectZone = state.Zone;
            }
            protectingPoints = protect;
        }
        if (protectingPoints)
        {
            Regroup(world, player, spot, (float)state.Radius + tuning.BotPointsProtectDistance, tuning, dt, look: true);
            status.Set("points: protecting the bank outside the zone");
            return true;
        }
        switch (pointsTarget)
        {
            case PlayerCharacter enemy:
                if (enemy.IsRagdolled || world.Scores.GetValueOrDefault(enemy.ActorId) <= 0) { pointsTimer = 0; return false; }
                Shoot(player, enemy, tuning, dt);
                motor.Melee(player, enemy.GlobalPosition + Vector3.Up * tuning.MeleeContactHeight);
                // Keep minting when already inside. Outside, close on the valuable bank while firing.
                if (BotMotor.Flat(here - spot).Length() > state.Radius)
                {
                    var gap = BotMotor.Flat(enemy.GlobalPosition - here);
                    if (gap.Length() > BotMotor.MeleeReach(tuning)) motor.MoveTo(enemy.GlobalPosition - gap.Normalized() * BotMotor.MeleeReach(tuning));
                }
                status.Set("points: spilling {0}", enemy.ActorId);
                return true;
            case PropaneTank tank:
                if (tank.Invulnerable) { pointsTimer = 0; return false; }
                motor.Face(player, tank.GlobalTransform * PropaneTank.LocalCenter, dt);
                motor.Intent.Aim = true;
                if (pointsShot != tank) { pointsShot = tank; pointsShotWait = 0; }
                var onTarget = BotMotor.OnTarget(world, player, tank);
                pointsShotWait = onTarget ? 0 : pointsShotWait + dt;
                if (onTarget) motor.Tap();
                if (pointsShotWait >= tuning.BotStuckAimTime)
                {
                    // A clear observation ray can still have no rifle shot (a player, tank or loose fence).
                    // Repeatedly choosing that high-rate prize must not pin Hard outside the zone forever.
                    pointsBlocked[tank.GetInstanceId()] = world.Now + tuning.BotTargetRetryTime;
                    pointsTarget = pointsShot = null;
                    pointsShotWait = pointsTimer = 0;
                    return false;
                }
                status.Set("points: banking a {4} tank", word: BotStatus.Name(tank.State));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Out of the zone for a while after a knockdown: fight or bank from its edge, moving around it.</summary>
    private void Regroup(IBotWorld world, PlayerCharacter player, Vector3 spot, float radius, Core.Tuning tuning, float dt, bool look, bool siege = false)
    {
        var here = player.GlobalPosition;
        var ring = radius + tuning.BotTankClearance * 2;
        if (BotMotor.Flat(regroupGoal - here).Length() < tuning.BotKingRegroupStep * 0.5f)
        {
            // Arrived: the next point along the zone's edge, so the bot keeps moving while it waits.
            var away = BotMotor.Flat(regroupGoal - spot);
            if (away.LengthSquared() < 0.01f) away = Vector3.Right;
            var turn = tuning.BotKingRegroupStep / Mathf.Max(ring, 1f) * regroupTurn;
            regroupGoal = spot + away.Normalized().Rotated(Vector3.Up, turn) * ring;
        }
        var watch = Nearest(world, here);
        var threat = watch != null && watch.GlobalPosition.DistanceTo(here) < 22f && world.Scores.GetValueOrDefault(watch.ActorId) > 0;
        if (watch != watched || look)
        {
            watched = watch;
            watchedVisible = threat && BotMotor.CanSee(world, player, BotMotor.AimPoint(watch!));
        }
        if (threat && watchedVisible)
        {
            Shoot(player, watch!, tuning, dt);
            motor.MoveTo(regroupGoal);
            status.Set(siege ? "siege: shooting {0}" : "regrouping, shooting {0}", watch!.ActorId);
        }
        else if (Prize(world, player, tuning, dt) is { } target)
        {
            var center = target.GlobalTransform * PropaneTank.LocalCenter;
            motor.Face(player, center, dt);
            motor.Intent.Aim = true;
            if (BotMotor.OnTarget(world, player, target)) motor.Tap();
            motor.MoveTo(regroupGoal);
            status.Set(siege ? "siege: shooting a {4} tank" : "regrouping, shooting a {4} tank", word: BotStatus.Name(target.State));
        }
        else
        {
            fightTime = 0;
            motor.Face(player, regroupGoal + Vector3.Up * 1.4f, dt);
            motor.MoveTo(regroupGoal, run: true);
            status.Set(siege ? "siege: circling outside the zone" : "regrouping outside the zone");
        }
    }

    private static bool Intruding(PlayerCharacter? body, Vector3 spot, float radius) =>
        body != null && GodotObject.IsInstanceValid(body) && !body.IsRagdolled && BotMotor.Flat(body.GlobalPosition - spot).Length() <= radius + 1f;

    /// <summary>The nearest upright actor inside the zone (with a step's grace) within reach of this bot.</summary>
    private static PlayerCharacter? NearestIntruder(IBotWorld world, Vector3 spot, float radius, Vector3 here, float within)
    {
        PlayerCharacter? best = null;
        var bestDistance = within;
        var bodies = world.RemoteBodies;
        for (var i = 0; i < bodies.Count; i++)
        {
            var body = bodies[i];
            if (!Intruding(body, spot, radius)) continue;
            var distance = body.GlobalPosition.DistanceTo(here);
            if (distance <= bestDistance) { best = body; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>The nearest intruder in sight: nearer ones first, so it casts only as many rays as it must.</summary>
    private static PlayerCharacter? VisibleIntruder(IBotWorld world, PlayerCharacter player, Vector3 spot, float radius)
    {
        var here = player.GlobalPosition;
        var bodies = world.RemoteBodies;
        var tried = -1f;
        while (true)
        {
            PlayerCharacter? next = null;
            var nextDistance = float.MaxValue;
            for (var i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                var distance = body.GlobalPosition.DistanceTo(here);
                if (distance > tried && distance < nextDistance && Intruding(body, spot, radius)) { next = body; nextDistance = distance; }
            }
            if (next == null || BotMotor.CanSee(world, player, BotMotor.AimPoint(next))) return next;
            tried = nextDistance;
        }
    }

    private static PlayerCharacter? Nearest(IBotWorld world, Vector3 here)
    {
        PlayerCharacter? best = null;
        var bestDistance = float.MaxValue;
        var bodies = world.RemoteBodies;
        for (var i = 0; i < bodies.Count; i++)
        {
            if (bodies[i].IsRagdolled) continue;
            var distance = bodies[i].GlobalPosition.DistanceTo(here);
            if (distance < bestDistance) { best = bodies[i]; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>A venting tank in the zone this bot can see and set off from beyond its throw; chosen when looking.</summary>
    private PropaneTank? Blocker(IBotWorld world, PlayerCharacter player, Vector3 spot, float radius, Core.Tuning tuning, bool look)
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

    /// <summary>The tank to bank, chosen a few times a second: the nearest in sight, nearest first, so few rays.</summary>
    private PropaneTank? Prize(IBotWorld world, PlayerCharacter player, Core.Tuning tuning, float dt)
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

    /// <summary>A point inside the zone, beside its pile and clear of every tank, to hold it from.</summary>
    private void PickStand(KingBotView state, IBotWorld world, Vector3 spot)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var angle = rng.RandfRange(0, Mathf.Tau);
            standOffset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (float)state.Radius * rng.RandfRange(0.4f, 0.8f);
            if (Clear(world, spot + standOffset)) return;
        }
    }

    private static bool Clear(IBotWorld world, Vector3 point)
    {
        var clearance = world.World.Tuning.BotTankClearance + 0.8f;
        var tanks = world.LiveTanks;
        for (var i = 0; i < tanks.Count; i++)
        {
            if (BotMotor.Flat(tanks[i].GlobalPosition - point).Length() <= clearance) return false;
        }
        return true;
    }

    private void Shoot(PlayerCharacter player, PlayerCharacter target, Core.Tuning tuning, float dt)
    {
        fightTime += dt;
        burstClock += dt;
        var error = Mathf.Lerp(tuning.BotAimErrorEasy, tuning.BotAimErrorHard, Skill) * Mathf.Lerp(1.6f, 0.55f, Mathf.Clamp(fightTime / 1.2f, 0, 1));
        var wobble = new Vector3(Mathf.Sin(burstClock * 1.7f), 0.6f * Mathf.Sin(burstClock * 2.3f + 1.1f), Mathf.Cos(burstClock * 1.9f)) * error;
        var aim = BotMotor.AimPoint(target) + wobble;
        motor.Face(player, aim, dt);
        motor.Intent.Aim = target.GlobalPosition.DistanceTo(player.GlobalPosition) > 6f;
        var off = Mathf.RadToDeg((-player.Aim.EyeTransform.Basis.Z).AngleTo(aim - player.Aim.EyeOrigin));
        var reacted = fightTime > Mathf.Lerp(tuning.BotReactionEasy, tuning.BotReactionHard, Skill);
        if (reacted && off < 2.5f && Mathf.PosMod(burstClock, 0.65f) < 0.35f)
        {
            motor.Intent.FireHeld = true;
            motor.Intent.FirePressed = true;
        }
    }

    private static Vector3 ToVector(ModeSpot spot) => new((float)spot.X, (float)spot.Y, (float)spot.Z);
}

/// <summary>
/// Only the zone facts a King brain needs, as a value: reading local rules or advancing a received snapshot's
/// clocks must not construct a snapshot (and its minted roster) for every brain every physics tick.
/// </summary>
public readonly record struct KingBotView(int Zone, ModeSpot Spot, ModeSpot Next, double Radius, int Holder,
    bool Warning, double UntilMove)
{
    public static KingBotView? Read(KingRules rules, ModeClock clock)
    {
        if (rules.Zone < 0) return null;
        var elapsed = rules.Elapsed(clock);
        return new KingBotView(rules.Zone, rules.SpotOf(rules.Zone), rules.SpotOf(rules.Zone + 1), rules.Radius,
            rules.Holder, rules.Warning(elapsed), rules.UntilMove(elapsed));
    }

    public static KingBotView Read(KingState state, double elapsed) =>
        new(state.Zone, state.Spot, state.Next, state.Radius, state.Holder,
            elapsed >= state.MoveAt - state.WarningSeconds, System.Math.Max(0, state.MoveAt - elapsed));
}
