using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Diagnostics;
using Propane.Lab;
using Propane.ReleaseSmoke;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

internal sealed partial class MeasureScenario(ILabScenarioHost host, bool play = false) : LabScenario(host)
{
    protected override Task RunCore() => play ? MeasurePlay() : Measure();

    private async Task Measure()
    {
        // The dummy headless renderer otherwise sleeps 6.9 ms even with low-processor mode disabled.
        OS.LowProcessorUsageMode = false;
        OS.LowProcessorUsageModeSleepUsec = 0;
        Engine.MaxFps = 0;
        ConfigurePacingProbe();
        var count = (int)DevArgs.GetFloat("count", Tuning.LabStackMaximum);
        Assert(count >= Tuning.LabStackMinimum && count <= 1024, "measurement count must be 24..1024");
        template.LabStackCount = Math.Min(count, Tuning.LabStackMaximum);
        // Profile beyond the product ceiling through the registry callback, using the same fixture builder.
        var scenario = LabScenarios.Get("stack") with { Setup = lab => { lab.World.Tuning.LabStackCount = count; lab.BuildStack(); } };
        var lab = Enter(scenario, LabEnvironment.Void);
        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        if (DevArgs.Get("measure-physics-cap") != null) lab.World.Tuning.DenseChainPhysicsSteps = 0;
        await Frames(180).ConfigureAwait(true);
        var warmup = DevArgs.GetFloat("measure-warmup", 0);
        var seconds = DevArgs.GetFloat("measure-seconds", 0);
        Assert(float.IsFinite(warmup) && (warmup == 0 || warmup is >= .1f and <= 120), "measurement warm-up must be 0 or 0.1..120 seconds");
        Assert(float.IsFinite(seconds) && (seconds == 0 || seconds is >= .1f and <= 120), "measurement duration must be 0 or 0.1..120 seconds");
        if (warmup > 0)
        {
            var warmUntil = lab.World.Clock.Now + warmup;
            while (lab.World.Clock.Now < warmUntil) await Frames(1).ConfigureAwait(true);
        }
        var phase = DevArgs.Get("phase") ?? "idle";
        Assert(phase is "idle" or "blast" or "chain", "unknown measurement phase");
        EffectProfile.Enabled = true;
        foreach (var system in Enum.GetValues<EffectProfile.System>().Where(s => s != EffectProfile.System.Count))
        { EffectProfile.Drain(system); EffectProfile.DrainBytes(system); }
        var allocated = GC.GetTotalAllocatedBytes();
        var gcPause = GC.GetTotalPauseDuration().TotalMilliseconds;
        var pipelineMesh = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh);
        var pipelineSurface = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface);
        var pipelineDraw = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw);
        var pipelineSpecialization = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization);
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var hide = DevArgs.Get("profile-hide");
        Assert(hide is null or "particles" or "smoke" or "lights" or "shadows" or "decals", "unknown diagnostic exclusion");
        var excludedLights = new LightExclusion { ProcessPriority = int.MaxValue };
        if (hide == "lights") AddChild(excludedLights);
        void Hide(Node node)
        {
            if (node is GpuParticles3D particle && hide == "particles") particle.Visible = false;
            if (node is GpuParticles3D puff && hide == "smoke" && (puff.DrawPass1 as PrimitiveMesh)?.Material is ShaderMaterial material && material.Shader.ResourcePath == "res://shaders/fx_smoke.gdshader") puff.Visible = false;
            if (node is OmniLight3D light && hide == "lights")
            {
                excludedLights.Lights.Add(light);
                light.TreeExiting += () => excludedLights.Lights.Remove(light);
                light.Visible = false;
            }
            if (node is OmniLight3D shadow && hide == "shadows") shadow.ShadowEnabled = false;
            if (node is Decal decal && hide == "decals") decal.Visible = false;
        }
        IDisposable? observation = null;
        if (hide != null)
        {
            var tree = GetTree();
            var guarded = Guard<Node>(Hide);
            SceneTree.NodeAddedEventHandler observe = node => guarded(node);
            tree.NodeAdded += observe;
            observation = DetachOnDisposal(() => tree.NodeAdded -= observe);
        }
        var simulationStarted = lab.World.Clock.Now;
        var wallStarted = Time.GetTicksUsec();
        var last = wallStarted;
        if (phase == "blast")
        {
            var target = lab.LiveTanks.First();
            target.TakeShot(target.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
            target.TakeShot(target.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
        }
        else if (phase == "chain")
        {
            foreach (var tank in lab.LiveTanks.ToArray())
                tank.TakeShot(tank.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
            lab.LiveTanks.First().Detonate();
        }
        var series = new Dictionary<string, List<double>>();
        var rows = new List<string>();
        void Sample(string name, double value)
        {
            if (!series.TryGetValue(name, out var values)) series[name] = values = new List<double>();
            values.Add(value);
        }
        var measureUntil = lab.World.Clock.Now + seconds;
        for (var i = 0; seconds > 0 ? lab.World.Clock.Now < measureUntil : i < 600; i++)
        {
            await Frames(1).ConfigureAwait(true);
            var now = Time.GetTicksUsec(); Sample("wall", (now - last) / 1000d); last = now;
            Sample("simulation_time", lab.World.Clock.Now - simulationStarted);
            Sample("physics_cap", Engine.MaxPhysicsStepsPerFrame);
            Sample("gpu", RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()));
            Sample("render_cpu", RenderingServer.ViewportGetMeasuredRenderTimeCpu(GetViewport().GetViewportRid()) + RenderingServer.GetFrameSetupTimeCpu());
            Sample("process", Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000);
            Sample("physics", Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000);
            Sample("active_bodies", Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects));
            Sample("contacts", Performance.GetMonitor(Performance.Monitor.Physics3DCollisionPairs));
            Sample("draw_calls", Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame));
            Sample("objects", Performance.GetMonitor(Performance.Monitor.ObjectCount));
            Sample("resources", Performance.GetMonitor(Performance.Monitor.ObjectResourceCount));
            Sample("nodes", Performance.GetMonitor(Performance.Monitor.ObjectNodeCount));
            Sample("gc_pause", GC.GetTotalPauseDuration().TotalMilliseconds - gcPause);
            Sample("pipeline_mesh", Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh) - pipelineMesh);
            Sample("pipeline_surface", Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface) - pipelineSurface);
            Sample("pipeline_draw", Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw) - pipelineDraw);
            Sample("pipeline_specialization", Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization) - pipelineSpecialization);
            foreach (var system in Enum.GetValues<EffectProfile.System>().Where(s => s != EffectProfile.System.Count))
            {
                Sample(system.ToString(), EffectProfile.Drain(system));
                Sample(system + "_bytes", EffectProfile.DrainBytes(system));
            }
            rows.Add(string.Join(",", series.Values.Select(v => v[^1].ToString("R", CultureInfo.InvariantCulture))));
        }
        EffectProfile.Enabled = false;
        observation?.Dispose();
        excludedLights.Free();
        if (DevArgs.Get("metrics") is { } path)
            File.WriteAllLines(path, new[] { string.Join(",", series.Keys) }.Concat(rows));
        GD.Print($"[lab measure] tanks={count} phase={phase} live={lab.LiveTanks.Count()} allocated_bytes={GC.GetTotalAllocatedBytes() - allocated} gc={string.Join(",", collections.Select((v, i) => GC.CollectionCount(i) - v))}");
        GD.Print($"[lab pacing] simulation={lab.World.Clock.Now - simulationStarted:R} wall={(last - wallStarted) / 1000000d:R} cap={Engine.MaxPhysicsStepsPerFrame}");
        foreach (var (name, values) in series)
        {
            values.Sort();
            GD.Print($"[lab profile] {name} p50={values[(int)(values.Count * .5)]:F3} p95={values[(int)(values.Count * .95)]:F3} worst={values[^1]:F3}");
        }
        capture.Screenshot($"stack_{count}");
        lab.Session.Dispose(); lab.Free();
    }

    // These flags affect only diagnostic scenes, never the product's pacing policy.
    private static void ConfigurePacingProbe()
    {
        if (DevArgs.Get("measure-physics-cap") != null)
        {
            var cap = DevArgs.GetFloat("measure-physics-cap", 0);
            Assert(cap is 2 or 4 or 6 or 12, "measurement physics cap must be 2, 4, 6 or 12");
            Engine.MaxPhysicsStepsPerFrame = (int)cap;
        }
        if (DevArgs.Get("measure-max-fps") != null)
        {
            var fps = DevArgs.GetFloat("measure-max-fps", 0);
            Assert(fps is 0 or 30, "measurement frame cap must be 0 or 30");
            Engine.MaxFps = (int)fps;
        }
    }

    /// <summary>The owner's primary Lab view with four in-process banking bots, measured against wall time.</summary>
    private async Task MeasurePlay()
    {
        OS.LowProcessorUsageMode = false;
        OS.LowProcessorUsageModeSleepUsec = 0;
        Engine.MaxFps = 0;
        ConfigurePacingProbe();
        var seconds = DevArgs.GetFloat("measure-seconds", 30);
        Assert(float.IsFinite(seconds) && seconds is >= .1f and <= 120, "play measurement must be 0.1..120 wall seconds");
        Assert(DevArgs.Get("frame-metrics") != null, "play measurement requires frame metrics");
        var lab = new LabSession
        {
            Scenario = LabScenarios.Get("duel"),
            EnvironmentKind = LabEnvironment.Suburb,
            Template = template,
            TestRandomSeed = 23,
            TestBankBots = 4,
            TestSuburbSettings = Propane.World.SuburbPlans.Match(template, 5) with { MapSize = template.LabSuburbSize },
            PauseOnFocusLoss = false,
            Name = "Lab"
        };
        AddLab(lab);
        Assert(lab.Initialized && lab.Session.Actors.Count == 5 && ActorView.For(lab.Player) != null, "play fixture needs four bots and the primary view");
        var count = lab.LiveTanks.Count();
        AddChild(new FrameMetrics { ProcessPriority = int.MaxValue, WarmupFrames = 0 });
        // Equal simulation age keeps the bot/pile workload comparable when a tested cap slows the world.
        var warmUntil = lab.World.Clock.Now + 3;
        while (lab.World.Clock.Now < warmUntil) await Frames(1).ConfigureAwait(true);
        var start = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        var simulation = lab.World.Clock.Now;
        double end;
        do
        {
            await Frames(1).ConfigureAwait(true);
            Assert(lab.Bank!.IsPlaying && !lab.World.Paused, "play measurement ended or paused early");
            end = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        } while (end - start < seconds);
        GD.Print($"[lab play pacing] start={start:R} end={end:R} simulation={lab.World.Clock.Now - simulation:R} wall={end - start:R} cap={Engine.MaxPhysicsStepsPerFrame} tanks={count}");
        capture.Screenshot("play-pacing");
        lab.Session.Dispose(); lab.Free();
    }

    // Apply after effects and the clustering service, which update light visibility each frame.
    private sealed partial class LightExclusion : Node
    {
        public List<OmniLight3D> Lights { get; } = new();
        public override void _Process(double delta)
        {
            foreach (var light in Lights) light.Visible = false;
        }
    }
}
