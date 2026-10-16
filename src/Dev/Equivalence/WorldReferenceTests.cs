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
            ReferenceFile.Check("segments", inventory);
        });
    }
}
