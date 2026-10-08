using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// Physical bones for the mannequin, built in code from the skeleton's rest pose: capsules along each bone,
/// cone joints for the spine, neck, shoulders and hips, and hinges for elbows and knees.
/// </summary>
public partial class Ragdoll : PhysicalBoneSimulator3D
{
    private enum Joint
    {
        Root,
        Cone,
        Hinge,
    }

    private sealed record BoneSpec(string Bone, string? LengthTo, float Length, float Radius, float Mass, Joint Joint,
        float Swing, float Twist, float HingeMin = 0, float HingeMax = 0);

    // Lengths come from the child bone where one is named; otherwise the fixed length is used.
    private static readonly BoneSpec[] Specs =
    {
        new("pelvis", "spine_01", 0, 0.13f, 11f, Joint.Root, 0, 0),
        new("spine_01", "spine_03", 0, 0.13f, 9f, Joint.Cone, 22, 14),
        new("spine_03", "neck_01", 0, 0.15f, 12f, Joint.Cone, 22, 16),
        new("Head", null, 0.2f, 0.11f, 5f, Joint.Cone, 35, 30),
        new("upperarm_l", "lowerarm_l", 0, 0.055f, 2.6f, Joint.Cone, 75, 45),
        new("lowerarm_l", "hand_l", 0, 0.045f, 1.6f, Joint.Hinge, 0, 0, -140, 4),
        new("hand_l", null, 0.12f, 0.04f, 0.5f, Joint.Cone, 35, 20),
        new("upperarm_r", "lowerarm_r", 0, 0.055f, 2.6f, Joint.Cone, 75, 45),
        new("lowerarm_r", "hand_r", 0, 0.045f, 1.6f, Joint.Hinge, 0, 0, -140, 4),
        new("hand_r", null, 0.12f, 0.04f, 0.5f, Joint.Cone, 35, 20),
        new("thigh_l", "calf_l", 0, 0.08f, 8f, Joint.Cone, 55, 18),
        new("calf_l", "foot_l", 0, 0.062f, 4f, Joint.Hinge, 0, 0, -4, 140),
        new("foot_l", "ball_l", 0, 0.045f, 1.1f, Joint.Cone, 22, 10),
        new("thigh_r", "calf_r", 0, 0.08f, 8f, Joint.Cone, 55, 18),
        new("calf_r", "foot_r", 0, 0.062f, 4f, Joint.Hinge, 0, 0, -4, 140),
        new("foot_r", "ball_r", 0, 0.045f, 1.1f, Joint.Cone, 22, 10),
    };

    private const int LaunchSteps = 2;
    private const float FlailSpeed = 3.5f;

    private readonly List<PhysicalBone3D> bones = new();
    private Vector3 pendingVelocity;
    private Vector3 pendingSpin;
    private int pendingSteps;
    private readonly Dictionary<PhysicalBone3D, Vector3> flail = new();
    private PhysicalBone3D? pelvis;

    public IReadOnlyList<PhysicalBone3D> Bones => bones;

    public PhysicalBone3D Pelvis => pelvis!;

    public bool Simulating { get; private set; }

    /// <summary>Creates the physical bones. Call after adding this node under the skeleton.</summary>
    public void Build(Skeleton3D skeleton)
    {
        foreach (var spec in Specs)
        {
            var index = skeleton.FindBone(spec.Bone);
            var rest = skeleton.GetBoneGlobalRest(index);
            var length = spec.Length;
            if (spec.LengthTo != null)
            {
                length = rest.Origin.DistanceTo(skeleton.GetBoneGlobalRest(skeleton.FindBone(spec.LengthTo)).Origin);
            }

            // Bones run along their local +Y, so a Y capsule centred halfway along the bone fits it.
            var half = Vector3.Up * length * 0.5f;
            var body = new PhysicalBone3D
            {
                Name = "Physical_" + spec.Bone,
                Mass = spec.Mass,
                Friction = 0.85f,
                Bounce = 0.05f,
                LinearDamp = 0.15f,
                AngularDamp = 2.5f,
                CollisionLayer = Layers.Ragdoll,
                CollisionMask = Layers.World | Layers.Props | Layers.Tanks | Layers.Debris | Layers.Floor,
                // Body = bone * BodyOffset, so the body sits halfway down the bone; the joint is back at its head.
                BodyOffset = new Transform3D(Basis.Identity, half),
                JointOffset = new Transform3D(Basis.Identity, -half),
            };
            body.Set("bone_name", spec.Bone);
            var capsuleHeight = Mathf.Max(length, spec.Radius * 2.05f);
            body.AddChild(new CollisionShape3D
            {
                Shape = new CapsuleShape3D { Radius = spec.Radius, Height = capsuleHeight },
            });
            AddChild(body);
            body.Transform = rest * new Transform3D(Basis.Identity, half);
            ConfigureJoint(body, spec);
            bones.Add(body);
            if (spec.Bone == "pelvis")
            {
                pelvis = body;
            }
        }

        // Limbs overlapping the torso would otherwise fight each other.
        for (var i = 0; i < bones.Count; i++)
        {
            for (var j = i + 1; j < bones.Count; j++)
            {
                bones[i].AddCollisionExceptionWith(bones[j]);
            }
        }
        Active = false;
    }

    private static void ConfigureJoint(PhysicalBone3D body, BoneSpec spec)
    {
        switch (spec.Joint)
        {
            case Joint.Root:
                body.JointType = PhysicalBone3D.JointTypeEnum.None;
                break;
            case Joint.Cone:
                body.JointType = PhysicalBone3D.JointTypeEnum.Cone;
                // A cone joint twists about its X axis; turn it to run along the bone (+Y).
                body.JointRotation = new Vector3(0, 0, Mathf.DegToRad(90));
                body.Set("joint_constraints/swing_span", spec.Swing);
                body.Set("joint_constraints/twist_span", spec.Twist);
                body.Set("joint_constraints/bias", 0.3f);
                body.Set("joint_constraints/softness", 0.8f);
                body.Set("joint_constraints/relaxation", 1f);
                break;
            case Joint.Hinge:
                body.JointType = PhysicalBone3D.JointTypeEnum.Hinge;
                // A hinge turns about its Z axis; the mannequin's elbows and knees bend about the bone's local X.
                body.JointRotation = new Vector3(0, Mathf.DegToRad(90), 0);
                body.Set("joint_constraints/angular_limit_enabled", true);
                body.Set("joint_constraints/angular_limit_lower", spec.HingeMin);
                body.Set("joint_constraints/angular_limit_upper", spec.HingeMax);
                body.Set("joint_constraints/angular_limit_bias", 0.3f);
                body.Set("joint_constraints/angular_limit_softness", 0.9f);
                body.Set("joint_constraints/angular_limit_relaxation", 1f);
                break;
        }
    }

    /// <summary>Starts simulating, every bone moving at <paramref name="velocity"/> plus a tumble.</summary>
    public void Begin(Vector3 velocity, Vector3 spin)
    {
        Active = true;
        Influence = 1f;
        PhysicalBonesStartSimulation();
        Simulating = true;
        // Starting the simulation takes effect on the next physics step and resets velocities, so the launch is
        // applied over the first steps of the simulation instead.
        pendingVelocity = velocity;
        pendingSpin = spin;
        pendingSteps = LaunchSteps;
        flail.Clear();
        var rng = new RandomNumberGenerator();
        foreach (var body in bones)
        {
            var limb = body.Mass < 5f ? FlailSpeed : FlailSpeed * 0.2f;
            flail[body] = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-0.5f, 1), rng.RandfRange(-1, 1)) * limb;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (pendingSteps <= 0 || !Simulating)
        {
            return;
        }
        pendingSteps--;
        // Tumble the body as a whole (v = v0 + w x r about the pelvis), and give each limb its own flail.
        var center = Pelvis.GlobalPosition;
        foreach (var body in bones)
        {
            var arm = body.GlobalPosition - center;
            body.LinearVelocity = pendingVelocity + pendingSpin.Cross(arm) + flail[body];
            body.AngularVelocity = pendingSpin;
        }
    }

    /// <summary>Adds a velocity change to every bone (a second blast while already down).</summary>
    public void Push(Vector3 deltaVelocity)
    {
        foreach (var body in bones)
        {
            body.LinearVelocity += deltaVelocity;
        }
    }

    public void End()
    {
        pendingSteps = 0;
        PhysicalBonesStopSimulation();
        Active = false;
        Simulating = false;
    }

    /// <summary>Average speed of the bodies, for deciding when the ragdoll has settled.</summary>
    public float AverageSpeed()
    {
        var total = 0f;
        foreach (var body in bones)
        {
            total += body.LinearVelocity.Length();
        }
        return bones.Count > 0 ? total / bones.Count : 0f;
    }
}
