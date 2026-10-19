using Godot;
using Propane.Core;

namespace Propane.Player;

// Body presentation remains on the actor: skeleton poses also supply ragdoll recovery and muzzle placement.
public partial class PlayerCharacter
{
    private ActorPoseCadence poseCadence = null!;
    private bool poseThrottled, hasRiflePose;
    private Transform3D rifleInVisual;
    private (MeshInstance3D Mesh, float Bias)[] detailMeshes = [];
    private float meshDetail = 1;

    private bool PoseSample(float dt, out float sampleDelta)
    {
        var camera = World.Viewport.GetCamera3D();
        var forceFull = Runtime.HasLocalView || state != State.Active || camera == null ||
            camera.GetWorld3D() != World.Physics || animator.HitPlaying;
        var center = GlobalPosition + Vector3.Up;
        var interval = poseCadence.Interval(World.Tuning,
            camera == null ? 0 : camera.GlobalPosition.DistanceTo(center),
            camera == null || camera.IsPositionInFrustum(center), forceFull);
        var throttled = interval > 0;
        SetMeshDetail(throttled ? World.Tuning.ActorMeshLodBias : 1);
        if (poseThrottled != throttled)
        {
            poseThrottled = throttled;
            animator.SetThrottled(throttled);
            skeleton.ModifierCallbackModeProcess = throttled
                ? Skeleton3D.ModifierCallbackModeProcessEnum.Manual : Skeleton3D.ModifierCallbackModeProcessEnum.Idle;
        }
        return poseCadence.Advance(dt, interval, out sampleDelta);
    }

    private void RememberRiflePose()
    {
        if (!Initialized) return;
        rifleInVisual = visual.GlobalTransform.AffineInverse() * rifle.GlobalTransform;
        hasRiflePose = true;
    }

    private void ResetPoseCadence()
    {
        poseCadence.Reset();
        SetMeshDetail(1);
        poseThrottled = false;
        animator.SetThrottled(false);
        skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Idle;
    }

    private void SetMeshDetail(float bias)
    {
        if (meshDetail == bias) return;
        meshDetail = bias;
        foreach (var (mesh, original) in detailMeshes) mesh.LodBias = original * bias;
    }

    private void PlayHit()
    {
        if (poseThrottled)
        {
            // Consume only the old animation time before queuing this new one-shot. Its pose can become
            // physical if another hit throws us, so keep the reaction at full rate through its fade.
            animator.ApplyPose();
            animator.Sample(poseCadence.PendingTime);
            skeleton.Advance(poseCadence.PendingTime);
            ResetPoseCadence();
        }
        animator.PlayHit();
    }

    public override void _Process(double delta)
    {
        using var profile = ActorProfile.Measure(ActorProfile.System.Presentation);
        var dt = (float)delta;
        var tuning = World.Tuning;
        UpdateBodyContrast();
        var sample = PoseSample(dt, out var sampleDelta);
        if (!poseThrottled && sampleDelta > dt)
        {
            // Catch up the old tree before writing this frame's seeks. Its ordinary idle callback then
            // samples the exact current stride/jump phase, rather than advancing past a consumed seek.
            var pending = sampleDelta - dt;
            animator.Sample(pending);
            skeleton.Advance(pending);
        }
        animator.UpdateLocomotion(ResolvedState.Active ? BodyTravel(dt) : Vector2.Zero, dt, ResolvedState.Gait, sample);

        visual.Rotation = new Vector3(0, facingYaw, 0);
        rig.AimTarget = aimTarget;
        rig.AimAmount = aimAmount;
        rig.SprintAmount = sprintAmount;
        rig.ReadyAmount = readyAmount;
        rig.Reload = ResolvedState.Weapon == WeaponAction.Melee ? 0 : IsRemote ? remoteState.Reload : Ammo?.ReloadProgress ?? 0f;
        rig.MeleeProgress = IsRemote ? remoteState.Melee : ResolvedState.MeleeProgress;
        rig.Melee = ResolvedState.Weapon == WeaponAction.Melee;
        rig.AdvanceReload(dt, IsRemote, rig.Melee);
        if (sample && poseThrottled)
        {
            animator.Sample(sampleDelta);
            skeleton.Advance(sampleDelta);
        }
        else if (!sample && hasRiflePose)
        {
            // The rifle is top-level: carry its held pose with the moving body between skeleton samples.
            rifle.GlobalTransform = visual.GlobalTransform * rifleInVisual;
        }
        if (IsRemote)
        {
            return;
        }

        var aimFocus = state switch
        {
            State.Ragdoll or State.Capturing => ragdoll.Pelvis.GlobalPosition + Vector3.Up * 0.45f,
            _ => GlobalPosition + Vector3.Up * tuning.CameraHeight,
        };
        if (state == State.GettingUp)
        {
            var t = Mathf.Clamp(stateTimer / animator.GetUpDuration, 0, 1);
            var pelvis = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone("pelvis")).Origin;
            aimFocus = (pelvis + Vector3.Up * 0.45f).Lerp(GlobalPosition + Vector3.Up * tuning.CameraHeight, Mathf.SmoothStep(0, 1, t));
        }
        if (Spectating is { } watched && IsInstanceValid(watched))
        {
            aimFocus = watched.IsRagdolled ? watched.PelvisPosition + Vector3.Up * 0.45f : watched.GlobalPosition + Vector3.Up * tuning.CameraHeight;
        }
        Aim.Target = aimFocus;
        Aim.Advance(delta);
    }

    /// <summary>Planar travel in the body frame (x right, y forward) for the clips. Local players use their
    /// velocity; remote copies use the displayed displacement, so an interpolation stall stops the feet too.
    /// Turning on the spot steps sideways, at the speed the feet would swing around the body.</summary>
    private Vector2 BodyTravel(float dt)
    {
        var root = GlobalPosition;
        var velocity = Velocity;
        if (IsRemote)
        {
            // Displayed displacement, unless it is a snapshot correction: faster than anyone runs, it is a jump
            // to a corrected position, and the replicated velocity says how the player is really moving.
            velocity = hasDisplayedRoot ? (root - displayedRoot) / Mathf.Max(dt, 0.00001f) : Vector3.Zero;
            if (velocity.Length() > World.Tuning.SprintSpeed * 1.5f) velocity = Velocity;
        }
        // An aimed body turns in short bursts as the look passes CombatTurnDegrees; smooth them into one turn.
        var turn = hasDisplayedRoot ? Mathf.Wrap(facingYaw - displayedYaw, -Mathf.Pi, Mathf.Pi) / Mathf.Max(dt, 0.00001f) : 0;
        turnRate = Mathf.Lerp(turnRate, turn, 1 - Mathf.Exp(-dt / Mathf.Max(World.Tuning.GaitBlendTime, 0.001f)));
        displayedRoot = root;
        displayedYaw = facingYaw;
        hasDisplayedRoot = true;
        var facing = new Basis(Vector3.Up, facingYaw);
        var travel = new Vector2(velocity.Dot(facing.Column0), -velocity.Dot(facing.Column2));
        // A left turn (positive yaw) leads with the left foot; a quick turn steps no faster than the creep.
        var step = new Vector2(-Mathf.Clamp(turnRate * World.Tuning.TurnStepRadius, -World.Tuning.CreepSpeed, World.Tuning.CreepSpeed), 0);
        return step.LengthSquared() > travel.LengthSquared() ? step : travel;
    }

    private void UpdateBodyContrast()
    {
        if (bodyMaterial == null) { return; }
        var strength = AppearanceContrast.Strength(bodyColor, World.Tuning) * World.Tuning.AppearanceBodyContrast;
        var edge = AppearanceContrast.Edge(bodyColor, World.Tuning);
        var power = World.Tuning.AppearanceRimPower;
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
