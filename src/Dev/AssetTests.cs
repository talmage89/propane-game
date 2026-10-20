using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using Propane.Assets;
using Propane.Core;
using Propane.Player;
using Propane.ReleaseSmoke;
using Propane.World;

namespace Propane.Dev;

/// <summary>Real final-head attachment, appearance preservation and retirement with an empty shipped library.</summary>
public partial class AssetTests : Node3D
{
    private WorldContext world = null!;
    private PlayerCharacter actor = null!, remote = null!;
    private HatCatalog catalog = null!;
    private PackedScene scene = null!;
    private readonly ScriptedInput input = new();
    private Skeleton3D alternate = null!;
    private HeadAttachment? alternateHat;
    private Camera3D camera = null!;
    private CaptureDirector capture = null!;
    private int frames, activeFrames, downFrames, upFrames;
    private bool thrown, gotUp, completed, sawFar;
    private Callable checkFrame;
    private readonly Dictionary<Skeleton3D, Transform3D> finalSockets = new();

    public override void _Ready()
    {
        try
        {
            DevArgs.MarkReady();
            InputSetup.Register();
            using var defaults = new Tuning();
            world = new WorldContext(this, defaults, present: true);
            world.Random.Seed = 1923;
            world.SetContentRoot(world.Root);
            world.Spawn.InWorld(new VoidEnvironment());
            scene = AssetFixture.Scene();
            catalog = AssetFixture.Catalog(scene);
            Require(!HatCatalog.ValidId("UPPER") && !HatCatalog.ValidId("../path") && !HatCatalog.ValidId("") && HatCatalog.ValidId("owner_01"), "native catalog id grammar");
            using (var duplicate = new HatLibrary())
            {
                duplicate.Hats.Add(new HatDefinition { Id = "same", DisplayName = "One", Scene = scene });
                duplicate.Hats.Add(new HatDefinition { Id = "same", DisplayName = "Two", Scene = scene });
                var rejected = false;
                try { _ = new HatCatalog(duplicate); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "native catalog accepted duplicate identities");
            }
            var forbidden = new StaticBody3D();
            try
            {
                var rejected = false;
                try { HatCatalog.ValidateScene(forbidden); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "native catalog accepted collision content");
            }
            finally { forbidden.Free(); }
            actor = world.Spawn.InWorld(new PlayerCharacter { World = world, CosmeticCatalog = catalog, InputOverride = input, ActorId = 1 });
            remote = world.Spawn.InWorld(new PlayerCharacter { World = world, CosmeticCatalog = catalog, IsRemote = true, ActorId = 2, Position = Vector3.Right * 3 });
            foreach (var character in new[] { actor, remote })
            {
                var skeleton = Skeleton(character);
                var rest = skeleton.GetBoneGlobalRest(skeleton.FindBone("Head"));
                Require(rest.Origin.DistanceTo(new Vector3(0, 1.568700552f, .0050996924f)) < .000001f, "reference mannequin head frame changed; review the frozen authoring contract");
                var final = skeleton.GetNode<RifleHandFollow>("RifleHandFollow");
                // Godot restores modifier poses after update. Observe them inside the last modifier signal.
                final.ModificationProcessed += () => finalSockets[skeleton] = skeleton.GetBoneGlobalPose(skeleton.FindBone("Head")) *
                    rest.AffineInverse() * new Transform3D(Basis.Identity, rest.Origin);
            }
            // Native alternate target: different height and bone axes, same frozen authoring space.
            alternate = new Skeleton3D { Name = "AlternateHeadTarget", Visible = false };
            alternate.AddBone("Head");
            alternate.SetBoneRest(0, new Transform3D(Basis.FromEuler(new Vector3(.2f, -.3f, .1f)), new Vector3(.1f, 2.1f, -.2f)));
            alternate.ResetBonePoses();
            AddChild(alternate);
            alternateHat = new HeadAttachment(alternate);
            alternateHat.Apply(catalog.Resolve("dev_probe"), Colors.White, 1);
            var orange = actor.BodyColor;
            actor.SetHat("dev_probe");
            Require(actor.BodyColor == orange, "offline hat quantized the existing float color");
            remote.Appearance = new AppearanceRecord(30, 170, 240, ModelOrSkin: "unknown_model", Outfit: "unknown_outfit", Hat: "dev_probe", GunSkin: "unknown_gun", BulletSkin: "unknown_bullet");
            var before = remote.Appearance;
            remote.BodyColor = Colors.Red;
            Require(remote.Appearance == before.WithColor(Colors.Red), "color erased cosmetic slots");
            var physical = actor.FindChildren("*", "CollisionShape3D", owned: false).Count;
            var seed = world.Random.State;
            actor.SetHat(null); Require(Hat(actor) == null, "None did not remove the hat");
            actor.SetHat("unavailable"); Require(Hat(actor) == null && actor.Appearance.Hat == "unavailable", "unknown hat must fall back without erasing the id");
            actor.SetHat("dev_probe");
            Require(actor.FindChildren("*", "CollisionShape3D", owned: false).Count == physical && world.Random.State == seed, "hat altered collisions or consumed RNG");
            Require(Hat(actor) != Hat(remote), "actors share a mutable hat instance");
            var tracked = ((MeshInstance3D Mesh, float Bias)[])typeof(PlayerCharacter).GetField("detailMeshes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actor)!;
            Require(tracked.Select(entry => entry.Mesh.GetInstanceId()).Distinct().Count() == tracked.Length, "mesh detail bookkeeping duplicates the hat");
            var original = GameAssets.MeshInstances(Hat(actor)!).Single().Mesh.SurfaceGetMaterial(0) as BaseMaterial3D;
            Require(original!.AlbedoColor == Colors.White, "tint mutated the shared material");
            camera = world.AddView(this, new Camera3D { Current = true, Fov = 45 });
            capture = new CaptureDirector { CollectFrameMetrics = false };
            AddChild(capture);
            input.Hold(.3f, 1.2f, new PlayerIntent { Move = Vector2.Down * .25f });
            input.Hold(1.2f, 2, new PlayerIntent { Move = Vector2.Down });
            input.Hold(2, 2.8f, new PlayerIntent { Move = Vector2.Down, Sprint = true });
            input.Hold(2.8f, 3.6f, new PlayerIntent { Aim = true });
            actor.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 5, Reserve = 90 };
            input.Reload(3.6f);
            input.Melee(4.1f);
            actor.GotUp += (_, _) => gotUp = true;
            ProcessPhysicsPriority = int.MinValue;
            ProcessPriority = int.MaxValue;
            checkFrame = Callable.From(CheckFrame);
        }
        catch (Exception error) { Fail(error); }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (actor == null || completed) return;
        input.Advance((float)delta);
        if (!thrown && input.Now > 5)
        {
            thrown = true;
            actor.RemoteRagdoll(actor.GlobalPosition, new Vector3(0, 4, -1), new Vector3(1, 0, 1));
            remote.RemoteRagdoll(remote.GlobalPosition, new Vector3(0, 4, -1), new Vector3(1, 0, 1));
        }
        if (!remote.IsRagdolled)
        {
            var state = actor.CaptureNetState();
            state.Position += Vector3.Right * 3;
            remote.SetRemoteState(state);
        }
        else if (gotUp)
        {
            remote.RemoteGetUp(actor.GlobalPosition + Vector3.Right * 3, actor.FacingYaw);
        }
    }

    public override void _Process(double delta)
    {
        if (actor == null || completed) return;
        frames++;
        var at = actor.IsRagdolled ? actor.PelvisPosition : actor.GlobalPosition + Vector3.Up;
        camera.LookAtFromPosition(at + (frames is >= 60 and < 120 ? new Vector3(0, 1, 110) : new Vector3(4, 1, 5)), at);
        checkFrame.CallDeferred();
    }

    private void CheckFrame()
    {
        try
        {
            if (completed) return;
            Verify(actor); Verify(remote);
            var rest = alternate.GetBoneGlobalRest(0);
            var target = alternate.GlobalTransform * alternate.GetBoneGlobalPose(0) * rest.AffineInverse() * new Transform3D(Basis.Identity, rest.Origin);
            var alternateMesh = GameAssets.MeshInstances(alternateHat!.Instance!).Single();
            Require(alternateMesh.GlobalPosition.DistanceTo(target * new Vector3(0, .22f, 0)) < .0001f &&
                alternateMesh.GlobalBasis.X.DistanceTo(target.Basis.X) < .0001f && alternateMesh.GlobalBasis.Y.DistanceTo(target.Basis.Y) < .0001f && alternateMesh.GlobalBasis.Z.DistanceTo(target.Basis.Z) < .0001f,
                $"alternate native head target differs: shown={alternateMesh.GlobalTransform} socket={target}");
            if (frames == 90)
            {
                sawFar = actor.FindChildren("*", "AnimationTree", owned: false).OfType<AnimationTree>().Single().CallbackModeProcess == AnimationMixer.AnimationCallbackModeProcess.Manual;
                Require(sawFar, "far held-pose coverage missing");
                actor.SetHat(null); actor.SetHat("dev_probe");
                Require(Mathf.IsEqualApprox(GameAssets.MeshInstances(Hat(actor)!).Single().LodBias, world.Tuning.ActorMeshLodBias), "new distant hat missed mesh detail");
            }
            if (frames == 150) Require(Mathf.IsEqualApprox(GameAssets.MeshInstances(Hat(actor)!).Single().LodBias, 1), "hat detail did not restore near");
            if (actor.ResolvedState.Lifecycle == PlayerLifecycle.GettingUp) upFrames++;
            else if (actor.IsRagdolled) downFrames++;
            else activeFrames++;
            if (frames is 30 or 150 or 210 or 270 or 320 or 420 or 600) capture.Screenshot($"attachment_{frames:000}");
            Require(frames < 1800, "ragdoll/get-up coverage timed out");
            if (frames < 660 || !gotUp || actor.IsRagdolled || remote.IsRagdolled) return;
            Require(sawFar && activeFrames > 200 && downFrames > 10 && upFrames > 10, "missing lifecycle coverage");
            completed = true;
            var old = Hat(remote)!;
            remote.SetHat(null);
            Require(!IsInstanceValid(old), "replaced hat remains alive");
            remote.Free();
            GD.Print($"[assets] PASS final-head transforms frames={frames} active={activeFrames} ragdoll={downFrames} getup={upFrames}; local/remote/alternate native socket, replace/fallback, tint, RNG and collision invariance");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error) { Fail(error); }
    }

    internal static Node3D? Hat(PlayerCharacter actor) => Skeleton(actor).GetNode<BoneAttachment3D>("HeadAttachment").GetChildOrNull<Node3D>(0);
    private static Skeleton3D Skeleton(PlayerCharacter actor) => actor.GetNode<Node3D>("Visual/Model").FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single();
    private void Verify(PlayerCharacter actor)
    {
        var skeleton = Skeleton(actor);
        Require(finalSockets.ContainsKey(skeleton), "no final modifier pose was observed");
        var socket = skeleton.GlobalTransform * finalSockets[skeleton];
        // Independent frozen authoring frame: do not use the production conversion as its own oracle.
        var reference = new Transform3D(Basis.Identity, new Vector3(0, 1.568700552f, .0050996924f));
        var expected = socket * reference.AffineInverse();
        var shown = Hat(actor) ?? throw new InvalidOperationException("missing fixture hat");
        Require(shown.GlobalPosition.DistanceTo(expected.Origin) < .0001f &&
            shown.GlobalBasis.X.DistanceTo(expected.Basis.X) < .0001f && shown.GlobalBasis.Y.DistanceTo(expected.Basis.Y) < .0001f && shown.GlobalBasis.Z.DistanceTo(expected.Basis.Z) < .0001f,
            $"hat differs actor={actor.ActorId} shown={shown.GlobalTransform} expected={expected} bone={skeleton.GetNode<BoneAttachment3D>("HeadAttachment").GlobalTransform}");
        var mesh = GameAssets.MeshInstances(shown).Single();
        Require(mesh.GlobalPosition.DistanceTo(socket * new Vector3(0, .22f, 0)) < .0001f,
            "model-space hat geometry does not sit above the final head socket");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Fail(Exception error) { completed = true; GD.PushError($"[assets] FAIL {error}"); ShutdownGuard.Quit(GetTree(), 1); }
    public override void _ExitTree() { alternateHat?.Dispose(); world?.Dispose(); scene?.Dispose(); }
}
