using System.Collections.Generic;
using Godot;
using Propane.Player;

namespace Propane.Bots;

/// <summary>
/// One King brain's stand, crowd, rival/watch and recovery perimeter state. All timers use Think's physics delta;
/// lifetime ends with the brain.
/// </summary>
internal sealed class KingZoneTactics
{
    private int standZone = -1;
    private Vector3 standOffset;
    private float standCheck;
    private PlayerCharacter? rival;
    private PlayerCharacter? watched;
    private bool retiredWatched;
    private bool watchedVisible;
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
    public Vector3 StandOffset => standOffset;
    public PlayerCharacter? Rival => rival;
    public void SetProtectGoal(Vector3 goal) => regroupGoal = goal;

    public void ForgetActor(PlayerCharacter actor)
    {
        if (ReferenceEquals(rival, actor)) rival = null;
        if (ReferenceEquals(watched, actor)) { watched = null; retiredWatched = true; }
    }

    public void Waiting()
    {
        wasDown = false;
        regroupTime = raidTime = contestedTime = contestPatience = 0;
        crowdTimer = 0;
        raidCommitted = false;
    }

    public void Down() => wasDown = true;

    public bool Recover(IBotWorld world, KingBotView state, Vector3 here, Vector3 spot, Core.Tuning tuning, RandomNumberGenerator rng)
    {
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
                return true;
            }
        }
        return false;
    }

    public void UpdateStand(KingBotView state, IBotWorld world, int zone, Vector3 spot, float dt, RandomNumberGenerator rng)
    {
        if (zone != standZone)
        {
            // A spot sits on its pile, which nobody can stand in: hold the zone from beside it.
            standZone = zone;
            contestedTime = contestPatience = raidTime = crowdTimer = 0;
            raidCommitted = false;
            PickStand(state, world, spot, rng);
        }
        standCheck -= dt;
        if (standCheck <= 0)
        {
            // Piles move and new ones materialize: a stand point a tank has rolled onto is no longer reachable.
            standCheck = 0.5f;
            if (!Clear(world, spot + standOffset)) PickStand(state, world, spot, rng);
        }
    }

    public bool AdvanceRegroup(float dt)
    {
        if (regroupTime <= 0) return false;
        regroupTime -= dt;
        return true;
    }

    public Vector3 StepRegroup(Vector3 here, Vector3 spot, float radius, Core.Tuning tuning)
    {
        var ring = radius + tuning.BotTankClearance * 2;
        if (BotMotor.Flat(regroupGoal - here).Length() < tuning.BotKingRegroupStep * 0.5f)
        {
            // Arrived: the next point along the zone's edge, so the bot keeps moving while it waits.
            var away = BotMotor.Flat(regroupGoal - spot);
            if (away.LengthSquared() < 0.01f) away = Vector3.Right;
            var turn = tuning.BotKingRegroupStep / Mathf.Max(ring, 1f) * regroupTurn;
            regroupGoal = spot + away.Normalized().Rotated(Vector3.Up, turn) * ring;
        }
        return regroupGoal;
    }

    public PlayerCharacter? ObserveIntruders(IBotWorld world, PlayerCharacter player, Vector3 here, Vector3 spot, float radius, float reach, bool inZone, bool look)
    {
        var close = NearestIntruder(world, spot, radius, here, reach + 0.4f);
        if (!Intruding(rival, spot, radius)) rival = null;
        if (close == null && inZone && look) rival = VisibleIntruder(world, player, spot, radius);
        return close;
    }

    public PlayerCharacter? Watch(IBotWorld world, PlayerCharacter player, Vector3 here, bool look, out bool visibleThreat)
    {
        var watch = Nearest(world, here);
        var threat = watch != null && watch.GlobalPosition.DistanceTo(here) < 22f && world.Scores.GetValueOrDefault(watch.ActorId) > 0;
        if (retiredWatched || watch != watched || look)
        {
            retiredWatched = false;
            watched = watch;
            watchedVisible = threat && BotMotor.CanSee(world, player, BotMotor.AimPoint(watch!));
        }
        visibleThreat = threat && watchedVisible;
        return watch;
    }

    /// <summary>Stop buying a permanent contest: fight from the perimeter, then make independently timed raids.</summary>
    public bool BreakCrowd(IBotWorld world, KingBotView state, PlayerCharacter player, Vector3 spot, float radius,
        Core.Tuning tuning, float dt, RandomNumberGenerator rng)
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
            return true;
        }

        return false;
    }

    /// <summary>A point inside the zone, beside its pile and clear of every tank, to hold it from.</summary>
    public void PickStand(KingBotView state, IBotWorld world, Vector3 spot, RandomNumberGenerator rng)
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
}
