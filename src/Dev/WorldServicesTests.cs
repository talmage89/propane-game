using System;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Independent physics spaces at the same coordinates, scoped services and synchronous retirement.</summary>
public partial class WorldServicesTests : Node
{
    public override async void _Ready()
    {
        try
        {
            await Exercise().ConfigureAwait(true); // Warm immutable assets before counting.
            await Collect().ConfigureAwait(true);
            var baseline = Counts();
            for (var i = 0; i < 10; i++)
            {
                await Exercise().ConfigureAwait(true);
                await Collect().ConfigureAwait(true);
                Assert(Counts() == baseline, $"world leaked on cycle {i + 1}: {baseline} -> {Counts()}");
            }
            GD.Print($"[world services] PASS two overlapping worlds, distinct tuning, events, blasts, callbacks, partial failure; 10 retirements stable at {baseline}");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError($"[world services] FAIL {error}");
            GetTree().Quit(1);
        }
    }

    private async Task Exercise()
    {
        using var template = new Tuning { TankMass = 7, BlastSpeed = 4 };
        using var a = new WorldContext(this, template);
        template.TankMass = 29;
        using var b = new WorldContext(this, template);
        Assert(a.Physics.Space != b.Physics.Space && a.Tuning != b.Tuning, "world physics/tuning shared");
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
            Assert(!a.IsAlive && a.Clock.PendingCount == 0 && a.BlastReceivers.Count == 0, "stop ran before invalidation/cancellation");
            stops++;
        });
        // Flush the physics broadphase. Freeze prevents the fixture moving before the query.
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
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
