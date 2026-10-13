using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using Propane.Fx;

namespace Propane.Dev;

/// <summary>Optional harness telemetry, rendered or headless, after 180 startup frames; each process owns its CSV.</summary>
public partial class FrameMetrics : Node
{
    private StreamWriter? output;
    private ulong last;
    private int frames;
    private static readonly EffectProfile.System[] systems = Enum.GetValues<EffectProfile.System>()
        .Where(s => s != EffectProfile.System.Count).ToArray();

    public override void _Ready()
    {
        var directory = DevArgs.Get("frame-metrics")!;
        Directory.CreateDirectory(directory);
        output = new StreamWriter(Path.Combine(directory, $"frames-{OS.GetProcessId()}.csv"));
        output.WriteLine("frame,wall,gpu,process,physics," + string.Join(",", systems) + ",time,blast_count,explosion_count");
        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        EffectProfile.Enabled = true;
        last = Time.GetTicksUsec();
    }

    public override void _Process(double delta)
    {
        var now = Time.GetTicksUsec();
        var timestamp = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        var values = new double[8 + systems.Length];
        values[0] = frames;
        values[1] = (now - last) / 1000d;
        values[2] = RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid());
        values[3] = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
        values[4] = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000;
        for (var i = 0; i < systems.Length; i++) values[5 + i] = EffectProfile.Drain(systems[i]);
        values[^3] = timestamp;
        values[^2] = EffectProfile.DrainCalls(EffectProfile.System.Blast);
        values[^1] = EffectProfile.DrainCalls(EffectProfile.System.ExplosionCreate);
        last = now;
        if (frames++ >= 180) output!.WriteLine(string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
    }

    public override void _ExitTree()
    {
        output?.Dispose();
        EffectProfile.Enabled = false;
    }
}
