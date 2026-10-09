using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev;

/// <summary>
/// The first bot: picks a target (another player it can see, a tank, or ammo when low), runs at it and shoots.
/// No sense of danger, so it often throws itself. An easy opponent (<c>--brain=simple</c>), and the baseline the
/// <see cref="Bot"/> is measured against.
/// </summary>
public sealed class SimpleBot : IBotBrain
{
    private PlayerIntent intent;
    private Node3D? target;
    private Vector3 targetPoint;
    private float retarget;
    private float stuck;
    private float blocked;
    private Vector3 lastPosition;
    private readonly RandomNumberGenerator rng = new();

    public PlayerIntent Read()
    {
        var read = intent;
        intent.FirePressed = false;
        intent.JumpPressed = false;
        intent.ReloadPressed = false;
        return read;
    }

    public string Status => "";

    public void Think(Match match, float dt)
    {
        var player = match.Player;
        intent = new PlayerIntent();
        if (!match.IsPlaying || player.IsRagdolled)
        {
            return;
        }
        var ammo = player.Ammo!;
        retarget -= dt;
        if (retarget <= 0 || target == null || !GodotObject.IsInstanceValid(target) || target is PropaneTank { State: PropaneTank.TankState.Exploded })
        {
            retarget = 0.6f;
            var previous = target;
            target = Choose(match, ammo);
            if (target != previous)
            {
                blocked = 0;
            }
        }
        if (target == null)
        {
            return;
        }
        targetPoint = target switch
        {
            PlayerCharacter p => p.IsRagdolled ? p.PelvisPosition : p.GlobalPosition + Vector3.Up * 1.1f,
            PropaneTank t => t.GlobalTransform * PropaneTank.LocalCenter,
            _ => target.GlobalPosition + Vector3.Up * 0.3f,
        };
        var to = targetPoint - player.GlobalPosition;
        var flat = new Vector3(to.X, 0, to.Z);
        var distance = flat.Length();
        var wantRange = target switch
        {
            AmmoPickup => 0f,
            PlayerCharacter => 12f,
            _ => 9f,
        };

        // Face the target: camera yaw so its forward runs along the flat direction, pitch from the camera.
        var camera = player.CameraRig.Camera;
        var view = targetPoint - camera.GlobalPosition;
        var yaw = Mathf.RadToDeg(Mathf.Atan2(-view.X, -view.Z));
        var pitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(view.Normalized().Y, -1, 1)));
        player.CameraRig.SetOrientation(yaw, pitch);

        // In range but unable to hit it (a fence or house in the way): close in, then give up on it.
        if (blocked > 1.2f)
        {
            wantRange = 1.5f;
        }
        if (blocked > 4f)
        {
            blocked = 0;
            target = match.LiveTanks.Where(t => !t.Invulnerable && t != target).OrderBy(_ => rng.Randf()).FirstOrDefault();
            return;
        }
        if (distance > wantRange)
        {
            intent.Move = new Vector2(0, 1);
            intent.Sprint = distance > wantRange + 10f;
        }
        else if (target is not AmmoPickup)
        {
            intent.Aim = true;
            intent.Move = new Vector2(rng.RandfRange(-1, 1) * 0.4f, 0);
        }

        // Fire when the crosshair is on the target.
        if (target is not AmmoPickup && distance < 45f)
        {
            var space = player.GetWorld3D().DirectSpaceState;
            var forward = -camera.GlobalBasis.Z;
            var start = camera.GlobalPosition + forward * 2.5f;
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(start, start + forward * 60f, Layers.ShotMask));
            var collider = hit.Count > 0 ? hit["collider"].As<GodotObject>() : null;
            var onTarget = collider == target || target is PlayerCharacter pc && PlayerCharacter.Owning(collider) == pc;
            if (onTarget)
            {
                intent.FirePressed = true;
                intent.FireHeld = true;
                blocked = 0;
            }
            else if (distance <= wantRange + 1f)
            {
                blocked += dt;
            }
        }
        if (ammo.Magazine < 3 && !intent.FireHeld && ammo.Reserve > 0)
        {
            intent.ReloadPressed = true;
        }

        // Unstick: jump and sidestep if barely moving while trying to.
        var moved = player.GlobalPosition.DistanceTo(lastPosition);
        lastPosition = player.GlobalPosition;
        stuck = intent.Move.Y > 0 && moved < 0.02f ? stuck + dt : 0;
        if (stuck > 0.5f)
        {
            intent.JumpPressed = true;
            intent.Move = new Vector2(rng.Randf() < 0.5f ? -1 : 1, 0.5f);
            if (stuck > 2.5f)
            {
                retarget = 0;
                stuck = 0;
            }
        }
    }

    private Node3D? Choose(Match match, AmmoState ammo)
    {
        var player = match.Player;
        var here = player.GlobalPosition;
        if (ammo.Magazine + ammo.Reserve < 25)
        {
            var pickup = match.Pickups.Where(p => p.Available).OrderBy(p => p.GlobalPosition.DistanceTo(here)).FirstOrDefault();
            if (pickup != null)
            {
                return pickup;
            }
        }
        var space = player.GetWorld3D().DirectSpaceState;
        var visible = match.RemoteBodies.Where(b => b.GlobalPosition.DistanceTo(here) < 30f &&
            space.IntersectRay(PhysicsRayQueryParameters3D.Create(here + Vector3.Up * 1.5f, b.GlobalPosition + Vector3.Up * 1.2f, Layers.World)).Count == 0)
            .OrderBy(b => b.GlobalPosition.DistanceTo(here)).FirstOrDefault();
        if (visible != null && rng.Randf() < 0.5f)
        {
            return visible;
        }
        return match.LiveTanks.Where(t => !t.Invulnerable).OrderBy(t => t.GlobalPosition.DistanceTo(here) + rng.RandfRange(0, 6)).FirstOrDefault();
    }
}
