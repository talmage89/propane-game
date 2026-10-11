using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Tank;

namespace Propane.Player;

/// <summary>
/// The player: a capsule character controller with the animated mannequin, the rifle, the orbit camera and the
/// ragdoll. Close blasts throw it as a ragdoll; once it settles it gets back up. There is no health.
/// In a match, other players appear as remote copies (<see cref="IsRemote"/>): no camera or input, just the body
/// following the states and events their own game sends.
/// </summary>
public partial class PlayerCharacter : CharacterBody3D, IBlastReceiver
{
    private enum State
    {
        Active,
        Ragdoll,
        Capturing,
        GettingUp,
    }

    private const float CapsuleRadius = 0.32f;
    private const float CapsuleHeight = 1.8f;
    private const float RagdollSettleSpeed = 0.45f;
    private const float RagdollSettleHold = 0.35f;
    private const float RagdollGroundReach = 0.45f;
    private const float GetUpBlendTime = 0.45f;
    private const float RigReturnTime = 0.3f;
    private const float StaggerControlTime = 0.35f;
    private const float PushStrength = 6f;
    private const float MinRagdollLaunch = 6f;
    private const float MaxRagdollLift = 5.5f;
    private const float GetUpPelvisOffset = 0.239f;

    private State state = State.Active;
    private Node3D visual = null!;
    private Node3D model = null!;
    private Skeleton3D skeleton = null!;
    private CharacterAnimator animator = null!;
    private FootPlacement feet = null!;
    private RifleRig rig = null!;
    private RifleHandFollow handFollow = null!;
    private Ragdoll ragdoll = null!;
    private PoseBlend poseBlend = null!;
    private Rifle rifle = null!;
    private CollisionShape3D capsule = null!;
    private readonly DeviceInput deviceInput = new();
    private IPlayerInputSource? inputOverride;
    private PlayerIntent intent;
    private readonly PlayerStateResolver resolver = new();

    /// <summary>The last physics tick's decision, shared by locomotion, shooting, animation and snapshots.</summary>
    public PlayerState ResolvedState { get; private set; }

    private static StateTuning ResolverTuning(Tuning t) => new(t.RunSpeed, t.SprintSpeed, t.CreepSpeed,
        t.CoyoteTime, t.JumpBufferTime, t.ReadyHoldTime, t.SprintForwardThreshold);

    private float facingYaw;
    private float aimAmount;
    private float sprintAmount;
    private float readyAmount;
    private float fireCooldown;
    private float groundedTimer;
    private float bloom;
    private float airTime;
    private Vector3 knockback;
    private float staggerTimer;
    private float stateTimer;
    private float settleTimer;
    private Vector3 aimTarget;
    private Godot.Collections.Array<Rid> shotExclude = new();
    private Color bodyColor = new(0.92f, 0.44f, 0.16f);
    private StandardMaterial3D? bodyMaterial;
    private ShaderMaterial? bodyRim;
    private (Color Color, Color Edge, float Strength, float Power)? bodyContrast;
    private byte staggers;
    private byte remoteStaggers;
    private bool hasRemoteState;
    private PlayerNetState remoteState;
    private Vector3? pendingRoot;
    private float remoteUpTimer;
    private float pendingYaw;

    public CameraRig CameraRig { get; private set; } = null!;

    /// <summary>Current shot spread (full cone half-angle in degrees), for the crosshair.</summary>
    public float SpreadDegrees { get; private set; }

    public float AimAmount => aimAmount;

    /// <summary>Which way the body faces (radians about up; 0 faces -Z).</summary>
    public float FacingYaw => facingYaw;

    /// <summary>The ragdoll's hips, for spawning things where a thrown player is.</summary>
    public Vector3 PelvisPosition => ragdoll.Pelvis.GlobalPosition;

    public bool IsRagdolled => state != State.Active;

    /// <summary>Another player's body in a match, driven by their game. Set before adding to the tree.</summary>
    public bool IsRemote { get; init; }

    /// <summary>The player's id in a match (0 in single player).</summary>
    public int PeerId { get; set; }

    /// <summary>Rounds in the magazine and reserve, in a match. Null means unlimited (single player).</summary>
    public AmmoState? Ammo { get; set; }

    /// <summary>Only looking around is allowed (the countdown before a match, and after it ends).</summary>
    public bool InputLocked { get; set; }

    /// <summary>The body color: the player's color in a match.</summary>
    public Color BodyColor
    {
        get => bodyColor;
        set
        {
            bodyColor = value;
            if (bodyMaterial != null)
            {
                bodyMaterial.AlbedoColor = value;
                UpdateBodyContrast();
            }
        }
    }

    /// <summary>Where a score tag or name floats: over the head, or over the body while it is down.</summary>
    public Vector3 TagPosition => state is State.Ragdoll or State.Capturing
        ? ragdoll.Pelvis.GlobalPosition + Vector3.Up * 0.9f
        : GlobalPosition + Vector3.Up * 2.15f;

    /// <summary>The player fired (this game's player only).</summary>
    public event Action<ShotReport>? Fired;

    /// <summary>A blast threw the player: where they stood, and the launch velocity and spin of the ragdoll.</summary>
    public event Action<Vector3, Vector3, Vector3>? Ragdolled;

    /// <summary>The ragdoll settled and the get-up began: the standing position and facing it gets up to.</summary>
    public event Action<Vector3, float>? GotUp;

    /// <summary>The player walked into a loose body and pushed it.</summary>
    public event Action<RigidBody3D>? PushedBody;

    /// <summary>Replaces keyboard and mouse, for scripted tests.</summary>
    public IPlayerInputSource? InputOverride
    {
        get => inputOverride;
        set => inputOverride = value;
    }

    public Vector3 BlastTargetPosition => state == State.Ragdoll ? ragdoll.Pelvis.GlobalPosition : GlobalPosition + Vector3.Up * 1.0f;

    public override void _Ready()
    {
        AddToGroup(IBlastReceiver.Group);
        CollisionLayer = Layers.Player;
        // A remote player only stands where its own game puts it; it is here to be seen and shot.
        CollisionMask = IsRemote ? 0 : Layers.PlayerMask;
        FloorMaxAngle = Mathf.DegToRad(50);
        FloorSnapLength = 0.35f;
        FloorStopOnSlope = true;
        SafeMargin = 0.02f;

        capsule = new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = CapsuleRadius, Height = CapsuleHeight },
            Position = new Vector3(0, CapsuleHeight / 2, 0),
        };
        AddChild(capsule);

        visual = new Node3D { Name = "Visual" };
        AddChild(visual);
        model = GameAssets.Instantiate("res://assets/character/mannequin.glb");
        model.Name = "Model";
        // The mannequin faces +Z; turn it so the visual's -Z (Godot forward) is the character's front.
        model.RotationDegrees = new Vector3(0, 180, 0);
        visual.AddChild(model);
        skeleton = model.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().First();
        StyleMannequin();

        rifle = new Rifle { Name = "Rifle", TopLevel = true };
        AddChild(rifle);
        if (Ammo != null || IsRemote)
        {
            // A match, where reloads drop magazines.
            DroppedMagazine.Preload();
        }

        ragdoll = new Ragdoll { Name = "Ragdoll" };
        skeleton.AddChild(ragdoll);
        ragdoll.Build(skeleton);
        var player = model.FindChildren("*", "AnimationPlayer", owned: false).OfType<AnimationPlayer>().First();
        animator = new CharacterAnimator(player, model, model.GetPathTo(skeleton));
        animator.Initialize();
        poseBlend = new PoseBlend { Name = "PoseBlend" };
        skeleton.AddChild(poseBlend);
        feet = new FootPlacement { Name = "Feet", Player = this, Pose = animator.Pose };
        skeleton.AddChild(feet);
        rig = new RifleRig { Name = "RifleRig", Rifle = rifle, Pose = animator.Pose };
        skeleton.AddChild(rig);
        handFollow = new RifleHandFollow { Name = "RifleHandFollow", Rifle = rifle, Rig = rig, Blend = poseBlend };
        skeleton.AddChild(handFollow);

        if (!IsRemote)
        {
            CameraRig = new CameraRig { Name = "CameraRig" };
            AddChild(CameraRig);
            CameraRig.Exclude.Add(GetRid());
        }
        shotExclude.Add(GetRid());
        foreach (var bone in ragdoll.Bones)
        {
            if (!IsRemote)
            {
                CameraRig.Exclude.Add(bone.GetRid());
            }
            shotExclude.Add(bone.GetRid());
            bone.AddCollisionExceptionWith(this);
        }

        facingYaw = Rotation.Y;
        if (!IsRemote)
        {
            CameraRig.SetOrientation(Mathf.RadToDeg(facingYaw), -10f);
            CameraRig.Target = GlobalPosition + Vector3.Up * Tuning.Current.CameraHeight;
        }
        aimTarget = GlobalPosition + Forward(facingYaw) * 20f;
        Rotation = Vector3.Zero;
        visual.Rotation = new Vector3(0, facingYaw, 0);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsRemote)
        {
            deviceInput.Accumulate(@event);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPaused || what == NotificationUnpaused ||
            what == NotificationApplicationFocusOut || what == NotificationApplicationFocusIn)
        {
            (inputOverride as DeviceInput ?? deviceInput).Reset();
        }
    }

    /// <summary>Moves the player (and camera) to a spawn point, clearing any motion.</summary>
    public void Teleport(Vector3 position, float yaw)
    {
        ResetToActive();
        resolver.Reset();
        ResolvedState = default;
        airTime = 0;
        animator.Pose.Reset();
        feet.Reset();
        GlobalPosition = position;
        Velocity = Vector3.Zero;
        knockback = Vector3.Zero;
        facingYaw = yaw;
        visual.Rotation = new Vector3(0, yaw, 0);
        aimTarget = position + Vector3.Up * 1.4f + Forward(yaw) * 20f;
        if (IsRemote)
        {
            hasRemoteState = false;
            return;
        }
        CameraRig.SetOrientation(Mathf.RadToDeg(yaw), -10f);
        CameraRig.Target = position + Vector3.Up * Tuning.Current.CameraHeight;
        CameraRig.Snap();
    }

    /// <summary>Abandons any ragdoll or get-up in progress, including its captured pose.</summary>
    private void ResetToActive()
    {
        if (state == State.Active)
        {
            return;
        }
        ragdoll.End();
        poseBlend.Cancel();
        animator.StopGetUp();
        state = State.Active;
        capsule.Disabled = false;
        rig.Weight = 1f;
        animator.SetHoldWeight(1f);
    }

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        if (IsRemote)
        {
            UpdateRemote(dt);
            animator.Pose.ObservePhysics(ResolvedState, Velocity.Y);
            return;
        }
        intent = (inputOverride ?? deviceInput).Read();
        if (InputLocked)
        {
            intent = new PlayerIntent { Look = intent.Look, LookStick = intent.LookStick };
        }
        CameraRig.Look(intent.Look, intent.LookStick, dt);
        if (Ammo != null)
        {
            Ammo.Update(dt);
            if (state != State.Active)
            {
                // Thrown mid-reload: the reload is lost.
                Ammo.CancelReload();
            }
        }

        PrepareCombat(dt);
        var tuning = Tuning.Current;
        var lifecycle = state == State.Active ? PlayerLifecycle.Active
            : state == State.GettingUp ? PlayerLifecycle.GettingUp : PlayerLifecycle.Ragdoll;
        ResolvedState = resolver.Resolve(new StateIntent(intent.Move.X, intent.Move.Y, intent.Sprint,
                intent.Aim, intent.JumpPressed, intent.FirePressed || (tuning.FullAuto && intent.FireHeld)),
            new StatePhysics(lifecycle, IsOnFloor(), Ammo?.Reloading ?? false, staggerTimer > 0,
                fireCooldown <= 0 && (Ammo == null || Ammo.Magazine > 0)), ResolverTuning(tuning), dt);

        switch (state)
        {
            case State.Active:
                UpdateAim();
                UpdateMovement(dt);
                UpdateShooting(dt);
                break;
            case State.Ragdoll:
                UpdateRagdoll(dt);
                break;
            case State.Capturing:
                if (poseBlend.HasCapture)
                {
                    BeginGetUp();
                }
                break;
            case State.GettingUp:
                UpdateGettingUp(dt);
                break;
        }
        animator.Pose.ObservePhysics(ResolvedState, Velocity.Y);
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        var tuning = Tuning.Current;
        UpdateBodyContrast();
        var planar = feet.SamplePlanarVelocity(dt);
        animator.UpdateLocomotion(ResolvedState.Active ? planar.Length() : 0f, dt, ResolvedState.Gait);

        visual.Rotation = new Vector3(0, facingYaw, 0);
        rig.AimTarget = aimTarget;
        rig.AimAmount = aimAmount;
        rig.SprintAmount = sprintAmount;
        rig.ReadyAmount = readyAmount;
        rig.Reload = IsRemote ? remoteState.Reload : Ammo?.ReloadProgress ?? 0f;
        rig.AdvanceReload(dt, IsRemote);
        rifle.Animate(dt, rig.Reload);
        if (IsRemote)
        {
            return;
        }

        var cameraTarget = state switch
        {
            State.Ragdoll or State.Capturing => ragdoll.Pelvis.GlobalPosition + Vector3.Up * 0.45f,
            _ => GlobalPosition + Vector3.Up * tuning.CameraHeight,
        };
        if (state == State.GettingUp)
        {
            var t = Mathf.Clamp(stateTimer / animator.GetUpDuration, 0, 1);
            var pelvis = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone("pelvis")).Origin;
            cameraTarget = (pelvis + Vector3.Up * 0.45f).Lerp(GlobalPosition + Vector3.Up * tuning.CameraHeight, Mathf.SmoothStep(0, 1, t));
        }
        CameraRig.Target = cameraTarget;
    }

    // ------------------------------------------------------------------ Movement

    private void UpdateMovement(float dt)
    {
        var tuning = Tuning.Current;
        var grounded = ResolvedState.Grounded;

        var cameraYaw = Mathf.DegToRad(CameraRig.Yaw);
        var wish = new Basis(Vector3.Up, cameraYaw) * new Vector3(ResolvedState.MoveX, 0, -ResolvedState.MoveY);
        if (wish.LengthSquared() > 1f)
        {
            wish = wish.Normalized();
        }

        var resolved = ResolvedState;
        var sprinting = resolved.Gait == PlayerGait.Sprint;
        sprintAmount = Mathf.MoveToward(sprintAmount, sprinting ? 1f : 0f,
            dt * (sprinting ? tuning.SprintRaiseSpeed : tuning.SprintLowerSpeed));
        aimAmount = resolved.EffectiveAim ? Mathf.MoveToward(aimAmount, 1f, dt * tuning.AimBlendSpeed) : 0f;
        readyAmount = Mathf.MoveToward(readyAmount, resolved.Ready ? 1f : 0f,
            dt * (resolved.Ready ? tuning.ReadyRaiseSpeed : tuning.ReadyLowerSpeed));

        var speed = resolved.TargetSpeed;
        var control = staggerTimer > 0 ? tuning.StaggerMoveControl : 1f;
        staggerTimer -= dt;
        var target = wish * speed;
        var velocity = Velocity;
        var planar = new Vector3(velocity.X, 0, velocity.Z);
        var accel = tuning.Acceleration * (grounded ? 1f : tuning.AirControl) * control;
        planar = planar.MoveToward(target, accel * dt);

        // Horizontal knockback decays separately so a blast is not canceled by walking against it.
        knockback = knockback.MoveToward(Vector3.Zero, (grounded ? tuning.GroundKnockbackFriction : tuning.AirKnockbackFriction) * dt);
        knockback.Y = 0;
        velocity.X = planar.X;
        velocity.Z = planar.Z;

        if (resolved.Jump)
        {
            velocity.Y = tuning.JumpVelocity;
        }
        else if (!grounded)
        {
            velocity.Y -= tuning.PlayerGravity * dt;
        }
        else if (velocity.Y < 0)
        {
            velocity.Y = 0;
        }

        Velocity = velocity + knockback;
        MoveAndSlide();
        // Remove the knockback part again so it does not compound through the acceleration step.
        var after = Velocity;
        Velocity = new Vector3(after.X - knockback.X, after.Y, after.Z - knockback.Z);
        PushBodies(dt);

        if (!grounded)
        {
            airTime += dt;
        }
        else
        {
            if (resolved.Support == PlayerSupport.Landing && airTime > tuning.LandingMinAirTime)
            {
                CameraRig.AddTrauma(Mathf.Clamp(airTime * tuning.LandingTraumaPerSecond, 0, tuning.LandingTraumaMax));
            }
            airTime = 0;
        }
        UpdateFacing(dt, wish, resolved.Ready);
    }

    private void UpdateFacing(float dt, Vector3 wish, bool ready)
    {
        var tuning = Tuning.Current;
        var aimYaw = Mathf.DegToRad(CameraRig.Yaw);
        float targetYaw;
        if (ready)
        {
            if (wish.LengthSquared() > tuning.FacingInputThreshold * tuning.FacingInputThreshold)
            {
                // Legs follow the movement, torso twists to the aim. Moving backward, the legs face forward and
                // the feet step in actual travel direction, keeping the spine within its comfortable range.
                var moveYaw = Mathf.Atan2(-wish.X, -wish.Z);
                var offset = Mathf.Wrap(moveYaw - aimYaw, -Mathf.Pi, Mathf.Pi);
                if (Mathf.Abs(offset) > Mathf.DegToRad(tuning.BackpedalAngleDegrees))
                {
                    offset = Mathf.Wrap(offset + Mathf.Pi, -Mathf.Pi, Mathf.Pi);
                }
                var limit = Mathf.DegToRad(tuning.CombatSpineTwistDegrees);
                targetYaw = aimYaw + Mathf.Clamp(offset, -limit, limit);
            }
            else
            {
                // Standing: turn in place only once the twist gets large.
                var offset = Mathf.Wrap(facingYaw - aimYaw, -Mathf.Pi, Mathf.Pi);
                targetYaw = Mathf.Abs(offset) > Mathf.DegToRad(tuning.CombatTurnDegrees) ? aimYaw : facingYaw;
            }
        }
        else if (wish.LengthSquared() > tuning.FacingInputThreshold * tuning.FacingInputThreshold)
        {
            targetYaw = Mathf.Atan2(-wish.X, -wish.Z);
        }
        else
        {
            targetYaw = facingYaw;
        }
        facingYaw = Mathf.LerpAngle(facingYaw, targetYaw, 1f - Mathf.Exp(-tuning.TurnSharpness * dt));
    }

    private void PushBodies(float dt)
    {
        for (var i = 0; i < GetSlideCollisionCount(); i++)
        {
            var collision = GetSlideCollision(i);
            if (collision.GetCollider() is not RigidBody3D body || body.Freeze && body is not PropaneTank { Gathering: true })
            {
                continue;
            }
            var normal = collision.GetNormal();
            var into = -Velocity.Dot(normal);
            if (into <= 0)
            {
                continue;
            }
            var push = -new Vector3(normal.X, 0, normal.Z).Normalized() * into * Mathf.Min(body.Mass, 30f) * PushStrength * dt;
            if (body is PropaneTank tank) { tank.Disturb(); }
            body.ApplyImpulse(push, collision.GetPosition() - body.GlobalPosition);
            PushedBody?.Invoke(body);
        }
    }

    // ------------------------------------------------------------------ Aim and shooting

    private void UpdateAim()
    {
        var camera = CameraRig.Camera;
        var origin = camera.GlobalPosition;
        var forward = -camera.GlobalBasis.Z;
        var start = origin + forward * Mathf.Max(0, (GlobalPosition + Vector3.Up * 1.4f - origin).Dot(forward));
        var query = PhysicsRayQueryParameters3D.Create(start, start + forward * Tuning.Current.ShotRange, Layers.ShotMask, shotExclude);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        aimTarget = hit.Count > 0 ? hit["position"].AsVector3() : start + forward * Tuning.Current.ShotRange;
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
        if (state == State.Active && Ammo != null &&
            (intent.ReloadPressed || (intent.FirePressed && Ammo.Magazine <= 0)))
        {
            Ammo.StartReload();
        }
    }

    private void UpdateShooting(float dt)
    {
        var tuning = Tuning.Current;
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
        CameraRig.Kick(tuning.RecoilKickDegrees * Mathf.Lerp(1f, tuning.AimRecoilScale, aim));
    }

    private void Fire(float spreadDegrees)
    {
        var tuning = Tuning.Current;
        var camera = CameraRig.Camera;
        var origin = camera.GlobalPosition;
        var forward = -camera.GlobalBasis.Z;
        var direction = RandomInCone(forward, Mathf.DegToRad(spreadDegrees));
        var start = origin + forward * Mathf.Max(0, (GlobalPosition + Vector3.Up * 1.4f - origin).Dot(forward));
        var space = GetWorld3D().DirectSpaceState;

        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(start, start + direction * tuning.ShotRange, Layers.ShotMask, shotExclude));
        var end = hit.Count > 0 ? hit["position"].AsVector3() : start + direction * tuning.ShotRange;

        // The bullet really leaves the muzzle: something between the muzzle and the target blocks it first.
        var muzzle = rifle.MuzzlePosition;
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
        Spawn.Effect(Tracer.Create(muzzle, end));

        if (DevLog.Enabled)
        {
            var hitNode = hit.Count > 0 ? hit["collider"].As<Node>() : null;
            var model = hitNode?.GetChildren().OfType<Node3D>().FirstOrDefault(c => c is not CollisionShape3D)?.Name.ToString() ?? "";
            GD.Print($"[shot] spread {spreadDegrees:0.00} hit {hitNode?.Name.ToString() ?? "nothing"} ({model}) at {end} from {start}");
        }
        if (hit.Count == 0)
        {
            Fired?.Invoke(new ShotReport(muzzle, end, Vector3.Zero, direction, null, false));
            return;
        }
        var normal = hit["normal"].AsVector3();
        var collider = hit["collider"].As<GodotObject>();
        var shotDir = (end - muzzle).Normalized();
        switch (collider)
        {
            case PropaneTank tank:
                tank.TakeShot(end, normal, shotDir);
                ImpactEffect.Spawn(end, normal, metal: true, leaveDecal: false);
                break;
            case PlayerCharacter or PhysicalBone3D:
                // Another player: no decal on a person.
                ImpactEffect.Spawn(end, normal, metal: false, leaveDecal: false);
                break;
            case RigidBody3D { Freeze: false } body:
                body.ApplyImpulse(shotDir * tuning.BulletImpulse, end - body.GlobalPosition);
                ImpactEffect.Spawn(end, normal, metal: body is DebrisBody, leaveDecal: false);
                break;
            default:
                ImpactEffect.Spawn(end, normal, metal: false, leaveDecal: true);
                break;
        }
        Fired?.Invoke(new ShotReport(muzzle, end, normal, shotDir, collider, true));
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

    private static Vector3 RandomInCone(Vector3 axis, float halfAngle)
    {
        if (halfAngle <= 0.00001f)
        {
            return axis;
        }
        // Uniform over the cone's solid angle.
        var cosMax = Mathf.Cos(halfAngle);
        var z = (float)GD.RandRange(cosMax, 1.0);
        var phi = (float)GD.RandRange(0, Mathf.Tau);
        var r = Mathf.Sqrt(1 - z * z);
        var basis = ImpactEffect.BasisAlong(axis);
        return (basis * new Vector3(r * Mathf.Cos(phi), r * Mathf.Sin(phi), z)).Normalized();
    }

    // ------------------------------------------------------------------ Blasts and ragdoll

    public void ReceiveBlast(BlastInfo blast)
    {
        var tuning = Tuning.Current;
        var push = tuning.BlastSpeed * blast.Falloff * tuning.PlayerKnockback;
        switch (state)
        {
            case State.Ragdoll:
                ragdoll.Push(blast.Direction * push);
                return;
            case State.Capturing:
            case State.GettingUp:
                return;
        }
        if (IsRemote)
        {
            // That player's own game decides whether the blast throws them.
            return;
        }
        if (blast.Distance < tuning.RagdollRadius && tuning.PlayerKnockback > 0)
        {
            var speed = Mathf.Max(push, MinRagdollLaunch * tuning.PlayerKnockback);
            var flat = new Vector3(blast.Direction.X, 0, blast.Direction.Z);
            flat = flat.LengthSquared() > 0.0001f ? flat.Normalized() : Vector3.Zero;
            // Mostly outward with a modest hop, so the flight is short and the landing is the show.
            var launch = flat * speed * 0.8f + Vector3.Up * Mathf.Min(speed * 0.45f, MaxRagdollLift);
            // Feet are swept out from under the body: it pitches backward away from the blast.
            var tumbleAxis = flat.LengthSquared() > 0.0001f ? Vector3.Up.Cross(flat).Normalized() : Vector3.Right;
            var spin = tumbleAxis * (float)GD.RandRange(2.5, 4.5) + Vector3.Up * (float)GD.RandRange(-2.0, 2.0);
            BeginRagdoll(Velocity + launch, spin);
        }
        else if (blast.Distance < tuning.StaggerRadius)
        {
            knockback += new Vector3(blast.Direction.X, 0, blast.Direction.Z) * push * 0.55f;
            // A small hop, applied once.
            Velocity += Vector3.Up * Mathf.Min(push * 0.25f, 3f);
            staggerTimer = StaggerControlTime;
            animator.PlayHit();
            staggers++;
        }
    }

    private void BeginRagdoll(Vector3 velocity, Vector3 spin)
    {
        state = State.Ragdoll;
        // A blast can arrive between our ticks. Drop control/presentation immediately so neither snapshots nor
        // the returning rig inherit an aimed or sprinting pose from before the throw.
        resolver.Reset();
        ResolvedState = new PlayerState(PlayerLifecycle.Ragdoll, PlayerSupport.Airborne, PlayerGait.Default,
            WeaponAction.Carry, 0, 0, 0, intent.Aim, false, false);
        aimAmount = sprintAmount = readyAmount = 0;
        airTime = 0;
        animator.Pose.Reset();
        feet.Reset();
        Ammo?.CancelReload();
        stateTimer = 0;
        settleTimer = 0;
        groundedTimer = 0;
        rig.Weight = 0f;
        animator.SetHoldWeight(0f);
        capsule.Disabled = true;
        ragdoll.Begin(velocity, spin);
        if (!IsRemote)
        {
            Ragdolled?.Invoke(GlobalPosition, velocity, spin);
        }
        Velocity = Vector3.Zero;
        knockback = Vector3.Zero;
    }

    private void UpdateRagdoll(float dt)
    {
        var tuning = Tuning.Current;
        stateTimer += dt;
        settleTimer = ragdoll.AverageSpeed() < RagdollSettleSpeed ? settleTimer + dt : 0;
        if (DevLog.Enabled && Engine.GetPhysicsFrames() % 12 == 0)
        {
            GD.Print($"[ragdoll] t {stateTimer:0.00} sim {ragdoll.IsSimulatingPhysics()} active {ragdoll.Active} pelvisBody {ragdoll.Pelvis.GlobalPosition} v {ragdoll.Pelvis.LinearVelocity} avg {ragdoll.AverageSpeed():0.00} boneSim {ragdoll.Pelvis.IsSimulatingPhysics()}");
        }
        // Keep the (disabled) capsule under the body so blast distance and the floor follow it.
        GlobalPosition = new Vector3(ragdoll.Pelvis.GlobalPosition.X, GlobalPosition.Y, ragdoll.Pelvis.GlobalPosition.Z);
        // However far the body is thrown, it only gets up once it is lying on something. The time limit counts only
        // time on the ground, and cuts short a body that keeps twitching there.
        var grounded = RagdollGrounded();
        groundedTimer = grounded ? groundedTimer + dt : 0;
        var settled = stateTimer > tuning.RagdollMinTime && settleTimer > RagdollSettleHold;
        if (grounded && (settled || groundedTimer > tuning.RagdollMaxTime))
        {
            poseBlend.RequestCapture();
            state = State.Capturing;
        }
    }

    /// <summary>The ragdoll's hips are resting on (or right above) the ground, a roof, a car or anything solid.</summary>
    private bool RagdollGrounded()
    {
        var pelvis = ragdoll.Pelvis.GlobalPosition;
        var query = PhysicsRayQueryParameters3D.Create(pelvis, pelvis + Vector3.Down * RagdollGroundReach, Layers.PlayerMask | Layers.Debris, shotExclude);
        return GetWorld3D().DirectSpaceState.IntersectRay(query).Count > 0;
    }

    private void BeginGetUp()
    {
        // Lay the get-up clip's lying pose over the settled ragdoll: the clip starts face up with the head toward
        // the model's -Z (the visual's +Z), pelvis 0.24 m from the origin.
        var pelvisIndex = skeleton.FindBone("pelvis");
        var pelvisWorld = (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(pelvisIndex)).Orthonormalized();
        var spine = pelvisWorld.Basis.Column1;
        var headDir = new Vector3(spine.X, 0, spine.Z);
        headDir = headDir.LengthSquared() > 0.001f ? headDir.Normalized() : Forward(facingYaw);
        ragdoll.End();

        var yaw = Mathf.Atan2(headDir.X, headDir.Z);
        var root = pelvisWorld.Origin - headDir * GetUpPelvisOffset;
        var groundQuery = PhysicsRayQueryParameters3D.Create(root + Vector3.Up * 1.5f, root + Vector3.Down * 3f, Layers.PlayerMask, shotExclude);
        var ground = GetWorld3D().DirectSpaceState.IntersectRay(groundQuery);
        root.Y = ground.Count > 0 ? ground["position"].AsVector3().Y : Mathf.Max(0, root.Y - 0.1f);
        if (IsRemote && pendingRoot is { } given)
        {
            // Get up exactly where that player's own game got up.
            root = given;
            yaw = pendingYaw;
            pendingRoot = null;
        }
        else if (!IsRemote)
        {
            GotUp?.Invoke(root, yaw);
        }

        GlobalPosition = root;
        facingYaw = yaw;
        visual.Rotation = new Vector3(0, yaw, 0);
        capsule.Disabled = false;
        Velocity = Vector3.Zero;

        animator.PlayGetUp();
        poseBlend.BeginBlend(GetUpBlendTime);
        state = State.GettingUp;
        stateTimer = 0;
    }

    private void UpdateGettingUp(float dt)
    {
        stateTimer += dt;
        var duration = animator.GetUpDuration;
        var rigIn = Mathf.Clamp((stateTimer - (duration - RigReturnTime)) / RigReturnTime, 0, 1);
        rig.Weight = rigIn;
        animator.SetHoldWeight(rigIn);
        if (!IsRemote)
        {
            Velocity = new Vector3(0, Velocity.Y - Tuning.Current.PlayerGravity * dt, 0);
            MoveAndSlide();
        }
        if (stateTimer >= duration)
        {
            rig.Weight = 1f;
            animator.SetHoldWeight(1f);
            state = State.Active;
        }
    }

    // ------------------------------------------------------------------ Network

    /// <summary>This player's body and animation inputs, to send to the other players.</summary>
    public PlayerNetState CaptureNetState() => new()
    {
        Position = GlobalPosition,
        Velocity = Velocity,
        Yaw = facingYaw,
        AimTarget = aimTarget,
        Aim = aimAmount,
        Sprint = sprintAmount,
        Ready = readyAmount,
        Reload = Ammo?.ReloadProgress ?? 0f,
        Grounded = ResolvedState.Grounded,
        Gait = ResolvedState.Gait,
        Support = ResolvedState.Support,
        Weapon = ResolvedState.Weapon,
        State = state switch
        {
            State.Active => PlayerNetState.Mode.Active,
            State.GettingUp => PlayerNetState.Mode.GettingUp,
            _ => PlayerNetState.Mode.Ragdoll,
        },
        PelvisPosition = ragdoll.Pelvis.GlobalPosition,
        PelvisVelocity = ragdoll.Pelvis.LinearVelocity,
        Staggers = staggers,
    };

    /// <summary>The state a remote player should show now (already interpolated).</summary>
    public void SetRemoteState(PlayerNetState remote)
    {
        if (!hasRemoteState)
        {
            remoteStaggers = remote.Staggers;
        }
        remoteState = remote;
        hasRemoteState = true;
    }

    /// <summary>That player's game threw them: throw this copy the same way from where they stood.</summary>
    public void RemoteRagdoll(Vector3 at, Vector3 velocity, Vector3 spin)
    {
        ResetToActive();
        GlobalPosition = at;
        BeginRagdoll(velocity, spin);
    }

    /// <summary>That player's game got them up: settle this copy's ragdoll and get up at the same spot.</summary>
    public void RemoteGetUp(Vector3 root, float yaw)
    {
        pendingRoot = root;
        pendingYaw = yaw;
        if (state == State.Ragdoll)
        {
            poseBlend.RequestCapture();
            state = State.Capturing;
        }
    }

    /// <summary>Plays the muzzle flash and tracer of another player's shot.</summary>
    public void RemoteShot(Vector3 end)
    {
        rifle.Fire();
        Spawn.Effect(Tracer.Create(rifle.MuzzlePosition, end));
        rig.Kick();
        readyAmount = 1f;
    }

    private void UpdateRemote(float dt)
    {
        var tuning = Tuning.Current;
        if (state != State.Active)
        {
            ResolvedState = new PlayerState(state == State.GettingUp ? PlayerLifecycle.GettingUp : PlayerLifecycle.Ragdoll,
                PlayerSupport.Airborne, PlayerGait.Default, WeaponAction.Carry, 0, 0, 0, false, false, false);
        }
        switch (state)
        {
            case State.Active:
                if (!hasRemoteState)
                {
                    return;
                }
                GlobalPosition = remoteState.Position;
                Velocity = remoteState.Velocity;
                facingYaw = remoteState.Yaw;
                aimTarget = remoteState.AimTarget;
                aimAmount = remoteState.Weapon == WeaponAction.Aim && remoteState.Grounded ? remoteState.Aim : 0;
                sprintAmount = remoteState.Sprint;
                readyAmount = Mathf.Max(remoteState.Ready, readyAmount - dt * tuning.ReadyLowerSpeed);
                ResolvedState = new PlayerState(PlayerLifecycle.Active, remoteState.Support, remoteState.Gait,
                    remoteState.Weapon, 0, 0, 0, false, false, false);
                if (remoteState.Staggers != remoteStaggers)
                {
                    remoteStaggers = remoteState.Staggers;
                    animator.PlayHit();
                }
                break;
            case State.Ragdoll:
            {
                stateTimer += dt;
                // Their game has them standing again without a get-up here (a respawn, say): stand this copy up too.
                remoteUpTimer = hasRemoteState && remoteState.State == PlayerNetState.Mode.Active ? remoteUpTimer + dt : 0;
                if (remoteUpTimer > 1f)
                {
                    remoteUpTimer = 0;
                    ResetToActive();
                    break;
                }
                if (hasRemoteState && remoteState.State == PlayerNetState.Mode.Ragdoll)
                {
                    // Steer this copy's ragdoll after the real one: the limbs fall their own way, the body follows.
                    var error = remoteState.PelvisPosition - ragdoll.Pelvis.GlobalPosition;
                    if (error.Length() > tuning.SnapDistance)
                    {
                        foreach (var bone in ragdoll.Bones)
                        {
                            bone.GlobalPosition += error;
                        }
                    }
                    else
                    {
                        var desired = remoteState.PelvisVelocity + error / Mathf.Max(tuning.CorrectionBlendTime, 0.02f);
                        var change = (desired - ragdoll.Pelvis.LinearVelocity) * Mathf.Min(1f, dt * 8f);
                        foreach (var bone in ragdoll.Bones)
                        {
                            bone.LinearVelocity += change;
                        }
                    }
                }
                GlobalPosition = new Vector3(ragdoll.Pelvis.GlobalPosition.X, GlobalPosition.Y, ragdoll.Pelvis.GlobalPosition.Z);
                break;
            }
            case State.Capturing:
                if (poseBlend.HasCapture)
                {
                    BeginGetUp();
                }
                break;
            case State.GettingUp:
                UpdateGettingUp(dt);
                break;
        }
    }

    // ------------------------------------------------------------------ Helpers

    private static Vector3 Forward(float yaw) => new(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));

    private void UpdateBodyContrast()
    {
        if (bodyMaterial == null) { return; }
        var strength = AppearanceContrast.Strength(bodyColor) * Tuning.Current.AppearanceBodyContrast;
        var edge = AppearanceContrast.Edge(bodyColor);
        var power = Tuning.Current.AppearanceRimPower;
        var next = (bodyColor, edge, strength, power);
        if (bodyContrast == next) { return; }
        bodyContrast = next;
        if (strength <= 0) { bodyMaterial.NextPass = null; return; }
        bodyRim ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/appearance_rim.gdshader") };
        bodyRim.SetShaderParameter("edge_color", edge);
        bodyRim.SetShaderParameter("strength", strength);
        bodyRim.SetShaderParameter("rim_power", power);
        bodyMaterial.NextPass = bodyRim;
    }

    private void StyleMannequin()
    {
        var body = bodyMaterial = new StandardMaterial3D { AlbedoColor = bodyColor, Roughness = 0.5f, Metallic = 0.0f };
        UpdateBodyContrast();
        var joints = new StandardMaterial3D { AlbedoColor = new Color(0.13f, 0.13f, 0.14f), Roughness = 0.6f, Metallic = 0.2f };
        foreach (var mesh in GameAssets.MeshInstances(model))
        {
            mesh.Layers = RenderLayers.Actors;
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var name = mesh.Mesh.SurfaceGetMaterial(surface)?.ResourceName ?? "";
                mesh.SetSurfaceOverrideMaterial(surface, name.Contains("Joint") ? joints : body);
            }
        }
    }
}
