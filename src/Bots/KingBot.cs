using System.Collections.Generic;
using Godot;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// King of the Cul-de-sac's goal policy, kept simple for the prototype: get into the zone and hold it; shove anyone
/// else inside out with melee, and shoot intruders it can see (a hit spills their bank); with nobody to fight, shoot
/// tanks in sight from a safe distance to bank them; head for the next spot
/// when the zone is about to move and it could not otherwise make it in time. It reads the zone from the rules.
/// Sight lines are asked a few times a second, not every tick.
/// </summary>
public sealed class KingBot : IBotGoalPolicy
{

    private readonly RandomNumberGenerator rng = new();
    private readonly BotMotor motor = new();
    private readonly BotStatus status = new();
    private readonly List<PropaneTank> candidates = new(256);
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

    public string Status => status.ToString();
    public string NavigationDiagnostics => motor.NavigationDiagnostics;
    public float Skill { get => motor.Skill; set => motor.Skill = value; }

    public KingBot(ulong? seed = null)
    {
        if (seed is { } value) rng.Seed = value;
        // Bots that start together look around on different ticks.
        sightTimer = rng.Randf();
    }

    public void Dispose() => rng.Dispose();

    public void Prepare(IBotWorld world) => motor.Prepare(world);

    public PlayerIntent Read() => motor.Read();

    public void Think(IBotWorld world, float dt)
    {
        motor.Begin(dt);
        var player = world.Player;
        if (!world.IsPlaying || world.Rules is not KingRules rules || player.IsRagdolled || rules.Zone < 0)
        {
            motor.Reset();
            status.Set(player.IsRagdolled ? "down" : "waiting");
            return;
        }
        var tuning = world.World.Tuning;
        var here = player.GlobalPosition;
        var elapsed = rules.Elapsed(world.Clock);
        var zone = rules.Zone;
        var spot = ToVector(rules.SpotOf(zone));
        // Leave early for the next spot only when the walk would otherwise outlast the warning.
        var next = ToVector(rules.SpotOf(zone + 1));
        if (rules.Warning(elapsed) && rules.UntilMove(elapsed) < BotMotor.Flat(next - here).Length() / tuning.SprintSpeed + 0.5)
        {
            zone++;
            spot = next;
        }
        if (zone != standZone)
        {
            // A spot sits on its pile, which nobody can stand in: hold the zone from beside it.
            standZone = zone;
            PickStand(rules, world, spot);
        }
        standCheck -= dt;
        if (standCheck <= 0)
        {
            // Piles move and new ones materialize: a stand point a tank has rolled onto is no longer reachable.
            standCheck = 0.5f;
            if (!Clear(world, spot + standOffset)) PickStand(rules, world, spot);
        }
        var radius = (float)rules.Radius;

        if (BotMotor.Danger(world, player) is { } danger)
        {
            var away = here + BotMotor.Flat(here - danger.GlobalPosition).Normalized() * 6f;
            motor.Face(player, away + Vector3.Up * 1.4f, dt);
            motor.MoveTo(away, run: true);
            status.Set("clearing a venting tank");
            motor.Navigate(world, dt);
            return;
        }

        var inZone = BotMotor.Flat(here - spot).Length() <= radius * 0.85f;
        if (!sightStarted) { sightTimer *= tuning.BotSightInterval; sightStarted = true; }
        sightTimer -= dt;
        var look = sightTimer <= 0;
        if (look) sightTimer = tuning.BotSightInterval;
        var reach = BotMotor.MeleeReach(tuning);
        var close = NearestIntruder(world, spot, radius, here, reach + 0.4f);
        if (close == null && inZone && (look || !Intruding(rival, spot, radius))) rival = VisibleIntruder(world, player, spot, radius);
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
                status.Set(rules.Holder == player.ActorId ? "holding the zone" : "in the zone");
            }
        }
        motor.Navigate(world, dt);
        if (motor.Failed) PickStand(rules, world, spot);
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
        if (prize != null && GodotObject.IsInstanceValid(prize) && prize.State != PropaneTank.TankState.Exploded && prizeTimer > 0 &&
            prize.GlobalPosition.DistanceTo(here) > safe)
        {
            return prize;
        }
        prizeTimer = 0.4f;
        candidates.Clear();
        var tanks = world.LiveTanks;
        var bodies = world.RemoteBodies;
        for (var i = 0; i < tanks.Count; i++)
        {
            var t = tanks[i];
            var d = t.GlobalPosition.DistanceTo(here);
            if (t.Invulnerable || d <= safe || d >= 25f) continue;
            var crowded = false;
            for (var j = 0; j < bodies.Count && !crowded; j++) crowded = bodies[j].GlobalPosition.DistanceTo(t.GlobalPosition) <= 1f;
            if (!crowded) candidates.Add(t);
        }
        prize = null;
        while (prize == null && candidates.Count > 0)
        {
            var nearest = 0;
            for (var i = 1; i < candidates.Count; i++)
            {
                if (candidates[i].GlobalPosition.DistanceTo(here) < candidates[nearest].GlobalPosition.DistanceTo(here)) nearest = i;
            }
            var t = candidates[nearest];
            candidates.RemoveAt(nearest);
            if (BotMotor.CanSee(world, player, t.GlobalTransform * PropaneTank.LocalCenter)) prize = t;
        }
        return prize;
    }

    /// <summary>A point inside the zone, beside its pile and clear of every tank, to hold it from.</summary>
    private void PickStand(KingRules rules, IBotWorld world, Vector3 spot)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var angle = rng.RandfRange(0, Mathf.Tau);
            standOffset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (float)rules.Radius * rng.RandfRange(0.4f, 0.8f);
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
