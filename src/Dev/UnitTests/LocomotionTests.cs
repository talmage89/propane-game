using System;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class LocomotionTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("locomotion keeps phase across gaits and stops", Phase);
        runner.Run("locomotion blends synchronized gaits over actual speed", SpeedBlend);
        runner.Run("directional locomotion keeps a continuous distance clock through reversal", Directions);
        runner.Run("locomotion latches jump across physics/render rates", Jump);
        runner.Run("locomotion handles remote takeoff without a jumping snapshot", RemoteJump);
        runner.Run("locomotion scales landing and allows immediate movement", Landing);
        runner.Run("locomotion resets phase and support blends on lifecycle exit", Reset);
        runner.Run("pose blend preserves unit rotations during tiny simulation steps", () => PoseNormalization(runner.World));
    }

    private static void Directions()
    {
        using var tuning = new Tuning();
        var pose = new LocomotionPose(tuning);
        pose.ObservePhysics(State(PlayerSupport.Grounded), 0);
        const float dt = 1f / 60;
        void Settle(Vector2 direction)
        {
            for (var i = 0; i < 180; i++) pose.Update(tuning.CreepSpeed, PlayerGait.Creep, dt, direction);
            TestRunner.Assert(pose.Direction.Dot(direction) > .999f, "direction did not settle on travel");
        }
        Settle(Vector2.Down);
        var forwardStride = pose.Stride;
        for (var i = 0; i < 180; i++)
        {
            var before = pose.Phase;
            pose.Update(tuning.CreepSpeed, PlayerGait.Creep, dt, Vector2.Up);
            TestRunner.Assert(Mathf.Abs(pose.Direction.Length() - 1) < .00001f, "reversal collapsed travel direction");
            var advance = Mathf.PosMod(pose.Phase - before, 1);
            TestRunner.Assert(advance > 0 && advance < .1f, "reversal reset or jumped the stride clock");
        }
        TestRunner.Assert(pose.Direction.Dot(Vector2.Up) > .999f && pose.Stride < forwardStride,
            "backpedal did not retain forward-facing, shorter steps");
        foreach (var direction in new[] { Vector2.Left, Vector2.Right, new Vector2(1, 1).Normalized(), new Vector2(-1, -1).Normalized() })
            Settle(direction);
        var phase = pose.Phase;
        var facing = pose.Direction;
        pose.Update(0, PlayerGait.Default, dt, Vector2.Zero);
        TestRunner.Assert(pose.Phase == phase && pose.Direction == facing, "stopping lost the authored cycle");
    }

    private static void PoseNormalization(WorldContext world)
    {
        var skeleton = new Skeleton3D();
        skeleton.AddBone("index_test");
        skeleton.AddChild(new Ragdoll { Name = "Ragdoll" });
        skeleton.AddChild(new RifleRig { World = world, Name = "RifleRig", Active = false });
        var blend = new PoseBlend { World = world, Active = false };
        skeleton.AddChild(blend);
        world.Root.AddChild(skeleton);
        try
        {
            blend._ProcessModificationWithDelta(0.000001);
            var target = new Quaternion(Vector3.Up, 0.06f);
            for (var i = 0; i < 6000; i++)
            {
                skeleton.SetBonePoseRotation(0, target);
                // Direct invocation propagates errors to the runner instead of Godot logging and swallowing them.
                blend._ProcessModificationWithDelta(0.000001);
                TestRunner.Assert(skeleton.GetBonePoseRotation(0).IsNormalized(), "pose blend lost its unit rotation");
            }
        }
        finally { skeleton.Free(); }
    }

    private static PlayerState State(PlayerSupport support, PlayerLifecycle lifecycle = PlayerLifecycle.Active) =>
        new(lifecycle, support, PlayerGait.Default, WeaponAction.Carry, 0, 0, 0, false, support == PlayerSupport.Jumping, false);

    private static void Phase()
    {
        var pose = new LocomotionPose(Tuning.SavedTemplate);
        pose.ObservePhysics(State(PlayerSupport.Grounded), 0);
        foreach (var gait in new[] { PlayerGait.Creep, PlayerGait.Sprint, PlayerGait.Default, PlayerGait.Creep })
        {
            var phase = pose.Phase;
            pose.Update(0, gait, 1f / 60);
            TestRunner.Assert(pose.Phase == phase, "gait or stop restarted the stride clock");
            for (var i = 0; i < 60; i++) pose.Update(1.6f, gait, 1f / 60);
        }
        foreach (var speed in new[] { 0.98f, 1.6f, 2.2f, 2.8f, 4.9f })
        {
            var phase = pose.Phase;
            pose.Update(speed, PlayerGait.Creep, 1f / 60);
            var distance = Mathf.PosMod(pose.Phase - phase, 1) * Tuning.SavedTemplate.WalkStrideLength;
            TestRunner.Assert(Math.Abs(distance - speed / 60) < 0.00001f, $"creep stride caps speed {speed}");
        }
    }

    private static void Jump()
    {
        var pose = new LocomotionPose(Tuning.SavedTemplate);
        pose.ObservePhysics(State(PlayerSupport.Grounded), 0);
        // The jump lasts one 120 Hz physics tick; render sees Airborne instead of Jumping.
        pose.ObservePhysics(State(PlayerSupport.Jumping), 6.4f);
        pose.ObservePhysics(State(PlayerSupport.Airborne), 6.2f);
        pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Takeoff && pose.Takeoffs == 1, "lost the one-tick takeoff");
        var phase = pose.Phase;
        for (var i = 0; i < 12; i++) pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Rising && pose.Phase == phase, "air advanced contacts or skipped rising");
        pose.ObservePhysics(State(PlayerSupport.Airborne), 0);
        pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Apex, "apex does not follow velocity");
        pose.ObservePhysics(State(PlayerSupport.Airborne), -4);
        pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Falling, "fall does not follow velocity");
    }

    private static void SpeedBlend()
    {
        var pose = new LocomotionPose(Tuning.SavedTemplate);
        void Settle(float speed, PlayerGait gait)
        {
            for (var i = 0; i < 30; i++) pose.Update(speed, gait, 1f / 60);
        }
        Settle(Tuning.SavedTemplate.RunSpeed / 2, PlayerGait.Default);
        TestRunner.Assert(pose.Walk > 0 && pose.Walk < 1 && pose.Sprint == 0, "slow default motion does not blend walk and jog");
        Settle(Tuning.SavedTemplate.RunSpeed, PlayerGait.Default);
        TestRunner.Assert(pose.Walk == 0, "full default speed does not reach jog");
        Settle(Tuning.SavedTemplate.SprintSpeed / 2, PlayerGait.Sprint);
        TestRunner.Assert(pose.Sprint > 0 && pose.Sprint < 1, "slow sprint motion does not blend its gait");
        Settle(Tuning.SavedTemplate.SprintSpeed, PlayerGait.Sprint);
        TestRunner.Assert(pose.Sprint == 1, "full sprint speed does not reach sprint");
        Settle(Tuning.SavedTemplate.CreepSpeed, PlayerGait.Creep);
        TestRunner.Assert(pose.Walk == 1 && pose.Sprint == 0, "resolved creep lost its walk presentation");
    }

    private static void RemoteJump()
    {
        var pose = new LocomotionPose(Tuning.SavedTemplate);
        pose.ObservePhysics(State(PlayerSupport.Grounded), 0);
        pose.ObservePhysics(State(PlayerSupport.Airborne), 5);
        pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Takeoff, "missed remote upward support change");
        pose.Reset();
        pose.ObservePhysics(State(PlayerSupport.Airborne), -1);
        pose.Update(6, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(pose.Jump == JumpPose.Apex && pose.Takeoffs == 0, "a walk off an edge invented a jump");
    }

    private static void Landing()
    {
        float Impact(float velocity)
        {
            var pose = new LocomotionPose(Tuning.SavedTemplate);
            pose.ObservePhysics(State(PlayerSupport.Airborne), velocity);
            pose.ObservePhysics(State(PlayerSupport.Landing), 0);
            pose.Update(6, PlayerGait.Default, 1f / 60);
            TestRunner.Assert(pose.Jump == JumpPose.Landing && pose.Landing > 0 && pose.Phase > 0, "moving off blocked or erased landing");
            return pose.Impact;
        }
        TestRunner.Assert(Impact(-6) > Impact(-2), "landing does not scale with impact");
        var rebound = new LocomotionPose(Tuning.SavedTemplate);
        rebound.ObservePhysics(State(PlayerSupport.Airborne), -6);
        rebound.ObservePhysics(State(PlayerSupport.Landing), 0);
        rebound.ObservePhysics(State(PlayerSupport.Jumping), 6);
        rebound.Update(0, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(rebound.Landing == 0 && rebound.Jump == JumpPose.Takeoff, "rebound retains the old landing");
        rebound.ObservePhysics(State(PlayerSupport.Airborne), -6);
        // The controller accepts a jump from post-move floor contact before presentation sees Grounded.
        rebound.ObservePhysics(State(PlayerSupport.Jumping), 6);
        rebound.ObservePhysics(State(PlayerSupport.Jumping), 6); // Repeated remote support must not retrigger.
        rebound.Update(0, PlayerGait.Default, 1f / 60);
        TestRunner.Assert(rebound.Takeoffs == 2 && rebound.Jump == JumpPose.Takeoff,
            "skipped grounded contact lost a rebound or repeated its takeoff");
    }

    private static void Reset()
    {
        var pose = new LocomotionPose(Tuning.SavedTemplate);
        pose.ObservePhysics(State(PlayerSupport.Jumping), 6);
        pose.Update(6, PlayerGait.Sprint, 1f / 60);
        pose.ObservePhysics(State(PlayerSupport.Airborne, PlayerLifecycle.Ragdoll), 6);
        TestRunner.Assert(pose.Air == 0 && pose.Sprint == 0 && pose.Phase == 0 && pose.Takeoffs == 0, "lifecycle retained presentation");
    }
}
