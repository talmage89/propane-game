using System;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests;
using Propane.Net;
using Propane.Tank;
using Propane.World;
using Propane.World.Plan;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Independent physics spaces at the same coordinates, scoped services and synchronous retirement.</summary>
public partial class WorldServicesTests : Node
{
    public override async void _Ready()
    {
        DevArgs.MarkReady();
        try
        {
            if (DevArgs.Get("scenario") == "presentation-lifetime")
            {
                PresentationLifetime();
                ShutdownGuard.Quit(GetTree());
                return;
            }
            if (DevArgs.Get("scenario") == "construction-failure")
            {
                ConstructionFailure();
                ShutdownGuard.Quit(GetTree());
                return;
            }
            var events = new GameEvents();
            var stale = 0;
            events.Explosion += _ => events.Clear();
            events.Explosion += _ => stale++;
            events.RaiseExplosion(Vector3.Zero);
            Assert(stale == 0, "invalidation did not stop remaining event callbacks");
            await PauseAndLease().ConfigureAwait(true);
            await Exercise().ConfigureAwait(true); // Warm immutable assets before counting.
            await Collect().ConfigureAwait(true);
            var baseline = Counts();
            for (var i = 0; i < 10; i++)
            {
                await Exercise().ConfigureAwait(true);
                await Collect().ConfigureAwait(true);
                Assert(Counts() == baseline, $"world leaked on cycle {i + 1}: {baseline} -> {Counts()}");
            }
            ConstructionFailure();
            PresentationLifetime();
            GD.Print($"[world services] PASS two overlapping worlds, distinct tuning, events, blasts, callbacks, partial failure; 10 retirements stable at {baseline}");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[world services] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private void PresentationLifetime()
    {
        using var template = new Tuning();
        using var original = new World3D();
        var view = new SubViewport { World3D = original, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
        AddChild(view);
        try
        {
            foreach (var retireFirst in new[] { true, false })
            {
                using var a = new WorldContext(this, template);
                using var b = new WorldContext(this, template);
                a.Present(view);
                b.Present(view);
                if (retireFirst)
                {
                    a.Dispose();
                    Assert(view.World3D == b.Physics, "retiring hidden world displaced survivor presentation");
                    b.Dispose();
                }
                else
                {
                    b.Dispose();
                    Assert(view.World3D == a.Physics, "retiring selected world did not restore live predecessor");
                    a.Dispose();
                }
                Assert(view.World3D == original, "presentation restored a retired physics world");
            }
            using var invalidated = new WorldContext(this, template);
            using var selected = new WorldContext(this, template);
            invalidated.Present(view);
            selected.Present(view);
            invalidated.Invalidate();
            selected.Dispose();
            Assert(view.World3D == original, "presentation restored an invalidated predecessor");
            invalidated.Dispose();
        }
        finally { view.Free(); }
        GD.Print("[world services] PASS shared presentation survives both world retirement orders");
    }

    private void ConstructionFailure()
    {
        // Fail after the detached builder has created the ground and collision, before AddChild can own it.
        _ = Catalog.Houses;
        using var template = new Tuning();
        using var partial = new WorldContext(this, template);
        var before = Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount);
        var plan = new SuburbPlan { Seed = 23, Bounds = new Rect2(-10, -10, 20, 20) };
        plan.Lots.Add(new Lot
        {
            Bounds = new OrientedRect(Vector2.Zero, Vector2.One, 0),
            House = new OrientedRect(Vector2.Zero, Vector2.One, 0),
            Seed = 23,
            HouseModel = int.MaxValue
        });
        Throws<ArgumentOutOfRangeException>(() => partial.Construct(() =>
            partial.Root.AddChild(Suburb.Build(partial, plan, SuburbPlans.SinglePlayer(partial.Tuning)))));
        Assert(!partial.IsAlive && partial.Reveal.Count == 0, "failed suburb retained world services");
        Assert(Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount) == before,
            "failed detached suburb leaked orphan nodes");
        GD.Print("[world services] PASS detached suburb construction failure leaves no orphan nodes");
    }

    private async Task Exercise()
    {
        using var template = new Tuning { TankMass = 7, BlastSpeed = 4, BotRouteSpacing = 7 };
        using var a = new WorldContext(this, template);
        template.TankMass = 29;
        template.BotRouteSpacing = 29;
        using var b = new WorldContext(this, template);
        Assert(a.Physics.Space != b.Physics.Space && a.Tuning != b.Tuning, "world physics/tuning shared");
        Assert(a.Tuning.BotRouteSpacing == 7 && b.Tuning.BotRouteSpacing == 29, "world copy lost local bot tuners");
        var first = PropaneTank.Create(a);
        var second = PropaneTank.Create(b);
        a.Root.AddChild(first); b.Root.AddChild(second);
        first.GlobalPosition = second.GlobalPosition = Vector3.Up;
        first.Freeze = second.Freeze = true;
        Assert(first.Mass == 7 && second.Mass == 29, "factory used application or other world's tuning");
        a.Tuning.BlastSpeed = 12;
        Assert(b.Tuning.BlastSpeed == 4 && template.BlastSpeed == 4, "live tuner escaped its world");
        var ra = new Receiver { World = a }; a.Root.AddChild(ra);
        var rb = new Receiver { World = b }; b.Root.AddChild(rb);
        var explosionsA = 0; var explosionsB = 0; var blastsA = 0; var blastsB = 0;
        a.Events.Explosion += _ => explosionsA++;
        b.Events.Explosion += _ => explosionsB++;
        a.Events.LocalBlast += (_, bodies, _) => { blastsA++; Assert(bodies.Count == 1 && bodies[0] == first, "foreign body in local blast"); };
        b.Events.LocalBlast += (_, _, _) => blastsB++;
        var durable = a.Spawn.InWorld(new Node3D());
        var effect = b.Spawn.Effect(new Node3D());
        Assert(durable.GetParent() == a.ContentRoot && effect.GetParent() == b.EffectsRoot, "spawn crossed worlds");
        var old = 0; var surviving = 0; var deferred = 0; var stops = 0;
        a.Schedule(first, 2, () => old++);
        a.Defer(() => deferred++);
        b.Schedule(second, 2, () => surviving++);
        a.OnStopping(() =>
        {
            Assert(!a.IsAlive && a.Clock.PendingCount == 0 && a.BlastReceivers.Count == 0 && !a.Root.CanProcess() && !PhysicsServer3D.SpaceIsActive(a.Physics.Space), "stop ran before invalidation/cancellation");
            stops++;
        });
        // Flush the physics broadphase. Freeze prevents the fixture moving before the query.
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        // Two physics ticks may belong to one render frame; wait for the deferred-work phase explicitly.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        // The deferred callback was valid before teardown. Queue another to prove cancellation.
        Assert(deferred == 1, "world deferred work did not run");
        a.Defer(() => old++);
        Blast.Detonate(a, Vector3.Up, null);
        Assert(first.State == PropaneTank.TankState.Venting && second.State == PropaneTank.TankState.Intact, "blast chain crossed spaces");
        Assert(ra.Hits == 1 && rb.Hits == 0 && explosionsA == 1 && explosionsB == 0 && blastsA == 1 && blastsB == 0, "world events/receiver registry crossed worlds");
        a.Dispose();
        a.Dispose();
        a.Clock.Advance(10); a.FlushDeferred();
        Assert(stops == 1 && old == 0 && !GodotObject.IsInstanceValid(first) && b.IsAlive && GodotObject.IsInstanceValid(second), "teardown affected survivor or fired old work");
        b.Clock.Advance(2);
        Assert(surviving == 1, "surviving world's callback canceled");
        var rejected = new Node3D();
        try { Throws<ObjectDisposedException>(() => a.Spawn.Effect(rejected)); }
        finally { rejected.Free(); }
        // Disposed worlds cannot send notifications through their old event bus.
        a.Events.RaiseExplosion(Vector3.Zero);
        Assert(explosionsA == 1 && explosionsB == 0, "old event subscription survived");
        using var replacement = new WorldContext(this, template);
        var fresh = 0;
        replacement.Clock.Schedule(0, () => fresh++);
        replacement.Clock.Advance(0);
        Assert(old == 0 && fresh == 1, "old callback reached a replacement world");
        using var partial = new WorldContext(this, template);
        var partialStops = 0;
        partial.OnStopping(() => partialStops++);
        Throws<InvalidOperationException>(() => partial.Construct(() =>
        {
            partial.Spawn.InWorld(new Node3D());
            partial.Clock.Schedule(0, () => old++);
            throw new InvalidOperationException("deliberate construction failure");
        }));
        partial.Clock.Advance(10);
        Assert(!partial.IsAlive && partialStops == 1 && old == 0, "partial failure skipped the ordinary cleanup path");
    }

    private async Task PauseAndLease()
    {
        using var template = new Tuning();
        using var a = new WorldContext(this, template);
        Assert(a.TryHitstop(1, 0.1f) && Engine.TimeScale < 0.11, "sole offline world denied hitstop");
        using var b = new WorldContext(this, template);
        Assert(Engine.TimeScale == 1 && !a.TryHitstop(1, 0.1f), "multiple worlds shared engine time scale");
        var ma = a.Spawn.InWorld(new MeshInstance3D { Mesh = new BoxMesh() });
        var mb = b.Spawn.InWorld(new MeshInstance3D { Mesh = new BoxMesh() });
        a.Reveal.Center = Vector3.Right; a.Reveal.Radius = 2;
        b.Reveal.Center = Vector3.Left; b.Reveal.Radius = 7;
        Assert(ma.GetInstanceShaderParameter("reveal_radius").AsSingle() == 2 && mb.GetInstanceShaderParameter("reveal_radius").AsSingle() == 7 &&
            ma.GetInstanceShaderParameter("reveal_center").AsVector3() == Vector3.Right, "reveal escaped its world");
        ma.Free();
        Assert(a.Reveal.Count == 0 && b.Reveal.Count == 1, "reveal retained a retired instance");
        var late = a.Spawn.InWorld(new MeshInstance3D { Mesh = new BoxMesh() });
        Assert(late.GetInstanceShaderParameter("reveal_radius").AsSingle() == 2, "late spawn missed world reveal state");
        RigidBody3D Body(WorldContext world)
        {
            var body = new RigidBody3D { GravityScale = 0, LinearVelocity = Vector3.Right };
            body.AddChild(new CollisionShape3D { Shape = new SphereShape3D() });
            return world.Spawn.InWorld(body);
        }
        var ba = Body(a); var bb = Body(b);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        a.SetPaused(true);
        var at = ba.GlobalPosition; var bt = bb.GlobalPosition; var time = a.Clock.Now;
        var called = 0;
        a.Schedule(ba, 0.01, () => called++);
        Assert(!PhysicsServer3D.SpaceIsActive(a.Physics.Space) && PhysicsServer3D.SpaceIsActive(b.Physics.Space), "pause did not isolate physics activity");
        // Production connection polling is outside world roots, so a handshake still finishes during pause.
        var network = new LoopbackNetwork();
        var rawServer = network.Create();
        var server = new LobbyServer(new ServerTransport(rawServer, rawServer.PeerIds)) { Port = 1 };
        var client = new NetClient(network.Create());
        AddChild(server); AddChild(client);
        try
        {
            Assert(server.Start().Success, "pause fixture server failed");
            client.Connect("", 1, "Pause", 0);
            for (var i = 0; i < 12; i++) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            Assert(client.State == NetClient.Status.Connected, "world pause stopped connection polling");
            Assert(ba.GlobalPosition == at && bb.GlobalPosition.X > bt.X && a.Clock.Now == time && called == 0,
                "paused physics/timer advanced or survivor stopped");
            a.SetPaused(false);
            for (var i = 0; i < 4; i++) { await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame); }
            Assert(ba.GlobalPosition.X > at.X && a.Clock.Now > time && called == 1, "resume did not restart physics/timers");
        }
        finally { client.Free(); server.Free(); }
        b.Dispose();
        Assert(a.TryHitstop(1, 0.1f), "surviving sole offline world denied lease");
        a.SetPaused(true);
        Assert(Engine.TimeScale == 1, "pause did not release lease");
        a.SetPaused(false);
        Assert(a.TryHitstop(0, 0.1f), "lease not reacquired after resume");
        a.PollTimeScale();
        Assert(Engine.TimeScale == 1, "expired lease retained time scale");
        a.Dispose();
        using var net = new WorldContext(this, template, networked: true);
        Assert(!net.TryHitstop(1, 0.1f) && Engine.TimeScale == 1, "network world acquired lease");
    }

    private (int Nodes, int Objects, int Orphans) Counts() => (GetTree().GetNodeCount(),
        (int)Performance.GetMonitor(Performance.Monitor.ObjectCount), (int)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount));

    private async Task Collect()
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GC.Collect(); GC.WaitForPendingFinalizers();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private partial class Receiver : Node3D, IBlastReceiver
    {
        public required WorldContext World { get; init; }
        public int Hits { get; private set; }
        public Vector3 BlastTargetPosition => GlobalPosition;
        public override void _Ready() => World.Register(this);
        public override void _ExitTree() => World.Unregister(this);
        public void ReceiveBlast(BlastInfo blast) => Hits++;
    }
}
