using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev;

/// <summary>Native animation/skeleton cadence, held rifle contacts, movement and recovery through distance bands.</summary>
public partial class ActorDetailTests : Node3D
{
    private WorldContext world = null!;
    private PlayerCharacter player = null!;
    private Camera3D camera = null!;
    private LocalView? localView;
    private readonly ScriptedInput input = new();
    private ActorDetailContact contact = null!;
    private GripProbe grip = null!;
    private CaptureDirector capture = null!;
    private Callable checkFrame;
    private int frames, phase = -1, phaseFrames, poseCalls, recoveryFrames;
    private bool gotUp, wasDown, sawRecovery;
    private bool hitStarted, sawHit;
    private AnimationTree tree = null!;
    private (MeshInstance3D Mesh, float Bias)[] meshes = [];

    public override void _Ready()
    {
        DevArgs.MarkReady();
        InputSetup.Register();
        using var defaults = new Tuning();
        world = new WorldContext(this, defaults, present: true);
        world.SetContentRoot(world.Root);
        world.Random.Seed = 9023;
        world.Spawn.InWorld(new VoidEnvironment());
        player = world.Spawn.InWorld(new PlayerCharacter { World = world, ActorId = 8, InputOverride = input });
        meshes = GameAssets.MeshInstances(player).Where(mesh => (mesh.Layers & RenderLayers.Actors) != 0)
            .Select(mesh => (mesh, mesh.LodBias)).ToArray();
        player.Ammo = new AmmoState { Tuning = world.Tuning, Magazine = 5, Reserve = 90 };
        input.Hold(0, 12, new PlayerIntent { Move = Vector2.Right, Aim = true });
        input.Reload(4.2f);
        player.GotUp += (_, _) => gotUp = true;
        var skeleton = player.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single(s => s.FindBone("hand_r") >= 0);
        tree = player.FindChildren("*", "AnimationTree", owned: false).OfType<AnimationTree>().Single();
        contact = new ActorDetailContact { Player = player };
        skeleton.AddChild(contact);
        grip = GripProbe.Attach(player);
        camera = world.AddView(this, new Camera3D { Current = true, Fov = 55 });
        capture = new CaptureDirector { CollectFrameMetrics = false };
        AddChild(capture);
        ProcessPhysicsPriority = int.MinValue;
        ProcessPriority = int.MaxValue;
        checkFrame = Callable.From(CheckFrame);
    }

    public override void _PhysicsProcess(double delta)
    {
        try { StepPhysics(delta); }
        catch (Exception error) { Fail(error); }
    }

    private void StepPhysics(double delta)
    {
        input.Advance((float)delta);
        var next = Math.Min((int)(input.Now / 2), 7);
        if (next != phase)
        {
            if (phase >= 0) FinishPhase();
            phase = next;
            phaseFrames = poseCalls = 0;
            if (phase == 5) localView = LocalView.Attach(player, this);
            if (phase == 6)
            {
                localView!.Free();
                player.Collapse();
                wasDown = player.IsRagdolled;
            }
        }
        var distance = phase switch { 0 or 4 => 6f, 1 or 3 => 50f, _ => 110f };
        var focus = player.GlobalPosition + Vector3.Up;
        camera.LookAtFromPosition(focus + new Vector3(0, 2, distance), phase == 3 ? focus + Vector3.Back * 200 : focus);
        camera.MakeCurrent();
    }

    public override void _Process(double delta)
    {
        frames++;
        phaseFrames++;
        if (player.ResolvedState.Lifecycle == PlayerLifecycle.GettingUp) sawRecovery = true;
        checkFrame.CallDeferred();
    }

    private void CheckFrame()
    {
        try
        {
            var calls = contact.DrainCalls();
            poseCalls += calls;
            if (player.IsRagdolled)
            {
                recoveryFrames++;
                Assert(calls > 0, "far recovery skipped its native pose update");
                Assert(meshes.All(part => part.Mesh.LodBias == part.Bias), "recovery retained distant mesh detail");
            }
            if (phase == 3 && phaseFrames == 45) world.Tuning.ActorMeshLodBias = .2f;
            contact.VerifyHeldContact();
            if (phaseFrames == 90 && phase <= 5) capture.Screenshot($"actor-detail-{phase}");
            if (phase <= 5 && phaseFrames == 90)
            {
                var bias = phase is 1 or 2 or 3 ? world.Tuning.ActorMeshLodBias : 1;
                Assert(meshes.Length > 0 && meshes.All(part => part.Mesh.LodBias == part.Bias * bias), "near/distant/owning mesh detail missing");
                var reloading = player.Ammo!.Reloading;
                var speed = reloading ? world.Tuning.RunSpeed : world.Tuning.CreepSpeed;
                Assert(Math.Abs(player.Velocity.X - speed) < .01f, "LOD changed actor movement");
                Assert(player.ResolvedState.EffectiveAim == !reloading && player.ResolvedState.Grounded, "LOD changed aim/support");
            }
            Assert(frames < 1800, "far recovery/hit restoration timed out");
            if (frames < 1200 || !gotUp || player.IsRagdolled) return;
            if (!hitStarted)
            {
                hitStarted = true;
                var distance = (world.Tuning.RagdollRadius + world.Tuning.StaggerRadius) / 2;
                player.ReceiveBlast(new BlastInfo(Vector3.Zero, Vector3.Forward, distance, .5f));
                return;
            }
            if (tree.Get("parameters/hit/active").AsBool() || tree.Get("parameters/hit/internal_active").AsBool())
            {
                sawHit = true;
                Assert(calls > 0 && tree.CallbackModeProcess == AnimationMixer.AnimationCallbackModeProcess.Idle,
                    "distant hit skipped its native pose update");
                Assert(meshes.All(part => part.Mesh.LodBias == part.Bias), "hit retained distant mesh detail");
                return;
            }
            if (!sawHit || tree.CallbackModeProcess != AnimationMixer.AnimationCallbackModeProcess.Manual) return;
            Assert(meshes.All(part => part.Mesh.LodBias == part.Bias * world.Tuning.ActorMeshLodBias), "hit did not restore distant mesh detail");
            FinishPhase();
            Assert(wasDown && gotUp && sawRecovery && recoveryFrames >= 60, "far ragdoll/get-up path missing");
            grip.Finish();
            GD.Print("[actor-detail] PASS movement, sampled/held contacts, distance/visibility/owning-view cadence, ragdoll/get-up, hit/restore");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void Fail(Exception error)
    {
        GD.PushError($"[actor-detail] FAIL {error}");
        ShutdownGuard.Quit(GetTree(), 1);
    }

    private void FinishPhase()
    {
        GD.Print($"[actor-detail] phase={phase} frames={phaseFrames} poses={poseCalls}");
        if (phase is 0 or 4 or 5) Assert(poseCalls >= phaseFrames - 3, "near/owning/recovery pose skipped");
        if (phase == 1) Assert(poseCalls >= 55 && poseCalls <= 65, "30 Hz pose rate missing");
        if (phase == 2) Assert(poseCalls >= 25 && poseCalls <= 35, "15 Hz pose rate missing");
        if (phase == 3) Assert(poseCalls >= 15 && poseCalls <= 25, "10 Hz hidden pose rate missing");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public override void _ExitTree() => world.Dispose();
}

/// <summary>Observe the actual final modifier pose, then check its held skin-space contacts between samples.</summary>
internal partial class ActorDetailContact : SkeletonModifier3D
{
    public required PlayerCharacter Player { get; init; }
    private Transform3D right, left, rightGrip, leftGrip;
    private RifleRig rig = null!;
    private int calls;
    private bool captured;

    public override void _ProcessModificationWithDelta(double delta)
    {
        calls++;
        var skeleton = GetSkeleton();
        rig = skeleton.GetNode<RifleRig>("RifleRig");
        right = skeleton.GetBoneGlobalPose(skeleton.FindBone("hand_r"));
        left = skeleton.GetBoneGlobalPose(skeleton.FindBone("hand_l"));
        var toRifle = rig.Rifle!.GlobalTransform.AffineInverse();
        rightGrip = toRifle * rig.RightTarget;
        leftGrip = toRifle * rig.LeftTarget;
        captured = true;
    }

    public int DrainCalls() { var count = calls; calls = 0; return count; }

    public void VerifyHeldContact()
    {
        if (!captured || Player.IsRagdolled || rig.Weight < .999f) return;
        var skeleton = GetSkeleton();
        var r = (skeleton.GlobalTransform * right).Origin;
        var l = (skeleton.GlobalTransform * left).Origin;
        var error = Math.Max(r.DistanceTo((rig.Rifle!.GlobalTransform * rightGrip).Origin),
            l.DistanceTo((rig.Rifle.GlobalTransform * leftGrip).Origin));
        if (!float.IsFinite(error) || error > .02f)
            throw new InvalidOperationException($"held skinned wrist lost its rifle contact: {error:F6}m");
    }
}
