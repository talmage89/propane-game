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
            Warmup.RunEffects(world, world.Root, Vector3.Down * 400);
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
