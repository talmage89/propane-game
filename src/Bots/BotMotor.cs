using Godot;
using Propane.Core;
using Propane.Navigation;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// The shared hands and feet of the prototype goal policies: a policy picks a destination, a point to face and the
/// buttons to press each tick; this turns them into intent through the shared navigation (<see cref="BotNavigator"/>)
/// and aiming (<see cref="BotAim"/>), with the same capsule, hazards and jump capabilities as the banking brain.
/// </summary>
public sealed class BotMotor
{
    private readonly BotNavigator navigator = new();
    private readonly BotAim aim = new();
    private CapsuleShape3D? capsule;
    private PlayerCharacter? capsuleOwner;
    private float tapCooldown;
    private Vector3? goal;
    private bool sprint;
    private Vector3 stallAnchor;
    private float stalled;
    private float direct;
    public PlayerIntent Intent;
    public float Skill { get; set; } = 1f;
    public string NavigationDiagnostics => navigator.Diagnostics;
    public bool Failed { get; private set; }
    /// <summary>A crosshair hit test can produce a tap only after the existing trigger cooldown.</summary>
    internal bool CanTap => tapCooldown <= 0;

    public void Prepare(IBotWorld world)
    {
        capsule = BotServices.Capsule(world.Player);
        capsuleOwner = world.Player;
        world.Services.Navigation(BotServices.Capabilities(capsule, world.World.Tuning));
        world.Services.PrepareActor(world.Player);
        for (var i = 0; i < world.RemoteBodies.Count; i++) world.Services.PrepareActor(world.RemoteBodies[i]);
        navigator.Prepare(world.World.Tuning);
    }

    /// <summary>Starts a tick: clears the buttons and the destination.</summary>
    public void Begin(float dt)
    {
        tapCooldown -= dt;
        Intent = new PlayerIntent();
        goal = null;
        sprint = false;
        Failed = false;
    }

    public void Reset()
    {
        aim.Reset();
        navigator.Reset();
        Intent = new PlayerIntent();
        goal = null;
        stalled = direct = 0;
    }

    public PlayerIntent Read()
    {
        var read = Intent;
        Intent.FirePressed = false;
        Intent.JumpPressed = false;
        Intent.ReloadPressed = false;
        Intent.MeleePressed = false;
        return read;
    }

    public void Face(PlayerCharacter player, Vector3 point, float dt) => aim.Track(player, point, dt, Skill);

    public void MoveTo(Vector3 point, bool run = false)
    {
        goal = point;
        sprint = run;
    }

    /// <summary>A single trigger pull, spaced like a person tapping.</summary>
    public void Tap()
    {
        if (tapCooldown > 0) return;
        Intent.FirePressed = true;
        tapCooldown = 0.16f;
    }

    /// <summary>Swings when the target is in the melee arc ahead.</summary>
    public bool Melee(PlayerCharacter player, Vector3 target)
    {
        var tuning = player.World.Tuning;
        var reach = tuning.MeleeRange + tuning.MeleeLungeSpeed * (tuning.MeleeWindup + tuning.MeleeActive);
        var direction = Flat(target - player.GlobalPosition);
        var forward = Flat(-player.Aim.EyeTransform.Basis.Z).Normalized();
        var swing = player.CanMelee && MeleeContact.InArc(direction, forward, reach, tuning.MeleeHalfAngleDegrees);
        Intent.MeleePressed |= swing;
        return swing;
    }

    public static float MeleeReach(Tuning tuning) => tuning.MeleeRange + tuning.MeleeLungeSpeed * (tuning.MeleeWindup + tuning.MeleeActive);

    /// <summary>
    /// Resolves the destination into movement, or strafes on <see cref="Intent"/>.Move when there is none.
    /// <paramref name="area"/> is the mode's own limit on where to step, if any.
    /// </summary>
    public void Navigate(IBotWorld world, float dt, INavigationArea? area = null)
    {
        var player = world.Player;
        var tuning = player.World.Tuning;
        if (goal == null)
        {
            if (Intent.Move.LengthSquared() < 0.01f && !navigator.Jumping) { navigator.Reset(); return; }
            var basis = new Basis(Vector3.Up, Mathf.DegToRad(player.Aim.Yaw));
            var wish = basis * new Vector3(Intent.Move.X, 0, -Intent.Move.Y);
            goal = player.GlobalPosition + wish.Normalized() * tuning.BotRouteSpacing * 2;
        }
        if (capsuleOwner != player || capsule == null)
        {
            capsule = BotServices.Capsule(player);
            capsuleOwner = player;
        }
        var services = world.Services;
        if (player.GlobalPosition.DistanceTo(stallAnchor) > tuning.BotDirectProgressDistance)
        {
            stallAnchor = player.GlobalPosition;
            stalled = 0;
        }
        else if (!navigator.AwaitingRoute && Flat(goal.Value - player.GlobalPosition).Length() > tuning.BotGoalRadius * 2)
        {
            // A search spread over ticks is not a stall: interrupting it would restart it, and a long one never ends.
            stalled += dt;
        }
        if (!navigator.Jumping && tuning.BotDirectStallTime > 0 && stalled > tuning.BotDirectStallTime)
        {
            direct = tuning.BotDirectTime;
            stalled = 0;
        }
        // Keep progress/retry state alive during direct recovery. Resetting it at 1.2 s prevented the navigator's
        // 1.25 s stall window from ever expiring, so a blocked motor could retry the same wall forever.
        var grid = services.Navigation(BotServices.Capabilities(capsule, tuning));
        var move = navigator.Navigate(grid, services.Hazards, new NavigationState(player.GlobalPosition, player.Velocity, player.IsOnFloor()),
            goal.Value, tuning, dt, area);
        if (move.Failed)
        {
            Failed = true;
            direct = stalled = 0;
            navigator.Reset();
            Intent.Move = Vector2.Zero;
            return;
        }
        if (navigator.Jumping) direct = 0;
        if (direct > 0 && !navigator.Jumping && player.IsOnFloor())
        {
            direct -= dt;
            var direction = Flat(goal.Value - player.GlobalPosition).Normalized();
            var next = player.GlobalPosition + direction * (tuning.SprintSpeed * dt);
            // The controller still resolves solid collisions. Walking recovery must respect live tank clearance
            // and a mode's ground limits; it never bypasses the planner's ballistic takeoff checks.
            if (RecoveryAllowed(player.GlobalPosition, next, services.Hazards, tuning.BotRouteSpacing, area))
            {
                Steer(player, direction, sprint);
                return;
            }
            direct = 0;
        }
        if (navigator.Jumping || move.Sprint && sprint)
        {
            Face(player, player.GlobalPosition + move.Direction * 10 + Vector3.Up * 1.4f, dt);
            Intent.Aim = false;
        }
        Steer(player, move.Direction, sprint && move.Sprint);
        Intent.JumpPressed = move.Jump;
    }


    internal static bool RecoveryAllowed(Vector3 from, Vector3 to, NavigationHazards hazards, float spacing,
        INavigationArea? area)
    {
        if (!hazards.SafeAlong(from, to, spacing, from)) return false;
        if (area == null) return true;
        var samples = System.Math.Max(1, (int)System.MathF.Ceiling(from.DistanceTo(to) / spacing));
        for (var i = 0; i <= samples; i++)
        {
            if (!area.Allows(from.Lerp(to, (float)i / samples))) return false;
        }
        return true;
    }

    private void Steer(PlayerCharacter player, Vector3 direction, bool run)
    {
        var yaw = Mathf.DegToRad(player.Aim.Yaw);
        var forward = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        var right = new Vector3(Mathf.Cos(yaw), 0, -Mathf.Sin(yaw));
        Intent.Move = new Vector2(direction.Dot(right), direction.Dot(forward));
        Intent.Sprint = run && Intent.Move.Y > player.World.Tuning.SprintForwardThreshold;
        if (!player.IsOnFloor()) Intent.Aim = false;
    }

    /// <summary>A fixed escape destination; retries alternate around the away direction after a route fails.</summary>
    internal static Vector3 EscapeGoal(Vector3 here, Vector3 danger, Tuning tuning, int attempt)
    {
        var away = Flat(here - danger);
        away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Forward;
        var directions = System.Math.Max(1, tuning.BotFleeDirections);
        attempt %= directions;
        var angle = ((attempt + 1) / 2) * (attempt % 2 == 0 ? -1 : 1) * Mathf.Tau / directions;
        return here + away.Rotated(Vector3.Up, angle) * tuning.BotFleeDistance;
    }

    public static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>Where to aim at a body: its pelvis when down, its chest when up.</summary>
    public static Vector3 AimPoint(PlayerCharacter body) => body.IsRagdolled ? body.PelvisPosition : body.GlobalPosition + Vector3.Up * 1.15f;

    /// <summary>Nothing solid between this player's eye and a point.</summary>
    public static bool CanSee(IBotWorld world, PlayerCharacter player, Vector3 point) =>
        world.Services.Clear(player.GlobalPosition + Vector3.Up * 1.6f, point, Layers.World | Layers.Props);

    /// <summary>The crosshair is on the target, with nothing in the way.</summary>
    public static bool OnTarget(IBotWorld world, PlayerCharacter player, Node3D target)
    {
        var view = player.Aim;
        var forward = -view.EyeTransform.Basis.Z;
        var start = view.EyeOrigin + forward * 2.5f;
        if (!world.Services.Ray(start, start + forward * 80f, Layers.ShotMask, out var hit)) return false;
        return target is CollisionObject3D collider && hit.Collider == collider.GetRid() ||
            target is PlayerCharacter body && world.Services.ActorOwns(body, hit.Collider);
    }

    /// <summary>The nearest venting tank close enough to fear, if any.</summary>
    public static PropaneTank? Danger(IBotWorld world, PlayerCharacter player) =>
        world.Services.NearestVenting(player.GlobalPosition, player.World.Tuning.RagdollRadius + 1f);
}
