using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// Over-the-shoulder orbit camera. It follows a target point with a little lag, orbits with the mouse, pulls in
/// when geometry is behind it, and adds trauma-based shake and recoil kick. Aiming does not move it.
/// </summary>
public partial class CameraRig : Node3D
{
    private const float PitchMin = -75f;
    private const float PitchMax = 45f;
    private const float FollowSharpness = 18f;
    private const float CollisionRadius = 0.22f;
    private const float RecoilRecovery = 7f;
    private const float RecoilReturnShare = 0.65f;
    private const float TraumaDecay = 1.6f;
    private const float MaxShakeDegrees = 3.2f;
    private const float MaxShakeOffset = 0.12f;

    private float yaw;
    private float pitch = -8f;
    private float recoilPitch;
    private float trauma;
    private float shakeTime;
    private float currentDistance;
    private Vector3 smoothedTarget;
    private bool hasTarget;
    private readonly SphereShape3D probe = new() { Radius = CollisionRadius };
    private readonly FastNoiseLite shakeNoise = new() { Frequency = 0.9f, NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin };

    public Camera3D Camera { get; private set; } = null!;

    /// <summary>The point the camera orbits (the player's head height, or the ragdoll's hips).</summary>
    public Vector3 Target { get; set; }

    /// <summary>Bodies the camera collision ignores (the player and its ragdoll).</summary>
    public Godot.Collections.Array<Rid> Exclude { get; } = new();

    public float Yaw => yaw;

    public float Pitch => pitch + recoilPitch;

    public override void _Ready()
    {
        TopLevel = true;
        Camera = new Camera3D { Name = "Camera", Current = true, Near = 0.05f, Far = 2000f };
        AddChild(Camera);
        currentDistance = Tuning.Current.CameraDistance;
        GameEvents.Explosion += OnExplosion;
    }

    public override void _ExitTree() => GameEvents.Explosion -= OnExplosion;

    /// <summary>Jumps straight to the target next frame instead of easing there, for teleports.</summary>
    public void Snap()
    {
        hasTarget = false;
        currentDistance = Tuning.Current.CameraDistance;
        trauma = 0;
        recoilPitch = 0;
    }

    public void SetOrientation(float yawDegrees, float pitchDegrees)
    {
        yaw = yawDegrees;
        pitch = pitchDegrees;
    }

    public void Look(Vector2 mouseDelta)
    {
        var sensitivity = Mathf.RadToDeg(Tuning.Current.MouseSensitivity);
        yaw -= mouseDelta.X * sensitivity;
        pitch = Mathf.Clamp(pitch - mouseDelta.Y * sensitivity, PitchMin, PitchMax);
    }

    /// <summary>Recoil: kicks the view up; most of it recovers on its own.</summary>
    public void Kick(float degrees)
    {
        recoilPitch += degrees;
        yaw += (float)GD.RandRange(-0.25, 0.25) * degrees;
    }

    public void AddTrauma(float amount) => trauma = Mathf.Clamp(trauma + amount, 0, 1);

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        var tuning = Tuning.Current;
        Camera.Fov = tuning.FieldOfView;

        // Recoil recovers toward a fraction of the kick, so sustained fire climbs a little.
        var recovered = recoilPitch * (1f - Mathf.Exp(-RecoilRecovery * dt));
        recoilPitch -= recovered;
        pitch = Mathf.Clamp(pitch + recovered * (1f - RecoilReturnShare), PitchMin, PitchMax);

        if (!hasTarget)
        {
            smoothedTarget = Target;
            hasTarget = true;
        }
        smoothedTarget = smoothedTarget.Lerp(Target, 1f - Mathf.Exp(-FollowSharpness * dt));

        var basis = new Basis(Vector3.Up, Mathf.DegToRad(yaw)) * new Basis(Vector3.Right, Mathf.DegToRad(Pitch));
        var right = basis.Column0;
        var back = basis.Column2;

        // Shoulder offset first, then pull back; each leg is collision-checked so the camera never enters walls.
        var shoulder = CastClear(smoothedTarget, smoothedTarget + right * tuning.ShoulderOffset);
        // Looking up swings the camera down behind the player; easing it in keeps it off the ground.
        var lookUp = Mathf.SmoothStep(5f, PitchMax, Pitch);
        var desired = shoulder + back * tuning.CameraDistance * Mathf.Lerp(1f, 0.6f, lookUp);
        var clear = CastClear(shoulder, desired);
        var clearDistance = shoulder.DistanceTo(clear);
        // Pull in instantly, ease back out.
        currentDistance = clearDistance < currentDistance
            ? clearDistance
            : Mathf.Lerp(currentDistance, clearDistance, 1f - Mathf.Exp(-6f * dt));
        GlobalTransform = new Transform3D(basis, shoulder + back * currentDistance);

        // Trauma shake, squared so small bumps stay subtle.
        trauma = Mathf.Max(0, trauma - TraumaDecay * dt);
        shakeTime += dt;
        var shake = trauma * trauma * tuning.CameraShake;
        float N(float seed) => shakeNoise.GetNoise2D(shakeTime * 25f, seed);
        Camera.Rotation = new Vector3(Mathf.DegToRad(N(1) * MaxShakeDegrees * shake), Mathf.DegToRad(N(2) * MaxShakeDegrees * shake),
            Mathf.DegToRad(N(3) * MaxShakeDegrees * 0.6f * shake));
        Camera.Position = new Vector3(N(4), N(5), 0) * MaxShakeOffset * shake;
    }

    private Vector3 CastClear(Vector3 from, Vector3 to)
    {
        var space = GetWorld3D().DirectSpaceState;
        var motion = to - from;
        if (motion.LengthSquared() < 0.0001f)
        {
            return to;
        }
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = probe,
            Transform = new Transform3D(Basis.Identity, from),
            Motion = motion,
            // The void floor too, so looking up from outside the suburb never puts the camera under it.
            CollisionMask = Layers.World | Layers.Props | Layers.Floor,
            Exclude = Exclude,
        };
        var result = space.CastMotion(query);
        var safe = result.Length > 0 ? result[0] : 1f;
        return from + motion * safe;
    }

    private void OnExplosion(Vector3 position)
    {
        var tuning = Tuning.Current;
        var distance = GlobalPosition.DistanceTo(position);
        var closeness = Mathf.Clamp(1f - distance / tuning.CameraShakeDistance, 0, 1);
        AddTrauma(closeness * closeness * 1.1f + closeness * 0.25f);
    }
}
