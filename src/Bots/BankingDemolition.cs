using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// One Banking brain's firing runway, blocked-shot progress and reused vantage search. Timers are physics
/// seconds; discarded with the brain.
/// </summary>
internal sealed class BankingDemolition
{
    private readonly Vantage[] vantages = new Vantage[48];
    private float blocked;
    private float unaimed;
    private Vector3 unaimedAnchor;
    private Vector3? vantage;
    private float vantageTimer;

    public readonly record struct Decision(Vector3? Travel = null, bool Aim = false, bool Sprint = false,
        bool Tap = false, bool LeavePile = false, float RetrySeconds = 0);

    public void ResetTarget() { blocked = unaimed = 0; vantage = null; }
    public void ForgetVantage() => vantage = null;

    public Decision Demolish(IBotWorld match, PlayerCharacter player, PropaneTank tank, Vector3 here, Tuning tuning, BankingCombat combat, BotAim aimControl, float skill, float dt, RandomNumberGenerator rng, BotStatus status)
    {
        var center = tank.GlobalTransform * PropaneTank.LocalCenter;
        var distance = Flat(center - here).Length();
        // Setting a tank off needs distance: past its blast, and past the blasts of the pile it sets off. Both shots
        // come from there, in one burst: the first punctures, the next sets it (and its pile) off before it moves.
        var index = match.Services.IndexOf(tank);
        var spread = index >= 0 ? match.Services.PileSpread(index) : 0;
        var safe = tuning.RagdollRadius + 1.2f + spread;
        var near = safe;
        var far = Mathf.Max(near + 8f, 18f);
        var clear = distance >= near && distance <= far + 4f && combat.Sees(match, player, tank, center, tuning);
        if (here.DistanceTo(unaimedAnchor) > 2f)
        {
            unaimedAnchor = here;
            unaimed = 0;
        }

        if (clear && blocked < 1f)
        {
            // In a good spot: stand, shoulder the rifle and fire when the crosshair is on it.
            aimControl.Track(player, center, dt, skill);
            var tap = false;
            var leavePile = false;
            vantage = null;
            status.Set("shooting {4} tank at {1:0.0} m (keeping {2:0.0})", first: distance, second: near, word: BotStatus.Name(tank.State));
            if (tank.Invulnerable)
            {
                return new Decision(Aim: true);
            }
            if (OnTarget(match, player, tank))
            {
                // Single taps: one punctures, the next sets it off. Ammo is scarce.
                tap = true;
                blocked = 0;
                unaimed = 0;
            }
            else
            {
                blocked += dt;
                unaimed += dt;
                if (unaimed >= tuning.BotStuckAimTime)
                {
                    // The vantage search sees past things the shot cannot (a loose fence panel in front of the
                    // rifle), so it can keep choosing this very spot. Leave this pile for a while.
                    leavePile = true;
                    vantage = null;
                    unaimed = 0;
                    status.Set("no clear shot: leaving this pile");
                }
            }
            return new Decision(Aim: true, Tap: tap, LeavePile: leavePile);
        }

        // Somewhere better: a spot at the right distance with a clear view of it, that can be walked to.
        vantageTimer -= dt;
        if (vantage == null || vantageTimer <= 0 &&
            (Flat(vantage.Value - center).Length() < near || Flat(vantage.Value - center).Length() > far + 4f ||
             !VisibleFrom(match, vantage.Value + Vector3.Up * 1.6f, center)))
        {
            vantageTimer = 0.6f;
            vantage = FindVantage(match, player, center, near, far, rng);
            blocked = 0;
            if (vantage == null)
            {
                status.Set("no vantage point");
                return new Decision(RetrySeconds: 10f);
            }
        }
        vantageTimer = Mathf.Max(vantageTimer, 0);
        var to = Flat(vantage.Value - here);
        if (to.Length() < 0.6f)
        {
            // Arrived but still no shot (the tank moved): look again.
            vantage = null;
            aimControl.Track(player, center, dt, skill);
            return default;
        }
        var sprint = false;
        if (distance < 30f && to.Length() < 12f)
        {
            // Close: walk there keeping the tank in view, ready to shoot.
            aimControl.Track(player, center, dt, skill);
        }
        else
        {
            aimControl.Track(player, vantage.Value + Vector3.Up * 1.4f, dt, skill);
            sprint = to.Length() > 8f;
        }
        status.Set("moving to a vantage {1:0.0} m away for a {4} tank at {2:0.0} m (keeping {3:0.0})", first: to.Length(), second: distance,
            third: near, word: BotStatus.Name(tank.State));
        return new Decision(Travel: to, Sprint: sprint);
    }

    private struct Vantage
    {
        public Vector3 Point;
        public float Distance;
    }

    /// <summary>
    /// The nearest open spot between <paramref name="near"/> and <paramref name="far"/> from a target with a clear
    /// view of it, on the ground (not a roof), preferring spots this bot can walk to in a straight line. Spots are
    /// tried nearest first and the search stops once no untried spot could beat the best, so it casts few rays.
    /// </summary>
    private Vector3? FindVantage(IBotWorld match, PlayerCharacter player, Vector3 target, float near, float far, RandomNumberGenerator rng)
    {
        var services = match.Services;
        var here = player.GlobalPosition;
        var turn = rng.RandfRange(0, Mathf.Tau);
        var count = 0;
        for (var ring = 0; ring < 3; ring++)
        {
            var radius = ring == 0 ? near + 1f : ring == 1 ? (near + far) / 2f : far;
            for (var i = 0; i < 16; i++)
            {
                var angle = turn + i * Mathf.Tau / 16f;
                var p = target + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                vantages[count++] = new Vantage { Point = p, Distance = Flat(p - here).Length() };
            }
        }
        // Insertion sort: 48 spots, nearest first.
        for (var i = 1; i < count; i++)
        {
            var item = vantages[i];
            var j = i - 1;
            for (; j >= 0 && vantages[j].Distance > item.Distance; j--) vantages[j + 1] = vantages[j];
            vantages[j + 1] = item;
        }
        Vector3? best = null;
        var bestCost = float.MaxValue;
        for (var i = 0; i < count && vantages[i].Distance < bestCost; i++)
        {
            var p = vantages[i].Point;
            if (!services.Ray(new Vector3(p.X, 4f, p.Z), new Vector3(p.X, -1f, p.Z), Layers.World | Layers.Props | Layers.Floor, out var ground) ||
                ground.Point.Y > 0.6f || ground.Normal.Y < 0.8f)
            {
                continue;
            }
            var spot = ground.Point;
            // Routes keep out of tank clearances and vent throws: a spot inside one can never be reached.
            if (!services.Hazards.Safe(spot) || !services.Clear(spot + Vector3.Up * 1.6f, target, Layers.World | Layers.Props))
            {
                continue;
            }
            var walk = services.Clear(here + Vector3.Up * 0.6f, spot + Vector3.Up * 0.6f, Layers.World | Layers.Props);
            var cost = here.DistanceTo(spot) + (walk ? 0f : 40f);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = spot;
            }
        }
        return best;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
    private static bool OnTarget(IBotWorld world, PlayerCharacter player, Node3D target) => BotMotor.OnTarget(world, player, target);
    private static bool VisibleFrom(IBotWorld world, Vector3 eye, Vector3 point) => world.Services.Clear(eye, point, Layers.World | Layers.Props);
}
