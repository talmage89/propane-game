using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// Camera-independent aiming state. The historical orbit, collision, recoil and shake all affected shot rays;
/// retain their calculation and render-frame timing even for actors with no view.
/// </summary>
public sealed class LogicalAim : System.IDisposable
{
    private readonly WorldContext World;

    public LogicalAim(WorldContext world)
    {
        World = world;
        currentDistance = world.Tuning.CameraDistance;
    }

    private const float PitchMin = -75f;
    private const float PitchMax = 45f;
    private const float FollowSharpness = 18f;
    private const float CollisionRadius = 0.22f;
    private const float TraumaDecay = 1.6f;
    private const float MaxShakeDegrees = 3.2f;
    private const float MaxShakeOffset = 0.12f;

    private float yaw;
    private float pitch = -8f;
    private float recoilPitch;
    private float recoilYaw;
    private float trauma;
    private float shakeTime;
    private float currentDistance;
    private Vector3 smoothedTarget;
    private bool hasTarget;
    private readonly SphereShape3D probe = new() { Radius = CollisionRadius };
    private readonly FastNoiseLite shakeNoise = new() { Frequency = 0.9f, NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin };

    public Transform3D Transform { get; private set; } = Transform3D.Identity;
    public Transform3D EyeTransform { get; private set; } = Transform3D.Identity;
    public Vector3 EyeOrigin => EyeTransform.Origin;
    public Vector3 Forward => -EyeTransform.Basis.Z;

    /// <summary>The point the camera orbits (the player's head height, or the ragdoll's hips).</summary>
    public Vector3 Target { get; set; }

    /// <summary>Bodies the camera collision ignores (the player and its ragdoll).</summary>
    public Godot.Collections.Array<Rid> Exclude { get; } = new();

    public float Yaw => yaw;

    public float Pitch => pitch + recoilPitch;

    /// <summary>Jumps straight to the target next frame instead of easing there, for teleports.</summary>
    public void Snap()
    {
        hasTarget = false;
        currentDistance = World.Tuning.CameraDistance;
        trauma = 0;
        recoilPitch = recoilYaw = 0;
    }

    public void SetOrientation(float yawDegrees, float pitchDegrees)
    {
        yaw = yawDegrees;
        pitch = pitchDegrees;
    }

    public void Look(Vector2 mouseDelta, Vector2 stick = default, float dt = 0)
    {
        var sensitivity = Mathf.RadToDeg(World.Tuning.MouseSensitivity);
        var displacement = mouseDelta * sensitivity + StickLookDelta(stick, dt,
            World.Tuning.StickLookDegreesPerSecond, World.Tuning.StickLookCurve);
        yaw -= displacement.X;
        pitch = Mathf.Clamp(pitch - displacement.Y, PitchMin, PitchMax);
    }

    /// <summary>Radial response preserves stick direction; rate integration is independent of tick frequency.</summary>
    public static Vector2 StickLookDelta(Vector2 stick, float dt, float degreesPerSecond, float curve)
    {
        var length = Mathf.Min(stick.Length(), 1f);
        return length > 0 ? stick.Normalized() * Mathf.Pow(length, curve) * degreesPerSecond * dt : Vector2.Zero;
    }

    /// <summary>Transient view kick. Recovery leaves the player's input orientation unchanged.</summary>
    public void Kick(float degrees)
    {
        recoilPitch += degrees;
        recoilYaw += World.Random.RandiRange(-1, 1) * degrees * World.Tuning.CameraRecoilSideShare;
    }

    public void AddTrauma(float amount) => trauma = Mathf.Clamp(trauma + amount, 0, 1);

    public void Advance(double delta)
    {
        var dt = (float)delta;
        var tuning = World.Tuning;

        var recoilReturn = Mathf.Exp(-tuning.CameraRecoilRecovery * dt);
        recoilPitch *= recoilReturn;
        recoilYaw *= recoilReturn;

        if (!hasTarget)
        {
            smoothedTarget = Target;
            hasTarget = true;
        }
        smoothedTarget = smoothedTarget.Lerp(Target, 1f - Mathf.Exp(-FollowSharpness * dt));

        var basis = new Basis(Vector3.Up, Mathf.DegToRad(yaw + recoilYaw)) * new Basis(Vector3.Right, Mathf.DegToRad(Pitch));
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
        Transform = new Transform3D(basis, shoulder + back * currentDistance);

        // Trauma shake, squared so small bumps stay subtle.
        trauma = Mathf.Max(0, trauma - TraumaDecay * dt);
        shakeTime += dt;
        var shake = trauma * trauma * tuning.CameraShake;
        float N(float seed) => shakeNoise.GetNoise2D(shakeTime * 25f, seed);
        var rotation = new Vector3(Mathf.DegToRad(N(1) * MaxShakeDegrees * shake), Mathf.DegToRad(N(2) * MaxShakeDegrees * shake),
            Mathf.DegToRad(N(3) * MaxShakeDegrees * 0.6f * shake));
        var offset = new Vector3(N(4), N(5), 0) * MaxShakeOffset * shake;
        EyeTransform = Transform * new Transform3D(Basis.FromEuler(rotation), offset);
    }

    private Vector3 CastClear(Vector3 from, Vector3 to)
    {
        var space = World.Physics.DirectSpaceState;
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

    public void OnExplosion(Vector3 position)
    {
        var tuning = World.Tuning;
        var distance = Transform.Origin.DistanceTo(position);
        var closeness = Mathf.Clamp(1f - distance / tuning.CameraShakeDistance, 0, 1);
        AddTrauma(closeness * closeness * 1.1f + closeness * 0.25f);
    }
    private bool disposed;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        probe.Dispose();
        shakeNoise.Dispose();
        Exclude.Clear();
    }
}
