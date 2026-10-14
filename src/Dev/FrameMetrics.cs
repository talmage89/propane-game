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
    public int WarmupFrames { get; init; } = 180;
    public bool Recording { get; set; } = true;
    public bool MeasurePhysicsTail { get; init; }
    public Action? AfterSample { get; init; }
    public int Stage { get; set; }
    private ulong physicsCallbacksStart, processCallbacksStart;
    private double physicsCallbacks;
    private ulong physicsTailStart;
    private double physicsTail;
    private ulong physicsFrames;
    private StreamWriter? output;
    private ulong last;
    private int frames;
    private static readonly EffectProfile.System[] systems = Enum.GetValues<EffectProfile.System>()
        .Where(s => s != EffectProfile.System.Count).ToArray();

    private readonly double[] values = new double[16 + systems.Length];

    public override void _Ready()
    {
        var directory = DevArgs.Get("frame-metrics")!;
        Directory.CreateDirectory(directory);
        output = new StreamWriter(Path.Combine(directory, $"frames-{OS.GetProcessId()}.csv"));
        output.WriteLine("frame,wall,gpu,process,physics," + string.Join(",", systems) + ",time,blast_count,explosion_count,physics_steps,physics_tail,stage,process_callbacks,physics_callbacks,brain_calls,navigation_sweep_calls,navigation_support_calls");
        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        EffectProfile.Enabled = true;
        last = Time.GetTicksUsec();
        physicsFrames = Engine.GetPhysicsFrames();
        if (MeasurePhysicsTail)
        {
            GetTree().PhysicsFrame += BeginPhysics;
            GetTree().ProcessFrame += BeginProcess;
        }
    }

    public override void _Process(double delta)
    {
        var now = Time.GetTicksUsec();
        var timestamp = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        values[0] = frames;
        values[1] = (now - last) / 1000d;
        values[2] = RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid());
        values[3] = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
        values[4] = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000;
        for (var i = 0; i < systems.Length; i++) values[5 + i] = EffectProfile.Drain(systems[i]);
        values[5 + systems.Length] = timestamp;
        values[6 + systems.Length] = EffectProfile.DrainCalls(EffectProfile.System.Blast);
        values[7 + systems.Length] = EffectProfile.DrainCalls(EffectProfile.System.ExplosionCreate);
        values[8 + systems.Length] = Engine.GetPhysicsFrames() - physicsFrames;
        values[9 + systems.Length] = physicsTail;
        values[10 + systems.Length] = Stage;
        values[11 + systems.Length] = processCallbacksStart == 0 ? 0 : (now - processCallbacksStart) / 1000d;
        values[12 + systems.Length] = physicsCallbacks;
        values[^3] = EffectProfile.DrainCalls(EffectProfile.System.BotBrain);
        values[^2] = EffectProfile.DrainCalls(EffectProfile.System.NavigationSweep);
        values[^1] = EffectProfile.DrainCalls(EffectProfile.System.NavigationSupport);
        physicsCallbacks = 0;
        physicsTail = 0;
        physicsFrames = Engine.GetPhysicsFrames();
        last = now;
        if (frames++ >= WarmupFrames && Recording)
        {
            // Unthrottled headless frames must not manufacture GC pressure by allocating numeric strings/arrays.
            Span<char> formatted = stackalloc char[32];
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0) output!.Write(',');
                if (!values[i].TryFormat(formatted, out var written, "R", CultureInfo.InvariantCulture))
                    throw new InvalidOperationException("frame metric did not fit the formatting buffer");
                output!.Write(formatted[..written]);
            }
            output!.WriteLine();
        }
        AfterSample?.Invoke();
    }

    // After the last physics callback until the next physics/process signal: native step plus engine bookkeeping
    // and query flush (including force integrators). This is an upper bound, not Jolt's internal solver timer.
    public override void _PhysicsProcess(double delta)
    {
        if (MeasurePhysicsTail)
        {
            physicsTailStart = Time.GetTicksUsec();
            if (physicsCallbacksStart != 0) physicsCallbacks += (physicsTailStart - physicsCallbacksStart) / 1000d;
        }
    }

    private void BeginPhysics()
    {
        ClosePhysicsTail();
        physicsCallbacksStart = Time.GetTicksUsec();
    }

    private void BeginProcess()
    {
        ClosePhysicsTail();
        processCallbacksStart = Time.GetTicksUsec();
    }

    private void ClosePhysicsTail()
    {
        if (physicsTailStart == 0) return;
        physicsTail += (Time.GetTicksUsec() - physicsTailStart) / 1000d;
        physicsTailStart = 0;
    }

    public override void _ExitTree()
    {
        if (MeasurePhysicsTail)
        {
            GetTree().PhysicsFrame -= BeginPhysics;
            GetTree().ProcessFrame -= BeginProcess;
        }
        output?.Dispose();
        EffectProfile.Enabled = false;
    }
}
