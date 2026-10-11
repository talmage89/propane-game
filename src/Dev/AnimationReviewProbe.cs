using System;
using System.IO;
using System.Linq;
using Godot;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Observes the final rendered skeleton after all modifiers. Clearance proxies are documented separately.</summary>
public partial class AnimationReviewProbe : SkeletonModifier3D
{
    public PlayerCharacter Player { get; init; } = null!;
    public ScriptedInput Clock { get; init; } = null!;
    public Func<float, (string Name, float LocalTime)> CaseAt { get; init; } = null!;
    public string Output { get; init; } = "";
    private StreamWriter? writer;
    private Vector3 headCenter;
    private Vector3 headRadius;
    private bool cached;

    public override void _Ready()
    {
        writer = new StreamWriter(Output);
        writer.WriteLine("case,time,localTime,speed,aim,hipAimYaw,leftX,leftY,leftZ,rightX,rightY,rightZ,pelvisY,headY,kneeGap,thighGap,stockHead,stockShoulder");
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        try
        {
            var bones = GetSkeleton()!;
            Vector3 Bone(string name) => bones.GlobalTransform * bones.GetBoneGlobalPose(bones.FindBone(name)).Origin;
            if (!cached) CacheHead(bones);
            var left = Bone("foot_l");
            var right = Bone("foot_r");
            var pelvis = bones.GetBoneGlobalPose(bones.FindBone("pelvis"));
            var forward = bones.GlobalBasis * pelvis.Basis * bones.GetBoneGlobalRest(bones.FindBone("pelvis")).Basis.Inverse() * Vector3.Back;
            var hipYaw = Mathf.Atan2(-forward.X, -forward.Z);
#if PROPANE_REVIEW_RELEASE
            var aimYaw = Player.CameraRig.Yaw;
#else
            var aimYaw = Player.Aim.Yaw;
#endif
            var yaw = Mathf.RadToDeg(Mathf.Wrap(hipYaw - Mathf.DegToRad(aimYaw), -Mathf.Pi, Mathf.Pi));
            var rifle = Player.GetNode<Rifle>("Rifle");
            var toHead = (bones.GlobalTransform * bones.GetBoneGlobalPose(bones.FindBone("Head"))).AffineInverse() * rifle.GlobalTransform;
            var head = float.PositiveInfinity;
            var shoulder = float.PositiveInfinity;
            // A rounded stock core from 0.08 to 0.255 m behind the grip, radius 18 mm. Socket_Butt is z=0.2605.
            for (var i = 0; i <= 20; i++)
            {
                var stock = new Vector3(0, 0.064f, Mathf.Lerp(0.08f, 0.255f, i / 20f));
                var relative = (toHead * stock - headCenter) / headRadius;
                head = Mathf.Min(head, (relative.Length() - 1) * Mathf.Min(headRadius.X, Mathf.Min(headRadius.Y, headRadius.Z)) - 0.018f);
                var p = rifle.GlobalTransform * stock;
                // Proximal 14 cm of the right upper arm, 55 mm radius: shoulder volume, not the whole arm's reach.
                var upper = Bone("upperarm_r");
                var end = upper.MoveToward(Bone("lowerarm_r"), 0.14f);
                shoulder = Mathf.Min(shoulder, SegmentDistance(p, upper, end) - 0.055f - 0.018f);
            }
            var kneeGap = Bone("calf_l").DistanceTo(Bone("calf_r"));
            var thighGap = SegmentGap(Bone("thigh_l"), Bone("calf_l"), Bone("thigh_r"), Bone("calf_r"));
            var episode = CaseAt(Clock.Now);
            var speed = new Vector2(Player.Velocity.X, Player.Velocity.Z).Length();
            var values = new[] { Clock.Now, episode.LocalTime, speed, Player.AimAmount, yaw,
                left.X, left.Y, left.Z, right.X, right.Y, right.Z, Bone("pelvis").Y - Player.GlobalPosition.Y,
                Bone("Head").Y - Player.GlobalPosition.Y, kneeGap, thighGap, head, shoulder };
            if (values.Any(v => !float.IsFinite(v))) throw new InvalidOperationException("non-finite final pose");
            writer!.WriteLine(episode.Name + "," + string.Join(",", values.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        }
        catch (Exception error)
        {
            GD.PushError($"[animation-review] FAIL {error}");
            AnimationReview.Quit(GetTree(), 1);
        }
    }

    private void CacheHead(Skeleton3D skeleton)
    {
        var head = skeleton.FindBone("Head");
        var inverseHead = skeleton.GetBoneGlobalRest(head).AffineInverse();
        var points = new System.Collections.Generic.List<Vector3>();
        foreach (var mesh in Player.FindChildren("*", "MeshInstance3D", owned: false).OfType<MeshInstance3D>())
        {
            if (mesh.Skin == null || mesh.GetNodeOrNull<Skeleton3D>(mesh.Skeleton) != skeleton) continue;
            var skin = mesh.Skin;
            var map = Enumerable.Range(0, skin.GetBindCount()).Select(i => skin.GetBindName(i).ToString().Length > 0
                ? skeleton.FindBone(skin.GetBindName(i)) : skin.GetBindBone(i)).ToArray();
            var toHead = inverseHead * skeleton.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var arrays = mesh.Mesh.SurfaceGetArrays(surface);
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var ids = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                var influences = ids.Length / vertices.Length;
                for (var v = 0; v < vertices.Length; v++)
                {
                    var weight = 0f;
                    for (var j = 0; j < influences; j++) if (map[ids[v * influences + j]] == head) weight += weights[v * influences + j];
                    if (weight > 0.5f) points.Add(toHead * vertices[v]);
                }
            }
        }
        if (points.Count < 20) throw new InvalidOperationException("missing head skin vertices");
        var min = points.Aggregate((a, b) => a.Min(b));
        var max = points.Aggregate((a, b) => a.Max(b));
        headCenter = (min + max) / 2;
        headRadius = (max - min) / 2;
        if (headRadius.X <= 0 || headRadius.Y <= 0 || headRadius.Z <= 0)
            throw new InvalidOperationException("degenerate head proxy");
        cached = true;
        GD.Print($"[animation-review] head proxy {points.Count} skin vertices, center={headCenter}, radii={headRadius}");
    }

    private static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        var axis = b - a;
        return p.DistanceTo(a + axis * Mathf.Clamp((p - a).Dot(axis) / axis.LengthSquared(), 0, 1));
    }

    private static float SegmentGap(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        // Convex distance along one segment; ternary search avoids parallel-segment singularities.
        var low = 0f;
        var high = 1f;
        for (var i = 0; i < 24; i++)
        {
            var x = Mathf.Lerp(low, high, 1f / 3);
            var y = Mathf.Lerp(low, high, 2f / 3);
            if (SegmentDistance(a.Lerp(b, x), c, d) < SegmentDistance(a.Lerp(b, y), c, d)) high = y;
            else low = x;
        }
        return SegmentDistance(a.Lerp(b, (low + high) / 2), c, d);
    }

    public override void _ExitTree() => writer?.Dispose();
}
