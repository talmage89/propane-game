using System.Collections.Generic;
using Godot;
using Propane.Modes;
using Propane.Navigation;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// Ring Out's goal policy, kept simple for the prototype: stay on ground that is not about to dissolve, keeping to
/// the middle; pick the opponent nearest the edge (and near this bot); get to the side away from their nearest edge and
/// shove them toward it with melee; and when that opponent stands by a pile, set the pile off from a safe distance to throw
/// them. It reads the boundary from the rules, as a pure function of round time, and never routes over a lot that has
/// gone (the shared navigation lattice remembers the ground as it was).
/// </summary>
public sealed class RingOutBot : IBotGoalPolicy, INavigationArea
{
    private const float RetargetInterval = 0.5f;
    private const float EdgeMargin = 1.4f;

    private readonly RandomNumberGenerator rng = new();
    private readonly BotMotor motor = new();
    private readonly BotStatus status = new();
    private readonly List<PropaneTank> candidates = new(256);
    private readonly List<RingOutLot> lots = new();
    private readonly float blastAppetite;
    private PlayerCharacter? quarry;
    private PropaneTank? charge;
    private float chargeTime;
    private float retarget;
    private RingOutPlan? plan;
    private double elapsed;

    public string Status => status.ToString();
    public string StatusKind => status.Format;
    public string NavigationDiagnostics => motor.NavigationDiagnostics;
    public float Skill { get => motor.Skill; set => motor.Skill = value; }

    public RingOutBot(ulong? seed = null)
    {
        if (seed is { } value) rng.Seed = value;
        blastAppetite = rng.RandfRange(0.3f, 1f); // Some bots reach for the rifle sooner than others.
        retarget = rng.Randf() * RetargetInterval; // Bots that start together choose on different ticks.
    }

    public void Dispose() => rng.Dispose();

    public void Prepare(IBotWorld world) => motor.Prepare(world);

    public PlayerIntent Read() => motor.Read();

    /// <summary>Routes stay on lots that still stand a moment from now.</summary>
    public bool Allows(Vector3 feet) => plan == null || plan.Solid(feet.X, feet.Z, elapsed + 0.25);

    public void Think(IBotWorld world, float dt)
    {
        motor.Begin(dt);
        var player = world.Player;
        if (!world.IsPlaying || world.Rules is not RingOutRules rules || !rules.IsAlive(player.ActorId) || player.IsRagdolled)
        {
            motor.Reset();
            status.Set(player.IsRagdolled ? "down" : "out");
            return;
        }
        if (plan != rules.Plan)
        {
            plan = rules.Plan;
            lots.Clear();
            lots.AddRange(plan.Lots);
        }
        elapsed = rules.Elapsed(world.Clock);
        var tuning = world.World.Tuning;
        var here = player.GlobalPosition;

        // 1. Off safe ground, or at an edge: back toward the middle. Once one lot remains there is nowhere better to
        //    be, so it holds its edge and fights.
        var lastLot = double.IsInfinity(plan.NextDissolve(elapsed));
        if (!Safe(here) || !lastLot && NearEdge(here))
        {
            var refuge = Refuge(here);
            motor.Face(player, refuge + Vector3.Up * 1.4f, dt);
            motor.MoveTo(refuge, run: true);
            status.Set("retreating {1:0.0} m to safe ground", first: BotMotor.Flat(refuge - here).Length());
            motor.Navigate(world, dt, this);
            return;
        }

        // 2. A venting tank close by goes up any moment: step away from it, toward the middle.
        if (BotMotor.Danger(world, player) is { } danger)
        {
            var away = BotMotor.Flat(here - danger.GlobalPosition).Normalized() + BotMotor.Flat(-here).Normalized() * 0.5f;
            var to = here + away.Normalized() * 6f;
            if (!Safe(to)) to = Refuge(here);
            motor.Face(player, to + Vector3.Up * 1.4f, dt);
            motor.MoveTo(to, run: true);
            status.Set("clearing a venting tank");
            motor.Navigate(world, dt, this);
            return;
        }

        // 3. Choose whom to push: near the edge and near this bot.
        retarget -= dt;
        var fresh = retarget <= 0;
        if (fresh || quarry == null || !GodotObject.IsInstanceValid(quarry) || !rules.IsAlive(quarry.ActorId))
        {
            retarget = RetargetInterval;
            quarry = null;
            var best = float.MaxValue;
            var bodies = world.RemoteBodies;
            for (var i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                if (!rules.IsAlive(body.ActorId)) continue;
                var score = body.GlobalPosition.DistanceTo(here) + 1.5f * Edge(plan, elapsed, body.GlobalPosition).Distance;
                if (score < best) { best = score; quarry = body; }
            }
        }
        if (quarry == null)
        {
            status.Set("alone");
            motor.Navigate(world, dt, this);
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
            charge = Charge(world, player, target, blast);
            chargeTime = 1.6f;
        }
        if (charge != null)
        {
            var center = charge.GlobalTransform * PropaneTank.LocalCenter;
            motor.Face(player, center, dt);
            motor.Intent.Aim = true;
            if (BotMotor.OnTarget(world, player, charge)) motor.Tap();
            status.Set("shooting a {4} tank by {0}", quarry.ActorId, word: BotStatus.Name(charge.State));
            motor.Navigate(world, dt, this);
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
            status.Set("shoving {0} at {1:0.0} m", quarry.ActorId, distance);
        }
        else
        {
            motor.MoveTo(aligned || distance > 6f ? target : behind, run: distance > tuning.BotSprintDistance);
            status.Set("closing on {0} at {1:0.0} m", quarry.ActorId, distance);
        }
        motor.Navigate(world, dt, this);
        if (motor.Failed) retarget = 0;
    }

    /// <summary>A lot is safe while it is neither warning nor about to: a bot leaves a pulsing lot, never waits on it.</summary>
    private bool Safe(Vector3 p) => plan!.LotAt(p.X, p.Z) is { } lot && plan.Stage(lot, elapsed + 1.0) == LotStage.Solid;

    private bool NearEdge(Vector3 p)
    {
        for (var i = 0; i < 8; i++)
        {
            if (!Safe(p + new Vector3(Mathf.Cos(i * Mathf.Tau / 8), 0, Mathf.Sin(i * Mathf.Tau / 8)) * EdgeMargin)) return true;
        }
        return false;
    }

    /// <summary>The nearest tank by the target that this bot can see and set off from beyond its throw: nearest first, so few rays.</summary>
    private PropaneTank? Charge(IBotWorld world, PlayerCharacter player, Vector3 target, float blast)
    {
        var here = player.GlobalPosition;
        candidates.Clear();
        var tanks = world.LiveTanks;
        for (var i = 0; i < tanks.Count; i++)
        {
            var t = tanks[i];
            if (!t.Invulnerable && t.GlobalPosition.DistanceTo(target) < blast * 0.9f && t.GlobalPosition.DistanceTo(here) > blast + 2.5f &&
                t.GlobalPosition.DistanceTo(here) < 30f)
            {
                candidates.Add(t);
            }
        }
        while (candidates.Count > 0)
        {
            var nearest = 0;
            for (var i = 1; i < candidates.Count; i++)
            {
                if (candidates[i].GlobalPosition.DistanceTo(target) < candidates[nearest].GlobalPosition.DistanceTo(target)) nearest = i;
            }
            var t = candidates[nearest];
            candidates.RemoveAt(nearest);
            if (BotMotor.CanSee(world, player, t.GlobalTransform * PropaneTank.LocalCenter)) return t;
        }
        return null;
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
    private Vector3 Refuge(Vector3 here)
    {
        var horizon = elapsed + plan!.WarningSeconds + 1.5;
        var best = Vector3.Zero;
        var bestCost = float.MaxValue;
        foreach (var lot in lots)
        {
            if (plan.Stage(lot, horizon) != LotStage.Solid) continue;
            var (x, z) = plan.Center(lot);
            var center = new Vector3((float)x, 0.05f, (float)z);
            var cost = BotMotor.Flat(center - here).Length() + 0.4f * BotMotor.Flat(center).Length();
            if (cost < bestCost) { bestCost = cost; best = center; }
        }
        return best;
    }
}
