using Godot;
using Propane.Core;

namespace Propane.Player;

// Body presentation remains on the actor: skeleton poses also supply ragdoll recovery and muzzle placement.
public partial class PlayerCharacter
{
    public override void _Process(double delta)
    {
        var dt = (float)delta;
        var tuning = World.Tuning;
        UpdateBodyContrast();
        var planar = feet.SamplePlanarVelocity(dt);
        animator.UpdateLocomotion(ResolvedState.Active ? planar.Length() : 0f, dt, ResolvedState.Gait);

        visual.Rotation = new Vector3(0, facingYaw, 0);
        rig.AimTarget = aimTarget;
        rig.AimAmount = aimAmount;
        rig.SprintAmount = sprintAmount;
        rig.ReadyAmount = readyAmount;
        rig.Reload = ResolvedState.Weapon == WeaponAction.Melee ? 0 : IsRemote ? remoteState.Reload : Ammo?.ReloadProgress ?? 0f;
        rig.MeleeProgress = IsRemote ? remoteState.Melee : ResolvedState.MeleeProgress;
        rig.Melee = ResolvedState.Weapon == WeaponAction.Melee;
        rig.AdvanceReload(dt, IsRemote, rig.Melee);
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
        Aim.Target = aimFocus;
        Aim.Advance(delta);
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
