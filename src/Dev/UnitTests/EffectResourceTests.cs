using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class EffectResourceTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("effects/overlapping dense leases restore the prior catch-up value", () =>
        {
            var original = Engine.MaxPhysicsStepsPerFrame;
            try
            {
                Engine.MaxPhysicsStepsPerFrame = 8;
                using var tuning = Tuning.LoadBaked();
                tuning.DenseChainFuseThreshold = 1;
                using var first = new WorldContext(runner, tuning);
                using var second = new WorldContext(runner, tuning);
                second.Tuning.DenseChainPhysicsSteps = 2;
                foreach (var world in new[] { first, second })
                {
                    var tank = PropaneTank.Create(world); world.Spawn.InWorld(tank);
                    tank.FuseScheduleOverride = (_, _) => { };
                    tank.PunctureRemote(Vector3.Up * .2f, Vector3.Right, 1);
                    tank.ChainHit(Vector3.Zero, 0);
                }
                Assert(Engine.MaxPhysicsStepsPerFrame == 2, "overlapping leases did not select the smaller budget");
                second.Invalidate();
                Assert(Engine.MaxPhysicsStepsPerFrame == 4, "one world's retirement lost the other lease");
                var simulation = first.EffectsRoot.GetChildren().OfType<TankSimulation>().Single();
                first.Tuning.DenseChainPhysicsSteps = 0; simulation._Process(0);
                Assert(Engine.MaxPhysicsStepsPerFrame == 8, "disabling the limiter lost the prior engine setting");
                first.Tuning.DenseChainPhysicsSteps = 12; simulation._Process(0);
                Assert(Engine.MaxPhysicsStepsPerFrame == 8, "dense lease raised the ordinary catch-up budget");
                first.Invalidate();
                Assert(Engine.MaxPhysicsStepsPerFrame == 8, "retirement did not restore the prior setting");
            }
            finally { Engine.MaxPhysicsStepsPerFrame = original; }
        });
        runner.Run("effects/dense catch-up is transient, offline and restores the engine budget", () =>
        {
            var original = Engine.MaxPhysicsStepsPerFrame;
            try
            {
                Engine.MaxPhysicsStepsPerFrame = 12;
                using var tuning = Tuning.LoadBaked();
                tuning.DenseChainFuseThreshold = 2;
                using var world = new WorldContext(runner, tuning);
                PropaneTank Fuse(WorldContext owner)
                {
                    var tank = PropaneTank.Create(owner); owner.Spawn.InWorld(tank);
                    tank.FuseScheduleOverride = (_, _) => { };
                    tank.PunctureRemote(Vector3.Up * .2f, Vector3.Right, 1);
                    return tank;
                }
                var first = Fuse(world);
                var second = Fuse(world);
                var simulation = world.EffectsRoot.GetChildren().OfType<TankSimulation>().Single();
                var now = 0d;
                simulation.CatchupClock = () => now;
                first.ChainHit(Vector3.Zero, 0);
                Assert(Engine.MaxPhysicsStepsPerFrame == 12, "ordinary chain changed the engine budget");
                second.ChainHit(Vector3.Zero, 0);
                Assert(Engine.MaxPhysicsStepsPerFrame == 4, "dense offline chain did not bound catch-up");
                first.RestoreIntact();
                simulation._Process(0);
                Assert(Engine.MaxPhysicsStepsPerFrame == 4, "dense lease lost its short tail");
                now = 1;
                simulation._Process(0);
                Assert(Engine.MaxPhysicsStepsPerFrame == 12, "dense lease changed game speed after expiry");
                first.PunctureRemote(Vector3.Up * .2f, Vector3.Right, 1);
                first.ChainHit(Vector3.Zero, 0);
                Assert(Engine.MaxPhysicsStepsPerFrame == 4, "dense lease failed to rearm");
                using (var network = new WorldContext(runner, tuning, networked: true))
                {
                    _ = Fuse(network);
                    Assert(Engine.MaxPhysicsStepsPerFrame == 12, "offline lease slowed a concurrent network world");
                    Assert(!network.EffectsRoot.GetChildren().OfType<TankSimulation>().Single().IsProcessing(),
                        "network simulation retained offline render-frame bookkeeping");
                }
                Assert(Engine.MaxPhysicsStepsPerFrame == 4, "network retirement lost an offline lease");
                world.ContentRoot.ProcessMode = Node.ProcessModeEnum.Disabled;
                simulation._Process(0);
                Assert(Engine.MaxPhysicsStepsPerFrame == 12, "disabled retiring source retained its dense lease");
                world.ContentRoot.ProcessMode = Node.ProcessModeEnum.Inherit;
                first.CancelChainFuse(); second.CancelChainFuse();
                first.ChainHit(Vector3.Zero, 0); second.ChainHit(Vector3.Zero, 0);
                world.SetPaused(true); simulation._Process(0);
                Assert(Engine.MaxPhysicsStepsPerFrame == 12, "pause retained its dense lease");
                world.SetPaused(false); simulation._Process(0);
                Assert(Engine.MaxPhysicsStepsPerFrame == 4, "resume lost pending fuse pressure");
                world.Invalidate();
                Assert(Engine.MaxPhysicsStepsPerFrame == 12, "world invalidation leaked the engine budget");
            }
            finally { Engine.MaxPhysicsStepsPerFrame = original; }
        });
        runner.Run("effects/crowded blasts keep fireballs and restore full slots on retirement", () =>
        {
            using var tuning = Tuning.LoadBaked();
            using var world = new WorldContext(runner, tuning);
            using var other = new WorldContext(runner, tuning);
            world.Tuning.ExplosionFullEffectBudget = 2;
            var first = ExplosionEffect.Create(world, Vector3.Zero, 0);
            var second = ExplosionEffect.Create(world, Vector3.Right, 0);
            var compact = ExplosionEffect.Create(world, Vector3.Left, 0);
            Assert(first.GetChildren().OfType<GpuParticles3D>().Count() == 5 &&
                second.GetChildren().OfType<GpuParticles3D>().Count() == 5 &&
                compact.GetChildren().OfType<GpuParticles3D>().Count() == 1 &&
                !compact.GetChildren().OfType<OmniLight3D>().Any(), "full explosion budget or essential fireball was lost");
            var independent = ExplosionEffect.Create(other, Vector3.Zero, 0);
            Assert(independent.GetChildren().OfType<GpuParticles3D>().Count() == 5, "another world's blasts consumed full slots");
            compact._Process(.2);
            Assert(!compact.IsProcessing(), "compact flash retained managed work after fading");
            world.Clock.Advance(2);
            Assert(compact.IsQueuedForDeletion(), "compact fireball did not retire after its native particles");
            first.Free();
            var restored = ExplosionEffect.Create(world, Vector3.Zero, 0);
            Assert(restored.GetChildren().OfType<GpuParticles3D>().Count() == 5, "retired full blast did not release its slot");
        });
        runner.Run("effects/debris queue bounds work and cancels retired content", () =>
        {
            using var tuning = Tuning.LoadBaked();
            using var world = new WorldContext(runner, tuning);
            using var other = new WorldContext(runner, tuning);
            world.Tuning.DebrisPiecesPerTank = 6;
            world.Tuning.DebrisSpawnPerFrame = 2;
            var pieces = GameAssets.Debris.Count;
            foreach (var count in Enumerable.Range(1, pieces))
            {
                var indices = Enumerable.Range(0, count).Select(i => DebrisQueue.PieceIndex(i, count, pieces)).ToArray();
                Assert(indices.Distinct().Count() == count && indices.All(i => i >= 0 && i < pieces),
                    "fragment selection repeated or escaped the authored pieces");
            }
            var randomState = world.Random.State;
            DebrisQueue.Enqueue(world, world.Spawn, Transform3D.Identity, Vector3.Zero, PropaneTank.LocalCenter);
            var queue = world.EffectsRoot.GetChildren().OfType<DebrisQueue>().Single();
            Assert(queue.PendingCount == 6 && !world.ContentRoot.GetChildren().OfType<DebrisBody>().Any(),
                "fragment registration was not deferred");
            queue._Process(0);
            Assert(queue.PendingCount == 4 && world.ContentRoot.GetChildren().OfType<DebrisBody>().Count() == 2,
                "per-frame registration budget was ignored");
            Assert(world.Random.State == randomState, "presentation work changed simulation randomness");
            DebrisQueue.Enqueue(other, other.Spawn, Transform3D.Identity, Vector3.Zero, PropaneTank.LocalCenter);
            var independent = other.EffectsRoot.GetChildren().OfType<DebrisQueue>().Single();
            var outgoing = world.ContentRoot;
            outgoing.ProcessMode = Node.ProcessModeEnum.Disabled;
            queue._Process(0);
            Assert(queue.PendingCount == 0 && outgoing.GetChildren().OfType<DebrisBody>().Count() == 2,
                "disabled retiring source received deferred fragments");
            outgoing.ProcessMode = Node.ProcessModeEnum.Inherit;
            DebrisQueue.Enqueue(world, world.Spawn, Transform3D.Identity, Vector3.Zero, PropaneTank.LocalCenter);
            var incoming = new Node3D(); world.Root.AddChild(incoming); world.SetContentRoot(incoming);
            world.RetireContent(outgoing);
            queue._Process(0);
            Assert(queue.PendingCount == 0 && incoming.GetChildCount() == 0 && independent.PendingCount == other.Tuning.DebrisPiecesPerTank,
                "retirement spawned stale fragments or crossed worlds");
            DebrisQueue.Enqueue(world, world.Spawn, Transform3D.Identity, Vector3.Zero, PropaneTank.LocalCenter);
            world.Invalidate();
            Assert(queue.PendingCount == 0, "invalidation retained deferred fragment work");
        });
        runner.Run("effects/shared resources survive live tuning and another world", () =>
        {
            using var tuning = Tuning.LoadBaked();
            using var world = new WorldContext(runner, tuning);
            using var other = new WorldContext(runner, tuning);
            VentJet Vent(WorldContext owner)
            {
                var jet = new VentJet { World = owner, ContentSpawn = owner.Spawn };
                owner.Root.AddChild(jet);
                return jet;
            }
            GpuParticles3D Flame(VentJet jet) => jet.GetChildren().OfType<GpuParticles3D>().First();
            var first = Vent(world);
            var second = Vent(world);
            var original = (ParticleProcessMaterial)Flame(first).ProcessMaterial;
            var velocity = original.InitialVelocityMax;
            Assert(Flame(second).ProcessMaterial == original, "same-scale vents did not share their process resource");
            Assert(Flame(second).DrawPass1 == Flame(first).DrawPass1, "same-material particles did not share a draw mesh");
            other.Tuning.VentFlameScale *= 2;
            var changed = Vent(other);
            var next = (ParticleProcessMaterial)Flame(changed).ProcessMaterial;
            Assert(next != original && Math.Abs(next.InitialVelocityMax - velocity * 2) < .001f,
                "another world reused the wrong scale");
            Assert(original.InitialVelocityMax == velocity, "live tuning mutated existing particle trajectories");
            var restored = Vent(world);
            Assert(((ParticleProcessMaterial)Flame(restored).ProcessMaterial).InitialVelocityMax == velocity,
                "switching back to the original scale retained another world's tuning");
            var explosion = ExplosionEffect.Create(world, Vector3.Zero, 0);
            var particles = explosion.GetChildren().OfType<GpuParticles3D>().ToArray();
            var repeat = ExplosionEffect.Create(world, Vector3.Zero, 0).GetChildren().OfType<GpuParticles3D>().ToArray();
            Assert(particles.Length == 5 && repeat.Select((p, i) => p.ProcessMaterial == particles[i].ProcessMaterial).All(v => v),
                "explosions did not share all five particle process resources");
            var debris = DebrisBody.Create(world, GameAssets.Debris[0]); world.Root.AddChild(debris);
            first._Process(1d / 60); explosion._Process(1d / 60); debris._Process(1d / 60);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var frame = 0; frame < 5; frame++)
            { first._Process(1d / 60); explosion._Process(1d / 60); debris._Process(1d / 60); }
            Assert(GC.GetAllocatedBytesForCurrentThread() == allocated,
                "steady vent/explosion/debris updates allocated shader parameter names");
            other.Tuning.FireballScale *= 2;
            var larger = ExplosionEffect.Create(other, Vector3.Zero, 0).GetChildren().OfType<GpuParticles3D>().First();
            Assert(((ParticleProcessMaterial)larger.ProcessMaterial).InitialVelocityMax ==
                ((ParticleProcessMaterial)particles[0].ProcessMaterial).InitialVelocityMax * 2,
                "explosion process scale did not follow its world");
        });
        runner.Run("effects/crowding and retirement are scoped to the world", () =>
        {
            using var tuning = Tuning.LoadBaked();
            using var world = new WorldContext(runner, tuning);
            using var other = new WorldContext(runner, tuning);
            world.Tuning.VentLightMergeThreshold = 2;
            world.Tuning.VentLightClusterCount = 2;
            OmniLight3D Light(WorldContext owner, float x)
            {
                var light = new OmniLight3D { Position = Vector3.Right * x, LightEnergy = 2 };
                owner.ContentRoot.AddChild(light);
                VentLightBudget.Register(owner, light);
                return light;
            }
            var first = Light(world, 0); var second = Light(world, 1); var third = Light(world, 2);
            var isolated = Light(other, 0);
            var budget = world.EffectsRoot.GetChildren().OfType<VentLightBudget>().Single();
            budget._Process(0);
            Assert(!first.Visible && !second.Visible && !third.Visible && isolated.Visible,
                "crowded presentation crossed world ownership or retained individual lights");
            var proxies = budget.GetChildren().OfType<OmniLight3D>().ToArray();
            Assert(proxies.Length == 2 && proxies.Where(p => p.Visible).Sum(p => p.LightEnergy) == 6,
                "crowded proxy budget did not preserve the emitted energy");
            budget.Remove(third); third.Free(); budget._Process(0);
            Assert(first.Visible && second.Visible && proxies.All(p => !p.Visible),
                "falling below the threshold did not restore ordinary lighting");
            var outgoing = world.ContentRoot;
            var incoming = new Node3D(); world.Root.AddChild(incoming); world.SetContentRoot(incoming);
            world.RetireContent(outgoing);
            Assert(GodotObject.IsInstanceValid(budget) && budget.IsInsideTree(), "retiring a suburb retired the world light service");
            budget._Process(0);
            Assert(proxies.All(p => !p.Visible), "retiring content retained its proxy light energy");
            var replacement = Light(world, 3);
            Assert(isolated.Visible && other.EffectsRoot.GetChildren().OfType<VentLightBudget>().Count() == 1,
                "another world lost its service during content retirement");
            budget._Process(0);
            Assert(replacement.Visible && world.EffectsRoot.GetChildren().OfType<VentLightBudget>().Single() == budget,
                "the next suburb could not reuse the surviving world service");
            var nested = new Node3D(); incoming.AddChild(nested); replacement.Reparent(nested);
            var next = Light(world, 4); var last = Light(world, 5); budget._Process(0);
            Assert(!replacement.Visible && !next.Visible && !last.Visible && proxies.Where(p => p.Visible).Sum(p => p.LightEnergy) == 6,
                "source re-entry lost registration or duplicated its emitted energy");
            var bodies = world.Root.FindChildren("*", "RigidBody3D", owned: false).Count;
            var randomState = world.Random.State;
            Warmup.RunEffects(world, world.Root, Vector3.Down * 400);
            Assert(world.Random.State == randomState, "presentation warm-up advanced the simulation's random stream");
            Assert(world.Root.FindChildren("*", "RigidBody3D", owned: false).Count == bodies,
                "presentation warm-up added a physics body");
            var labFlash = world.EffectsRoot.GetChildren().OfType<ExplosionEffect>().Single();
            world.Clock.Advance(1.6);
            Assert(labFlash.IsQueuedForDeletion(), "Lab warm-up retained its new invisible flash");
            Warmup.Run(other, other.Root, Vector3.Down * 400);
            var matchFlash = other.EffectsRoot.GetChildren().OfType<ExplosionEffect>().Single();
            other.Clock.Advance(1.6);
            Assert(!matchFlash.IsQueuedForDeletion() && other.ShadowedFlashes == 1,
                "match warm-up changed its original flash lifetime or shadow allocation");
        });
        runner.Run("effects/cluster energy and weighted centers", () =>
        {
            var positions = new[] { Vector3.Zero, Vector3.Right, Vector3.Right * 10, Vector3.Right * 11 };
            var energies = new[] { 1f, 3f, 2f, 2f };
            var groups = new int[positions.Length];
            var seeds = new int[2]; var centers = new Vector3[2]; var totals = new float[2];
            VentLightBudget.Cluster(positions, energies, Vector3.Zero, seeds, centers, totals, 2, groups);
            Assert(seeds[0] == 0 && seeds[1] == 3, "cluster seeds lost the nearby source or spatial coverage");
            Assert(totals.SequenceEqual(new[] { 4f, 4f }) && centers.SequenceEqual(new[] { Vector3.Right * .75f, Vector3.Right * 10.5f }),
                "cluster did not conserve source energy at weighted positions");
            VentLightBudget.Cluster(positions, energies, Vector3.Zero, seeds, centers, totals, .25f, groups);
            Assert(groups.SequenceEqual(new[] { 0, -1, -1, 1 }) && totals.Sum() == 3 &&
                totals.Sum() + energies.Where((_, i) => groups[i] < 0).Sum() == energies.Sum(),
                "distant vents were moved into a cluster or lost their original energy");
            Array.Fill(energies, 0);
            VentLightBudget.Cluster(positions, energies, Vector3.Zero, seeds, centers, totals, 2, groups);
            Assert(totals.All(v => v == 0) && centers.All(v => v.IsFinite()), "extinguished clusters produced nonfinite positions");
            Array.Fill(positions, Vector3.Zero); Array.Fill(energies, 1);
            VentLightBudget.Cluster(positions, energies, Vector3.Zero, seeds, centers, totals, 2, groups);
            Assert(totals.Sum() == 4 && centers.All(v => v == Vector3.Zero), "coincident lights lost energy");
        });
    }
}
