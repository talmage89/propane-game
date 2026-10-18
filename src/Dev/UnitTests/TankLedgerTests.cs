using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;
using Propane.Tank;
using Propane.World.Plan;

namespace Propane.Dev.UnitTests;

/// <summary>Compare incremental accounting with the parent's scan of every canonical record, including tombstones.</summary>
[UnitSuite]
public static class TankLedgerTests
{
    private static readonly FieldInfo BodiesField = typeof(TankLedger).GetField("bodies", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static void Register(TestRunner runner)
    {
        runner.Run("ledger/scripted accounting and refusals match the parent scan", Scripted);
        runner.Run("ledger/random operation sequences match the parent scan", () =>
        {
            for (var seed = 0; seed < 16; seed++) Randomized(seed);
        });
        if (DevArgs.Get("ledger-bench") != null) runner.Run("ledger/paired benchmark", Benchmark);
    }

    private static BodyState Pose(uint id) => new(id, Transform3D.Identity, Vector3.Zero, Vector3.Zero);

    private static SuburbPlan Plan()
    {
        var plan = new SuburbPlan { Seed = 23, Bounds = new Rect2(0, 0, 10, 10) };
        for (var i = 0; i < 4; i++) plan.Tanks.Add(new TankPlacement(Vector2.Zero, 0, 0, "ledger test"));
        plan.Props.Add(new PropPlacement("bin", Vector2.Zero, 0, 1, PropBody.Loose, 1, Colors.White));
        plan.Props.Add(new PropPlacement("car:test", Vector2.Zero, 0, 1, PropBody.Loose, 1, Colors.White));
        return plan;
    }

    private static void Scripted()
    {
        var sequence = new Sequence(Plan());
        var ledger = sequence.Ledger;
        var map = BodyIds.MapTankId(0);
        var car = BodyIds.CarId(1);
        var drop = Pose(BodyIds.DropTankId(0, 0));
        sequence.Check("constructed mixed plan", 4);
        sequence.Step("empty drops", () => ledger.RegisterDrops(0, 2, Array.Empty<BodyState>()), 4);
        sequence.Step("valid drops", () => ledger.RegisterDrops(0, 2, new[] { drop, Pose(drop.Id + 1) }), 6);
        foreach (var invalid in new[]
                 {
                     new[] { Pose(drop.Id + 2), drop }, // Whole batch refused after an otherwise valid first entry.
                     new[] { Pose(drop.Id + 2), Pose(drop.Id + 2) },
                     new[] { Pose(BodyIds.DropTankId(1, 0)) },
                     new[] { Pose(drop.Id + 2) with { Epoch = 2 } },
                     new[] { Pose(drop.Id + 2) with { Sequence = 1 } },
                 })
        {
            sequence.Step("refused drop registration", () => TestRunner.Throws<InvalidDataException>(() => ledger.RegisterDrops(0, 2, invalid)), 6);
        }

        sequence.Step("invalid refill owner", () => TestRunner.Throws<InvalidDataException>(() => ledger.SpawnWorldTanks(0, new[] { Transform3D.Identity })), 6);
        sequence.Step("exhausted refill", () => TestRunner.Throws<InvalidDataException>(() => ledger.SpawnWorldTanks(2, new OversizedPile())), 6);
        sequence.Step("empty refill", () => ledger.SpawnWorldTanks(2, Array.Empty<Transform3D>()), 6);
        sequence.Step("claim", () => Accepted(ledger.Claim(3, new ClaimMessage(1, new[] { ledger.Get(map)!.Body }), true)), 6);
        sequence.Step("puncture", () => Accepted(ledger.Puncture(3, Puncture(ledger.Get(map)!.Body, 2), 7, true)), 6);
        sequence.Step("repeated puncture", () => Refused(ledger.Puncture(3, Puncture(ledger.Get(map)!.Body, 3), 7, true), ClaimRefusal.Lifecycle), 6);
        sequence.Step("wrong phase", () => Refused(ledger.Detonate(3, new ExplodeMessage(map, Vector3.Zero, ledger.Get(map)!.Body.Epoch, 4), 7, false), ClaimRefusal.WrongPhase), 6);
        sequence.Step("stale epoch", () => Refused(ledger.Detonate(3, new ExplodeMessage(map, Vector3.Zero, 1, 4), 7, true), ClaimRefusal.StaleEpoch), 6);
        sequence.Step("unknown body", () => Refused(ledger.Detonate(3, new ExplodeMessage(0, Vector3.Zero, 1, 4), 7, true), ClaimRefusal.UnknownBody), 6);
        sequence.Step("car detonation", () => Refused(ledger.Detonate(3, new ExplodeMessage(car, Vector3.Zero, 1, 4), 7, true), ClaimRefusal.Lifecycle), 6);
        sequence.Step("detonate map", () => Accepted(ledger.Detonate(3, new ExplodeMessage(map, Vector3.Zero, ledger.Get(map)!.Body.Epoch, 4), 7, true)), 5);
        sequence.Step("repeated detonation", () => Refused(ledger.Detonate(3, new ExplodeMessage(map, Vector3.Zero, ledger.Get(map)!.Body.Epoch, 5), 7, true), ClaimRefusal.Exploded), 5);
        sequence.Step("duplicate request", () => Refused(ledger.Detonate(3, new ExplodeMessage(drop.Id, Vector3.Zero, 1, 4), 7, true), ClaimRefusal.DuplicateRequest), 5);
        sequence.Step("unknown dependency", () => Refused(ledger.Detonate(3, new ExplodeMessage(drop.Id, Vector3.Zero, 1, 5, 99), 7, true), ClaimRefusal.Cause), 5);
        sequence.Step("wrong actor dependency", () => Refused(ledger.Detonate(3, new ExplodeMessage(drop.Id, Vector3.Zero, 1, 5, 4), 8, true), ClaimRefusal.Cause), 5);
        sequence.Step("dependent drop detonation", () => Accepted(ledger.Detonate(3, new ExplodeMessage(drop.Id, Vector3.Zero, 1, 5, 4), 7, true)), 4);
        sequence.Step("tombstone reuse", () => TestRunner.Throws<InvalidDataException>(() => ledger.RegisterDrops(0, 2, new[] { drop })), 4);
        sequence.Step("departure transfer", () => ledger.Transfer(2, 3), 4);
        sequence.Step("repeated transfer", () => ledger.Transfer(2, 3), 4);
        TestRunner.Assert(ledger.Exploded(3, 4) && ledger.Exploded(3, 5) && ledger.MapDetonations == 1 && ledger.DropDetonations == 1,
            "explosion dependencies or map/drop audit changed");
        for (var refill = 0; refill < 64; refill++)
        {
            IReadOnlyList<BodyState> fresh = Array.Empty<BodyState>();
            sequence.Step("refill", () => fresh = ledger.SpawnWorldTanks(2, new[] { Transform3D.Identity, Transform3D.Identity }), 6);
            sequence.Step("refill transfer", () => ledger.Transfer(2, 3), 6);
            foreach (var body in fresh)
            {
                sequence.Step("refill detonation", () => Accepted(ledger.Detonate(3,
                    new ExplodeMessage(body.Id, Vector3.Zero, ledger.Get(body.Id)!.Body.Epoch, (uint)(10 + refill * 2 + (body.Id - fresh[0].Id))), 7, true)),
                    body.Id == fresh[0].Id ? 5 : 4);
            }
        }
        var remaining = ledger.Live().Where(b => b.Body.Id < BodyIds.CarBase).ToArray();
        for (var i = 0; i < remaining.Length; i++)
        {
            var body = remaining[i].Body;
            sequence.Step("deplete to zero", () => Accepted(ledger.Detonate(3,
                new ExplodeMessage(body.Id, Vector3.Zero, body.Epoch, (uint)i + 1000), 7, true)), remaining.Length - i - 1);
        }
        sequence.Step("refill after zero", () => ledger.SpawnWorldTanks(2, new[] { Transform3D.Identity }), 1);
        sequence.Finish("scripted");
        var empty = new Sequence(new SuburbPlan { Seed = 0, Bounds = new Rect2() });
        empty.Check("empty plan", 0);
        empty.Step("first refill", () => empty.Ledger.SpawnWorldTanks(2, new[] { Transform3D.Identity }), 1);
        empty.Finish("empty");
    }

    private static void Randomized(int seed)
    {
        var random = new Random(seed);
        var sequence = new Sequence(Plan());
        var ledger = sequence.Ledger;
        for (var step = 0; step < 512; step++)
        {
            var entries = sequence.Records.Values.ToArray();
            var current = entries[random.Next(entries.Length)];
            var body = current.Body;
            if (random.Next(4) == 0) body = body with { Epoch = body.Epoch + 1 };
            if (random.Next(16) == 0) body = body with { Id = 0 };
            var peer = random.Next(2, 5);
            var allowed = random.Next(4) != 0;
            var request = (uint)random.Next(1, 100);
            var cause = random.Next(4) == 0 ? (uint)random.Next(1, 100) : 0;
            var operation = random.Next(9);
            sequence.Step($"seed {seed}, step {step}, operation {operation}", () =>
            {
                switch (operation)
                {
                    case 0:
                        var id = BodyIds.DropTankId(0, random.Next(128));
                        var drops = new[] { Pose(id), Pose(id + 1) with { Sequence = random.Next(8) == 0 ? 1u : 0u } };
                        try { ledger.RegisterDrops(random.Next(8) == 0 ? 1 : 0, peer, drops); }
                        catch (InvalidDataException) { } // Count and records are still checked after the refusal.
                        break;
                    case 1:
                        try { ledger.SpawnWorldTanks(allowed ? peer : 0, Enumerable.Repeat(Transform3D.Identity, random.Next(5)).ToArray()); }
                        catch (InvalidDataException) { }
                        break;
                    case 2: ledger.Claim(peer, new ClaimMessage(request, new[] { body }, cause), allowed); break;
                    case 3: ledger.Puncture(peer, Puncture(body, request) with { Cause = cause }, peer + 10, allowed); break;
                    case 4: ledger.Detonate(peer, new ExplodeMessage(body.Id, Vector3.Zero, body.Epoch, request, cause), peer + 10, allowed); break;
                    case 5: ledger.Transfer(peer, peer == 2 ? 3 : 2); break;
                    case 6: ledger.Update(peer, body with { Sequence = (uint)random.Next(1, 100) }, random.Next(2) == 0); break;
                    case 7: ledger.Gather(peer, 2, new GatherMessage(GatherPhase.Settled, new[] { body with { Sequence = (uint)random.Next(1, 100) } }), allowed); break;
                    case 8: ledger.Transfer(peer, peer); break;
                }
            });
        }
        sequence.Finish($"seed-{seed}");
    }

    private static PunctureMessage Puncture(BodyState body, uint request) =>
        new(body.Id, body.Transform, body.Linear, body.Angular, Vector3.Zero, Vector3.Up, 0, body.Epoch, request);

    private static void Accepted(AuthorityMessage result) => Refused(result, ClaimRefusal.None);
    private static void Refused(AuthorityMessage result, ClaimRefusal expected) =>
        TestRunner.Assert(result.Reason == expected, $"expected {expected}, got {result.Reason}");

    private sealed class Sequence
    {
        public readonly TankLedger Ledger;
        public readonly Dictionary<uint, BodyAuthority> Records;
        private readonly StringBuilder transcript = new();
        private int operations;

        public Sequence(SuburbPlan plan)
        {
            Ledger = new TankLedger(plan, 2);
            Records = (Dictionary<uint, BodyAuthority>)BodiesField.GetValue(Ledger)!;
            Check("initial");
        }

        public void Step(string label, Action operation, int? expected = null)
        {
            operation();
            operations++;
            Check(label, expected);
        }

        public void Check(string label, int? expected = null)
        {
            // Exact LiveTanks implementation from the immediate parent, over canonical records, not Live().
            var scanned = Records.Values.Count(b => b.State != BodyState.State.Exploded && b.Body.Id < BodyIds.CarBase);
            TestRunner.Assert(Ledger.LiveTanks == scanned && (expected == null || scanned == expected),
                $"{label}: counter {Ledger.LiveTanks}, parent scan {scanned}, expected {expected}");
            var live = Records.Values.Where(b => b.State != BodyState.State.Exploded).OrderBy(b => b.Body.Id).ToArray();
            TestRunner.Assert(Ledger.Live().SequenceEqual(live), $"{label}: live snapshot order/content changed");
            transcript.Append($"{operations}:{Ledger.LiveTanks}:{Ledger.MapDetonations}:{Ledger.DropDetonations}|");
            foreach (var entry in Records.Values)
                transcript.Append($"{entry.Body.Id},{entry.Owner},{entry.State},{entry.Body.Epoch},{entry.Body.Sequence},{entry.Rest},{entry.CauseActorId};");
            transcript.AppendLine();
        }

        public void Finish(string label) => GD.Print($"[ledger] {label} operations={operations} transcript={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transcript.ToString())))}");
    }

    /// <summary>Reject a pile beyond identity capacity without allocating hundreds of millions of poses.</summary>
    private sealed class OversizedPile : IReadOnlyList<Transform3D>
    {
        public int Count => BodyIds.MaxWorldEntities;
        public Transform3D this[int index] => throw new InvalidOperationException("oversized pile was traversed");
        public IEnumerator<Transform3D> GetEnumerator() => throw new InvalidOperationException("oversized pile was traversed");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void Benchmark()
    {
        foreach (var history in new[] { 0, 4096, 16384 })
        {
            using var fixture = new LobbyWorldFixture(3);
            // Use the real roaming-world ledger and the full server phase tick, without recording packets.
            var lobbies = (System.Collections.IDictionary)typeof(LobbyServer)
                .GetField("lobbies", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Server)!;
            var lobby = lobbies.Values.Cast<object>().Single();
            var world = lobby.GetType().GetField("World")!.GetValue(lobby)!;
            var ledger = (TankLedger)world.GetType().GetField("Tanks")!.GetValue(world)!;
            fixture.Server.SendOverride = (_, _, _) => { };
            var fresh = ledger.SpawnWorldTanks(2, Enumerable.Repeat(Transform3D.Identity, history).ToArray());
            for (var i = 0; i < fresh.Count; i++)
                Accepted(ledger.Detonate(2, new ExplodeMessage(fresh[i].Id, Vector3.Zero, 1, (uint)i + 1), 2, true));
            var live = ledger.LiveTanks;
            var records = ((Dictionary<uint, BodyAuthority>)BodiesField.GetValue(ledger)!).Count;
            Measure($"read history={history} records={records} live={live}", () => ReadCounts(ledger), 16);
            // StockInterval is .5 s. Each sample includes gate expiration, stock and rule processing.
            Measure($"server-tick history={history} records={records} live={live}", () => fixture.Advance(.5), 1);
            TestRunner.Assert(ledger.LiveTanks == live, "benchmark changed standing stock");
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadCounts(TankLedger ledger)
    {
        var sum = 0;
        for (var i = 0; i < 16; i++) sum += ledger.LiveTanks;
        TestRunner.Assert(sum > 0, "benchmark lost its live stock");
    }

    private static void Measure(string label, Action tick, int reads)
    {
        const int samples = 1024;
        for (var i = 0; i < 128; i++) tick();
        var times = new double[samples];
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            tick();
            times[i] = (Stopwatch.GetTimestamp() - start) * (1_000_000.0 / Stopwatch.Frequency);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var mean = times.Average() / reads;
        Array.Sort(times);
        GD.Print(FormattableString.Invariant($"[ledger-bench] {label} samples={samples} mean_us={mean:F4} p95_us={times[(int)(samples * .95)] / reads:F4} p99_us={times[(int)(samples * .99)] / reads:F4} bytes={allocated / (double)(samples * reads):F2}"));
    }
}
