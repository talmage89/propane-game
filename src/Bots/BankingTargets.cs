using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// One Banking brain's objectives, retry deadlines and scouting leg. Deadlines use the brain's accumulated
/// physics seconds; discarded with the brain.
/// </summary>
internal sealed class BankingTargets
{
    private const float RetargetInterval = 0.35f;
    private const float PlayerRange = 38f;
    private const float AmmoLow = 15;
    private readonly Dictionary<ulong, float> blacklist = new(1024);
    private readonly List<ulong> expired = new(1024);
    private readonly BotPoints points = new();
    private Node3D? target;
    private bool seekingRival;
    private Vector3? roamGoal;
    private float roamUntil;
    private float retarget;
    public Node3D? Target => target;
    public bool SeekingRival => seekingRival;
    public Vector3? ScoutingGoal => roamGoal;
    public int BlacklistedTargets { get; private set; }

    public void Stagger(RandomNumberGenerator rng) => retarget = rng.Randf() * RetargetInterval;

    // Called only once the higher-priority flee/cover policies have declined the tick.
    public bool Advance(float dt)
    {
        retarget -= dt;
        return retarget <= 0 || target != null && !IsValid(target);
    }

    public bool Select(IBotWorld match, AmmoState ammo, Tuning tuning, float clock, bool protecting, bool pointsDriven, float skill)
    {
        retarget = RetargetInterval;
        var chosen = Choose(match, ammo, tuning, clock, protecting, pointsDriven, skill);
        // Nearly equivalent tanks must not discard a demolition runway on each selection interval.
        if (chosen is PropaneTank nextTank && target is PropaneTank current && IsValid(current) &&
            !blacklist.ContainsKey(current.GetInstanceId()) && (!pointsDriven ||
                nextTank.GlobalPosition.DistanceTo(current.GlobalPosition) <= tuning.ChainRadius)) { chosen = target; }
        if (chosen == target) return false;
        roamGoal = null;
        target = chosen;
        return true;
    }

    public void Retry() => retarget = 0;
    public void Abandon() { target = null; retarget = 0; }
    public void ForgetRoam() => roamGoal = null;

    private Node3D? Choose(IBotWorld match, AmmoState ammo, Tuning tuning, float clock, bool protecting, bool pointsDriven, float skill)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.BotChoose);
        var player = match.Player;
        var here = player.GlobalPosition;
        seekingRival = false;
        expired.Clear();
        foreach (var (key, until) in blacklist)
        {
            if (until < clock) expired.Add(key);
        }
        foreach (var key in expired) blacklist.Remove(key);

        // Ammo: go out of the way for it when low; grab a can on the way when it is close.
        var rounds = ammo.Magazine + ammo.Reserve;
        AmmoPickup? pickup = null;
        var pickups = match.Pickups;
        for (var i = 0; i < pickups.Count; i++)
        {
            var p = pickups[i];
            if (p.Available && !blacklist.ContainsKey(p.GetInstanceId()) &&
                (pickup == null || p.GlobalPosition.DistanceTo(here) < pickup.GlobalPosition.DistanceTo(here)))
            {
                pickup = p;
            }
        }
        if (pickup != null && ammo.Reserve < tuning.MaxReserve &&
            (rounds < AmmoLow || rounds < AmmoLow * 3 && pickup.GlobalPosition.DistanceTo(here) < 12f))
        {
            return pickup;
        }

        Node3D? best = null;
        var bestScore = pointsDriven ? 0 : float.MinValue;
        if (pointsDriven) points.Begin(match);
        // Players worth shooting: in sight, in range, with tanks to spill.
        var bodies = match.RemoteBodies;
        var cleared = tuning.BotHuntWhenEmpty && match.Services.Tanks.Count == 0;
        for (var i = 0; i < bodies.Count; i++)
        {
            var enemy = bodies[i];
            var banked = match.Scores.GetValueOrDefault(enemy.ActorId);
            var distance = enemy.GlobalPosition.DistanceTo(here);
            // A fight costs ammo and invites return fire: worth it for a real bank, for a cheap close one, or
            // against whoever just shot this bot.
            var avenging = enemy.ActorId == match.LastHitBy && match.Now - match.LastHitTime < 3;
            var hunting = cleared && banked > match.MyScore;
            var worth = pointsDriven ? banked > 0 && !enemy.IsRagdolled : protecting ? banked > match.MyScore : banked >= 3 || banked >= 1 && distance < 18f || avenging && banked > 0;
            if (blacklist.ContainsKey(enemy.GetInstanceId()) || !worth && !hunting || distance > PlayerRange && !hunting)
            {
                continue;
            }
            // Worth it against anyone holding more; against smaller banks only when this bot has little to lose.
            var score = pointsDriven ? points.Fight(match, enemy, distance) :
                60f + banked * 6f - distance - (match.MyScore > banked + 2 ? 45f : 0f);
            if (pointsDriven && enemy == target) score *= 1 + tuning.BotPointsHysteresis;
            if (score > bestScore && (hunting || CanSee(match, player, Aim(enemy))))
            {
                bestScore = score;
                best = enemy;
            }
        }

        var services = match.Services;
        var tanks = services.Tanks;
        for (var i = 0; i < tanks.Count; i++)
        {
            var tank = tanks[i];
            var at = tank.GlobalPosition;
            var distance = at.DistanceTo(here);
            if (pointsDriven)
            {
                if (blacklist.ContainsKey(tank.GetInstanceId()) || protecting && Near(bodies, at, 25f, upright: false)) continue;
                var rate = points.Tank(match, tank, services.PileCount(i), distance);
                if (tank == target) rate *= 1 + tuning.BotPointsHysteresis;
                if (rate > bestScore) { bestScore = rate; best = tank; }
                continue;
            }
            if (tank.Invulnerable && distance >= 30f || blacklist.ContainsKey(tank.GetInstanceId()))
            {
                continue;
            }
            if (protecting && Near(bodies, at, 25f, upright: false))
            {
                continue;
            }
            var pile = services.PileCount(i);
            var score = (tank.State == PropaneTank.TankState.Venting ? 25f : 10f) + Mathf.Min(pile, 10) * 6f - distance * 0.7f;
            if (distance < 20f)
            {
                // Close tanks are quick points, piles or not.
                score += 12f;
            }
            // A pile with a rival beside it: setting it off throws them, spilling five.
            for (var j = 0; j < bodies.Count; j++)
            {
                var enemy = bodies[j];
                if (!enemy.IsRagdolled && match.Scores.GetValueOrDefault(enemy.ActorId) > 0 &&
                    enemy.GlobalPosition.DistanceTo(at) < tuning.RagdollRadius)
                {
                    score += 30f;
                }
            }
            if (tank.Invulnerable)
            {
                score -= 8f;
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = tank;
            }
        }
        // Easy often exhausts its useful targets before it earns a bank worth defending. Scout near the
        // nearest upright rival while waiting for someone richer to become visible. This is a fallback:
        // ammo, tanks and profitable fights still win, and the ordinary aim/reaction/navigation limits apply.
        if (best == null && tuning.BotEasySeekRivals && !pointsDriven &&
            BotProfile.Nearest(skill, tuning) == BotDifficulty.Easy)
        {
            if (roamGoal != null && clock < roamUntil && target is PlayerCharacter current &&
                GodotObject.IsInstanceValid(current) && !current.IsRagdolled && !blacklist.ContainsKey(current.GetInstanceId()))
            {
                seekingRival = true;
                return current;
            }
            var nearest = float.MaxValue;
            for (var i = 0; i < bodies.Count; i++)
            {
                var enemy = bodies[i];
                if (enemy.IsRagdolled || blacklist.ContainsKey(enemy.GetInstanceId())) continue;
                var distance = enemy.GlobalPosition.DistanceSquaredTo(here);
                if (distance >= nearest) continue;
                nearest = distance;
                best = enemy;
            }
            seekingRival = best != null;
        }
        return best;
    }

    public Vector3 Roam(PlayerCharacter enemy, Vector3 here, Tuning tuning, float clock, RandomNumberGenerator rng, BotStatus status)
    {
        if (roamGoal == null || clock >= roamUntil || Flat(roamGoal.Value - here).Length() < tuning.BotGoalRadius * 2)
        {
            var toward = Flat(enemy.GlobalPosition - here);
            var angle = Mathf.Atan2(toward.X, toward.Z) +
                Mathf.DegToRad(rng.RandfRange(-tuning.BotEasyRoamSpreadDegrees, tuning.BotEasyRoamSpreadDegrees));
            roamGoal = here + new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * tuning.BotEasyRoamRadius;
            roamUntil = clock + tuning.BotEasyRoamSeconds;
        }
        // A weak player scouts a short leg roughly toward company, rather than routing across the map to
        // an exact rival position. A held destination also avoids per-tick combat replans.
        status.Set("looking for rivals near {0}, {1:0.0} m to walk", enemy.ActorId, Flat(roamGoal.Value - here).Length());
        return roamGoal.Value;
    }

    public void Blacklist(Node3D node, float seconds, float clock)
    {
        // Fleeing can bypass retargeting while a previous tank is freed by an authoritative explosion.
        if (!GodotObject.IsInstanceValid(node)) { return; }
        blacklist[node.GetInstanceId()] = clock + seconds;
        BlacklistedTargets++;
    }

    private static bool Near(IReadOnlyList<PlayerCharacter> bodies, Vector3 at, float within, bool upright)
    {
        for (var i = 0; i < bodies.Count; i++)
        {
            if ((!upright || !bodies[i].IsRagdolled) && bodies[i].GlobalPosition.DistanceTo(at) < within) return true;
        }
        return false;
    }

    private static bool IsValid(Node3D? node) => node != null && GodotObject.IsInstanceValid(node) &&
                                                 node is not PropaneTank { State: PropaneTank.TankState.Exploded } &&
                                                 node is not AmmoPickup { Available: false };

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
    private static Vector3 Aim(PlayerCharacter enemy) => BotMotor.AimPoint(enemy);
    private static bool CanSee(IBotWorld world, PlayerCharacter player, Vector3 point) => BotMotor.CanSee(world, player, point);
}
