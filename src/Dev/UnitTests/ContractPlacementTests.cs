using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Dev.UnitTests;

/// <summary>Parent/candidate witnesses for contracts outside the frozen wire and world inventory.</summary>
[UnitSuite]
public static class ContractPlacementTests
{
    public static void Register(TestRunner runner)
    {
        var transcript = new List<object>();
        runner.Run("contracts/identity arithmetic and refusal details", () =>
        {
            foreach (var index in new[] { 0, 1, 71, BodyIds.MaxWorldEntities - 1 })
            {
                var map = BodyIds.MapTankId(index);
                var car = BodyIds.CarId(index);
                TestRunner.Assert(map == 0x10000000u + index && car == 0x30000000u + index, "world identity arithmetic changed");
                transcript.Add(new { index, map, car });
            }
            foreach (var slot in new[] { 0, 1, 71, BodyIds.MaxAllocationSlots - 1 })
            {
                foreach (var counter in new[] { 0, 1, 71, BodyIds.MaxDropsPerActor - 1 })
                {
                    var drop = BodyIds.DropTankId(slot, counter);
                    TestRunner.Assert(drop == 0x20000000u + ((uint)slot << 20) + counter, "drop identity arithmetic changed");
                    transcript.Add(new { slot, counter, drop });
                }
            }

            foreach (var index in new[] { -1, BodyIds.MaxWorldEntities })
            {
                Rejected(() => BodyIds.MapTankId(index), "index", transcript);
                Rejected(() => BodyIds.CarId(index), "index", transcript);
            }
            foreach (var pair in new[] { (-1, 0), (BodyIds.MaxAllocationSlots, 0), (0, -1), (0, BodyIds.MaxDropsPerActor) })
                Rejected(() => BodyIds.DropTankId(pair.Item1, pair.Item2), "counter", transcript);
            TestRunner.Assert((int)BodyState.State.Intact == 0 && (int)BodyState.State.Venting == 1 &&
                (int)BodyState.State.Exploded == 2 && (byte)LobbyPhase.Staging == 5 &&
                (int)GatherPhase.Settled == 2, "enum values changed");
        });
        runner.Run("contracts/catalog capture order and detached values", () =>
        {
            var source = new CatalogWitness();
            var snapshot = GameCatalog.Capture(source);
            TestRunner.Assert(source.Reads.SequenceEqual(new[] { "houses", "palettes" }.Concat(PlanCatalogSnapshot.Keys)), "catalog read order changed");
            var houses = snapshot.HouseFootprints.ToArray();
            source.Houses[0] = Vector2.Zero;
            TestRunner.Assert(snapshot.HouseFootprints.SequenceEqual(houses), "snapshot borrowed mutable house data");
            foreach (var key in PlanCatalogSnapshot.Keys)
            {
                var size = snapshot.PropFootprint(key, 2);
                TestRunner.Assert(size == new Vector2(key.Length, 3) * 2, "snapshot scale changed");
                transcript.Add(new { key, x = Bits(size.X), y = Bits(size.Y) });
            }
            TestRunner.Throws<KeyNotFoundException>(() => snapshot.PropFootprint("unknown", 1));
            transcript.Add(source.Reads);
            using var tuning = new Tuning { DissolveTanksStart = 17, DissolveTanksMin = 19, DissolvePileMin = 8, DissolvePileMax = 3 };
            var settings = SuburbPlans.Segments(tuning);
            TestRunner.Assert(settings.TanksMin == 17 && settings.Suburb.ClusterSizeMin == 3 && settings.Suburb.ClusterSizeMax == 3, "settings clamps changed");
            transcript.Add(settings);
        });
        runner.Run("contracts/ordered pile means and car registration", () =>
        {
            var plan = new SuburbPlan { Seed = 23, Bounds = new Rect2(-10, -10, 20, 20) };
            plan.Tanks.Add(new TankPlacement(new Vector2(1.25f, -2), 0.7f, 0.4f, "first", Pile: 9));
            plan.Tanks.Add(new TankPlacement(new Vector2(9, 7), 0, 0, "second", Pile: 2));
            plan.Tanks.Add(new TankPlacement(new Vector2(3, 5), 0, 0, "third", Pile: 9));
            var spots = KingPlacement.SpotsFrom(plan);
            TestRunner.Assert(spots.Count == 2 && spots[0].X == 9 && spots[0].Z == 7 &&
                spots[1].X == 2.125 && spots[1].Z == 1.5 && spots.All(s => s.Y == PlanCoordinates.GroundHeight), "pile means/order changed");
            TestRunner.Assert(KingPlacement.SpotsFrom(new SuburbPlan { Seed = 23, Bounds = new Rect2() }).Count == 0, "empty pile list changed");
            transcript.Add(spots);
            foreach (var model in new[] { "car:sedan", "CAR:sedan", "prop:wheelie_bin", "car:truck", "car:", "xcar:sedan" })
                plan.Props.Add(new PropPlacement(model, new Vector2(plan.Props.Count, 2), 0.3f, 1, PropBody.Static, 0, Colors.White));
            var ledger = new TankLedger(plan, 2);
            var live = ledger.Live();
            TestRunner.Assert(live.Select(b => b.Body.Id).SequenceEqual(new[] { BodyIds.MapTankId(0), BodyIds.MapTankId(1),
                BodyIds.MapTankId(2), BodyIds.CarId(0), BodyIds.CarId(3), BodyIds.CarId(4) }), "car membership/order changed");
            transcript.Add(live.Select(b => new { b.Body.Id, x = Bits(b.Body.Transform.Origin.X), y = Bits(b.Body.Transform.Origin.Y), z = Bits(b.Body.Transform.Origin.Z) }).ToArray());
        });
        runner.Run("contracts/appearance fallback and malformed count details", () =>
        {
            foreach (var index in Enumerable.Range(-17, 35))
            {
                var appearance = AppearanceRecord.FromPalette(index);
                TestRunner.Assert(appearance == AppearanceRecord.FromColor(AppearancePalette.PlayerColor(index)), "palette wrap changed");
                transcript.Add(new { index, appearance, appearance.LegacyIndex, encoded = Convert.ToHexString(AppearanceCodec.Encode(appearance)) });
            }
            try { AppearanceCodec.Decode(new byte[] { 1, 1, 2, 3, 17 }); throw new InvalidOperationException("accepted bad count"); }
            catch (InvalidDataException e) { TestRunner.Assert(e.Message == "bad count 17", "count diagnostic changed"); transcript.Add(e.Message); }
        });
        runner.Run("contracts/melee samples live bounds without changing an active window", () =>
        {
            using var t = new Tuning
            {
                MeleeWindup = .25f,
                MeleeActive = .5f,
                MeleeRecovery = .75f,
                MeleeCooldown = .5f,
                MeleeRelayGrace = 0,
                MeleeMaxContacts = 1
            };
            MeleeTiming Timing() => new(t.MeleeWindup, t.MeleeActive, t.MeleeRecovery, t.MeleeCooldown, t.MeleeRelayGrace);
            var validation = new MeleeValidation();
            var start = new MeleeStartMessage(1, Vector3.Forward);
            var contact = new MeleeContactMessage(1, 2, -1);
            var first = validation.Start(start, 0, Timing());
            t.MeleeWindup = 1;
            var oldWindow = !validation.Contact(contact, .751, t.MeleeMaxContacts);
            var oldCooldown = !validation.Start(start with { Swing = 2 }, 1.99, Timing());
            t.MeleeWindup = .5f; t.MeleeActive = .5f; t.MeleeRecovery = .5f;
            t.MeleeCooldown = .75f; t.MeleeRelayGrace = .25f;
            var second = validation.Start(start with { Swing = 2 }, 2, Timing());
            contact = contact with { Swing = 2 };
            var hit = validation.Contact(contact, 3.25, t.MeleeMaxContacts);
            var cap = !validation.Contact(contact with { Victim = 3 }, 3.25, t.MeleeMaxContacts);
            t.MeleeMaxContacts = 2;
            var liveCap = validation.Contact(contact with { Victim = 3 }, 3.25, t.MeleeMaxContacts);
            t.MeleeMaxContacts = 4;
            var late = !validation.Contact(contact with { Victim = 4 }, 3.251, t.MeleeMaxContacts);
            var results = new[] { first, oldWindow, oldCooldown, second, hit, cap, liveCap, late };
            TestRunner.Assert(results.All(v => v), "live contact cap or frozen swing bounds changed");
            transcript.Add(results);
        });
        runner.Run("contracts/write optional parent transcript", () =>
        {
            if (DevArgs.Get("contracts-transcript") is { } path) File.WriteAllText(path, JsonSerializer.Serialize(transcript));
        });
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    private static void Rejected(Action action, string parameter, List<object> transcript)
    {
        try { action(); throw new InvalidOperationException("accepted exhausted identity"); }
        catch (ArgumentOutOfRangeException e)
        {
            TestRunner.Assert(e.ParamName == parameter, "identity refusal parameter changed");
            transcript.Add(new { e.ParamName, e.Message });
        }
    }

    private sealed class CatalogWitness : IPlanCatalog
    {
        public readonly List<string> Reads = new();
        public readonly Vector2[] Houses = { new(3, 4), new(5, 6) };
        public IReadOnlyList<Vector2> HouseFootprints { get { Reads.Add("houses"); return Houses; } }
        public int HousePaletteCount { get { Reads.Add("palettes"); return 4; } }
        public Vector2 PropFootprint(string model, float scale) { Reads.Add(model); return new Vector2(model.Length, 3) * scale; }
    }
}
