using System.Collections.Generic;
using Godot;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// King of the Cul-de-sac's goal policy: hold alone, shove or shoot intruders, and break off prolonged contests
/// to fight from the perimeter before independently timed raids. With nobody to fight, shoot
/// tanks in sight from a safe distance to bank them; head for the next spot
/// when the zone is about to move and it could not otherwise make it in time. It reads the zone from the mode's
/// <see cref="KingState"/>: the Lab's rules, or the server's snapshot in a match.
/// Hard compares nearby banks/spills with zone income and the cost of risking its bank in a crowd.
/// Prize searches are periodic; combat visibility stays live by default for brief sight gaps near the zone.
/// </summary>
public sealed class KingBot : IBotGoalPolicy
{
    private readonly RandomNumberGenerator rng = new();
    private readonly KingZoneTactics zonePolicy = new();
    private readonly KingPrizes prizes = new();
    private readonly BotMotor motor = new();
    private readonly BotStatus status = new();
    private float fightTime;
    private float burstClock;
    private float sightTimer;
    private bool sightStarted;
    public bool PointsDriven { get; set; }

    public string Status => status.ToString();
    public string StatusKind => status.Format;
    public string NavigationDiagnostics => motor.NavigationDiagnostics;
    public float Skill { get => motor.Skill; set => motor.Skill = value; }

    public KingBot(ulong? seed = null)
    {
        if (seed is { } value) rng.Seed = value;
        // Stagger sight without consuming the goal policy's seeded random stream: stand choices must not change
        // merely because observations moved to a periodic cadence.
        sightTimer = (unchecked((seed ?? 0) * 2654435761UL) & 1023) / 1024f;
    }

    public void Dispose() => rng.Dispose();

    public void Prepare(IBotWorld world) => motor.Prepare(world);

    public PlayerIntent Read() => motor.Read();

    public void Think(IBotWorld world, float dt)
    {
        motor.Begin(dt);
        var player = world.Player;
        if (!world.IsPlaying || world.King is not KingBotView state)
        {
            motor.Reset();
            zonePolicy.Waiting();
            prizes.Waiting();
            status.Set("waiting");
            return;
        }
        if (player.IsRagdolled)
        {
            zonePolicy.Down();
            prizes.Down();
            motor.Reset();
            status.Set("down");
            return;
        }
        var tuning = world.World.Tuning;
        var here = player.GlobalPosition;
        var zone = state.Zone;
        var spot = ToVector(state.Spot);
        if (zonePolicy.Recover(world, state, here, spot, tuning, rng)) motor.Reset();
        // Leave early for the next spot only when the walk would otherwise outlast the warning.
        var next = ToVector(state.Next);
        if (state.Warning && state.UntilMove < BotMotor.Flat(next - here).Length() / tuning.SprintSpeed + 0.5)
        {
            zone++;
            spot = next;
        }
        zonePolicy.UpdateStand(state, world, zone, spot, dt, rng);
        var radius = (float)state.Radius;

        if (BotMotor.Danger(world, player) is { } danger)
        {
            var away = here + BotMotor.Flat(here - danger.GlobalPosition).Normalized() * 6f;
            motor.Face(player, away + Vector3.Up * 1.4f, dt);
            motor.MoveTo(away, run: true);
            status.Set("clearing a venting tank");
            motor.Navigate(world, dt);
            return;
        }

        if (!sightStarted) { sightTimer *= tuning.BotKingSightInterval; sightStarted = true; }
        sightTimer -= dt;
        var look = sightTimer <= 0;
        if (look) sightTimer = tuning.BotKingSightInterval;

        if (zonePolicy.AdvanceRegroup(dt))
        {
            // Charging straight back after every knockdown keeps a large roster permanently contested. Recover
            // outside for independently seeded intervals, then resume the same competitive zone policy. Meanwhile
            // keep playing from the edge: shoot a rival with a bank to spill, or bank a tank in sight, and circle.
            Regroup(world, player, spot, radius, tuning, dt, look);
            motor.Navigate(world, dt);
            return;
        }

        if (PointsDriven && ChoosePoints(world, state, spot, player, tuning, dt))
        {
            motor.Navigate(world, dt);
            if (motor.Failed) prizes.NavigationFailed();
            return;
        }

        if (zonePolicy.BreakCrowd(world, state, player, spot, radius, tuning, dt, rng))
        {
            Regroup(world, player, spot, radius, tuning, dt, look, siege: true);
            motor.Navigate(world, dt);
            return;
        }
        var inZone = BotMotor.Flat(here - spot).Length() <= radius * 0.85f;
        var reach = BotMotor.MeleeReach(tuning);
        var close = zonePolicy.ObserveIntruders(world, player, spot, radius, reach, inZone, look);
        var rival = zonePolicy.Rival;
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
        else if (!inZone && prizes.Blocker(world, player, spot, radius, tuning, look) is { } venting)
        {
            // A venting tank in the zone keeps everyone out until it goes up: set it off from beyond its throw.
            var center = venting.GlobalTransform * PropaneTank.LocalCenter;
            motor.Face(player, center, dt);
            motor.Intent.Aim = true;
            if (motor.CanTap && BotMotor.OnTarget(world, player, venting)) motor.Tap();
            status.Set("clearing a venting tank from the zone");
        }
        else if (!inZone)
        {
            fightTime = 0;
            motor.Face(player, spot + zonePolicy.StandOffset + Vector3.Up * 1.4f, dt);
            motor.MoveTo(spot + zonePolicy.StandOffset, run: true);
            status.Set("heading for zone {0} {1:0.0} m away", zone, BotMotor.Flat(spot - here).Length());
        }
        else
        {
            // Holding: watch the nearest challenger and shoot one that comes close with a bank to spill.
            var watch = zonePolicy.Watch(world, player, look, out var visibleThreat);
            if (visibleThreat)
            {
                Shoot(player, watch!, tuning, dt);
                status.Set("holding, shooting {0}", watch!.ActorId);
            }
            else if (prizes.Prize(world, player, tuning, dt) is { } target)
            {
                // Nothing to fight: bank the nearest tank in sight from where it stands, never one close enough to throw it.
                var center = target.GlobalTransform * PropaneTank.LocalCenter;
                motor.Face(player, center, dt);
                motor.Intent.Aim = true;
                if (motor.CanTap && BotMotor.OnTarget(world, player, target)) motor.Tap();
                status.Set("holding, shooting a {4} tank", word: BotStatus.Name(target.State));
            }
            else
            {
                fightTime = 0;
                if (watch != null) motor.Face(player, BotMotor.AimPoint(watch), dt);
                if (BotMotor.Flat(spot + zonePolicy.StandOffset - here).Length() > 1f) motor.MoveTo(spot + zonePolicy.StandOffset);
                status.Set(state.Holder == player.ActorId ? "holding the zone" : "in the zone");
            }
        }
        motor.Navigate(world, dt);
        if (motor.Failed) zonePolicy.PickStand(state, world, spot, rng);
    }

    /// <summary>Hard compares banking and spilling in sight against the zone's time-limited mint/denial rate.</summary>
    private bool ChoosePoints(IBotWorld world, KingBotView state, Vector3 spot, PlayerCharacter player, Core.Tuning tuning, float dt)
    {
        if (prizes.SelectPoints(world, state, spot, player, tuning, dt, out var protectionGoal)) fightTime = 0;
        if (protectionGoal is { } goal) zonePolicy.SetProtectGoal(goal);
        var here = player.GlobalPosition;
        if (prizes.ProtectingPoints)
        {
            Regroup(world, player, spot, (float)state.Radius + tuning.BotPointsProtectDistance, tuning, dt, look: true);
            status.Set("points: protecting the bank outside the zone");
            return true;
        }
        switch (prizes.PointsTarget)
        {
            case PlayerCharacter enemy:
                if (enemy.IsRagdolled || world.Scores.GetValueOrDefault(enemy.ActorId) <= 0) { prizes.RetryPoints(); return false; }
                Shoot(player, enemy, tuning, dt);
                motor.Melee(player, enemy.GlobalPosition + Vector3.Up * tuning.MeleeContactHeight);
                // Keep minting when already inside. Outside, close on the valuable bank while firing.
                if (BotMotor.Flat(here - spot).Length() > state.Radius)
                {
                    var gap = BotMotor.Flat(enemy.GlobalPosition - here);
                    if (gap.Length() > BotMotor.MeleeReach(tuning)) motor.MoveTo(enemy.GlobalPosition - gap.Normalized() * BotMotor.MeleeReach(tuning));
                }
                status.Set("points: spilling {0}", enemy.ActorId);
                return true;
            case PropaneTank tank:
                if (tank.Invulnerable) { prizes.RetryPoints(); return false; }
                motor.Face(player, tank.GlobalTransform * PropaneTank.LocalCenter, dt);
                motor.Intent.Aim = true;
                var onTarget = prizes.Shot(world, player, tank, tuning, dt, out var giveUp);
                if (onTarget) motor.Tap();
                if (giveUp) return false;
                status.Set("points: banking a {4} tank", word: BotStatus.Name(tank.State));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Out of the zone for a while after a knockdown: fight or bank from its edge, moving around it.</summary>
    private void Regroup(IBotWorld world, PlayerCharacter player, Vector3 spot, float radius, Core.Tuning tuning, float dt, bool look, bool siege = false)
    {
        var regroupGoal = zonePolicy.StepRegroup(player, spot, radius, tuning);
        var watch = zonePolicy.Watch(world, player, look, out var visibleThreat);
        if (visibleThreat)
        {
            Shoot(player, watch!, tuning, dt);
            motor.MoveTo(regroupGoal);
            status.Set(siege ? "siege: shooting {0}" : "regrouping, shooting {0}", watch!.ActorId);
        }
        else if (prizes.Prize(world, player, tuning, dt) is { } target)
        {
            var center = target.GlobalTransform * PropaneTank.LocalCenter;
            motor.Face(player, center, dt);
            motor.Intent.Aim = true;
            if (motor.CanTap && BotMotor.OnTarget(world, player, target)) motor.Tap();
            motor.MoveTo(regroupGoal);
            status.Set(siege ? "siege: shooting a {4} tank" : "regrouping, shooting a {4} tank", word: BotStatus.Name(target.State));
        }
        else
        {
            fightTime = 0;
            motor.Face(player, regroupGoal + Vector3.Up * 1.4f, dt);
            motor.MoveTo(regroupGoal, run: true);
            status.Set(siege ? "siege: circling outside the zone" : "regrouping outside the zone");
        }
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

/// <summary>
/// Only the zone facts a King brain needs, as a value: reading local rules or advancing a received snapshot's
/// clocks must not construct a snapshot (and its minted roster) for every brain every physics tick.
/// </summary>
public readonly record struct KingBotView(int Zone, ModeSpot Spot, ModeSpot Next, double Radius, int Holder,
    bool Warning, double UntilMove)
{
    public static KingBotView? Read(KingRules rules, ModeClock clock)
    {
        if (rules.Zone < 0) return null;
        var elapsed = rules.Elapsed(clock);
        return new KingBotView(rules.Zone, rules.SpotOf(rules.Zone), rules.SpotOf(rules.Zone + 1), rules.Radius,
            rules.Holder, rules.Warning(elapsed), rules.UntilMove(elapsed));
    }

    public static KingBotView Read(KingState state, double elapsed) =>
        new(state.Zone, state.Spot, state.Next, state.Radius, state.Holder,
            elapsed >= state.MoveAt - state.WarningSeconds, System.Math.Max(0, state.MoveAt - elapsed));
}
