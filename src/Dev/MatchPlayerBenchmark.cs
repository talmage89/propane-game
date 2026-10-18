using System;
using System.Diagnostics;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;

namespace Propane.Dev;

/// <summary>Measures the whole Match presentation callback after real frames warm the renderer and managed code.</summary>
public partial class MatchPlayerBenchmark : Node
{
    private PlayerRosterFixture fixture = null!;
    private readonly double[] samples = new double[2000];
    private readonly long[] allocations = new long[2000];
    private long warmup;
    private int frames;
    private int count;

    public override void _Ready()
    {
        fixture = new PlayerRosterFixture(this, botsFirst: true);
        // The controlled workload has fixed actors and camera; measure one presentation callback per real frame.
        fixture.Match.SetProcess(false);
        fixture.Match.SetPhysicsProcess(false);
        foreach (var actor in fixture.Match.Session.Actors) actor.Character.ProcessMode = ProcessModeEnum.Disabled;
        warmup = Stopwatch.GetTimestamp();
    }

    public override void _Process(double delta)
    {
        if (++frames < 180 || Stopwatch.GetElapsedTime(warmup).TotalSeconds < 5)
        {
            fixture.Match._Process(0);
            return;
        }
        if (count == 0 && (!fixture.Match.Session.IsAlive || fixture.Match.Player.View == null))
        {
            GD.PushError("[player-benchmark] inactive match or missing local view");
            SetProcess(false);
            ShutdownGuard.Quit(GetTree(), 1);
            return;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        fixture.Match._Process(0);
        samples[count] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        allocations[count] = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (++count < samples.Length) return;
        SetProcess(false);
        var total = 0d;
        var bytes = 0L;
        foreach (var sample in samples) total += sample;
        foreach (var allocation in allocations) bytes += allocation;
        Array.Sort(samples);
        GD.Print(FormattableString.Invariant($"[player-benchmark] frames={count} us={total / count:F3} p95={samples[1899]:F3} p99={samples[1979]:F3} bytes={bytes / (double)count:F1}"));
        ShutdownGuard.Quit(GetTree());
    }

    public override void _ExitTree() => fixture?.Dispose();
}
