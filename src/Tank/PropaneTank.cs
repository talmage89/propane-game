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
    public required WorldContext World { get; init; }

    public enum TankState
    {
        Intact,
        Venting,
        Exploded,
    }

    /// <summary>Center of the vessel in the tank's local frame (origin is the center of the foot ring).</summary>
    public static readonly Vector3 LocalCenter = Propane.World.Plan.PileBuilder.LocalCenter;

    private const float ShellRadius = 0.156f;
    private const float HoleDecalSize = 0.07f;
    private const float PunctureKick = 6f;

    private Transform3D? pilePose;
    private Transform3D? restPose;

    public required Spawn ContentSpawn { get; init; }
    private VentJet? vent;
    private Decal? holeDecal;
    private uint fuseGeneration;

    // Tests hold/replay the production fuse callback without advancing a scene clock.
    internal Action<float, Action>? FuseScheduleOverride { get; set; }
    private MeshInstance3D mesh = null!;
    private Vector3 holeLocal;
    private Vector3 holeNormalLocal;
    private Vector3 jetDirectionLocal;
    private float swirlSign = 1f;
    private float ventAge;
    private bool fuseLit;
    private float flutterPhase;
    // Fresh owner velocities replace speculative observer jet impulses. Their rotation-error steering
    // must also survive integration; owners, stale observers and local predictions keep ordinary physics.
    internal bool CorrectingMotion { get; set; }
    private double invulnerableUntil;
    private Color shieldColor = Colors.White;

    public TankState State { get; private set; } = TankState.Intact;

    /// <summary>Changes on hits, pushes and motion, even if the tank rests again between gather scans.</summary>
    public ulong Disturbance { get; private set; }

    public bool Gathering { get; private set; }

    internal Transform3D? PilePose => pilePose;

    /// <summary>The tank's id in a match (0 in single player), shared by every player's copy.</summary>
    public uint NetId { get; set; }

    /// <summary>Where the hole is, in the tank's own frame, once punctured.</summary>
    public Vector3 HoleLocal => holeLocal;

    public Vector3 HoleNormalLocal => holeNormalLocal;

    public Vector3 JetDirectionLocal => jetDirectionLocal;

    /// <summary>Immutable initiating explosion request for a network chain; 0 for a direct shot.</summary>
    public uint ChainCause { get; private set; }
    public int CauseActorId { get; private set; }

    /// <summary>Assigned by the match before this explosion starts its child chain.</summary>
    public uint BlastRequest { get; set; }

    /// <summary>Which way the jet leans around the tank's axis (+1 or -1).</summary>
    public float SwirlSign => swirlSign;

    /// <summary>A freshly dropped tank ignores bullets and chain reactions for a moment.</summary>
    public bool Invulnerable => Now < invulnerableUntil;

    /// <summary>Whether the last detonation was another player's, played here from the network.</summary>
    public bool DetonatedRemotely { get; private set; }

    /// <summary>Raised once, the moment the tank explodes.</summary>
    public event Action<PropaneTank>? Detonated;

    /// <summary>Raised when this game punctures the tank (a shot or a chain reaction), not for another player's puncture.</summary>
    public event Action<PropaneTank>? Punctured;

    /// <summary>Before the caller queues authority or applies its impulse/lifecycle change.</summary>
    public event Action<PropaneTank>? Disturbed;

    public bool AtRest => Sleeping || (LinearVelocity.Length() <= World.Tuning.GatherRestSpeed &&
        AngularVelocity.Length() <= World.Tuning.GatherRestSpin);

    public bool Loose
    {
        get
        {
            if (pilePose is not { } pose) { return true; }
            var tuning = World.Tuning;
            return (GlobalTransform * LocalCenter).DistanceTo(pose * LocalCenter) > tuning.GatherPileTolerance ||
                GlobalBasis.GetRotationQuaternion().AngleTo(pose.Basis.GetRotationQuaternion()) > Mathf.DegToRad(tuning.GatherPileAngle);
        }
    }

    public void MarkPile() => pilePose = IsInsideTree() ? GlobalTransform : Transform;

    public void MarkPile(Transform3D pose) => pilePose = pose;

    /// <summary>Called before impulses/lifecycle changes, including those while frozen for the construct glide.</summary>
    public void Disturb()
    {
        Disturbance++;
        EndGather();
        Disturbed?.Invoke(this);
    }

    public void BeginGather()
    {
        Gathering = true;
        Freeze = true;
        Sleeping = false;
        UpdateGatherShimmer();
    }

    public void EndGather()
    {
        if (!Gathering) { return; }
        Gathering = false;
        Freeze = false;
        Sleeping = false;
        mesh.SetInstanceShaderParameter("gather_shimmer", 0f);
    }

    private void UpdateGatherShimmer()
    {
        var tuning = World.Tuning;
        mesh.SetInstanceShaderParameter("gather_shimmer", tuning.GatherShimmer);
        mesh.SetInstanceShaderParameter("gather_speed", tuning.GatherShimmerSpeed);
        mesh.SetInstanceShaderParameter("gather_bands", tuning.GatherShimmerBands);
        mesh.SetInstanceShaderParameter("gather_sharpness", tuning.GatherShimmerSharpness);
    }

    /// <summary>Instantiates a ready-to-place tank.</summary>
    public static PropaneTank Create(WorldContext world, Spawn? spawn = null) =>
        new() { Name = "PropaneTank", World = world, ContentSpawn = spawn ?? world.Spawn };

    public override void _Ready()
    {
        World.Reveal.Track(this);
        Mass = World.Tuning.TankMass;
        CollisionLayer = Layers.Tanks;
        CollisionMask = Layers.DynamicMask;
        ContinuousCd = true;
        CenterOfMassMode = CenterOfMassModeEnum.Custom;
        CenterOfMass = new Vector3(0, 0.19f, 0);
        PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.55f, Bounce = 0.2f };
        AngularDamp = 0.6f;
        LinearDamp = 0.05f;

        AddChild(new CollisionShape3D { Shape = GameAssets.TankShape });
        mesh = new MeshInstance3D { Mesh = GameAssets.TankMesh, Layers = RenderLayers.Actors, Name = "Mesh" };
        AddChild(mesh);
        GameAssets.ApplyTankMaterials(mesh);
        // In a match every copy of a tank flutters alike, so the copies drift apart less.
        flutterPhase = NetId != 0 ? NetId % 997 * 0.1f : World.Random.Randf() * 100f;
        SetProcess(false);
        if (pilePose != null) { pilePose = GlobalTransform; }
    }

    /// <summary>Makes a dropped tank ignore bullets and chain reactions for a while, glowing in the dropper's color.</summary>
    public void MakeInvulnerable(float seconds, Color color)
    {
        pilePose = null;
        Disturb();
        invulnerableUntil = Now + seconds;
        shieldColor = color;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        // The shield pulses while it lasts, then fades out over its last moments.
        var left = (float)(invulnerableUntil - Now);
        if (left <= 0)
        {
            mesh.SetInstanceShaderParameter("shield_color", new Color(shieldColor, 0f));
            mesh.SetInstanceShaderParameter("shield_contrast", new Color(0, 0, 0, 0));
            SetProcess(false);
            return;
        }
        var pulse = 0.65f + 0.35f * Mathf.Sin((float)Now * 18f);
        var fade = Mathf.Clamp(left / 0.25f, 0, 1) * pulse;
        mesh.SetInstanceShaderParameter("shield_color", new Color(shieldColor, fade));
        mesh.SetInstanceShaderParameter("shield_contrast", new Color(AppearanceContrast.Edge(shieldColor, World.Tuning),
            AppearanceContrast.Strength(shieldColor, World.Tuning) * World.Tuning.AppearanceGlowContrast * fade));
    }

    /// <summary>
    /// A bullet hit at a world point with the surface normal there. Returns false if the tank ignored it (it is
    /// shielded or already gone).
    /// </summary>
    public bool TakeShot(Vector3 point, Vector3 normal, Vector3 shotDirection, int causeActorId = 0)
    {
        if (Invulnerable)
        {
            return false;
        }
        ChainCause = 0;
        CauseActorId = causeActorId;
        switch (State)
        {
            case TankState.Intact:
                Puncture(point, normal, World.Random.Randf() < 0.5f ? -1f : 1f);
                ApplyImpulse(shotDirection * PunctureKick, point - GlobalPosition);
                Punctured?.Invoke(this);
                return true;
            case TankState.Venting:
                Detonate();
                return true;
        }
        return false;
    }

    /// <summary>
    /// Adopts the canonical hole and vent. The caller initializes an intact copy's pose; replacing a contender's
    /// existing vent preserves its current motion and age.
    /// </summary>
    public void PunctureRemote(Vector3 holeLocalPoint, Vector3 holeNormal, float swirl)
    {
        if (State == TankState.Exploded) { return; }
        if (State == TankState.Venting && holeLocal == holeLocalPoint && holeNormalLocal == holeNormal && swirlSign == swirl) { return; }
        ClearVent();
        // Replacing a contender's vent changes only its parameters. Preserve the body's current pose, motion
        // and vent age; applying the winning bullet impulse a second time would disturb that motion.
        PunctureLocal(holeLocalPoint, holeNormal, swirl);
    }

    public void RestoreIntact()
    {
        ClearVent();
        State = TankState.Intact;
        CanSleep = true;
        ventAge = 0;
        CancelChainFuse();
    }

    internal void CancelChainFuse()
    {
        fuseLit = false;
        fuseGeneration++;
        ChainCause = 0;
    }

    private void ClearVent()
    {
        if (vent != null) { vent.Extinguish(); vent = null; }
        if (holeDecal != null) { holeDecal.QueueFree(); holeDecal = null; }
    }

    /// <summary>A blast went off within chain range: punctures an intact tank, or detonates a venting one after a fuse.</summary>
    public void ChainHit(Vector3 blastCenter, float distance, uint cause = 0, int causeActorId = 0)
    {
        if (State == TankState.Exploded || fuseLit || Invulnerable)
        {
            return;
        }
        ChainCause = cause;
        CauseActorId = causeActorId;
        if (State == TankState.Intact)
        {
            var (point, normal) = SurfaceFacing(blastCenter);
            Puncture(point, normal, World.Random.Randf() < 0.5f ? -1f : 1f);
            Punctured?.Invoke(this);
            return;
        }
        fuseLit = true;
        var tuning = World.Tuning;
        var closeness = Mathf.Clamp(distance / Mathf.Max(tuning.ChainRadius, 0.01f), 0, 1);
        var fuse = Mathf.Lerp(tuning.ChainFuseMin, tuning.ChainFuseMax, closeness) * World.Random.RandfRange(0.85f, 1.15f);
        var generation = ++fuseGeneration;
        void FinishFuse()
        {
            if (World.IsAlive && IsInstanceValid(this) && generation == fuseGeneration && fuseLit)
            {
                Detonate();
            }
        }
        if (FuseScheduleOverride != null) { FuseScheduleOverride(fuse, FinishFuse); }
        else { World.Schedule(this, fuse, FinishFuse); }
    }

    public override void _PhysicsProcess(double delta)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.TankPhysics);
        if (Gathering) { UpdateGatherShimmer(); }
        if (State == TankState.Intact && !Gathering)
        {
            var idleTuning = World.Tuning;
            if (!AtRest)
            {
                if (restPose != null) { Disturbance++; restPose = null; }
            }
            else if (restPose is not { } pose) { restPose = GlobalTransform; }
            else if ((GlobalTransform * LocalCenter).DistanceTo(pose * LocalCenter) > idleTuning.GatherRestDistance ||
                GlobalBasis.GetRotationQuaternion().AngleTo(pose.Basis.GetRotationQuaternion()) > Mathf.DegToRad(idleTuning.GatherRestAngle))
            {
                Disturbance++;
                restPose = GlobalTransform;
            }
        }
        if (State != TankState.Venting)
        {
            return;
        }
        var tuning = World.Tuning;
        ventAge += (float)delta;
        // BodySync has already supplied the owner's velocity plus positional/angular steering.
        // Applying this copy's phase-shifted jet afterward adds another impulse, particularly harmful
        // when rapidly changing floor contact turns a small rotation error into a different force.
        if (CorrectingMotion) { return; }
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

    /// <summary>Blows the tank apart: effects, debris, scorch, and the blast itself, which can set off other tanks.</summary>
    public void Detonate(int causeActorId) { CauseActorId = causeActorId; Detonate(); }
    public void Detonate() => Explode(GlobalTransform * LocalCenter, chain: true);

    /// <summary>
    /// Another player's detonation of this tank, at the center they had it at. It throws things here as everywhere,
    /// but sets off no tanks: that player runs the chain and sends each explosion.
    /// </summary>
    public void DetonateRemote(Vector3 center)
    {
        if (State == TankState.Exploded)
        {
            return;
        }
        GlobalPosition += center - GlobalTransform * LocalCenter;
        DetonatedRemotely = true;
        Explode(center, chain: false);
    }

    private void Explode(Vector3 center, bool chain)
    {
        if (State == TankState.Exploded)
        {
            return;
        }
        Disturb();
        State = TankState.Exploded;
        var groundY = GroundHeightBelow(center);

        vent?.Extinguish();
        ExplosionEffect.Create(World, center, groundY, ContentSpawn);
        SpawnDebris(center);
        SpawnScorch(new Vector3(center.X, groundY, center.Z));
        // The match assigns/sends the causal request before children emit their lifecycle requests.
        Detonated?.Invoke(this);
        Blast.Detonate(World, center, this, chain);
        QueueFree();
    }

    private double Now => World.Clock.Now;

    public override void _ExitTree() => CancelChainFuse();

    private void Puncture(Vector3 point, Vector3 normal, float swirl) =>
        PunctureLocal(ToLocal(point), (GlobalBasis.Inverse() * normal).Normalized(), swirl);

    private void PunctureLocal(Vector3 point, Vector3 normal, float swirl)
    {
        Disturb();
        State = TankState.Venting;
        CanSleep = false;
        Sleeping = false;
        holeLocal = point;
        holeNormalLocal = normal;
        // A torn hole rarely vents straight out: lean the jet around the tank's axis so its thrust has a
        // tangential part. Upright, that spins the tank; lying down, it rolls and pinwheels across the ground.
        swirlSign = swirl < 0 ? -1f : 1f;
        jetDirectionLocal = holeNormalLocal.Rotated(Vector3.Up, Mathf.DegToRad(World.Tuning.VentSwirlDegrees) * swirlSign).Normalized();

        var holeBasis = BasisFacing(jetDirectionLocal);
        vent = new VentJet { World = World, ContentSpawn = ContentSpawn, Name = "VentJet", Transform = new Transform3D(holeBasis, holeLocal) };
        AddChild(vent);

        // Bullet hole decal: a decal projects along its local -Y, so point +Y out of the surface.
        var surfaceBasis = BasisFacing(holeNormalLocal);
        var decalBasis = new Basis(surfaceBasis.Column0, surfaceBasis.Column2, -surfaceBasis.Column1);
        holeDecal = new Decal
        {
            Name = "BulletHole",
            Transform = new Transform3D(decalBasis, holeLocal),
            Size = new Vector3(HoleDecalSize, 0.05f, HoleDecalSize),
            TextureAlbedo = FxLibrary.BulletHoleTexture,
            CullMask = RenderLayers.Actors,
            UpperFade = 0.1f,
            LowerFade = 0.1f,
        };
        AddChild(holeDecal);
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
        using var profile = EffectProfile.Measure(EffectProfile.System.Debris);
        var tuning = World.Tuning;
        var rng = new RandomNumberGenerator();
        var inherited = LinearVelocity;
        foreach (var piece in GameAssets.Debris)
        {
            var body = DebrisBody.Create(World, piece);
            ContentSpawn.InWorld(body);
            body.GlobalTransform = GlobalTransform;
            var pieceCenter = GlobalTransform * piece.Centroid;
            var outward = pieceCenter - center;
            outward = outward.LengthSquared() > 0.0001f ? outward.Normalized() : Vector3.Up;
            var speed = tuning.DebrisSpeed * rng.RandfRange(0.45f, 1.15f);
            body.LinearVelocity = inherited + (outward + Vector3.Up * rng.RandfRange(0.3f, 1.1f)).Normalized() * speed;
            body.AngularVelocity = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)) * tuning.DebrisSpin;
        }
    }

    private void SpawnScorch(Vector3 groundPoint)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.Scorch);
        var size = World.Tuning.ScorchSize;
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
        ContentSpawn.InWorld(decal);
        decal.GlobalPosition = groundPoint;
        decal.RotateY(World.Random.RandfRange(0, Mathf.Tau));
    }
}
