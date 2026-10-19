using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.ReleaseSmoke;
using Propane.World;

namespace Propane.Dev;

/// <summary>Plays external CC0 candidates on the packaged mannequin without changing the packaged asset or rig.</summary>
public partial class AnimationClipReview : Node3D
{
    private AnimationPlayer animations = null!;
    private Skeleton3D skeleton = null!;
    private StreamWriter? metrics;
    private string current = "startup";
    private double localTime;
    private WorldContext? world;

    public override void _Ready()
    {
        try { Setup(); }
        catch (Exception error) { GD.PushError($"[clip-review] FAIL {error}"); AnimationReview.Quit(GetTree(), 1); }
    }

    private void Setup()
    {
        var path = DevArgs.Get("library") ?? throw new ArgumentException("--library=<external GLB> is required");
        using var document = new GltfDocument();
        using var state = new GltfState();
        if (document.AppendFromFile(path, state) != Error.Ok) throw new InvalidOperationException("cannot read library");
        var source = document.GenerateScene(state);
        AddChild(source);
        if (source is Node3D sourceModel) sourceModel.Visible = false;
        var sourcePlayer = source.FindChildren("*", "AnimationPlayer", owned: false).OfType<AnimationPlayer>().Single();
        var sourceSkeleton = source.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single();
        using var defaults = new Tuning();
        world = new WorldContext(this, defaults, present: true);
        var dynamic = new Node3D();
        AddChild(dynamic);
        world.SetContentRoot(dynamic);
        Node3D model;
        PlayerCharacter? character = null;
        if (DevArgs.Get("hands") != null)
        {
            InputSetup.Register();
            character = new PlayerCharacter { World = world, InputOverride = new ScriptedInput() };
            AddChild(character);
            model = character.GetNode<Node3D>("Visual/Model");
            foreach (var tree in model.FindChildren("*", "AnimationTree", owned: false).OfType<AnimationTree>()) tree.Active = false;
        }
        else
        {
            model = GD.Load<PackedScene>("res://assets/character/mannequin.glb").Instantiate<Node3D>();
            model.RotationDegrees = new Vector3(0, 180, 0);
            AddChild(model);
        }
        skeleton = model.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single();
        animations = model.FindChildren("*", "AnimationPlayer", owned: false).OfType<AnimationPlayer>().Single();
        var targetRoot = animations.GetNode(animations.RootNode);
        var library = new AnimationLibrary();
        foreach (var name in sourcePlayer.GetAnimationList().Where(n => n != "RESET"))
        {
            var clip = (Animation)sourcePlayer.GetAnimation(name).Duplicate();
            for (var i = 0; i < clip.GetTrackCount(); i++)
            {
                var track = clip.TrackGetPath(i);
                if (track.GetSubNameCount() != 1) throw new InvalidOperationException($"non-bone track {track}");
                var bone = track.GetSubName(0);
                var sourceIndex = sourceSkeleton.FindBone(bone);
                var targetIndex = skeleton.FindBone(bone);
                if (sourceIndex < 0 || targetIndex < 0) throw new InvalidOperationException($"unmapped bone {bone}");
                var sourceRest = sourceSkeleton.GetBoneGlobalRest(sourceIndex);
                var targetRest = skeleton.GetBoneGlobalRest(targetIndex);
                // Component-wise relative equality is too strict near zero; export roundoff is below 0.1 mm.
                if (sourceRest.Origin.DistanceTo(targetRest.Origin) > 0.0001f ||
                    sourceRest.Basis.X.DistanceTo(targetRest.Basis.X) > 0.0001f ||
                    sourceRest.Basis.Y.DistanceTo(targetRest.Basis.Y) > 0.0001f ||
                    sourceRest.Basis.Z.DistanceTo(targetRest.Basis.Z) > 0.0001f)
                {
                    throw new InvalidOperationException($"different rest pose at {bone}; requires retargeting");
                }

                clip.TrackSetPath(i, new NodePath($"{targetRoot.GetPathTo(skeleton)}:{bone}"));
            }
            if (name.ToString().EndsWith("_Loop")) clip.LoopMode = Animation.LoopModeEnum.Linear;
            library.AddAnimation(name, clip);
        }
        animations.AddAnimationLibrary("candidate", library);
        source.Free();
        var environment = new VoidEnvironment();
        AddChild(environment);
        var camera = new Camera3D { Current = true, Fov = 35 };
        AddChild(camera);
        var view = DevArgs.Get("view") ?? "side";
        camera.LookAtFromPosition(view == "side" ? new Vector3(4.2f, 1.1f, 0) : new Vector3(0, 1.1f, 4.2f), new Vector3(0, 0.95f, 0));
        if (view == "player")
        {
            if (character != null)
            {
                ActorView.For(character)!.CameraRig.Camera.Current = true;
            }
            else
            {
                var rig = new CameraRig { World = world, Target = Vector3.Up * world.Tuning.CameraHeight };
                AddChild(rig);
                rig.SetOrientation(0, -10);
            }
        }
        else if (view != "side")
        {
            throw new ArgumentException("view must be side or player");
        }

        var canvas = new CanvasLayer();
        AddChild(canvas);
        var caption = new Label { Position = new Vector2(16, 16) };
        caption.AddThemeFontSizeOverride("font_size", 22);
        canvas.AddChild(caption);
        var director = new CaptureDirector();
        var names = (DevArgs.Get("anims") ?? "Walk_Loop").Split(',');
        if (names.Distinct().Count() != names.Length) throw new ArgumentException("duplicate candidate");
        var cases = new System.Collections.Generic.List<object>();
        var start = 0f;
        foreach (var token in names)
        {
            var parts = token.Split('@');
            if (parts.Length > 2 || (parts.Length == 2 && parts[1] != "reverse")) throw new ArgumentException("only @reverse is supported");
            var name = parts[0];
            var imported = SourceName(name, library);
            var reverse = parts.Length > 1 && parts[1] == "reverse";
            var length = Mathf.Max(3, (float)library.GetAnimation(imported).Length);
            var label = token.Replace('@', '_');
            var time = start;
            cases.Add(new
            {
                name = label,
                start = time,
                end = time + length,
                available = true,
                clip_seconds = library.GetAnimation(imported).Length,
                loop = library.GetAnimation(imported).LoopMode != Animation.LoopModeEnum.None
            });
            director.At(time, () =>
            {
                current = label;
                localTime = 0;
                animations.Play("candidate/" + imported, customSpeed: reverse ? -1 : 1, fromEnd: reverse);
                caption.Text = $"{DevArgs.Get("label") ?? "CC0 candidate"} / {view} / {token} / {(character == null ? "raw authored pose" : "clip + current hands; stationary preview")}";
            });
            for (var i = 1; i < length * 12; i++) director.ShotAt(time + i / 12f, $"{label}_{i:000}");
            start += length;
        }
        director.QuitAfter = start;
        AddChild(director);
        var manifest = DevArgs.Get("manifest");
        if (manifest != null) File.WriteAllText(manifest, JsonSerializer.Serialize(cases));
        if (DevArgs.Get("metrics") is { } output)
        {
            metrics = new StreamWriter(output);
            metrics.WriteLine("case,time,leftY,rightY,pelvisY,headY");
        }
    }

    private static string SourceName(string name, AnimationLibrary library) => library.HasAnimation(name) ? name :
        library.HasAnimation(name.Replace("_Loop", "")) ? name.Replace("_Loop", "") : throw new ArgumentException($"missing clip {name}");

    public override void _Process(double delta)
    {
        localTime += delta;
        if (metrics == null) return;
        Vector3 Bone(string name) => skeleton.GetBoneGlobalPose(skeleton.FindBone(name)).Origin;
        metrics.WriteLine(FormattableString.Invariant($"{current},{localTime},{Bone("foot_l").Y},{Bone("foot_r").Y},{Bone("pelvis").Y},{Bone("Head").Y}"));
    }

    public override void _ExitTree()
    {
        metrics?.Dispose();
        world?.Dispose();
    }
}
