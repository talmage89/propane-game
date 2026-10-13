using System.Collections.Generic;
using System.Linq;
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
/// </summary>
public sealed class KingBot : IBotGoalPolicy
{
    private readonly RandomNumberGenerator rng = new();
    private readonly BotMotor motor = new();
    private int standZone = -1;
    private Vector3 standOffset;
    private float fightTime;
    private float burstClock;
    private PropaneTank? prize;
    private float prizeTimer;

    public string Status { get; private set; } = "";
    public float Skill { get => motor.Skill; set => motor.Skill = value; }

    public KingBot(ulong? seed = null)
    {
        if (seed is { } value) rng.Seed = value;
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
        if (!world.IsPlaying || world.Rules is not KingRules rules || player.IsRagdolled || rules.Zone < 0)
        {
            motor.Reset();
            Status = player.IsRagdolled ? "down" : "waiting";
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
            PickStand(rules);
        }
        var radius = (float)rules.Radius;

        if (BotMotor.Danger(player, world.LiveTanks) is { } danger)
        {
            var away = here + BotMotor.Flat(here - danger.GlobalPosition).Normalized() * 6f;
            motor.Face(player, away + Vector3.Up * 1.4f, dt);
            motor.MoveTo(away, run: true);
            Status = "clearing a venting tank";
            motor.Navigate(player, world.LiveTanks, dt);
            return;
        }

        var inZone = BotMotor.Flat(here - spot).Length() <= radius * 0.85f;
        var intruders = world.RemoteBodies.Where(b => !b.IsRagdolled && BotMotor.Flat(b.GlobalPosition - spot).Length() <= radius + 1f)
            .OrderBy(b => b.GlobalPosition.DistanceTo(here)).ToArray();
        var reach = BotMotor.MeleeReach(tuning);
        var close = intruders.FirstOrDefault(b => b.GlobalPosition.DistanceTo(here) <= reach + 0.4f);
        if (close != null)
        {
            motor.Face(player, BotMotor.AimPoint(close), dt);
            motor.Melee(player, close.GlobalPosition + Vector3.Up * tuning.MeleeContactHeight);
            motor.Intent.Move = new Vector2(0, 0.6f); // Into them: their own spot is never a standable goal.
            Status = $"shoving {close.ActorId} out of the zone";
        }
        else if (inZone && intruders.FirstOrDefault(b => BotMotor.CanSee(player, BotMotor.AimPoint(b))) is { } rival)
        {
            Shoot(player, rival, tuning, dt);
            var gap = BotMotor.Flat(rival.GlobalPosition - here);
            if (gap.Length() > 2.5f) motor.MoveTo(rival.GlobalPosition - gap.Normalized() * 1.5f);
            Status = $"contesting {rival.ActorId}";
        }
        else if (!inZone)
        {
            fightTime = 0;
            motor.Face(player, spot + standOffset + Vector3.Up * 1.4f, dt);
            motor.MoveTo(spot + standOffset, run: true);
            Status = $"heading for zone {zone} {BotMotor.Flat(spot - here).Length():0.0} m away";
        }
        else
        {
            // Holding: watch the nearest challenger and shoot one that comes close with a bank to spill.
            var watch = world.RemoteBodies.Where(b => !b.IsRagdolled).OrderBy(b => b.GlobalPosition.DistanceTo(here)).FirstOrDefault();
            if (watch != null && watch.GlobalPosition.DistanceTo(here) < 22f && world.Scores.GetValueOrDefault(watch.ActorId) > 0 &&
                BotMotor.CanSee(player, BotMotor.AimPoint(watch)))
            {
                Shoot(player, watch, tuning, dt);
                Status = $"holding, shooting {watch.ActorId}";
            }
            else if (Prize(world, player, tuning, dt) is { } prize)
            {
                // Nothing to fight: bank the nearest tank in sight from where it stands, never one close enough to throw it.
                var center = prize.GlobalTransform * PropaneTank.LocalCenter;
                motor.Face(player, center, dt);
                motor.Intent.Aim = true;
                if (BotMotor.OnTarget(player, prize)) motor.Tap();
                Status = $"holding, shooting a {prize.State} tank";
            }
            else
            {
                fightTime = 0;
                if (watch != null) motor.Face(player, BotMotor.AimPoint(watch), dt);
                if (BotMotor.Flat(spot + standOffset - here).Length() > 1f) motor.MoveTo(spot + standOffset);
                Status = rules.Holder == player.ActorId ? "holding the zone" : "in the zone";
            }
        }
        motor.Navigate(player, world.LiveTanks, dt);
        if (motor.Failed) PickStand(rules);
    }

    /// <summary>The tank to bank, chosen a few times a second: each choice casts a ray to every candidate.</summary>
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
        return prize = world.LiveTanks.Where(t => !t.Invulnerable && t.GlobalPosition.DistanceTo(here) is var d && d > safe && d < 25f &&
                world.RemoteBodies.All(b => b.GlobalPosition.DistanceTo(t.GlobalPosition) > 1f) &&
                BotMotor.CanSee(player, t.GlobalTransform * PropaneTank.LocalCenter))
            .OrderBy(t => t.GlobalPosition.DistanceTo(here)).FirstOrDefault();
    }

    private void PickStand(KingRules rules)
    {
        var angle = rng.RandfRange(0, Mathf.Tau);
        standOffset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (float)rules.Radius * rng.RandfRange(0.45f, 0.75f);
    }

    private void Shoot(PlayerCharacter player, PlayerCharacter rival, Core.Tuning tuning, float dt)
    {
        fightTime += dt;
        burstClock += dt;
        var error = Mathf.Lerp(tuning.BotAimErrorEasy, tuning.BotAimErrorHard, Skill) * Mathf.Lerp(1.6f, 0.55f, Mathf.Clamp(fightTime / 1.2f, 0, 1));
        var wobble = new Vector3(Mathf.Sin(burstClock * 1.7f), 0.6f * Mathf.Sin(burstClock * 2.3f + 1.1f), Mathf.Cos(burstClock * 1.9f)) * error;
        var aim = BotMotor.AimPoint(rival) + wobble;
        motor.Face(player, aim, dt);
        motor.Intent.Aim = rival.GlobalPosition.DistanceTo(player.GlobalPosition) > 6f;
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
