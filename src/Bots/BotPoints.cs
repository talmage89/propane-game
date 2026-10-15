using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// Local estimates of score swing per second, not scoring rules. Spills deny points immediately but only a
/// fraction are recovered; travel, return fire, shields and the zone's remaining life reduce the opportunity.
/// No rays, navigation searches or allocations: callers validate the most promising candidates themselves.
/// </summary>
public sealed class BotPoints
{
    private int leader;

    public void Begin(IBotWorld world)
    {
        leader = world.MyScore;
        var bodies = world.RemoteBodies;
        for (var i = 0; i < bodies.Count; i++) leader = Mathf.Max(leader, world.Scores.GetValueOrDefault(bodies[i].ActorId));
    }

    public float SpillValue(Tuning tuning, int mine, int bank) => tuning.BotPointsCapture + Denial(tuning, mine, bank, leader);

    internal static float Denial(Tuning tuning, int mine, int bank, int leading) => tuning.BotPointsDeny *
        (bank >= leading ? tuning.BotPointsLeader : bank > mine ? tuning.BotPointsAhead : tuning.BotPointsBehind);

    internal static float FightRate(Tuning tuning, int mine, int bank, int leading, float distance, bool down)
    {
        if (bank <= 0 || down) return 0;
        var seconds = Mathf.Max(0.01f, Mathf.Max(tuning.BotPointsFightSeconds, tuning.HitGraceTime));
        var spill = Mathf.Min(bank, distance <= BotMotor.MeleeReach(tuning) ? tuning.RagdollDropCount :
            tuning.HitDropCount * tuning.BotPointsHitsPerSecond * seconds);
        var exposure = Mathf.Min(mine, tuning.HitDropCount * tuning.BotPointsHitsPerSecond * seconds);
        var gain = spill * (tuning.BotPointsCapture + Denial(tuning, mine, bank, leading)) - tuning.BotPointsRisk * exposure;
        return Mathf.Max(0, gain) / (seconds + Travel(tuning, distance));
    }

    public float Fight(IBotWorld world, PlayerCharacter enemy, float distance) =>
        FightRate(world.World.Tuning, world.MyScore, world.Scores.GetValueOrDefault(enemy.ActorId), leader, distance, enemy.IsRagdolled);

    internal static float Travel(Tuning tuning, float distance) =>
        Mathf.Max(0, distance - tuning.BotPointsShotRange) / Mathf.Max(0.01f, tuning.SprintSpeed);

    public float Tank(IBotWorld world, PropaneTank tank, int pile, float distance)
    {
        var tuning = world.World.Tuning;
        var gain = (float)Mathf.Min(Mathf.Max(1, pile), tuning.BotPointsPileCap);
        var bodies = world.RemoteBodies;
        for (var i = 0; i < bodies.Count; i++)
        {
            var enemy = bodies[i];
            if (enemy.IsRagdolled || enemy.GlobalPosition.DistanceTo(tank.GlobalPosition) > tuning.RagdollRadius) continue;
            var bank = world.Scores.GetValueOrDefault(enemy.ActorId);
            gain += Mathf.Min(bank, tuning.RagdollDropCount) * SpillValue(tuning, world.MyScore, bank);
        }
        // A nearby chain can spill our own bank too. The acting policy still requires a safe shooting position.
        var retreat = Mathf.Max(0, tuning.RagdollRadius + tuning.BotVentClearance - distance) / Mathf.Max(0.01f, tuning.RunSpeed);
        var shots = tank.State == PropaneTank.TankState.Venting ? 1 : 2;
        return gain / Mathf.Max(0.01f, Travel(tuning, distance) + retreat + shots * tuning.BotPointsShotSeconds +
            (tank.Invulnerable ? tuning.DropInvulnerableTime : 0));
    }

    internal static float ZoneRate(Tuning tuning, int mine, int holderBank, int leading, bool rivalHolder,
        int competitors, float travel, float remaining)
    {
        var interval = Mathf.Max(0.05f, tuning.KingMintSeconds);
        if (remaining < travel + interval) return 0;
        var gain = tuning.BotPointsZone / (1 + competitors);
        if (rivalHolder) gain += Denial(tuning, mine, holderBank, leading);
        var exposure = Mathf.Min(mine, tuning.RagdollDropCount) * competitors / (1f + competitors);
        return gain / interval * (remaining - travel) / Mathf.Max(interval, remaining) -
            tuning.BotPointsRisk * exposure / Mathf.Max(0.01f, tuning.BotPointsFightSeconds);
    }

    public float Zone(IBotWorld world, KingBotView zone, Vector3 spot)
    {
        var tuning = world.World.Tuning;
        var travel = Mathf.Max(0, BotMotor.Flat(spot - world.Player.GlobalPosition).Length() - (float)zone.Radius) /
            Mathf.Max(0.01f, tuning.SprintSpeed);
        var competitors = 0;
        var bodies = world.RemoteBodies;
        for (var i = 0; i < bodies.Count; i++)
            if (!bodies[i].IsRagdolled && BotMotor.Flat(bodies[i].GlobalPosition - spot).Length() <= zone.Radius) competitors++;
        return ZoneRate(tuning, world.MyScore, world.Scores.GetValueOrDefault(zone.Holder), leader,
            zone.Holder != 0 && zone.Holder != world.Player.ActorId, competitors, travel, (float)zone.UntilMove);
    }
}
