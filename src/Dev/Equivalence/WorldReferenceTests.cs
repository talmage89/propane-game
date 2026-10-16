using System;
using System.Linq;
using System.Security.Cryptography;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests;
using Propane.Net;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Dev.Equivalence;

[UnitSuite(-1)]
public static class WorldReferenceTests
{
    // Generation and construction use libm trigonometry (Mathf.Sin/Cos/Atan2), whose last bits differ between C
    // libraries: the CI runner's glibc builds a prop yaw one ulp away from the reference machine's. Exact plans, segment
    // hashes and construction hashes are compared by make test-equivalence on the reference platform; the ordinary
    // suite keeps the platform-independent checks (inventory, codec round trips of the frozen plans, segment seeds).
    private static bool PlatformExact => DevArgs.Get("platform-references") != null || DevArgs.Get("write-reference") != null;

    public static void Register(TestRunner runner)
    {
        runner.Run("reference/world release plans and their constructed geometry", () =>
        {
            var formations = Enum.GetValues<PileFormation>();
            if (DevArgs.Get("write-reference") != null)
            {
                ReferenceFile.Check("world", formations.Select(formation =>
                {
                    var plan = WorldBuildTests.Fixture(formation);
                    return new
                    {
                        formation = formation.ToString(),
                        plan = Convert.ToBase64String(PlanCodec.Encode(plan)),
                        hash = WorldBuildTests.BuildHash(runner, plan, WorldBuildTests.Settings(formation))
                    };
                }).ToArray());
                return;
            }
            var saved = ReferenceFile.Read("world");
            TestRunner.Assert(saved.GetArrayLength() == formations.Length, "world reference: incomplete formation inventory");
            for (var i = 0; i < formations.Length; i++)
            {
                var formation = formations[i];
                TestRunner.Assert(saved[i].GetProperty("formation").GetString() == formation.ToString(), "world formation inventory/order changed");
                var bytes = Convert.FromBase64String(saved[i].GetProperty("plan").GetString()!);
                ReferenceFile.Bytes(bytes, PlanCodec.Encode(PlanCodec.Decode(bytes)), $"world.{formation}.codec");
                if (!PlatformExact) continue;
                var generated = WorldBuildTests.Fixture(formation);
                var encoded = PlanCodec.Encode(generated);
                if (!bytes.SequenceEqual(encoded))
                {
                    ReferenceFile.Compare(ReferenceFile.Element(ReferenceFile.Value(PlanCodec.Decode(bytes))),
                        ReferenceFile.Element(ReferenceFile.Value(generated)), $"world.{formation}.plan");
                }

                ReferenceFile.Bytes(bytes, encoded, $"world.{formation}.plan");
                var hash = WorldBuildTests.BuildHash(runner, PlanCodec.Decode(bytes), WorldBuildTests.Settings(formation));
                ReferenceFile.Compare(saved[i].GetProperty("hash"), ReferenceFile.Element(hash), $"world.{formation}.construction");
            }
        });
        runner.Run("reference/sixty release segment plans", () =>
        {
            using var tuning = new Tuning();
            var settings = SegmentSettings.From(tuning);
            var catalog = new PlanCatalogSnapshot(new GameCatalog());
            var inventory = Enumerable.Range(1, 5).SelectMany(run => Enumerable.Range(0, 12).Reverse().Select(index =>
            {
                var segment = SegmentPlans.Generate(settings, catalog, run, index);
                return new
                {
                    run,
                    index,
                    segment.Seed,
                    start = ReferenceFile.Value(segment.Start),
                    hash = Convert.ToHexString(SHA256.HashData(PlanCodec.Encode(segment.Plan)))
                };
            })).ToArray();
            if (PlatformExact)
            {
                ReferenceFile.Check("segments", inventory);
                return;
            }
            var saved = ReferenceFile.Read("segments");
            TestRunner.Assert(saved.GetArrayLength() == inventory.Length, "segment reference: incomplete inventory");
            for (var i = 0; i < inventory.Length; i++)
            {
                var path = $"segments[{i}]";
                ReferenceFile.Compare(saved[i].GetProperty("run"), ReferenceFile.Element(inventory[i].run), $"{path}.run");
                ReferenceFile.Compare(saved[i].GetProperty("index"), ReferenceFile.Element(inventory[i].index), $"{path}.index");
                ReferenceFile.Compare(saved[i].GetProperty("Seed"), ReferenceFile.Element(inventory[i].Seed), $"{path}.Seed");
            }
        });
    }
}
