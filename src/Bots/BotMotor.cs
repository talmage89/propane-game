using System.Linq;
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
public sealed class BotMotor : System.IDisposable
{
    private readonly BotNavigator navigator = new();
    private readonly NavigationQueries queries = new();
    private readonly BotAim aim = new();
    private CollisionShape3D? capsule;
    private float tapCooldown;
    private Vector3? goal;
    private bool sprint;
    public PlayerIntent Intent;
    public float Skill { get; set; } = 1f;
    public bool Failed { get; private set; }

    public void Dispose() => queries.Dispose();

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

    /// <summary>Resolves the destination into movement, or strafes on <see cref="Intent"/>.Move when there is none.</summary>
    public void Navigate(PlayerCharacter player, System.Collections.Generic.IEnumerable<PropaneTank> tanks, float dt)
    {
        var tuning = player.World.Tuning;
        if (goal == null)
        {
            if (Intent.Move.LengthSquared() < 0.01f && !navigator.Jumping) { navigator.Reset(); return; }
            var basis = new Basis(Vector3.Up, Mathf.DegToRad(player.Aim.Yaw));
            var wish = basis * new Vector3(Intent.Move.X, 0, -Intent.Move.Y);
            goal = player.GlobalPosition + wish.Normalized() * tuning.BotRouteSpacing * 2;
        }
        var shape = capsule != null && GodotObject.IsInstanceValid(capsule) && capsule.GetParent() == player ? capsule
            : capsule = player.GetChildren().OfType<CollisionShape3D>().Single();
        var capabilities = new NavigationCapabilities(shape.Shape, shape.Position, ((CapsuleShape3D)shape.Shape).Radius,
            tuning.RunSpeed, tuning.SprintSpeed, tuning.JumpVelocity, tuning.PlayerGravity, tuning.Acceleration, tuning.AirControl);
        // Only tanks near enough to matter: every route probe tests every hazard, and a King arena holds dozens.
        var here = player.GlobalPosition;
        var horizon = here.DistanceTo(goal.Value) + tuning.RagdollRadius + 4f;
        var hazards = tanks.Where(t => t.GlobalPosition.DistanceTo(here) < horizon).Select(t => new NavigationHazard(t.GlobalPosition,
            t.State == PropaneTank.TankState.Venting ? tuning.RagdollRadius + tuning.BotVentClearance : tuning.BotTankClearance)).ToArray();
        var world = new NavigationWorld(player.World.Physics.DirectSpaceState, capabilities, hazards, tuning.BotJumpMargin,
            tuning.BotJumpMinimumRange, player.GlobalPosition, queries);
        var move = navigator.Navigate(world, new NavigationState(player.GlobalPosition, player.Velocity, player.IsOnFloor()),
            capabilities, goal.Value, tuning, dt);
        if (move.Failed)
        {
            Failed = true;
            navigator.Reset();
            Intent.Move = Vector2.Zero;
            return;
        }
        if (navigator.Jumping || move.Sprint && sprint)
        {
            Face(player, player.GlobalPosition + move.Direction * 10 + Vector3.Up * 1.4f, dt);
            Intent.Aim = false;
        }
        var yaw = Mathf.DegToRad(player.Aim.Yaw);
        var forward = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        var right = new Vector3(Mathf.Cos(yaw), 0, -Mathf.Sin(yaw));
        Intent.Move = new Vector2(move.Direction.Dot(right), move.Direction.Dot(forward));
        Intent.Sprint = sprint && move.Sprint && Intent.Move.Y > tuning.SprintForwardThreshold;
        Intent.JumpPressed = move.Jump;
        if (!player.IsOnFloor()) Intent.Aim = false;
    }

    public static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>Where to aim at a body: its pelvis when down, its chest when up.</summary>
    public static Vector3 AimPoint(PlayerCharacter body) => body.IsRagdolled ? body.PelvisPosition : body.GlobalPosition + Vector3.Up * 1.15f;

    public static bool CanSee(PlayerCharacter player, Vector3 point)
    {
        var eye = player.GlobalPosition + Vector3.Up * 1.6f;
        return player.World.Physics.DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, point, Layers.World | Layers.Props)).Count == 0;
    }

    /// <summary>The crosshair is on the target, with nothing in the way.</summary>
    public static bool OnTarget(PlayerCharacter player, Node3D target)
    {
        var view = player.Aim;
        var forward = -view.EyeTransform.Basis.Z;
        var start = view.EyeOrigin + forward * 2.5f;
        var hit = player.World.Physics.DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(start, start + forward * 80f, Layers.ShotMask));
        var collider = hit.Count > 0 ? hit["collider"].As<GodotObject>() : null;
        return collider == target || target is PlayerCharacter body && PlayerCharacter.Owning(collider) == body;
    }

    /// <summary>The nearest venting tank close enough to fear, if any.</summary>
    public static PropaneTank? Danger(PlayerCharacter player, System.Collections.Generic.IEnumerable<PropaneTank> tanks)
    {
        var here = player.GlobalPosition;
        var radius = player.World.Tuning.RagdollRadius + 1f;
        return tanks.Where(t => t.State == PropaneTank.TankState.Venting && t.GlobalPosition.DistanceTo(here) < radius)
            .OrderBy(t => t.GlobalPosition.DistanceTo(here)).FirstOrDefault();
    }
}
