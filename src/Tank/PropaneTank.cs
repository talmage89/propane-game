using System;
using Godot;
using Propane.Core;
using Propane.Fx;

namespace Propane.Tank;

/// <summary>
/// A 20 lb propane cylinder. The first shot punctures it and it vents fire forever, pushed by its own jet. A second
/// shot detonates it. A nearby blast counts as a shot, after a short fuse, so tanks set each other off.
/// </summary>
public partial class PropaneTank : RigidBody3D
{
    public enum TankState
    {
        Intact,
        Venting,
        Exploded,
    }

    /// <summary>Centre of the vessel in the tank's local frame (origin is the centre of the foot ring).</summary>
    public static readonly Vector3 LocalCenter = new(0, 0.235f, 0);

    private const float ShellRadius = 0.156f;
    private const float HoleDecalSize = 0.07f;
    private const float PunctureKick = 6f;

    private VentJet? vent;
    private Vector3 holeLocal;
    private Vector3 holeNormalLocal;
    private Vector3 jetDirectionLocal;
    private float ventAge;
    private bool fuseLit;
    private float flutterPhase;

    public TankState State { get; private set; } = TankState.Intact;

    /// <summary>Raised once, the moment the tank explodes.</summary>
    public event Action<PropaneTank>? Detonated;

    /// <summary>Instantiates a ready-to-place tank.</summary>
    public static PropaneTank Create() => new() { Name = "PropaneTank" };

    public override void _Ready()
    {
        Mass = Tuning.Current.TankMass;
        CollisionLayer = Layers.Tanks;
        CollisionMask = Layers.DynamicMask;
        ContinuousCd = true;
        CenterOfMassMode = CenterOfMassModeEnum.Custom;
        CenterOfMass = new Vector3(0, 0.19f, 0);
        PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.55f, Bounce = 0.2f };
        AngularDamp = 0.6f;
        LinearDamp = 0.05f;

        AddChild(new CollisionShape3D { Shape = GameAssets.TankShape });
        var mesh = new MeshInstance3D { Mesh = GameAssets.TankMesh, Layers = RenderLayers.Actors, Name = "Mesh" };
        AddChild(mesh);
        GameAssets.ApplyTankMaterials(mesh);
        flutterPhase = GD.Randf() * 100f;
    }

    /// <summary>A bullet hit at a world point with the surface normal there.</summary>
    public void TakeShot(Vector3 point, Vector3 normal, Vector3 shotDirection)
    {
        switch (State)
        {
            case TankState.Intact:
                Puncture(point, normal);
                ApplyImpulse(shotDirection * PunctureKick, point - GlobalPosition);
                break;
            case TankState.Venting:
                Detonate();
                break;
        }
    }

    /// <summary>A blast went off within chain range: punctures an intact tank, or detonates a venting one after a fuse.</summary>
    public void ChainHit(Vector3 blastCenter, float distance)
    {
        if (State == TankState.Exploded || fuseLit)
        {
            return;
        }
        if (State == TankState.Intact)
        {
            var (point, normal) = SurfaceFacing(blastCenter);
            Puncture(point, normal);
            return;
        }
        fuseLit = true;
        var tuning = Tuning.Current;
        var closeness = Mathf.Clamp(distance / Mathf.Max(tuning.ChainRadius, 0.01f), 0, 1);
        var fuse = Mathf.Lerp(tuning.ChainFuseMin, tuning.ChainFuseMax, closeness) * (float)GD.RandRange(0.85, 1.15);
        GetTree().CreateTimer(fuse, processAlways: false, processInPhysics: true).Timeout += () =>
        {
            if (IsInstanceValid(this))
            {
                Detonate();
            }
        };
    }

    public override void _PhysicsProcess(double delta)
    {
        if (State != TankState.Venting)
        {
            return;
        }
        var tuning = Tuning.Current;
        ventAge += (float)delta;
        var ramp = tuning.VentThrustRampTime > 0 ? Mathf.Clamp(ventAge / tuning.VentThrustRampTime, 0, 1) : 1f;
        var t = ventAge + flutterPhase;
        var flutter = 1f + tuning.VentThrustFlutter * (0.6f * Mathf.Sin(t * 23f) + 0.4f * Mathf.Sin(t * 37f + 2f));
        var jet = GlobalBasis * jetDirectionLocal;
        ApplyForce(-jet * tuning.VentThrust * ramp * flutter, GlobalBasis * holeLocal);
        if (AngularVelocity.LengthSquared() > tuning.VentMaxSpin * tuning.VentMaxSpin)
        {
            AngularVelocity = AngularVelocity.LimitLength(tuning.VentMaxSpin);
        }
    }

    /// <summary>Blows the tank apart: effects, debris, scorch, and the blast itself.</summary>
    public void Detonate()
    {
        if (State == TankState.Exploded)
        {
            return;
        }
        State = TankState.Exploded;
        var center = GlobalTransform * LocalCenter;
        var groundY = GroundHeightBelow(center);

        vent?.Extinguish();
        ExplosionEffect.Create(center, groundY);
        SpawnDebris(center);
        SpawnScorch(new Vector3(center.X, groundY, center.Z));
        Blast.Detonate(this, center, this);
        Detonated?.Invoke(this);
        QueueFree();
    }

    private void Puncture(Vector3 point, Vector3 normal)
    {
        State = TankState.Venting;
        CanSleep = false;
        Sleeping = false;
        holeLocal = ToLocal(point);
        holeNormalLocal = (GlobalBasis.Inverse() * normal).Normalized();
        // A torn hole rarely vents straight out: lean the jet around the tank's axis so its thrust has a
        // tangential part. Upright, that spins the tank; lying down, it rolls and pinwheels across the ground.
        var swirl = Mathf.DegToRad(Tuning.Current.VentSwirlDegrees) * (GD.Randf() < 0.5f ? -1f : 1f);
        jetDirectionLocal = holeNormalLocal.Rotated(Vector3.Up, swirl).Normalized();

        var holeBasis = BasisFacing(jetDirectionLocal);
        vent = new VentJet { Name = "VentJet", Transform = new Transform3D(holeBasis, holeLocal) };
        AddChild(vent);

        // Bullet hole decal: a decal projects along its local -Y, so point +Y out of the surface.
        var surfaceBasis = BasisFacing(holeNormalLocal);
        var decalBasis = new Basis(surfaceBasis.Column0, surfaceBasis.Column2, -surfaceBasis.Column1);
        AddChild(new Decal
        {
            Name = "BulletHole",
            Transform = new Transform3D(decalBasis, holeLocal),
            Size = new Vector3(HoleDecalSize, 0.05f, HoleDecalSize),
            TextureAlbedo = FxLibrary.BulletHoleTexture,
            CullMask = RenderLayers.Actors,
            UpperFade = 0.1f,
            LowerFade = 0.1f,
        });
    }

    /// <summary>The point on the vessel's side facing a world position, and the outward normal there.</summary>
    private (Vector3 Point, Vector3 Normal) SurfaceFacing(Vector3 worldTarget)
    {
        var local = ToLocal(worldTarget) - LocalCenter;
        var flat = new Vector3(local.X, 0, local.Z);
        if (flat.LengthSquared() < 0.0001f)
        {
            flat = Vector3.Forward;
        }
        flat = flat.Normalized();
        var height = Mathf.Clamp(local.Y * 0.2f, -0.06f, 0.08f);
        var localPoint = LocalCenter + flat * ShellRadius + Vector3.Up * height;
        return (ToGlobal(localPoint), (GlobalBasis * flat).Normalized());
    }

    private static Basis BasisFacing(Vector3 forward)
    {
        var up = Mathf.Abs(forward.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
        var x = up.Cross(forward).Normalized();
        var y = forward.Cross(x).Normalized();
        return new Basis(x, y, forward);
    }

    private float GroundHeightBelow(Vector3 from)
    {
        var space = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(from + Vector3.Up * 0.1f, from + Vector3.Down * 30f, Layers.World | Layers.Floor);
        var hit = space.IntersectRay(query);
        return hit.Count > 0 ? hit["position"].AsVector3().Y : 0f;
    }

    private void SpawnDebris(Vector3 center)
    {
        var tuning = Tuning.Current;
        var rng = new RandomNumberGenerator();
        var inherited = LinearVelocity;
        foreach (var piece in GameAssets.Debris)
        {
            var body = DebrisBody.Create(piece);
            Spawn.InWorld(body);
            body.GlobalTransform = GlobalTransform;
            var pieceCenter = GlobalTransform * piece.Centroid;
            var outward = pieceCenter - center;
            outward = outward.LengthSquared() > 0.0001f ? outward.Normalized() : Vector3.Up;
            var speed = tuning.DebrisSpeed * rng.RandfRange(0.45f, 1.15f);
            body.LinearVelocity = inherited + (outward + Vector3.Up * rng.RandfRange(0.3f, 1.1f)).Normalized() * speed;
            body.AngularVelocity = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)) * tuning.DebrisSpin;
        }
    }

    private static void SpawnScorch(Vector3 groundPoint)
    {
        var size = Tuning.Current.ScorchSize;
        if (size <= 0)
        {
            return;
        }
        var decal = new Decal
        {
            Name = "Scorch",
            Size = new Vector3(size, 2.4f, size),
            TextureAlbedo = FxLibrary.ScorchTexture,
            CullMask = RenderLayers.Static,
            UpperFade = 0.3f,
            LowerFade = 0.3f,
            NormalFade = 0.2f,
            AlbedoMix = 1f,
        };
        Spawn.InWorld(decal);
        decal.GlobalPosition = groundPoint;
        decal.RotateY((float)GD.RandRange(0, Mathf.Tau));
    }
}
