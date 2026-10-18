using System.Collections.Generic;
using Godot;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// Short Fuse's goal policy, kept simple for the prototype. A thrown player is helpless, so: shoot a downed opponent
/// in sight first; set off a pile beside a standing opponent from beyond its throw (a blast sets up the finish); chip
/// at whoever else is in sight; otherwise hunt the nearest. It steps away from venting tanks, and while under fire it
/// will not stand next to a pile, where one shot could throw it. It reads plain health and life observations.
/// Whether its quarry is in sight is asked a few times a second, not every tick.
/// </summary>
public sealed class ShortFuseBot : IBotGoalPolicy
{
    private const float RetargetInterval = 0.4f;
    private const float FightRange = 38f;
    private const float UnderFire = 2.5f;

    private readonly RandomNumberGenerator rng = new();
    private readonly BotMotor motor = new();
    private readonly BotStatus status = new();
    private readonly List<PropaneTank> candidates = new(256);
    private readonly float blastAppetite;
    private PlayerCharacter? quarry;
    private PropaneTank? charge;
    private float chargeTime;
    private float retarget;
    private float strafe = 1f;
    private float strafeTime;
    private float detour;
    private float detourTime;
    private float closest = float.MaxValue;
    private float noProgress;
    private PlayerCharacter? seen;
    private bool seenVisible;
    private float sightTimer;

    public string Status => status.ToString();
    public string StatusKind => status.Format;
    public string NavigationDiagnostics => motor.NavigationDiagnostics;
    public float Skill { get => motor.Skill; set => motor.Skill = value; }

    public ShortFuseBot(ulong? seed = null)
    {
        if (seed is { } value) rng.Seed = value;
        blastAppetite = rng.RandfRange(0.4f, 1f);
        retarget = rng.Randf() * RetargetInterval; // Bots that start together choose on different ticks.
    }

    public void Dispose() => rng.Dispose();

    public void Prepare(IBotWorld world) => motor.Prepare(world);

    public PlayerIntent Read() => motor.Read();

    public void Think(IBotWorld world, float dt)
    {
        motor.Begin(dt);
        var player = world.Player;
        if (!world.IsPlaying || world.ShortFuseMaxHealth is not { } maxHealth || !world.RoundAlive(player.ActorId) || player.IsRagdolled)
        {
            motor.Reset();
            status.Set(player.IsRagdolled ? "down" : world.IsPlaying ? "dead" : "waiting");
            return;
        }
        var tuning = world.World.Tuning;
        var here = player.GlobalPosition;
        var throwRadius = tuning.RagdollRadius;

        // 1. A venting tank close by goes up any moment: step away from it.
        if (BotMotor.Danger(world, player) is { } danger)
        {
            var to = here + BotMotor.Flat(here - danger.GlobalPosition).Normalized() * 6f;
            motor.Face(player, to + Vector3.Up * 1.4f, dt);
            motor.MoveTo(to, run: true);
            status.Set("clearing a venting tank");
            motor.Navigate(world, dt);
            return;
        }

        // 2. Under fire beside a pile: one shot into it throws this bot. Get clear of it first.
        var hit = world.Now - world.LastHitTime < UnderFire;
        var pile = hit ? NearestTank(world, here, throwRadius * 0.8f) : null;
        if (pile != null)
        {
            var to = here + BotMotor.Flat(here - pile.GlobalPosition).Normalized() * (throwRadius + 1.5f);
            motor.Face(player, to + Vector3.Up * 1.4f, dt);
            motor.MoveTo(to, run: true);
            status.Set("leaving a pile under fire");
            motor.Navigate(world, dt);
            return;
        }

        // 3. Whom to fight: a downed opponent in sight first (helpless), then the weakest and nearest.
        retarget -= dt;
        var fresh = retarget <= 0;
        if (fresh || quarry == null || !GodotObject.IsInstanceValid(quarry) || !world.RoundAlive(quarry.ActorId))
        {
            retarget = RetargetInterval;
            fresh = true;
            var previous = quarry;
            quarry = null;
            var best = float.MaxValue;
            var bodies = world.RemoteBodies;
            for (var i = 0; i < bodies.Count; i++)
            {
                if (!world.RoundAlive(bodies[i].ActorId)) continue;
                var priority = Priority(maxHealth, world, player, bodies[i]);
                if (priority < best) { best = priority; quarry = bodies[i]; }
            }
            if (quarry != previous) closest = float.MaxValue;
        }
        if (quarry == null)
        {
            status.Set("alone");
            motor.Navigate(world, dt);
            return;
        }
        var target = BotMotor.AimPoint(quarry);
        var distance = here.DistanceTo(target);
        sightTimer -= dt;
        if (quarry != seen || sightTimer <= 0)
        {
            sightTimer = tuning.BotSightInterval;
            seen = quarry;
            seenVisible = distance < FightRange && BotMotor.CanSee(world, player, target);
        }
        var visible = distance < FightRange && seenVisible;

        // 4. Finish a downed opponent.
        if (quarry.IsRagdolled && visible)
        {
            Shoot(world, player, quarry, dt);
            status.Set("finishing {0} at {1:0} m", quarry.ActorId, distance);
            Approach(quarry, distance, close: 12f);
            motor.Navigate(world, dt);
            return;
        }

        // 5. A pile beside a standing opponent: set it off from beyond its throw. Once chosen it keeps at it a moment.
        chargeTime -= dt;
        if (charge != null && (!GodotObject.IsInstanceValid(charge) || charge.State == PropaneTank.TankState.Exploded || chargeTime <= 0 ||
            charge.GlobalPosition.DistanceTo(here) < throwRadius + 1f))
        {
            charge = null;
        }
        if (charge == null && fresh && rng.Randf() < blastAppetite)
        {
            charge = Charge(world, player, quarry.GlobalPosition, throwRadius);
            chargeTime = 2.2f;
        }
        if (charge != null)
        {
            motor.Face(player, charge.GlobalTransform * PropaneTank.LocalCenter, dt);
            motor.Intent.Aim = true;
            if (BotMotor.OnTarget(world, player, charge)) motor.Tap();
            status.Set("shooting a {4} tank by {0}", quarry.ActorId, word: BotStatus.Name(charge.State));
            motor.Navigate(world, dt);
            return;
        }

        // 6. Chip at them in sight, strafing; otherwise close in, round whatever hides them.
        if (visible)
        {
            Shoot(world, player, quarry, dt);
            strafeTime -= dt;
            if (strafeTime <= 0) { strafe = rng.Randf() < 0.5f ? -1f : 1f; strafeTime = rng.RandfRange(0.8f, 1.8f); }
            motor.Intent.Move = new Vector2(strafe * 0.7f, distance > 20f ? 0.5f : 0f);
            status.Set("shooting {0} at {1:0} m", quarry.ActorId, distance);
        }
        else
        {
            motor.Face(player, target, dt);
            Travel(player, quarry, distance, dt, run: distance > tuning.BotSprintDistance);
            status.Set(detourTime > 0 ? "hunting {0} {1:0} m away on a detour" : "hunting {0} {1:0} m away", quarry.ActorId, distance);
        }
        motor.Navigate(world, dt);
        if (motor.Failed)
        {
            // The route search gave up: try a leg off to one side for a moment, as a person would walk round a block.
            detour = (rng.Randf() < 0.5f ? -1f : 1f) * rng.RandfRange(0.5f, 1.6f);
            detourTime = 2.5f;
        }
    }

    private static PropaneTank? NearestTank(IBotWorld world, Vector3 here, float within)
    {
        PropaneTank? best = null;
        var bestDistance = within;
        var tanks = world.LiveTanks;
        for (var i = 0; i < tanks.Count; i++)
        {
            var distance = tanks[i].GlobalPosition.DistanceTo(here);
            if (distance < bestDistance) { best = tanks[i]; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>
    /// A tank beside the quarry this bot can see and set off from beyond its throw: venting first, then nearest the
    /// quarry, in that order, so it casts only as many rays as it must.
    /// </summary>
    private PropaneTank? Charge(IBotWorld world, PlayerCharacter player, Vector3 quarry, float throwRadius)
    {
        var here = player.GlobalPosition;
        candidates.Clear();
        var tanks = world.LiveTanks;
        for (var i = 0; i < tanks.Count; i++)
        {
            var t = tanks[i];
            if (!t.Invulnerable && t.GlobalPosition.DistanceTo(quarry + Vector3.Up) < throwRadius * 0.9f &&
                t.GlobalPosition.DistanceTo(here) > throwRadius + 2.5f && t.GlobalPosition.DistanceTo(here) < FightRange)
            {
                candidates.Add(t);
            }
        }
        while (candidates.Count > 0)
        {
            var first = 0;
            for (var i = 1; i < candidates.Count; i++)
            {
                if (Before(candidates[i], candidates[first], quarry)) first = i;
            }
            var t = candidates[first];
            candidates.RemoveAt(first);
            if (BotMotor.CanSee(world, player, t.GlobalTransform * PropaneTank.LocalCenter)) return t;
        }
        return null;
    }

    private static bool Before(PropaneTank a, PropaneTank b, Vector3 quarry)
    {
        var aVents = a.State == PropaneTank.TankState.Venting;
        var bVents = b.State == PropaneTank.TankState.Venting;
        if (aVents != bVents) return aVents;
        return a.GlobalPosition.DistanceTo(quarry) < b.GlobalPosition.DistanceTo(quarry);
    }

    /// <summary>
    /// Heads for a hidden quarry in legs the route search can manage: straight at them when near, else a leg of the
    /// way, turned aside while on a detour. Close but out of sight, it circles to a flank to get a line on them.
    /// </summary>
    private void Travel(PlayerCharacter player, PlayerCharacter body, float distance, float dt, bool run)
    {
        detourTime -= dt;
        // Not closing in (each blocked by the same house, say): a leg off to one side breaks the stalemate.
        if (distance < closest - 1f) { closest = distance; noProgress = 0; }
        else if ((noProgress += dt) > 3f && detourTime <= 0)
        {
            detour = (rng.Randf() < 0.5f ? -1f : 1f) * rng.RandfRange(1.1f, 1.9f);
            detourTime = 3f;
            noProgress = 0;
            closest = float.MaxValue;
        }
        var here = player.GlobalPosition;
        var to = BotMotor.Flat(body.GlobalPosition - here);
        if (distance < 12f && detourTime <= 0)
        {
            var side = new Vector3(-to.Z, 0, to.X).Normalized() * strafe;
            motor.MoveTo(body.GlobalPosition + side * 6f, run);
            return;
        }
        var direction = to.Normalized();
        if (detourTime > 0) direction = direction.Rotated(Vector3.Up, detour);
        motor.MoveTo(distance < 22f && detourTime <= 0 ? body.GlobalPosition : here + direction * Mathf.Min(distance, 18f), run);
    }

    /// <summary>Lower is first: a downed body in sight, then low health, then distance.</summary>
    private static float Priority(double maxHealth, IBotWorld world, PlayerCharacter player, PlayerCharacter body)
    {
        var distance = player.GlobalPosition.DistanceTo(body.GlobalPosition);
        var health = (float)(world.ShortFuseHealth(body.ActorId, world.Clock) / System.Math.Max(1, maxHealth));
        var down = body.IsRagdolled && distance < FightRange ? -100f : 0f;
        return down + distance + health * 15f;
    }

    private void Shoot(IBotWorld world, PlayerCharacter player, PlayerCharacter body, float dt)
    {
        motor.Face(player, BotMotor.AimPoint(body), dt);
        motor.Intent.Aim = true;
        if (BotMotor.OnTarget(world, player, body)) motor.Tap();
    }

    /// <summary>Walks in toward a target past <paramref name="close"/> meters, so its shots land.</summary>
    private void Approach(PlayerCharacter body, float distance, float close)
    {
        if (distance > close) motor.MoveTo(body.IsRagdolled ? body.PelvisPosition : body.GlobalPosition);
    }
}
