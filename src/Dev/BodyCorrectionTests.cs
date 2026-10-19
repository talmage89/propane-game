using System;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Dev.NetScenarios;
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
            await CheckShieldOverride().ConfigureAwait(true);
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
                2, BodyState.State.Venting, false, new VentParameters(observer.HoleLocal, observer.HoleNormalLocal, observer.SwirlSign), Vector3.Zero), 3);
            sync.Correct(3);
            Assert(observer.CorrectingMotion, "remote authority did not restore the fresh observer fixture");
            sync.OnAuthority(new BodyAuthority(new BodyState(1, observer.GlobalTransform, Vector3.Zero, Vector3.Zero, 2, 0),
                4, BodyState.State.Venting, false, new VentParameters(observer.HoleLocal, observer.HoleNormalLocal, observer.SwirlSign), Vector3.Zero), 3);
            Assert(!observer.CorrectingMotion, "local grant retained force suppression until another correction tick");
            observer.LinearVelocity = Vector3.Zero;
            sync.Correct(3);
            for (var tick = 0; tick < 30; tick++) { await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame); }
            Assert(observer.LinearVelocity.Length() > .01f, "confirmed ownership retained observer force suppression");
            await CheckSpinningCenters().ConfigureAwait(true);
            await CheckSettledContact().ConfigureAwait(true);
            CheckPoseProbe();
            await CheckReportedContact().ConfigureAwait(true);
            if (DevArgs.Get("spin-average-proof") != null || DevArgs.Get("authored-spin-proof") != null) { await CheckSpinAverage().ConfigureAwait(true); }
            GD.Print($"[body correction] PASS fresh observer impulse {freshSpeed:0.000000} m/s, owner peak {ownerSpeed:0.000} m/s; stale, predicted and granted-owner jets resume");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[body correction] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private void CheckPoseProbe()
    {
        var floor = new StaticBody3D { CollisionLayer = Layers.Floor, Position = new Vector3(40, -.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(10, 1, 10) } });
        world.Root.AddChild(floor);
        var body = PropaneTank.Create(world);
        world.Root.AddChild(body);
        body.CollisionLayer = 0;
        body.CollisionMask = Layers.Floor;
        body.GlobalPosition = new Vector3(40, 5, 0);
        var actual = body.GlobalTransform;
        using var parameters = new PhysicsTestMotionParameters3D { RecoveryAsCollision = true };
        using var result = new PhysicsTestMotionResult3D();
        parameters.From = actual;
        Assert(!PhysicsServer3D.BodyTestMotion(body.GetRid(), parameters, result), "free hull reported contact");
        parameters.From = new Transform3D(new Basis(Vector3.Forward, Mathf.Pi / 2), new Vector3(40, 0, 0));
        Assert(PhysicsServer3D.BodyTestMotion(body.GetRid(), parameters, result), "penetrating rotated hull missed floor");
        Assert(result.GetCollisionCount() > 0 && result.GetCollisionNormal().Dot(Vector3.Up) > .99f,
            "pose probe did not return world-space floor support");
        Assert(result.GetTravel().Y > .1f, "pose probe did not report hull recovery");
        Assert(body.GlobalTransform == actual && body.MaxContactsReported == 0 && !body.ContactMonitor,
            "pose probe changed the native body or contact-reporting policy");
        GD.Print($"[body correction] pose probe recovery {result.GetTravel()}; native body and contact policy unchanged");
        body.QueueFree();
        floor.QueueFree();
    }

    private async Task CheckSpinAverage()
    {
        foreach (var axial in new[] { false, true })
        {
            var tank = CreateVent(5, new Vector3(80, 20, 0));
            PropaneTank? replacement = null;
            using var tuning = world.Tuning.Copy();
            typeof(Tuning).GetProperty("CorrectionSpinAverageMin")?.SetValue(tuning, 6f);
            tuning.VentThrustRampTime = 0;
            var sync = new BodySync(tuning, 4, 2);
            sync.Register(5, tank, true, 2);
            async Task Check(PropaneTank current, uint sequence, double now, float gravityScale)
            {
                current.GravityScale = gravityScale;
                var axis = axial ? Vector3.Up : Vector3.Right;
                var spin = axis * world.Tuning.VentMaxSpin;
                var velocity = new Vector3(4, 2, 0);
                var center = current.GlobalTransform * current.CenterOfMass;
                sync.OnState(2, new BodyState(5, current.GlobalTransform, velocity, spin, 1, sequence), false, now);
                sync.Correct(now);
                for (var warmup = 0; warmup < 2; warmup++) { await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame); }
                var gravity = PhysicsServer3D.BodyGetDirectState(current.GetRid()).TotalGravity;
                Assert(gravityScale == 0 ? gravity == Vector3.Zero : gravity.Y < -1, "spin fixture lost native gravity selection");
                var measured = gravity + (axial ? axis * 6 : Vector3.Forward * 10);
                var expectedAcceleration = gravity + (axial ? axis * 6 : Vector3.Zero);
                if (DevArgs.Get("authored-spin-proof") != null)
                {
                    // A source collision can change either component of the measured velocity.
                    // The fast-spin free-flight model must retain only the authored axial force.
                    var sourceJet = new Basis(new Quaternion(axis, spin.Length() * .1f)) * current.JetDirectionLocal;
                    expectedAcceleration = gravity - axis * sourceJet.Dot(axis) * tuning.VentThrust / current.Mass;
                }
                var sourceRotation = new Quaternion(axis, spin.Length() * .1f);
                var sourceCenter = center + velocity * .1f + measured * .005f;
                var source = new Transform3D(new Basis(sourceRotation), sourceCenter - new Basis(sourceRotation) * current.CenterOfMass);
                var sourceVelocity = velocity + measured * .1f;
                sync.OnState(2, new BodyState(5, source, sourceVelocity, spin, 1, sequence + 1), false, now + .1);
                var currentRotation = new Quaternion(axis, spin.Length() * .1f) * sourceRotation;
                var predictedCenter = sourceCenter + sourceVelocity * .1f + expectedAcceleration * .005f;
                current.GlobalTransform = new Transform3D(new Basis(currentRotation), predictedCenter - new Basis(currentRotation) * current.CenterOfMass);
                sync.Correct(now + .2);
                var expected = sourceVelocity + expectedAcceleration * .1f;
                Assert(current.LinearVelocity.DistanceTo(expected) < .001f,
                    $"spin average axial={axial} gravity={gravityScale}: commanded {current.LinearVelocity}, expected {expected}");
            }
            try
            {
                await Check(tank, 1, 1, 1).ConfigureAwait(true);
                if (!axial)
                {
                    // Keep the first native body alive: a retained view would still report its nonzero gravity.
                    replacement = CreateVent(6, new Vector3(82, 20, 0));
                    sync.ReplaceBody(5, replacement);
                    await Check(replacement, 3, 2, 0).ConfigureAwait(true);
                }
            }
            finally { sync.Clear(); tank.QueueFree(); replacement?.QueueFree(); }
        }
        GD.Print("[body correction] PASS spin prediction and native gravity oracle; replacement drops the old native view");
    }

    private async Task CheckReportedContact()
    {
        var floor = new StaticBody3D { CollisionLayer = Layers.Floor, Position = new Vector3(60, -.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(10, 1, 10) } });
        world.Root.AddChild(floor);
        var tank = CreateVent(3, new Vector3(60, 0, 0));
        tank.CollisionMask = Layers.Floor;
        tank.GravityScale = 1;
        var pose = tank.GlobalTransform;
        // Isolate contact eligibility at a known gain; prediction scope has its own response oracle.
        using var tuning = world.Tuning.Copy();
        tuning.CorrectionBlendTime = .25f;
        typeof(Tuning).GetProperty("CorrectionVentBlendTime")?.SetValue(tuning, .25f);
        var sync = new BodySync(tuning, 4, 2);
        sync.Register(3, tank, true, 2);
        sync.OnState(2, new BodyState(3, pose, Vector3.Up, Vector3.Zero, 1, 1), false, 1);
        sync.Correct(1);
        sync.OnState(2, new BodyState(3, pose, Vector3.Zero, Vector3.Zero, 1, 2), false, 1.1);
        for (var tick = 0; tick < 30; tick++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var direct = PhysicsServer3D.BodyGetDirectState(tank.GetRid());
            var expected = (pose * tank.CenterOfMass - tank.GlobalTransform * tank.CenterOfMass) / tuning.CorrectionBlendTime;
            sync.Correct(1.2);
            if (tick > 10)
            {
                Assert(direct.GetContactCount() > 0, "native floor fixture lost contact reporting");
                Assert(tank.LinearVelocity.DistanceTo(expected) < .001f, "contact used free-flight acceleration");
            }
        }
        sync.OnAuthority(new BodyAuthority(new BodyState(3, pose, Vector3.Zero, Vector3.Zero, 2, 0),
            4, BodyState.State.Venting, false, new VentParameters(tank.HoleLocal, tank.HoleNormalLocal, tank.SwirlSign), Vector3.Zero), 2);
        Assert(tank.MaxContactsReported == 0 && !tank.ContactMonitor && !tank.CorrectingMotion,
            "grant retained an observer's native contact policy");
        sync.OnAuthority(new BodyAuthority(new BodyState(3, pose, Vector3.Zero, Vector3.Zero, 3, 0),
            2, BodyState.State.Venting, false, new VentParameters(tank.HoleLocal, tank.HoleNormalLocal, tank.SwirlSign), Vector3.Zero), 3);
        sync.Correct(3);
        sync.Claim(new[] { tank });
        Assert(tank.MaxContactsReported == 0, "local prediction retained observer contact reporting");
        sync.Clear();
        tank.MaxContactsReported = 8;
        sync.Register(3, tank, true, 2);
        sync.OnState(2, new BodyState(3, pose, Vector3.Zero, Vector3.Zero, 1, 1), false, 4);
        sync.Correct(4);
        sync.Clear();
        Assert(tank.MaxContactsReported == 8, "retirement overwrote preexisting contact reporting");
        GD.Print("[body correction] PASS native contact disables acceleration; grants, prediction and retirement restore contact policy");
        tank.QueueFree();
        floor.QueueFree();
    }

    private async Task CheckShieldOverride()
    {
        var tank = PropaneTank.Create(world);
        world.Root.AddChild(tank);
        tank.CollisionLayer = tank.CollisionMask = 0;
        tank.GravityScale = 0;
        var expected = AppearanceRecord.FromPalette(1).Color;
        tank.MakeInvulnerable(world.Tuning.DropInvulnerableTime, expected);
        var mesh = tank.GetNode<MeshInstance3D>("Mesh");
        var initial = mesh.GetInstanceShaderParameter("shield_color");
        Assert(initial.VariantType == Variant.Type.Nil, "new shield fixture already had an instance override");
        Assert(NetScenarioTools.ReadShieldColor(initial).A == 0, "unset shield override fabricated an opaque black glow");
        Assert(NetScenarioTools.ReadShieldColor(Colors.Black) == Colors.Black, "real opaque black was treated as an unset shield");
        for (var frame = 0; frame < 2; frame++) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        if (DisplayServer.GetName() != "headless")
        {
            var glow = NetScenarioTools.ReadShieldColor(mesh.GetInstanceShaderParameter("shield_color"));
            Assert(glow.A > 0 && new Color(glow, 1) == expected, "processed shield did not publish its accepted appearance");
            GD.Print("[body correction] rendered shield override transitions from unset to accepted color");
        }
        tank.QueueFree();
    }

    private async Task CheckSettledContact()
    {
        var floor = new StaticBody3D { CollisionLayer = Layers.Floor, Position = new Vector3(20, -.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(10, 1, 10) } });
        world.Root.AddChild(floor);
        var owner = PropaneTank.Create(world);
        world.Root.AddChild(owner);
        owner.CanSleep = false;
        owner.CollisionLayer = 0;
        owner.CollisionMask = Layers.Floor;
        owner.GlobalTransform = new Transform3D(new Basis(Vector3.Forward, Mathf.Pi / 2), new Vector3(20, .6f, 0));
        for (var tick = 0; tick < 360; tick++) { await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame); }
        Assert(owner.AtRest, "native contact owner did not settle");
        var observer = PropaneTank.Create(world);
        world.Root.AddChild(observer);
        observer.CanSleep = false;
        observer.CollisionLayer = 0;
        observer.CollisionMask = Layers.Floor;
        observer.GlobalTransform = new Transform3D(new Basis(Vector3.Right, .45f) * owner.GlobalBasis,
            owner.GlobalPosition + Vector3.Up * .15f);
        var sync = new BodySync(world.Tuning, 4, 2);
        sync.Register(1, observer, true, 2);
        var peak = 0f;
        var final = 0f;
        for (var tick = 0; tick < 720; tick++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var now = tick / 120.0;
            sync.OnState(2, new BodyState(1, owner.GlobalTransform, owner.LinearVelocity,
                owner.AngularVelocity, 1, (uint)tick + 1), false, now);
            sync.Correct(now);
            final = observer.GlobalPosition.DistanceTo(owner.GlobalPosition);
            if (tick > 360) { peak = Mathf.Max(peak, final); }
        }
        GD.Print($"[body correction] settled intact floor contact drift last-half peak={peak:0.000000}m final={final:0.000000}m");
        Assert(peak < .05f, $"awake settled owner left its observer trapped in floor contact: {peak:0.000000}m");
        Assert(sync.Get(1) is { TargetRest: false }, "native contact recovery fabricated a rest publication");
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
