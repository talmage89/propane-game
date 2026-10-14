using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// Camera-independent aiming state. Persistent recoil belongs to input orientation; weapon and camera kick are presentation only.
/// </summary>
public sealed class LogicalAim : System.IDisposable
{
    private readonly WorldContext World;

    public LogicalAim(WorldContext world)
    {
        World = world;
        currentDistance = world.Tuning.CameraDistance;
        cameraQuery = new PhysicsShapeQueryParameters3D
        {
            Shape = probe,
            CollisionMask = Layers.World | Layers.Props | Layers.Floor,
        };
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
    private float recoilSettle;
    private float trauma;
    private float shakeTime;
    private float currentDistance;
    private Vector3 smoothedTarget;
    private bool hasTarget;
    private readonly PhysicsShapeQueryParameters3D cameraQuery;
    private readonly SphereShape3D probe = new() { Radius = CollisionRadius };
    private readonly FastNoiseLite shakeNoise = new() { Frequency = 0.9f, NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin };

    public Transform3D Transform { get; private set; } = Transform3D.Identity;
    public Transform3D EyeTransform { get; private set; } = Transform3D.Identity;
    public Transform3D EyeOffset { get; private set; } = Transform3D.Identity;
    public Vector3 EyeOrigin => EyeTransform.Origin;
    public Vector3 Forward => -EyeTransform.Basis.Z;

    /// <summary>The point the camera orbits (the player's head height, or the ragdoll's hips).</summary>
    public Vector3 Target { get; set; }

    /// <summary>Bodies the camera collision ignores (the player and its ragdoll).</summary>
    public Godot.Collections.Array<Rid> Exclude { get; } = new();

    public float Yaw => yaw;

    public float Pitch => pitch;

    /// <summary>Jumps straight to the target next frame instead of easing there, for teleports.</summary>
    public void Snap()
    {
        hasTarget = false;
        currentDistance = World.Tuning.CameraDistance;
        trauma = 0;
        recoilSettle = 0;
    }

    public void SetOrientation(float yawDegrees, float pitchDegrees)
    {
        yaw = yawDegrees;
        pitch = Mathf.Clamp(pitchDegrees, PitchMin, PitchMax);
        recoilSettle = 0;
        RefreshOrientation();
    }

    public void Look(Vector2 mouseDelta, Vector2 stick = default, float dt = 0)
    {
        var sensitivity = Mathf.RadToDeg(World.Tuning.MouseSensitivity);
        var displacement = mouseDelta * sensitivity + StickLookDelta(stick, dt,
            World.Tuning.StickLookDegreesPerSecond, World.Tuning.StickLookCurve);
        Rotate(-displacement);
    }

    /// <summary>Device-independent look displacement in degrees; bots use it to pull against recoil.</summary>
    public void Rotate(Vector2 degrees)
    {
        yaw += degrees.X;
        pitch = Mathf.Clamp(pitch + degrees.Y, PitchMin, PitchMax);
        RefreshOrientation();
    }

    /// <summary>Radial response preserves stick direction; rate integration is independent of tick frequency.</summary>
    public static Vector2 StickLookDelta(Vector2 stick, float dt, float degreesPerSecond, float curve)
    {
        var length = Mathf.Min(stick.Length(), 1f);
        return length > 0 ? stick.Normalized() * Mathf.Pow(length, curve) * degreesPerSecond * dt : Vector2.Zero;
    }

    /// <summary>Move the player's aim immediately. Only a small bounded part can settle automatically.</summary>
    public void Kick(float degrees)
    {
        var before = pitch;
        pitch = Mathf.Clamp(pitch + degrees, PitchMin, PitchMax);
        recoilSettle += (pitch - before) * World.Tuning.RecoilReturnShare;
        yaw += World.Random.RandfRange(-1, 1) * World.Tuning.RecoilYawDegrees
            * (World.Tuning.RecoilKickDegrees > 0 ? degrees / World.Tuning.RecoilKickDegrees : 0);
        RefreshOrientation();
    }

    private void RefreshOrientation()
    {
        Transform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(yaw))
            * new Basis(Vector3.Right, Mathf.DegToRad(pitch)), Transform.Origin);
        EyeTransform = Transform * EyeOffset;
    }

    public void AddTrauma(float amount) => trauma = Mathf.Clamp(trauma + amount, 0, 1);

    public void Advance(double delta)
    {
        using var profile = ActorProfile.Measure(ActorProfile.System.Aim);
        var dt = (float)delta;
        var tuning = World.Tuning;

        var settled = recoilSettle * (1 - Mathf.Exp(-tuning.RecoilSettleSpeed * dt));
        recoilSettle -= settled;
        pitch = Mathf.Clamp(pitch - settled, PitchMin, PitchMax);

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
        Transform = new Transform3D(basis, shoulder + back * currentDistance);

        // Trauma shake, squared so small bumps stay subtle.
        trauma = Mathf.Max(0, trauma - TraumaDecay * dt);
        shakeTime += dt;
        var shake = trauma * trauma * tuning.CameraShake;
        float N(float seed) => shakeNoise.GetNoise2D(shakeTime * 25f, seed);
        var rotation = new Vector3(Mathf.DegToRad(N(1) * MaxShakeDegrees * shake), Mathf.DegToRad(N(2) * MaxShakeDegrees * shake),
            Mathf.DegToRad(N(3) * MaxShakeDegrees * 0.6f * shake));
        var offset = new Vector3(N(4), N(5), 0) * MaxShakeOffset * shake;
        EyeOffset = new Transform3D(Basis.FromEuler(rotation), offset);
        EyeTransform = Transform * EyeOffset;
    }

    private Vector3 CastClear(Vector3 from, Vector3 to)
    {
        using var profile = ActorProfile.Measure(ActorProfile.System.AimQuery);
        var space = World.Physics.DirectSpaceState;
        var motion = to - from;
        if (motion.LengthSquared() < 0.0001f)
        {
            return to;
        }
        cameraQuery.Transform = new Transform3D(Basis.Identity, from);
        cameraQuery.Motion = motion;
        // Callers can update exclusions; reuse the query without retaining an obsolete exclusion set.
        cameraQuery.Exclude = Exclude;
        var result = space.CastMotion(cameraQuery);
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
        cameraQuery.Dispose();
        probe.Dispose();
        shakeNoise.Dispose();
        Exclude.Clear();
    }
}
