using System.Linq;
using Godot;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// Ring Out's goal policy, kept simple for the prototype: stay on ground that is not about to dissolve, keeping to
/// the middle; pick the opponent nearest the edge (and near this bot); get to the side away from their nearest edge and
/// shove them toward it with melee; and when that opponent stands by a pile, set the pile off from a safe distance to throw
/// them. It reads the boundary from the rules, as a pure function of round time.
/// </summary>
public sealed class RingOutBot : IBotGoalPolicy
{
    private const float RetargetInterval = 0.5f;
    private const float EdgeMargin = 1.4f;

    private readonly RandomNumberGenerator rng = new();
    private readonly BotMotor motor = new();
    private readonly float blastAppetite;
    private PlayerCharacter? quarry;
    private PropaneTank? charge;
    private float chargeTime;
    private float retarget;

    public string Status { get; private set; } = "";
    public float Skill { get => motor.Skill; set => motor.Skill = value; }

    public RingOutBot(ulong? seed = null)
    {
        if (seed is { } value) rng.Seed = value;
        blastAppetite = rng.RandfRange(0.3f, 1f); // Some bots reach for the rifle sooner than others.
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
        if (!world.IsPlaying || world.Rules is not RingOutRules rules || !rules.IsAlive(player.ActorId) || player.IsRagdolled)
        {
            motor.Reset();
            Status = player.IsRagdolled ? "down" : "out";
            return;
        }
        var plan = rules.Plan;
        var elapsed = rules.Elapsed(world.Clock);
        var tuning = world.World.Tuning;
        var here = player.GlobalPosition;
        // A lot is safe while it is neither warning nor about to: a bot leaves a pulsing lot, never waits on it.
        bool Safe(Vector3 p) => plan.LotAt(p.X, p.Z) is { } lot && plan.Stage(lot, elapsed + 1.0) == LotStage.Solid;
        bool NearEdge(Vector3 p) => Enumerable.Range(0, 8).Any(i =>
            !Safe(p + new Vector3(Mathf.Cos(i * Mathf.Tau / 8), 0, Mathf.Sin(i * Mathf.Tau / 8)) * EdgeMargin));

        // 1. Off safe ground, or at an edge: back toward the middle. Once one lot remains there is nowhere better to
        //    be, so it holds its edge and fights.
        var lastLot = double.IsInfinity(plan.NextDissolve(elapsed));
        if (!Safe(here) || !lastLot && NearEdge(here))
        {
            var refuge = Refuge(plan, elapsed, here);
            motor.Face(player, refuge + Vector3.Up * 1.4f, dt);
            motor.MoveTo(refuge, run: true);
            Status = $"retreating {BotMotor.Flat(refuge - here).Length():0.0} m to safe ground";
            motor.Navigate(player, world.LiveTanks, dt);
            return;
        }

        // 2. A venting tank close by goes up any moment: step away from it, toward the middle.
        if (BotMotor.Danger(player, world.LiveTanks) is { } danger)
        {
            var away = BotMotor.Flat(here - danger.GlobalPosition).Normalized() + BotMotor.Flat(-here).Normalized() * 0.5f;
            var to = here + away.Normalized() * 6f;
            if (!Safe(to)) to = Refuge(plan, elapsed, here);
            motor.Face(player, to + Vector3.Up * 1.4f, dt);
            motor.MoveTo(to, run: true);
            Status = "clearing a venting tank";
            motor.Navigate(player, world.LiveTanks, dt);
            return;
        }

        // 3. Choose whom to push: near the edge and near this bot.
        retarget -= dt;
        var fresh = retarget <= 0;
        if (fresh || quarry == null || !GodotObject.IsInstanceValid(quarry) || !rules.IsAlive(quarry.ActorId))
        {
            retarget = RetargetInterval;
            quarry = world.RemoteBodies.Where(b => rules.IsAlive(b.ActorId))
                .OrderBy(b => b.GlobalPosition.DistanceTo(here) + 1.5f * Edge(plan, elapsed, b.GlobalPosition).Distance)
                .FirstOrDefault();
        }
        if (quarry == null)
        {
            Status = "alone";
            motor.Navigate(player, world.LiveTanks, dt);
            return;
        }
        var target = quarry.IsRagdolled ? quarry.PelvisPosition : quarry.GlobalPosition;

        // 4. A pile beside them: set it off from outside its throw, if this bot has the appetite for it. Once chosen,
        //    it keeps at it for a moment rather than flapping between rifle and shove.
        var blast = tuning.RagdollRadius;
        chargeTime -= dt;
        if (charge != null && (!GodotObject.IsInstanceValid(charge) || charge.State == PropaneTank.TankState.Exploded || chargeTime <= 0 ||
            charge.GlobalPosition.DistanceTo(here) < blast + 1f))
        {
            charge = null;
        }
        if (charge == null && fresh && rng.Randf() < blastAppetite)
        {
            charge = world.LiveTanks.Where(t => !t.Invulnerable && t.GlobalPosition.DistanceTo(target) < blast * 0.9f &&
                    t.GlobalPosition.DistanceTo(here) > blast + 2.5f && t.GlobalPosition.DistanceTo(here) < 30f &&
                    BotMotor.CanSee(player, t.GlobalTransform * PropaneTank.LocalCenter))
                .OrderBy(t => t.GlobalPosition.DistanceTo(target)).FirstOrDefault();
            chargeTime = 1.6f;
        }
        if (charge != null)
        {
            var center = charge.GlobalTransform * PropaneTank.LocalCenter;
            motor.Face(player, center, dt);
            motor.Intent.Aim = true;
            if (BotMotor.OnTarget(player, charge)) motor.Tap();
            Status = $"shooting a {charge.State} tank by {quarry.ActorId}";
            motor.Navigate(player, world.LiveTanks, dt);
            return;
        }

        // 5. Get to the side away from their nearest edge and shove them toward it. Swing from contact range, not
        //    the lunge's limit: a swing that falls short is a free shove for them.
        var (_, edge) = Edge(plan, elapsed, target);
        var behind = target - edge * 1.1f;
        if (!Safe(behind)) behind = target;
        var reach = tuning.MeleeRange + 0.3f;
        motor.Face(player, BotMotor.AimPoint(quarry), dt);
        var toward = BotMotor.Flat(target - here);
        var distance = toward.Length();
        var aligned = distance > 0.01f && toward.Normalized().Dot(edge) > 0.35f;
        if (distance <= reach && (aligned || distance < 0.9f))
        {
            motor.Melee(player, target + Vector3.Up * tuning.MeleeContactHeight);
            motor.Intent.Move = new Vector2(0, 0.6f);
            Status = $"shoving {quarry.ActorId} at {distance:0.0} m";
        }
        else
        {
            motor.MoveTo(aligned || distance > 6f ? target : behind, run: distance > tuning.BotSprintDistance);
            Status = $"closing on {quarry.ActorId} at {distance:0.0} m";
        }
        motor.Navigate(player, world.LiveTanks, dt);
        if (motor.Failed) retarget = 0;
    }

    /// <summary>The nearest ground that is going (or gone) from a point: how far, and which way (sixteen directions).</summary>
    private static (float Distance, Vector3 Direction) Edge(RingOutPlan plan, double elapsed, Vector3 at)
    {
        for (var r = 1f; r < 24f; r += 1f)
        {
            for (var i = 0; i < 16; i++)
            {
                var direction = new Vector3(Mathf.Cos(i * Mathf.Tau / 16), 0, Mathf.Sin(i * Mathf.Tau / 16));
                var p = at + direction * r;
                if (plan.LotAt(p.X, p.Z) is not { } lot || plan.Stage(lot, elapsed + 1.0) != LotStage.Solid) return (r, direction);
            }
        }
        var outward = BotMotor.Flat(at);
        return (24f, outward.LengthSquared() > 0.01f ? outward.Normalized() : Vector3.Forward);
    }

    /// <summary>The nearest lot center that stays solid past the next warning, weighted toward the middle.</summary>
    private static Vector3 Refuge(RingOutPlan plan, double elapsed, Vector3 here)
    {
        var horizon = elapsed + plan.WarningSeconds + 1.5;
        var best = plan.Lots.Where(lot => plan.Stage(lot, horizon) == LotStage.Solid)
            .Select(lot => plan.Center(lot)).Select(c => new Vector3((float)c.X, 0.05f, (float)c.Z))
            .OrderBy(c => BotMotor.Flat(c - here).Length() + 0.4f * BotMotor.Flat(c).Length()).FirstOrDefault();
        return best;
    }
}
