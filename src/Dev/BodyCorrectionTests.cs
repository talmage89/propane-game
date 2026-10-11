using System;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Native integration must preserve owner motion without adding an observer's speculative jet impulse.</summary>
public partial class BodyCorrectionTests : Node3D
{
    private WorldContext world = null!;
    public override void _ExitTree() => world.Dispose();

    public override async void _Ready()
    {
        DevArgs.MarkReady();
        using var tuning = new Tuning();
        world = new WorldContext(this, tuning, present: true);
        world.SetContentRoot(this);
        try
        {
            var observer = CreateVent(1, Vector3.Up * 10);
            var owner = CreateVent(2, Vector3.Up * 10 + Vector3.Right * 4);
            var sync = new BodySync(world.Tuning, 4, 2);
            sync.Register(1, observer, true, 2);
            sync.OnState(2, new BodyState(1, observer.GlobalTransform, Vector3.Zero, Vector3.Zero, 1, 1), false, 0);
            sync.Correct(0);
            var freshSpeed = 0f;
            var ownerSpeed = 0f;
            for (var tick = 0; tick < 120; tick++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                freshSpeed = Mathf.Max(freshSpeed, observer.LinearVelocity.Length());
                ownerSpeed = Mathf.Max(ownerSpeed, owner.LinearVelocity.Length());
                sync.Correct(0); // Fresh zero owner velocity isolates force added after correction.
            }
            Assert(ownerSpeed > .1f, "owner vent stopped applying its ordinary jet force");
            Assert(freshSpeed < .001f, $"fresh observer added its speculative jet after correction: {freshSpeed:0.000} m/s");
            sync.Correct(1); // No receipt: speculative local vent simulation must resume.
            for (var tick = 0; tick < 30; tick++) { await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame); }
            Assert(observer.LinearVelocity.Length() > .01f, "stale observer failed to resume local vent simulation");
            sync.OnState(2, new BodyState(1, observer.GlobalTransform, Vector3.Zero, Vector3.Zero, 1, 2), false, 2);
            sync.Correct(2);
            sync.Claim(new[] { observer });
            Assert(!observer.CorrectingMotion, "local prediction retained force suppression until another correction tick");
            observer.LinearVelocity = Vector3.Zero;
            sync.Correct(2.01);
            for (var tick = 0; tick < 30; tick++) { await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame); }
            Assert(observer.LinearVelocity.Length() > .01f, "predicted local claim failed to resume its jet force");
            sync.OnState(2, new BodyState(1, observer.GlobalTransform, Vector3.Zero, Vector3.Zero, 1, 3), false, 3);
            // Resolve the prediction back to the remote owner before testing a subsequent local grant.
            sync.OnAuthority(new BodyAuthority(new BodyState(1, observer.GlobalTransform, Vector3.Zero, Vector3.Zero, 1, 3),
                2, TankLedger.State.Venting, false, new VentParameters(observer.HoleLocal, observer.HoleNormalLocal, observer.SwirlSign), Vector3.Zero), 3);
            sync.Correct(3);
            Assert(observer.CorrectingMotion, "remote authority did not restore the fresh observer fixture");
            sync.OnAuthority(new BodyAuthority(new BodyState(1, observer.GlobalTransform, Vector3.Zero, Vector3.Zero, 2, 0),
                4, TankLedger.State.Venting, false, new VentParameters(observer.HoleLocal, observer.HoleNormalLocal, observer.SwirlSign), Vector3.Zero), 3);
            Assert(!observer.CorrectingMotion, "local grant retained force suppression until another correction tick");
            observer.LinearVelocity = Vector3.Zero;
            sync.Correct(3);
            for (var tick = 0; tick < 30; tick++) { await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame); }
            Assert(observer.LinearVelocity.Length() > .01f, "confirmed ownership retained observer force suppression");
            await CheckSpinningCenters().ConfigureAwait(true);
            GD.Print($"[body correction] PASS fresh observer impulse {freshSpeed:0.000000} m/s, owner peak {ownerSpeed:0.000} m/s; stale, predicted and granted-owner jets resume");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[body correction] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private async Task CheckSpinningCenters()
    {
        var worst = 0f;
        foreach (var custom in new[] { true, false })
        {
            var center = Vector3.Up * 20 + Vector3.Right * (custom ? 8 : 12);
            var offset = Vector3.Up * .19f;
            var reference = Spinner(center, offset, 0, custom);
            var follower = Spinner(center, offset, .8f, custom);
            reference.AngularVelocity = Vector3.Forward * 10;
            for (var tick = 0; tick < 4; tick++) { await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame); }
            Assert(PhysicsServer3D.BodyGetDirectState(follower.GetRid()).CenterOfMassLocal.DistanceTo(offset) < .0001f,
                "native spinner did not initialize its offset mass center");
            var sync = new BodySync(world.Tuning, 4, 2);
            sync.Register(1, follower, false, 2);
            var referenceDrift = 0f;
            var followerDrift = 0f;
            for (var tick = 0; tick < 120; tick++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                var now = tick / 120.0;
                sync.OnState(2, new BodyState(1, reference.GlobalTransform, reference.LinearVelocity,
                    reference.AngularVelocity, 1, (uint)tick + 1), false, now);
                sync.Correct(now);
                referenceDrift = Mathf.Max(referenceDrift, (reference.GlobalTransform * offset).DistanceTo(center));
                followerDrift = Mathf.Max(followerDrift, (follower.GlobalTransform * offset).DistanceTo(center));
            }
            GD.Print($"[body correction] spinning {(custom ? "custom" : "automatic")} center drift reference={referenceDrift:0.000000}m observer={followerDrift:0.000000}m");
            Assert(referenceDrift < .0002f, "native reference spinner translated without a force");
            worst = Mathf.Max(worst, followerDrift);
        }
        Assert(worst < .002f, $"rotation-only correction displaced the mass center by {worst:0.000000} m");
    }

    private RigidBody3D Spinner(Vector3 center, Vector3 offset, float angle, bool custom)
    {
        var body = new RigidBody3D
        {
            GravityScale = 0,
            LinearDamp = 0,
            AngularDamp = 0,
            CollisionLayer = 0,
            CollisionMask = 0,
            CenterOfMassMode = custom ? RigidBody3D.CenterOfMassModeEnum.Custom : RigidBody3D.CenterOfMassModeEnum.Auto
        };
        if (custom) { body.CenterOfMass = offset; }
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One * .2f }, Position = offset });
        world.Root.AddChild(body);
        var basis = new Basis(Vector3.Forward, angle);
        body.GlobalTransform = new Transform3D(basis, center - basis * offset);
        return body;
    }

    private PropaneTank CreateVent(uint id, Vector3 position)
    {
        var tank = PropaneTank.Create(world);
        tank.NetId = id;
        AddChild(tank);
        tank.GlobalPosition = position;
        tank.GravityScale = 0;
        tank.CollisionMask = 0;
        tank.LinearDamp = tank.AngularDamp = 0;
        tank.PunctureRemote(PropaneTank.LocalCenter + Vector3.Right * .15f, Vector3.Right, 1);
        return tank;
    }
}
