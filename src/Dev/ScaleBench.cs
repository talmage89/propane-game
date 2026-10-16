using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using Propane.Core;
using Propane.Lab;
using Propane.Player;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev;

/// <summary>Fixed supply and physics-tick workload using the production offline Tank Bank runner.</summary>
public partial class ScaleBench : Node3D
{
    private LabSession lab = null!;
    private FrameMetrics metrics = null!;
    private CaptureDirector capture = null!;
    private int ticks, warmup, measured;
    private int[] collections = new int[3];
    private TimeSpan pause;
    private long allocated;
    private long started;
    private TimeSpan cpuStarted;
    private ulong measuredPhysicsStart;
    private bool finished, pendingStart;
    private int tankCount;
    private string worldHash = "";
    private string omit = "none";
    private string cameraMode = "overview";
    private Camera3D camera = null!;
    private PlayerCharacter cameraActor = null!;
    private readonly Process process = Process.GetCurrentProcess();

    public override void _Ready()
    {
        try
        {
            // Headless cannot draw, so Godot otherwise applies its default 6900-us idle wait even with vsync off.
            OS.LowProcessorUsageMode = false;
            OS.LowProcessorUsageModeSleepUsec = 0;
            Engine.MaxFps = 0;
            var bots = (int)DevArgs.GetFloat("bots", 2);
            var multiplier = (int)DevArgs.GetFloat("tanks", 1);
            warmup = (int)(DevArgs.GetFloat("warmup", 2) * Engine.PhysicsTicksPerSecond);
            measured = (int)(DevArgs.GetFloat("seconds", 10) * Engine.PhysicsTicksPerSecond);
            if (bots < 0 || bots > 16 || multiplier < 1 || measured < 2 || warmup < 1)
                throw new ArgumentException("invalid scale benchmark workload");
            var config = typeof(ScaleBench).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            if (config != DevArgs.Get("config")) throw new InvalidOperationException($"loaded {config}, expected {DevArgs.Get("config")}");
            cameraMode = DevArgs.Get("camera") ?? "overview";
            if (cameraMode is not ("overview" or "near" or "far")) throw new ArgumentException("unknown benchmark camera");
            omit = DevArgs.Get("omit") ?? "none";
            if (omit is not ("none" or "actors" or "shadows" or "effects" or "debris"))
                throw new ArgumentException("unknown rendering feature omission");
            if (omit != "none") GetTree().NodeAdded += OmitFeature;
            using var tuning = Tuning.LoadBaked();
            tuning.FixedSeed = 23;
            tuning.LabBotSkill = DevArgs.GetFloat("bot-skill", tuning.LabBotSkill);
            if (!float.IsFinite(tuning.LabBotSkill) || tuning.LabBotSkill < 0 || tuning.LabBotSkill > 1)
                throw new ArgumentException("bot skill must be between zero and one");
            // Single-player pile sizes and map stay constant across the grid. Always reserve the same starts/pickups.
            var settings = SuburbPlans.SinglePlayer(tuning) with
            {
                TankCount = tuning.TankCount * multiplier,
                MapSize = tuning.MpMapSizeBase + tuning.MpMapSizePerPlayer * 16,
                PlayerSpawns = 17,
                AmmoSpots = tuning.PickupCount,
                StartSpacing = tuning.MpStartSpacing
            };
            tankCount = settings.TankCount;
            lab = new LabSession
            {
                Name = "ScaleLab",
                Scenario = LabScenarios.All.Single(s => s.Id == "duel"),
                EnvironmentKind = LabEnvironment.Suburb,
                Template = tuning,
                TestRandomSeed = 23,
                TestSuburbSettings = settings,
                TestBankBots = bots,
                CreateLocalView = false,
                PauseOnFocusLoss = false
            };
            AddChild(lab);
            if (lab.LiveTanks.Count() != tankCount || lab.Session.Actors.Count != bots + 1)
                throw new InvalidOperationException("fixture supply/actors differs from requested workload");
            lab.World.Present(GetViewport());
            camera = new Camera3D { Fov = 55, Current = true };
            cameraActor = lab.Session.Actors[bots > 0 ? 1 : 0].Character;
            lab.World.AddView(this, camera);
            var plan = ((Session.SuburbWorldSource)lab.Session.Source).Suburb.Plan;
            worldHash = Convert.ToHexString(SHA256.HashData(Net.PlanCodec.Encode(plan)));
            var bounds = plan.Bounds;
            var center = new Vector3(bounds.GetCenter().X, 0, bounds.GetCenter().Y);
            camera.LookAtFromPosition(center + new Vector3(0, bounds.Size.Length() * 0.55f, bounds.Size.Y * 0.45f), center);
            metrics = new FrameMetrics
            {
                ProcessPriority = int.MaxValue,
                ProcessPhysicsPriority = int.MaxValue,
                WarmupFrames = 0,
                Recording = false,
                MeasurePhysicsTail = true,
                AfterSample = FrameEnded
            };
            AddChild(metrics);
            capture = new CaptureDirector { CollectFrameMetrics = false };
            AddChild(capture);
            ProcessPhysicsPriority = int.MinValue;
            ProcessPriority = int.MaxValue;
            DevArgs.MarkReady();
            GD.Print($"[bench] assembly={config} bots={bots} tanks={tankCount} seed=23 ticks={measured}");
        }
        catch (Exception error) { Fail(error); }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (finished) return;
        try
        {
            ticks++;
            if (ticks == warmup) pendingStart = true;
            // A repeatable vent disturbance also exercises the zero-bot tank case. Normal shots, jets and chains follow.
            if (ticks == warmup + measured / 2)
            {
                metrics.Stage = 1;
                var tanks = lab.LiveTanks.ToArray();
                for (var i = 0; i < tanks.Length; i += 4)
                {
                    tanks[i].TakeShot(tanks[i].GlobalTransform * (PropaneTank.LocalCenter + Vector3.Right * World.Plan.PileBuilder.Radius),
                        tanks[i].GlobalBasis.X, Vector3.Zero);
                }
            }
            if (ticks >= warmup + measured) finished = true;
        }
        catch (Exception error) { Fail(error); }
    }

    public override void _Process(double delta)
    {
        if (cameraMode == "overview") return;
        var focus = cameraActor.BlastTargetPosition;
        var offset = cameraMode == "near" ? new Vector3(2, 1, 5) : new Vector3(0, 12, 50);
        camera.LookAtFromPosition(focus + new Basis(Vector3.Up, cameraActor.FacingYaw) * offset, focus);
    }

    private void FrameEnded()
    {
        if (pendingStart)
        {
            pendingStart = false;
            ticks = warmup; // Start at a process boundary after FrameMetrics drained the warm-up frame.
            collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            pause = GC.GetTotalPauseDuration();
            allocated = GC.GetTotalAllocatedBytes();
            cpuStarted = process.TotalProcessorTime;
            measuredPhysicsStart = Engine.GetPhysicsFrames();
            started = Stopwatch.GetTimestamp();
            metrics.Recording = true;
            lab.Bank!.ProcessMode = ProcessModeEnum.Inherit;
        }
        if (!finished || started <= 0) return;
        started = -started; // Finalize after the last measured physics tick; defer to let FrameMetrics sample it.
        Callable.From(Finish).CallDeferred();
    }

    private void Finish()
    {
        try
        {
            metrics.Recording = false;
            var elapsed = Stopwatch.GetElapsedTime(-started).TotalSeconds;
            var gcPause = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
            var bytes = GC.GetTotalAllocatedBytes() - allocated;
            var gcCounts = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
            var cpuSeconds = (process.TotalProcessorTime - cpuStarted).TotalSeconds;
            process.Refresh();
            var summary = new
            {
                headless_idle_usec = OS.LowProcessorUsageModeSleepUsec,
                max_fps = Engine.MaxFps,
                frame_delay_ms = ProjectSettings.GetSetting("application/run/frame_delay_msec", 0).AsInt32(),
                assembly_config = typeof(ScaleBench).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
                assembly_optimized = !(typeof(ScaleBench).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled ?? false),
                world_hash = worldHash,
                map_size = lab.World.Tuning.MpMapSizeBase + lab.World.Tuning.MpMapSizePerPlayer * 16,
                seed = 23,
                bots = lab.Session.Actors.Count - 1,
                bot_skill = lab.World.Tuning.LabBotSkill,
                tanks = tankCount,
                live_tanks_end = lab.LiveTanks.Count(),
                requested_physics_ticks = measured,
                physics_ticks = Engine.GetPhysicsFrames() - measuredPhysicsStart,
                physics_hz = Engine.PhysicsTicksPerSecond,
                elapsed_seconds = elapsed,
                process_cpu_seconds = cpuSeconds,
                gc_counts = gcCounts,
                gc_pause_ms = gcPause,
                allocated_bytes = bytes,
                managed_bytes = GC.GetTotalMemory(false),
                rss_bytes = process.WorkingSet64,
                peak_rss_bytes = process.PeakWorkingSet64,
                godot = Engine.GetVersionInfo()["string"].AsString(),
                runtime = System.Environment.Version.ToString(),
                cpu = OS.GetProcessorName(),
                cpu_count = System.Environment.ProcessorCount,
                gpu = RenderingServer.GetVideoAdapterName(),
                rendered = DisplayServer.GetName() != "headless",
                omitted_render_feature = omit,
                camera_mode = cameraMode
            };
            File.WriteAllText(Path.Combine(DevArgs.Get("frame-metrics")!, "summary.json"), JsonSerializer.Serialize(summary));
            // Captures happen after measurement, so image readback/encoding cannot contaminate the timings.
            capture.Screenshot("scale-final");
            GD.Print("[bench] PASS");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        finished = true;
        GD.PushError($"[bench] FAIL {error}");
        ShutdownGuard.Quit(GetTree(), 1);
    }

    // Rendering-only ablations: all brains, collisions and effect lifetimes continue unchanged.
    private void OmitFeature(Node node)
    {
        if (omit == "shadows" && node is Light3D light) light.ShadowEnabled = false;
        if (omit == "effects" && node is Node3D effect &&
            (node.Name == "Effects" || node is VentJet)) { effect.Visible = false; }
        if (omit == "debris" && node is DebrisBody debris) debris.Visible = false;
        if (omit != "actors" || node is not MeshInstance3D mesh) return;
        for (var parent = node.GetParent(); parent != null; parent = parent.GetParent())
        {
            if (parent is not PlayerCharacter) continue;
            // Styling runs in the actor's Ready after NodeAdded; apply after it has assigned visual layers.
            Callable.From(() => { if (IsInstanceValid(mesh)) mesh.Layers = 0; }).CallDeferred();
            break;
        }
    }

    public override void _ExitTree()
    {
        if (omit != "none") GetTree().NodeAdded -= OmitFeature;
        process.Dispose();
    }
}
