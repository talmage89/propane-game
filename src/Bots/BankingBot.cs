using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Navigation;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// Tank Bank's bot goal policy. It plays the tank bank to win rather than to test:
/// - it sets tanks off from outside their throw radius (wider for a pile, whose chain reaction reaches further);
/// - it runs from venting tanks that come close;
/// - it fights for tanks to spill, and then sets off what they spilled; Easy seeks rivals when no objective remains;
/// - it prefers venting tanks (one shot from a point) and big piles, especially piles with another player near them;
/// - it strafes in a fight, keeps its magazine topped up when nothing is happening, and fetches ammo when low.
/// It chooses a few times a second and steers every tick; whether something is in sight is asked at most
/// <see cref="Tuning.BotSightInterval"/> apart; hot paths reuse their storage.
/// </summary>
public sealed class BankingBot : IBotGoalPolicy
{
    private readonly RandomNumberGenerator rng = new();
    private readonly BankingTargets targets = new();
    private readonly BankingCombat combat = new();
    private readonly BankingDemolition demolition = new();
    private readonly BotStatus status = new();
    private PlayerIntent intent;
    private Tuning tuning = null!;
    private readonly BotNavigator navigator = new();
    private CapsuleShape3D? capsule;
    private PlayerCharacter? capsuleOwner;
    private Vector3? navigationGoal;
    private Vector3 fleeGoal;
    private int fleeAttempt;
    public int StuckEvents => navigator.StuckEvents;
    public int Replans => navigator.Replans;
    public int Jumps => navigator.Jumps;
    public int BlacklistedTargets => targets.BlacklistedTargets;
    private float tapCooldown;
    private bool protecting;
    private PropaneTank? fleeing;
    private float clock;
    private readonly BotAim aimControl = new();

    /// <summary>What the bot is doing, for logs.</summary>
    public string Status => status.ToString();
    public string StatusKind => status.Format;
    public string NavigationDiagnostics => navigator.Diagnostics;
    internal Vector3? ScoutingGoal => targets.ScoutingGoal;

    /// <summary>0..1: how quickly it turns onto a target and how tightly it holds aim. 1 is the hardest.</summary>
    public float Skill { get; set; } = 1f;
    public bool PointsDriven { get; set; }

    public BankingBot(ulong? seed = null)
    {
        if (seed is { } value) rng.Seed = value;
        // Bots that start together choose on different ticks.
        targets.Stagger(rng);
    }

    public void Dispose() => rng.Dispose();

    public void Prepare(IBotWorld world)
    {
        tuning = world.World.Tuning;
        capsule = BotServices.Capsule(world.Player);
        capsuleOwner = world.Player;
        world.Services.Navigation(BotServices.Capabilities(capsule, tuning));
        world.Services.PrepareActor(world.Player);
        for (var i = 0; i < world.RemoteBodies.Count; i++) world.Services.PrepareActor(world.RemoteBodies[i]);
        navigator.Prepare(tuning);
    }

    public PlayerIntent Read()
    {
        var read = intent;
        intent.FirePressed = false;
        intent.JumpPressed = false;
        intent.ReloadPressed = false;
        intent.MeleePressed = false;
        return read;
    }

    public void Think(IBotWorld match, float dt)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.BotBrain);
        tuning = match.World.Tuning;
        clock += dt;
        tapCooldown -= dt;
        combat.Begin(dt);
        var player = match.Player;
        intent = new PlayerIntent();
        navigationGoal = null;
        if (!match.IsPlaying || player.IsRagdolled || player.Ammo == null)
        {
            aimControl.Reset();
            navigator.Reset();
            return;
        }
        var here = player.GlobalPosition;
        var ammo = player.Ammo;

        // 1. Get away from a venting tank that comes close: it may go up any moment, and it hits like a car.
        // Once running, keep going until well clear, so it does not hover at the edge.
        if (fleeing != null && (!GodotObject.IsInstanceValid(fleeing) || fleeing.State == PropaneTank.TankState.Exploded ||
                                fleeing.GlobalPosition.DistanceTo(here) > tuning.RagdollRadius + 4f))
        {
            fleeing = null;
        }
        var danger = fleeing ?? match.Services.NearestVenting(here, tuning.RagdollRadius + 1f);
        if (danger != null && (danger != fleeing || Flat(fleeGoal - here).Length() < tuning.BotGoalRadius * 2))
        {
            // Keep the destination fixed while searching/traveling, but do not retry a failed escape forever.
            fleeAttempt = 0;
            fleeGoal = BotMotor.EscapeGoal(here, danger.GlobalPosition, tuning, fleeAttempt);
        }
        fleeing = danger;
        if (danger != null)
        {
            Face(player, fleeGoal + Vector3.Up * 1.4f, dt);
            MoveToward(player, fleeGoal - here);
            intent.Sprint = true;
            status.Set("fleeing venting tank {1:0.0} m away", first: danger.GlobalPosition.DistanceTo(here));
            Navigate(match, dt);
            return;
        }

        // A close opponent is worth knocking down even with an empty bank. Do not abandon a tank in the sights.
        if (targets.Target is not PropaneTank || !player.ResolvedState.EffectiveAim)
        {
            var reach = tuning.MeleeRange + tuning.MeleeLungeSpeed * (tuning.MeleeWindup + tuning.MeleeActive);
            var close = BankingCombat.Closest(match, here, reach, tuning.MeleeContactHeight);
            if (close != null)
            {
                Face(player, Aim(close), dt);
                var direction = Flat(close.GlobalPosition - here);
                var forward = Flat(-player.Aim.EyeTransform.Basis.Z).Normalized();
                intent.MeleePressed = player.CanMelee && MeleeContact.InArc(direction, forward, reach, tuning.MeleeHalfAngleDegrees);
                MoveToward(player, direction);
                status.Set("melee {0} at {1:0.0} m", close.ActorId, direction.Length());
                Navigate(match, dt);
                return;
            }
        }

        // 2. Under fire while holding more than the shooter: trading hits loses more than it wins, so break their
        //    line of sight. A shooter holding more gets shot back.
        if (combat.UnderFireCover(match, player, tuning, dt) is { } hitCover)
        {
            RunTo(player, hitCover, dt);
            status.Set("taking cover {1:0.0} m away", first: Flat(hitCover - here).Length());
            Navigate(match, dt);
            return;
        }
        // 3. Protect a valuable bank, retaining the watch and cover query cadence.
        protecting = BankingCombat.Protecting(match, PointsDriven);
        if (protecting && combat.ProtectionCover(match, player, tuning, dt) is { } bankCover && Flat(bankCover - here).Length() > 0.8f)
        {
            RunTo(player, bankCover, dt);
            status.Set("protecting {1:0}: hiding from {0}", combat.WatcherId, match.MyScore);
            Navigate(match, dt);
            return;
        }
        combat.ForgetCover();

        if (targets.Refresh(match, ammo, tuning, dt, clock, protecting, PointsDriven, Skill))
        {
            demolition.ResetTarget();
            if (!navigator.Jumping) { navigator.Reset(); }
        }
        var target = targets.Target;
        if (target == null)
        {
            status.Set("no target");
            if (navigator.Jumping) { Navigate(match, dt); }
            return;
        }

        switch (target)
        {
            case AmmoPickup pickup:
                status.Set("fetching ammo {1:0.0} m away", first: pickup.GlobalPosition.DistanceTo(here));
                Face(player, pickup.GlobalPosition + Vector3.Up * 0.4f, dt);
                MoveToward(player, Flat(pickup.GlobalPosition - here));
                intent.Sprint = true;
                break;
            case PlayerCharacter enemy:
                if (targets.SeekingRival)
                {
                    RunTo(player, targets.Roam(enemy, here, tuning, clock, rng, status), dt);
                }
                else
                {
                    var attack = combat.Fight(match, enemy, aimControl, Skill, clock, dt, rng, status);
                    intent.Aim = attack.Aim;
                    intent.Sprint = attack.Sprint;
                    intent.Move = attack.Strafe;
                    intent.FireHeld = intent.FirePressed = attack.Fire;
                    if (attack.Travel is { } travel) MoveToward(player, travel);
                }
                break;
            case PropaneTank tank:
                var action = demolition.Demolish(match, tank, combat, aimControl, Skill, dt, rng, status);
                intent.Aim = action.Aim;
                intent.Sprint = action.Sprint;
                if (action.Travel is { } direction) MoveToward(player, direction);
                if (action.Tap) Tap();
                if (action.LeavePile)
                {
                    var tanks = match.Services.Tanks;
                    for (var i = 0; i < tanks.Count; i++)
                    {
                        if (tanks[i].GlobalPosition.DistanceTo(tank.GlobalPosition) <= tuning.ChainRadius) Blacklist(tanks[i], tuning.BotTargetRetryTime);
                    }
                    Blacklist(tank, tuning.BotTargetRetryTime);
                }
                if (action.LeavePile) targets.Abandon();
                if (action.RetrySeconds > 0) { Blacklist(tank, action.RetrySeconds); targets.Retry(); }
                break;
        }

        // Top up the magazine while nothing is in the sights, or when it is empty.
        if (!intent.FireHeld && ammo.Reserve > 0 && (ammo.Magazine == 0 || ammo.Magazine < tuning.MagazineSize / 2 && target is not PlayerCharacter))
        {
            intent.ReloadPressed = true;
        }
        Navigate(match, dt);
    }

    private void RunTo(PlayerCharacter player, Vector3 spot, float dt)
    {
        Face(player, spot + Vector3.Up * 1.4f, dt);
        MoveToward(player, Flat(spot - player.GlobalPosition));
        intent.Sprint = true;
    }

    private void Tap()
    {
        if (tapCooldown <= 0)
        {
            intent.FirePressed = true;
            tapCooldown = 0.16f;
        }
    }

    /// <summary>Requests a destination; navigation resolves its route after tactical aim is chosen.</summary>
    private void MoveToward(PlayerCharacter player, Vector3 direction)
    {
        navigationGoal = player.GlobalPosition + Flat(direction);
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>Turns the view toward a point with skill-dependent smoothing, including while fleeing.</summary>
    internal void Face(PlayerCharacter player, Vector3 point, float dt) => aimControl.Track(player, point, dt, Skill);

    private static Vector3 Aim(PlayerCharacter enemy) => enemy.IsRagdolled ? enemy.PelvisPosition : enemy.GlobalPosition + Vector3.Up * 1.15f;

    internal void Blacklist(Node3D node, float seconds) => targets.Blacklist(node, seconds, clock);

    private void Navigate(IBotWorld match, float dt)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.BotNavigation);
        var player = match.Player;
        if (navigationGoal == null)
        {
            // Combat strafing uses the same collision checks and recovery as travel. Standing to shoot is
            // intentional; reset its progress window so a later walk is never counted as already stuck.
            if (intent.Move.LengthSquared() < 0.01f && !navigator.Jumping) { navigator.Reset(); return; }
            var basis = new Basis(Vector3.Up, Mathf.DegToRad(player.Aim.Yaw));
            var wish = basis * new Vector3(intent.Move.X, 0, -intent.Move.Y);
            navigationGoal = player.GlobalPosition + wish.Normalized() * tuning.BotRouteSpacing * 2;
        }
        // The actor's capsule never changes; finding it once avoids marshaling every child each tick.
        if (capsuleOwner != player || capsule == null)
        {
            capsule = BotServices.Capsule(player);
            capsuleOwner = player;
        }
        var services = match.Services;
        var grid = services.Navigation(BotServices.Capabilities(capsule, tuning));
        var move = navigator.Navigate(grid, services.Hazards, new NavigationState(player.GlobalPosition, player.Velocity, player.IsOnFloor()),
            navigationGoal.Value, tuning, dt);
        if (move.Failed)
        {
            if (fleeing != null && GodotObject.IsInstanceValid(fleeing))
            {
                fleeGoal = BotMotor.EscapeGoal(player.GlobalPosition, fleeing.GlobalPosition, tuning, ++fleeAttempt);
            }
            // A scouting target is a hint, not an objective: a failed waypoint/escape does not rule it out.
            if (targets.SeekingRival) { targets.ForgetRoam(); }
            else if (targets.Target != null) { Blacklist(targets.Target, tuning.BotTargetRetryTime); }
            targets.Abandon();
            demolition.ForgetVantage();
            combat.ForgetCover();
            navigator.Reset();
            intent.Move = Vector2.Zero;
            return;
        }
        if (navigator.Jumping || move.Sprint)
        {
            Face(player, player.GlobalPosition + move.Direction * 10 + Vector3.Up * 1.4f, dt);
            intent.Aim = false;
        }
        var yawRad = Mathf.DegToRad(player.Aim.Yaw);
        var forward = new Vector3(-Mathf.Sin(yawRad), 0, -Mathf.Cos(yawRad));
        var right = new Vector3(Mathf.Cos(yawRad), 0, -Mathf.Sin(yawRad));
        intent.Move = new Vector2(move.Direction.Dot(right), move.Direction.Dot(forward));
        intent.Sprint = move.Sprint && intent.Move.Y > tuning.SprintForwardThreshold;
        intent.JumpPressed = move.Jump;
        if (!player.IsOnFloor()) { intent.Aim = false; }
    }
}
