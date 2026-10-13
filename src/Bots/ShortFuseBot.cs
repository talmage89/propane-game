using System.Linq;
using Godot;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// Short Fuse's goal policy, kept simple for the prototype. A thrown player is helpless, so: shoot a downed opponent
/// in sight first; set off a pile beside a standing opponent from beyond its throw (a blast sets up the finish); chip
/// at whoever else is in sight; otherwise hunt the nearest. It steps away from venting tanks, and while under fire it
/// will not stand next to a pile, where one shot could throw it. It reads health and who is alive from the rules.
/// </summary>
public sealed class ShortFuseBot : IBotGoalPolicy
{
    private const float RetargetInterval = 0.4f;
    private const float FightRange = 38f;
    private const float UnderFire = 2.5f;

    private readonly RandomNumberGenerator rng = new();
    private readonly BotMotor motor = new();
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

    public string Status { get; private set; } = "";
    public float Skill { get => motor.Skill; set => motor.Skill = value; }

    public ShortFuseBot(ulong? seed = null)
    {
        if (seed is { } value) rng.Seed = value;
        blastAppetite = rng.RandfRange(0.4f, 1f);
    }

    public void Dispose()
    {
        rng.Dispose();
        motor.Dispose();
    }

    public PlayerIntent Read() => motor.Read();

    public void Think(IBotWorld world, float dt)
    {
        motor.Begin(dt);
        var player = world.Player;
        if (!world.IsPlaying || world.Rules is not ShortFuseRules rules || !rules.IsAlive(player.ActorId) || player.IsRagdolled)
        {
            motor.Reset();
            Status = player.IsRagdolled ? "down" : world.IsPlaying ? "dead" : "waiting";
            return;
        }
        var tuning = world.World.Tuning;
        var here = player.GlobalPosition;
        var throwRadius = tuning.RagdollRadius;

        // 1. A venting tank close by goes up any moment: step away from it.
        if (BotMotor.Danger(player, world.LiveTanks) is { } danger)
        {
            var to = here + BotMotor.Flat(here - danger.GlobalPosition).Normalized() * 6f;
            motor.Face(player, to + Vector3.Up * 1.4f, dt);
            motor.MoveTo(to, run: true);
            Status = "clearing a venting tank";
            motor.Navigate(player, world.LiveTanks, dt);
            return;
        }

        // 2. Under fire beside a pile: one shot into it throws this bot. Get clear of it first.
        var hit = world.Now - world.LastHitTime < UnderFire;
        var pile = world.LiveTanks.Where(t => t.GlobalPosition.DistanceTo(here) < throwRadius * 0.8f)
            .OrderBy(t => t.GlobalPosition.DistanceTo(here)).FirstOrDefault();
        if (hit && pile != null)
        {
            var to = here + BotMotor.Flat(here - pile.GlobalPosition).Normalized() * (throwRadius + 1.5f);
            motor.Face(player, to + Vector3.Up * 1.4f, dt);
            motor.MoveTo(to, run: true);
            Status = "leaving a pile under fire";
            motor.Navigate(player, world.LiveTanks, dt);
            return;
        }

        // 3. Whom to fight: a downed opponent in sight first (helpless), then the weakest and nearest.
        var alive = world.RemoteBodies.Where(b => rules.IsAlive(b.ActorId)).ToArray();
        retarget -= dt;
        var fresh = retarget <= 0;
        if (fresh || quarry == null || !GodotObject.IsInstanceValid(quarry) || !rules.IsAlive(quarry.ActorId))
        {
            retarget = RetargetInterval;
            fresh = true;
            var previous = quarry;
            quarry = alive.OrderBy(b => Priority(rules, world, player, b)).FirstOrDefault();
            if (quarry != previous) closest = float.MaxValue;
        }
        if (quarry == null)
        {
            Status = "alone";
            motor.Navigate(player, world.LiveTanks, dt);
            return;
        }
        var target = BotMotor.AimPoint(quarry);
        var distance = here.DistanceTo(target);
        var visible = distance < FightRange && BotMotor.CanSee(player, target);

        // 4. Finish a downed opponent.
        if (quarry.IsRagdolled && visible)
        {
            Shoot(player, quarry, dt);
            Status = $"finishing {quarry.ActorId} at {distance:0} m";
            Approach(player, quarry, distance, close: 12f);
            motor.Navigate(player, world.LiveTanks, dt);
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
            charge = world.LiveTanks.Where(t => !t.Invulnerable && t.GlobalPosition.DistanceTo(quarry.GlobalPosition + Vector3.Up) < throwRadius * 0.9f &&
                    t.GlobalPosition.DistanceTo(here) > throwRadius + 2.5f && t.GlobalPosition.DistanceTo(here) < FightRange &&
                    BotMotor.CanSee(player, t.GlobalTransform * PropaneTank.LocalCenter))
                .OrderBy(t => t.State == PropaneTank.TankState.Venting ? 0 : 1).ThenBy(t => t.GlobalPosition.DistanceTo(quarry.GlobalPosition))
                .FirstOrDefault();
            chargeTime = 2.2f;
        }
        if (charge != null)
        {
            motor.Face(player, charge.GlobalTransform * PropaneTank.LocalCenter, dt);
            motor.Intent.Aim = true;
            if (BotMotor.OnTarget(player, charge)) motor.Tap();
            Status = $"shooting a {charge.State} tank by {quarry.ActorId}";
            motor.Navigate(player, world.LiveTanks, dt);
            return;
        }

        // 6. Chip at them in sight, strafing; otherwise close in, round whatever hides them.
        if (visible)
        {
            Shoot(player, quarry, dt);
            strafeTime -= dt;
            if (strafeTime <= 0) { strafe = rng.Randf() < 0.5f ? -1f : 1f; strafeTime = rng.RandfRange(0.8f, 1.8f); }
            motor.Intent.Move = new Vector2(strafe * 0.7f, distance > 20f ? 0.5f : 0f);
            Status = $"shooting {quarry.ActorId} at {distance:0} m";
        }
        else
        {
            motor.Face(player, target, dt);
            Travel(player, quarry, distance, dt, run: distance > tuning.BotSprintDistance);
            Status = $"hunting {quarry.ActorId} {distance:0} m away" + (detourTime > 0 ? " on a detour" : "");
        }
        motor.Navigate(player, world.LiveTanks, dt);
        if (motor.Failed)
        {
            // The route search gave up: try a leg off to one side for a moment, as a person would walk round a block.
            detour = (rng.Randf() < 0.5f ? -1f : 1f) * rng.RandfRange(0.5f, 1.6f);
            detourTime = 2.5f;
        }
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
    private static float Priority(ShortFuseRules rules, IBotWorld world, PlayerCharacter player, PlayerCharacter body)
    {
        var distance = player.GlobalPosition.DistanceTo(body.GlobalPosition);
        var health = (float)(rules.Health.Health(body.ActorId, world.Clock.Now) / System.Math.Max(1, rules.MaxHealth));
        var down = body.IsRagdolled && distance < FightRange ? -100f : 0f;
        return down + distance + health * 15f;
    }

    private void Shoot(PlayerCharacter player, PlayerCharacter body, float dt)
    {
        motor.Face(player, BotMotor.AimPoint(body), dt);
        motor.Intent.Aim = true;
        if (BotMotor.OnTarget(player, body)) motor.Tap();
    }

    /// <summary>Walks in toward a target past <paramref name="close"/> meters, so its shots land.</summary>
    private void Approach(PlayerCharacter player, PlayerCharacter body, float distance, float close)
    {
        if (distance > close) motor.MoveTo(body.IsRagdolled ? body.PelvisPosition : body.GlobalPosition);
    }
}
