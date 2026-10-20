using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>A rendered follower of logical aim. Standalone review cameras own their own aim.</summary>
public partial class CameraRig : Node3D
{
    public required WorldContext World { get; init; }
    public LogicalAim? Aim { get; init; }
    public IPlayerSettings? Preferences { get; init; }
    private LogicalAim aim = null!;
    private readonly RecoilSpring recoil = new();
    public Camera3D Camera { get; private set; } = null!;
    public Vector3 Target { get => aim.Target; set => aim.Target = value; }
    public Godot.Collections.Array<Rid> Exclude => aim.Exclude;
    public float Yaw => aim.Yaw;
    public float Pitch => aim.Pitch;

    public override void _Ready()
    {
        TopLevel = true;
        aim = Aim ?? new LogicalAim(World);
        Camera = new Camera3D { Name = "Camera", Current = true, Near = 0.05f, Far = 2000f };
        AddChild(Camera);
        if (Aim == null) World.Events.Explosion += aim.OnExplosion;
        if (Preferences != null) Preferences.PreferenceChanged += OnPreferenceChanged;
        ApplyCameraPreferences();
    }

    public override void _ExitTree()
    {
        if (Preferences != null) Preferences.PreferenceChanged -= OnPreferenceChanged;
        if (Aim != null) return;
        World.Events.Explosion -= aim.OnExplosion;
        aim.Dispose();
    }

    public void Snap() => aim.Snap();
    public void SetOrientation(float yawDegrees, float pitchDegrees) => aim.SetOrientation(yawDegrees, pitchDegrees);
    public void Look(Vector2 mouseDelta, Vector2 stick = default, float dt = 0) => aim.Look(mouseDelta, stick, dt);
    public static Vector2 StickLookDelta(Vector2 stick, float dt, float degreesPerSecond, float curve) =>
        LogicalAim.StickLookDelta(stick, dt, degreesPerSecond, curve);
    public void Kick(float degrees) => aim.Kick(degrees);
    public void KickPresentation(float scale) => recoil.Kick(World.Tuning.RifleRecoilFrequency, World.Tuning.CameraRecoilKickDegrees * scale);
    public void AddTrauma(float amount) => aim.AddTrauma(amount);

    public override void _Process(double delta)
    {
        if (Aim == null) aim.Advance(delta);
        GlobalTransform = aim.Transform;
        recoil.Update((float)delta, World.Tuning.RifleRecoilFrequency, World.Tuning.RifleRecoilDamping);
        ApplyCameraPreferences();
    }

    private void OnPreferenceChanged(PreferenceDefinition preference)
    {
        if (preference.Target == PreferenceTarget.Camera) ApplyCameraPreferences();
    }

    private void ApplyCameraPreferences()
    {
        var eye = aim.EyeOffset;
        var strength = Preferences == null ? 1 : (float)Preferences.GetPreference(PreferenceCatalog.CameraShake);
        if (strength < 1) eye = new Transform3D(Basis.Identity.Slerp(eye.Basis, strength), eye.Origin * strength);
        Camera.Transform = eye * new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(recoil.Position)), Vector3.Zero);
        var fovScale = Preferences == null ? 1 : (float)(Preferences.GetPreference(PreferenceCatalog.FieldOfView) / PreferenceCatalog.FieldOfView.Default);
        Camera.Fov = Preferences == null ? World.Tuning.FieldOfView : Mathf.Clamp(World.Tuning.FieldOfView * fovScale, 30, 110);
    }
}
