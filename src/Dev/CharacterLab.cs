using System.Linq;
using Godot;
using Propane.World;

namespace Propane.Dev;

/// <summary>
/// Inspects the mannequin: prints the node tree, bone axes and per-animation foot speed, and screenshots poses.
/// Run: godot --fixed-fps 60 res://scenes/dev/character_lab.tscn -- --capture-dir=/tmp/lab [--anims=A,B] [--side]
/// </summary>
public partial class CharacterLab : Node3D
{
    private Node3D model = null!;
    private Skeleton3D skeleton = null!;
    private AnimationPlayer player = null!;

    public override void _Ready()
    {
        DevArgs.Setup();
        var env = new VoidEnvironment();
        AddChild(env);
        model = GD.Load<PackedScene>("res://assets/character/mannequin.glb").Instantiate<Node3D>();
        AddChild(model);
        skeleton = model.FindChildren("*", "Skeleton3D").OfType<Skeleton3D>().First();
        player = model.FindChildren("*", "AnimationPlayer").OfType<AnimationPlayer>().First();
        PrintTree(model, 0);

        var camera = new Camera3D { Fov = 35, Current = true };
        AddChild(camera);
        var side = DevArgs.Get("side") != null;
        camera.LookAtFromPosition(side ? new Vector3(4.2f, 1.1f, 0) : new Vector3(0, 1.1f, 4.2f), new Vector3(0, 0.95f, 0));
        env.Follow = camera;

        GD.Print("ANIMS " + string.Join(",", player.GetAnimationList()));
        if (DevArgs.Get("info") != null)
        {
            PrintBones();
            PrintFootSpeeds();
            foreach (var t in new[] { 0f, 0.2f, 0.4f, 0.6f, 0.8f, 1.0f, 1.2f })
            {
                player.Play("LayToIdle");
                player.Seek(t, true);
                var pelvis = skeleton.GetBoneGlobalPose(skeleton.FindBone("pelvis"));
                var head = skeleton.GetBoneGlobalPose(skeleton.FindBone("Head")).Origin;
                var b = pelvis.Basis.Orthonormalized();
                GD.Print($"LAY t {t:0.0} pelvis {Fmt(pelvis.Origin)} belly(Z) {Fmt(b.Column2)} spine(Y) {Fmt(b.Column1)} head {Fmt(head)}");
            }
        }

        var anims = (DevArgs.Get("anims") ?? "Idle").Split(',');
        var director = new CaptureDirector { QuitAfter = 0.5f + anims.Length * 0.4f };
        AddChild(director);
        for (var i = 0; i < anims.Length; i++)
        {
            var parts = anims[i].Split('@');
            var name = parts[0];
            var at = parts.Length > 1 ? float.Parse(parts[1]) : 0f;
            director.At(0.3f + i * 0.4f, () =>
            {
                player.Play(name);
                player.Seek(at, true);
                player.Pause();
            });
            director.ShotAt(0.5f + i * 0.4f, $"{i:00}_{name}_{at:0.00}{(side ? "_side" : "")}");
        }
    }

    private static void PrintTree(Node node, int depth)
    {
        GD.Print(new string(' ', depth * 2) + node.Name + " : " + node.GetClass());
        foreach (var child in node.GetChildren())
        {
            PrintTree(child, depth + 1);
        }
    }

    private void PrintBones()
    {
        string[] bones = { "root", "pelvis", "spine_01", "spine_02", "spine_03", "neck_01", "Head", "clavicle_r", "upperarm_r", "lowerarm_r", "hand_r", "middle_01_r", "thumb_01_r", "upperarm_l", "lowerarm_l", "hand_l", "middle_01_l", "thumb_01_l", "thigh_l", "calf_l", "foot_l", "ball_l" };
        foreach (var name in bones)
        {
            var i = skeleton.FindBone(name);
            var rest = skeleton.GetBoneGlobalRest(i);
            var b = rest.Basis.Orthonormalized();
            GD.Print($"BONE {name,-12} idx {i,2} parent {skeleton.GetBoneParent(i),2} pos {Fmt(rest.Origin)} X {Fmt(b.Column0)} Y {Fmt(b.Column1)} Z {Fmt(b.Column2)}");
        }
        GD.Print($"SKELETON global {skeleton.GlobalTransform}");
    }

    private void PrintFootSpeeds()
    {
        var foot = skeleton.FindBone("foot_l");
        var ball = skeleton.FindBone("ball_l");
        foreach (var anim in new[] { "Walk", "Jog_Fwd", "Sprint", "Crouch_Fwd" })
        {
            var a = player.GetAnimation(anim);
            player.Play(anim);
            const int samples = 240;
            var length = (float)a.Length;
            var contact = new Vector3[samples];
            for (var s = 0; s < samples; s++)
            {
                player.Seek(length * s / samples, true);
                var f = skeleton.GetBoneGlobalPose(foot).Origin;
                var b = skeleton.GetBoneGlobalPose(ball).Origin;
                contact[s] = f.Y < b.Y ? f : b;
            }
            var minY = contact.Min(p => p.Y);
            var planted = Enumerable.Range(0, samples).Where(s => contact[s].Y < minY + 0.03f).ToList();
            var speeds = planted.Select(s => -(contact[(s + 1) % samples].Z - contact[s].Z) / (length / samples)).OrderBy(v => v).ToList();
            var range = contact.Max(p => p.Z) - contact.Min(p => p.Z);
            GD.Print($"FOOT {anim,-12} T {length:0.000}s stance {planted.Count / (float)samples:0.00} median {speeds[speeds.Count / 2]:0.00} p25 {speeds[speeds.Count / 4]:0.00} p75 {speeds[speeds.Count * 3 / 4]:0.00} zrange {range:0.00} estimate {range / (planted.Count / (float)samples * length):0.00}");
        }
    }

    private static string Fmt(Vector3 v) => $"({v.X,6:0.000},{v.Y,6:0.000},{v.Z,6:0.000})";
}
