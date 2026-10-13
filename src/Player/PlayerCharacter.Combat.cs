using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Tank;

namespace Propane.Player;

public partial class PlayerCharacter
{
    // ------------------------------------------------------------------ Aim and shooting

    private void UpdateAim()
    {
        if (ResolvedState.Weapon == WeaponAction.Melee)
        {
            aimTarget = GlobalPosition + Vector3.Up * World.Tuning.MeleeContactHeight + meleeDirection * World.Tuning.ShotRange;
            return;
        }
        var origin = Aim.EyeOrigin;
        var forward = Aim.Forward;
        var start = origin + forward * Mathf.Max(0, (GlobalPosition + Vector3.Up * 1.4f - origin).Dot(forward));
        var query = PhysicsRayQueryParameters3D.Create(start, start + forward * World.Tuning.ShotRange, Layers.ShotMask, shotExclude);
        var hit = World.Physics.DirectSpaceState.IntersectRay(query);
        aimTarget = hit.Count > 0 ? hit["position"].AsVector3() : start + forward * World.Tuning.ShotRange;
        // Never aim at something right behind or inside the character.
        var chest = GlobalPosition + Vector3.Up * 1.35f;
        if ((aimTarget - chest).Dot(forward) < 2f)
        {
            aimTarget = chest + forward * 2f;
        }
    }

    // Ammo and cooldown are physics facts supplied to the resolver, before it chooses this tick's action.
    private void PrepareCombat(float dt)
    {
        fireCooldown -= dt;
        if (state == State.Active && ResolvedState.Weapon != WeaponAction.Melee && Ammo != null &&
            (intent.ReloadPressed || (intent.FirePressed && Ammo.Magazine <= 0)))
        {
            Ammo.StartReload();
        }
    }

    private void UpdateShooting(float dt)
    {
        var tuning = World.Tuning;
        bloom = Mathf.MoveToward(bloom, 0, tuning.BloomRecovery * dt);
        var moving = Mathf.Clamp(new Vector2(Velocity.X, Velocity.Z).Length() / Mathf.Max(tuning.RunSpeed, 0.1f), 0, 1.5f);
        // Logical aim is suppressed immediately at takeoff/reload, independent of presentation blending.
        var aim = ResolvedState.EffectiveAim ? 1f : 0f;
        SpreadDegrees = Mathf.Lerp(tuning.HipSpreadDegrees, tuning.AimSpreadDegrees, aim)
                        + tuning.MoveSpreadDegrees * moving * (1f - aim * tuning.AimMoveSpreadReduction)
                        + bloom + (ResolvedState.Grounded ? 0f : tuning.AirSpreadDegrees);
        if (!ResolvedState.Fire)
        {
            return;
        }
        if (Ammo != null)
        {
            Ammo.Magazine--;
        }
        fireCooldown = 1f / Mathf.Max(tuning.FireRate, 0.1f);
        sprintAmount = Mathf.Min(sprintAmount, tuning.ShotSprintPoseLimit);
        Fire(SpreadDegrees);
        bloom += tuning.ShotBloomDegrees;
        rig.Kick();
        var recoilScale = Mathf.Lerp(1f, tuning.AimRecoilScale, aim);
        Aim.Kick(tuning.RecoilKickDegrees * recoilScale);
        View?.CameraRig.KickPresentation(recoilScale);
    }

    private void Fire(float spreadDegrees)
    {
        var tuning = World.Tuning;
        var origin = Aim.EyeOrigin;
        var forward = Aim.Forward;
        var direction = RandomInCone(forward, Mathf.DegToRad(spreadDegrees));
        var start = origin + forward * Mathf.Max(0, (GlobalPosition + Vector3.Up * 1.4f - origin).Dot(forward));
        var space = World.Physics.DirectSpaceState;

        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(start, start + direction * tuning.ShotRange, Layers.ShotMask, shotExclude));
        var end = hit.Count > 0 ? hit["position"].AsVector3() : start + direction * tuning.ShotRange;

        // The bullet really leaves the muzzle: something between the muzzle and the target blocks it first. This
        // muzzle is where the logical aim holds the rifle, never the animated one, so the pose cannot change a hit.
        var muzzle = LogicalMuzzle(forward);
        var toEnd = end - muzzle;
        if (toEnd.LengthSquared() > 0.01f)
        {
            var blocked = space.IntersectRay(PhysicsRayQueryParameters3D.Create(muzzle, end - toEnd.Normalized() * 0.02f, Layers.ShotMask, shotExclude));
            if (blocked.Count > 0)
            {
                hit = blocked;
                end = blocked["position"].AsVector3();
            }
        }

        rifle.Fire(lastRound: Ammo is { Magazine: 0 });
        World.Spawn.Effect(Tracer.Create(rifle.MuzzlePosition, end));

        if (DevLog.Enabled)
        {
            var hitNode = hit.Count > 0 ? hit["collider"].As<Node>() : null;
            var model = hitNode?.GetChildren().OfType<Node3D>().FirstOrDefault(c => c is not CollisionShape3D)?.Name.ToString() ?? "";
            GD.Print($"[shot] spread {spreadDegrees:0.00} hit {hitNode?.Name.ToString() ?? "nothing"} ({model}) at {end} from {start}");
        }
        if (hit.Count == 0)
        {
            Fired?.Invoke(new ShotReport(muzzle, end, Vector3.Zero, (end - muzzle).Normalized(), null, false));
            return;
        }
        var normal = hit["normal"].AsVector3();
        var collider = hit["collider"].As<GodotObject>();
        var shotDir = (end - muzzle).Normalized();
        ShotImpacting?.Invoke(new ShotReport(muzzle, end, normal, shotDir, collider, true));
        switch (collider)
        {
            case PropaneTank tank:
                tank.TakeShot(end, normal, shotDir, ActorId);
                ImpactEffect.Spawn(World, end, normal, metal: true, leaveDecal: false);
                break;
            case PlayerCharacter or PhysicalBone3D:
                // Another player: no decal on a person.
                ImpactEffect.Spawn(World, end, normal, metal: false, leaveDecal: false);
                break;
            case RigidBody3D { Freeze: false } body:
                body.ApplyImpulse(shotDir * tuning.BulletImpulse, end - body.GlobalPosition);
                ImpactEffect.Spawn(World, end, normal, metal: body is DebrisBody, leaveDecal: false);
                break;
            default:
                ImpactEffect.Spawn(World, end, normal, metal: false, leaveDecal: true);
                break;
        }
        Fired?.Invoke(new ShotReport(muzzle, end, normal, shotDir, collider, true));
    }

    /// <summary>The muzzle as the logical aim holds it: along the aim from the body, higher when shouldered.</summary>
    private Vector3 LogicalMuzzle(Vector3 forward)
    {
        var t = World.Tuning;
        var right = forward.Cross(Vector3.Up);
        right = right.LengthSquared() > 0.0001f ? right.Normalized() : Vector3.Right;
        var aimed = ResolvedState.EffectiveAim;
        return GlobalPosition + Vector3.Up * (aimed ? t.ShotMuzzleAimHeight : t.ShotMuzzleHipHeight)
            + right * (aimed ? t.ShotMuzzleAimSide : t.ShotMuzzleHipSide)
            + forward * (aimed ? t.ShotMuzzleAimForward : t.ShotMuzzleHipForward);
    }

    /// <summary>The player a collider belongs to (its capsule or one of its ragdoll bones), if any.</summary>
    public static PlayerCharacter? Owning(GodotObject? collider)
    {
        var node = collider as Node;
        while (node != null)
        {
            if (node is PlayerCharacter player)
            {
                return player;
            }
            node = node.GetParent();
        }
        return null;
    }

    private Vector3 RandomInCone(Vector3 axis, float halfAngle)
    {
        if (halfAngle <= 0.00001f)
        {
            return axis;
        }
        // Uniform over the cone's solid angle.
        var cosMax = Mathf.Cos(halfAngle);
        var z = World.Random.RandfRange(cosMax, 1.0f);
        var phi = World.Random.RandfRange(0, Mathf.Tau);
        var r = Mathf.Sqrt(1 - z * z);
        var basis = ImpactEffect.BasisAlong(axis);
        return (basis * new Vector3(r * Mathf.Cos(phi), r * Mathf.Sin(phi), z)).Normalized();
    }

    // ------------------------------------------------------------------ Melee

    private void UpdateMeleeContact()
    {
        var t = World.Tuning;
        var origin = GlobalPosition + Vector3.Up * t.MeleeContactHeight;
        meleeShape.Radius = t.MeleeRange;
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = meleeShape,
            Transform = new Transform3D(Basis.Identity, origin),
            CollisionMask = Layers.Pushable | Layers.Player | Layers.Ragdoll,
            Exclude = shotExclude.Duplicate(),
        };
        var space = World.Physics.DirectSpaceState;
        foreach (var hit in Blast.QueryAll(space, query))
        {
            var collider = hit["collider"].As<GodotObject>();
            var victim = Owning(collider);
            var body = collider as RigidBody3D;
            if (victim == this || victim == null && (body == null || body.Freeze && body is not PropaneTank { Gathering: true })) continue;
            var target = victim?.BlastTargetPosition ?? (body is PropaneTank tank
                ? tank.GlobalTransform * PropaneTank.LocalCenter : body!.GlobalPosition);
            var obstruction = space.IntersectRay(PhysicsRayQueryParameters3D.Create(origin, target, Layers.ShotMask, shotExclude));
            if (obstruction.Count > 0)
            {
                var blocker = obstruction["collider"].As<GodotObject>();
                if (victim != null ? Owning(blocker) != victim : blocker != collider) continue;
                // Reach the struck surface, so a nearby bumper can be shoved even when the car's center is far.
                target = obstruction["position"].AsVector3();
            }
            if (!MeleeContact.InArc(target - origin, meleeDirection, t.MeleeRange, t.MeleeHalfAngleDegrees)) continue;
            if (!meleeContact.TryHit((victim as GodotObject ?? collider).GetInstanceId())) continue;
            if (victim != null)
            {
                if (!World.Networked && !victim.IsRemote) victim.ReceiveMelee(meleeDirection);
            }
            else
            {
                if (body is PropaneTank pushedTank) pushedTank.Disturb();
                body!.ApplyCentralImpulse(meleeDirection * t.MeleePropImpulse);
                PushedBody?.Invoke(body);
            }
            MeleeHit?.Invoke(new MeleeHit(meleeSwing, victim ?? collider));
        }
    }

    /// <summary>The victim owns knockdown and the ordinary Ragdolled event/drop. Already down bodies receive a push,
    /// never a second drop from the same knockdown. Remote copies wait for their owner's Ragdoll event.</summary>
    public void ReceiveMelee(Vector3 direction)
    {
        if (IsRemote || state is State.Capturing or State.GettingUp) return;
        var t = World.Tuning;
        var launch = direction * t.MeleeLaunchSpeed + Vector3.Up * t.MeleeLaunchLift;
        if (state == State.Ragdoll) { ragdoll.Push(launch); return; }
        BeginRagdoll(Velocity + launch, Vector3.Up.Cross(direction) * t.MeleeSpin);
    }

}
