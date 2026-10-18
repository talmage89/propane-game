using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

/// <summary>Opt-in whole client tick costs, including controlled authority receives and synchronous request sends.</summary>
[UnitSuite]
public static class AuthorityBenchTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("authority benchmark/opt-in workload", () =>
        {
            if (DevArgs.Get("authority-bench") == null) return;
            Measure(runner, requests: false);
            Measure(runner, requests: true);
        });
    }

    private static void Measure(TestRunner runner, bool requests)
    {
        const int warmup = 4000;
        const int ticks = 10000;
        using var server = new ServerFixture(length: 3600);
        var sent = new Queue<byte[]>();
        using var client = new MatchClient(runner, server.Setup, server.Clock, 2, authoritySend: sent.Enqueue);
        var match = client.Match;
        var tank = match.LiveTanks.First();
        var packets = 0;
        var payloadBytes = 0L;
        var notifications = 0;
        match.AuthorityResolved += _ => notifications++;
        // No physics stepping: identical awake bodies, no hits/drops or RNG in the measured workload.
        foreach (var entry in match.Bodies.Entries) entry.Body.Sleeping = false;
        void Tick()
        {
            server.Clock.Seconds += 1d / 120;
            if (requests)
            {
                Assert(match.Claim(new[] { tank }, atomic: true) != 0, "benchmark failed to queue a claim");
                while (sent.TryDequeue(out var bytes))
                {
                    packets++;
                    payloadBytes += bytes.Length;
                    server.Receive(bytes);
                }
                foreach (var packet in server.Packets)
                    if (packet.Peer == 2 && packet.Message.Type == Msg.Authority) match.OnMessage(packet.Message);
                server.Packets.Clear();
            }
            match._PhysicsProcess(1d / 120);
        }
        for (var i = 0; i < warmup; i++) Tick();
        packets = notifications = 0;
        payloadBytes = 0;
        var samples = new double[ticks];
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ticks; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Tick();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        var bytesPerTick = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)ticks;
        Assert(match.Session.IsAlive && (!requests || packets == ticks && notifications == ticks), "benchmark workload changed");
        var mean = samples.Average();
        Array.Sort(samples);
        GD.Print(FormattableString.Invariant($"[authority-bench] requests={requests} ticks={ticks} packets={packets} payload_bytes={payloadBytes} notifications={notifications} bytes_per_tick={bytesPerTick:F2} mean_us={mean:F3} p95_us={samples[9499]:F3} p99_us={samples[9899]:F3}"));
    }
}
