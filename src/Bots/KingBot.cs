using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Modes;
using Propane.Player;

namespace Propane.Bots;

/// <summary>
/// King of the Cul-de-sac's goal policy, kept simple for the prototype: get into the zone and hold it; shove anyone
/// else inside out with melee, and shoot intruders it can see (a hit spills their bank); head for the next spot
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
            standZone = zone;
            var angle = rng.RandfRange(0, Mathf.Tau);
            standOffset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (float)rules.Radius * rng.RandfRange(0.1f, 0.45f);
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
            motor.MoveTo(close.GlobalPosition);
            Status = $"shoving {close.ActorId} out of the zone";
        }
        else if (inZone && intruders.FirstOrDefault(b => BotMotor.CanSee(player, BotMotor.AimPoint(b))) is { } rival)
        {
            Shoot(player, rival, tuning, dt);
            motor.MoveTo(rival.GlobalPosition);
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
            else
            {
                fightTime = 0;
                if (watch != null) motor.Face(player, BotMotor.AimPoint(watch), dt);
                if (BotMotor.Flat(spot + standOffset - here).Length() > 1f) motor.MoveTo(spot + standOffset);
                Status = rules.Holder == player.ActorId ? "holding the zone" : "in the zone";
            }
        }
        motor.Navigate(player, world.LiveTanks, dt);
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
