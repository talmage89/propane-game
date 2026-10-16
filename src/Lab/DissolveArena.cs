using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Session;
using Propane.Tank;
using Propane.World;

namespace Propane.Lab;

/// <summary>
/// Dissolve Run's presenter and segment policy. It holds no rules: every step it reads the front and the furthest point
/// reached from the runner's rules, and from them keeps the playable window: segments are built ahead (one per step,
/// from plans a worker generated) and the street materializes up to <c>DissolveAhead</c> past the furthest point;
/// behind the front the floor is taken away, tanks dissolve (they can no longer chain or score) and whole segments are
/// retired with their bodies and effects. The window's two edges are one global shader value, so moving them costs
/// nothing per mesh. It also runs the ammo cans, the front's glowing sheet, the fall when caught and the best run.
/// </summary>
public partial class DissolveArena : Node3D
{
    private const string WindowParameter = "reveal_window";
    private const string BestFile = "user://dissolve_run.cfg";

    public required LabSession Lab { get; init; }
    public required SegmentWorldSource Source { get; init; }
    public required int RunSeed { get; init; }
    public bool Daily { get; init; }

    private readonly HashSet<PropaneTank> dissolved = new();
    private readonly List<AmmoPickup> pickups = new();
    private readonly List<double> buildSlices = new();
    private readonly List<double> retireMilliseconds = new();
    private PrototypeRunner? runner;
    private MeshInstance3D wall = null!;
    private ShaderMaterial wallMaterial = null!;
    private float horizon;
    private float? caughtCut;
    private int sweepAge;

    /// <summary>Physics steps between sweeps of the street behind the front (12 steps: ten a second at 120 Hz).</summary>
    private const int SweepEvery = 12;

    public Vector3 Start { get; private set; }
    public float StartYaw { get; private set; }
    /// <summary>World X of the window's back edge (the front, or where the caught runner stood) and its leading edge.</summary>
    public float Back { get; private set; }
    public float Horizon => horizon;
    public IReadOnlyList<AmmoPickup> Pickups => pickups;
    public IReadOnlyList<ModeSpot> Spots => new[] { new ModeSpot(Start.X, Start.Y, Start.Z) };
    /// <summary>Time each streaming step spent building segment pieces.</summary>
    public IReadOnlyList<double> BuildSlices => buildSlices;
    /// <summary>Time each step spent freeing retired segments.</summary>
    public IReadOnlyList<double> RetireMilliseconds => retireMilliseconds;
    public int DissolvedTanks { get; private set; }
    public int BestScore { get; private set; }
    public bool NewBest { get; private set; }
    public bool Dissolved(PropaneTank tank) => dissolved.Contains(tank);
    private DissolveRunRules? Rules => runner?.Rules as DissolveRunRules;
    private Tuning Tuning => Lab.World.Tuning;

    public override void _Ready()
    {
        var settings = Source.Settings;
        Source.Environment.FloorHeight = -Tuning.DissolveVoidDepth;
        Start = new Vector3(SegmentPlans.StartOf(settings, 0) + Mathf.Min(Tuning.DissolveStartOffset, settings.Length - 5f), 0.05f, 0);
        StartYaw = RunAxis.FacingYaw;
        Back = Start.X - Tuning.DissolveFrontStartGap;
        horizon = Start.X + Tuning.DissolveAhead;
        // Enough street for the start, built while the session assembles; the rest streams.
        while (NextIndex == 0 || Source.Segments[^1].Plan.Start < horizon + Tuning.DissolveBuildLead)
            Add(Source.Feed.Wait(NextIndex));
        Source.Feed.Request(NextIndex);
        wall = new MeshInstance3D
        {
            Name = "Front",
            Mesh = new QuadMesh { Size = new Vector2(Source.Settings.Suburb.MapSize + 20f, WallHeight), Orientation = PlaneMesh.OrientationEnum.X },
            MaterialOverride = wallMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/dissolve_front.gdshader") },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(wall);
        BestScore = LoadBest();
        Window();
        Lab.Session.OnStopping(() => RenderingServer.GlobalShaderParameterSet(WindowParameter, new Vector4(1, 0, -100000, 100000)));
    }

    private int NextIndex => Source.Segments.Count == 0 ? 0 : Source.Segments[^1].Index + 1;

    public void Bind(PrototypeRunner bound)
    {
        runner = bound; // It has tracked the tanks built so far; later segments' tanks are tracked as they are built.
        runner.Decided += command =>
        {
            switch (command)
            {
                case RunCaught:
                    // The front swallows the runner: the ground goes from under them, and they fall into the void.
                    var at = Lab.Player.IsRagdolled ? Lab.Player.PelvisPosition : Lab.Player.GlobalPosition;
                    caughtCut = Mathf.Max(Back, at.X + 1.5f);
                    Lab.Player.InputLocked = true;
                    foreach (var segment in Source.Segments.Where(s => s.Plan.Start < caughtCut))
                        if (segment.Suburb.StaticRoot.GetNodeOrNull<StaticBody3D>("Curbs") is { } curbs) curbs.CollisionLayer = 0;
                    break;
                case RoundOver:
                    SaveBest();
                    break;
            }
        };
    }

    /// <summary>Time the last step of this arena took, for the scripted runs.</summary>
    public double LastStepMilliseconds { get; private set; }

    public override void _PhysicsProcess(double delta)
    {
        var timer = Stopwatch.StartNew();
        Step();
        LastStepMilliseconds = timer.Elapsed.TotalMilliseconds;
    }

    private void Step()
    {
        if (Rules is not { } rules) return;
        var tuning = Tuning;
        Back = caughtCut is { } cut ? Mathf.Max((float)rules.Front, cut) : (float)rules.Front;
        horizon = Mathf.Max(horizon, (float)rules.Best + tuning.DissolveAhead);
        Window();
        Stream(tuning);
        var strip = Mathf.Max(0.5f, tuning.DissolveFloorStrip);
        var floorCut = caughtCut != null ? Back : Mathf.Floor(Back / strip) * strip;
        var sweep = (sweepAge += 1) >= SweepEvery;
        if (sweep) sweepAge = 0;
        for (var i = 0; i < Source.Segments.Count; i++)
        {
            var segment = Source.Segments[i];
            if (segment.Plan.Start > Back + 1f) break;
            SegmentWorldSource.CutFloor(segment, floorCut);
            if (sweep)
            {
                foreach (var tank in segment.Tanks)
                {
                    if (IsInstanceValid(tank) && !dissolved.Contains(tank) && tank.State != PropaneTank.TankState.Exploded && tank.GlobalPosition.X < Back - 0.3f)
                        Dissolve(tank);
                }
                DissolveBodies(segment);
            }
            if (rules.Front > segment.Plan.End + tuning.DissolveRetireLag)
            {
                Retire(segment);
                i--;
            }
        }
        TakeAmmo(tuning);
        if (Source.Retiring > 0)
        {
            var timer = Stopwatch.StartNew();
            Source.AdvanceRetire(RetireBudgetMilliseconds);
            retireMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Time freeing retired segments may take per physics step.</summary>
    public const double RetireBudgetMilliseconds = 1.5;

    public override void _Process(double delta)
    {
        var height = Tuning.DissolveFrontHeight;
        wall.Scale = new Vector3(1, height / WallHeight, 1);
        wall.Position = new Vector3(Back, height / 2f, Lab.Player.GlobalPosition.Z);
        wallMaterial.SetShaderParameter("strength", Tuning.DissolveFrontGlow);
    }

    private const float WallHeight = 14f;

    /// <summary>The window, for every mesh tagged with it: geometry exists only between the back edge and the horizon.</summary>
    private void Window() => RenderingServer.GlobalShaderParameterSet(WindowParameter, new Vector4(1, 0, Back, horizon));

    /// <summary>
    /// Builds the next segment, a few pieces per step inside <see cref="BuildBudgetMilliseconds"/>, once its plan is in
    /// and the horizon comes within <c>DissolveBuildLead</c> of the last built segment's end.
    /// </summary>
    private void Stream(Tuning tuning)
    {
        if (Source.Pending != null)
        {
            var timer = Stopwatch.StartNew();
            var built = Source.Advance(BuildBudgetMilliseconds);
            buildSlices.Add(timer.Elapsed.TotalMilliseconds);
            if (built != null) Register(built);
            return;
        }
        var next = NextIndex;
        if (Source.Segments.Count > 0 && Source.Segments[^1].Plan.End > horizon + tuning.DissolveBuildLead) return;
        if (!Source.Feed.TryGet(next, out var plan)) { Source.Feed.Request(next); return; }
        Source.Begin(plan);
        Source.Feed.Request(next + 1);
    }

    /// <summary>Time a streamed segment may take to build per physics step.</summary>
    public const double BuildBudgetMilliseconds = 2.5;

    private void Add(SegmentPlan plan) => Register(Source.BuildSegment(plan));

    private void Register(RunSegment segment)
    {
        foreach (var tank in segment.Tanks)
        {
            Lab.TrackDrop(tank);
            runner?.Track(tank);
        }
        foreach (var spot in segment.Plan.Plan.AmmoSpots)
        {
            var pickup = new AmmoPickup { Index = pickups.Count, Position = Suburb.ToWorld(spot, 0.05f), Name = $"Ammo_{pickups.Count}" };
            segment.Suburb.DynamicRoot.AddChild(pickup);
            segment.Pickups.Add(pickup);
            pickups.Add(pickup);
        }
    }

    private void Retire(RunSegment segment)
    {
        foreach (var tank in segment.Tanks)
        {
            runner?.ForgetTank(tank);
            dissolved.Remove(tank);
            Lab.ForgetTank(tank);
        }
        foreach (var pickup in segment.Pickups) pickups.Remove(pickup);
        Source.Retire(segment);
    }

    /// <summary>A tank behind the front is gone: out of physics, out of sight, out of any chain.</summary>
    private void Dissolve(PropaneTank tank)
    {
        dissolved.Add(tank);
        DissolvedTanks++;
        tank.CancelChainFuse();
        tank.Visible = false;
        tank.ProcessMode = ProcessModeEnum.Disabled;
        Lab.ForgetTank(tank);
    }

    /// <summary>
    /// Loose bodies behind the front (props, fence panels, debris) are gone too: out of sight and out of physics, so
    /// nothing tumbles through the void and nothing can be knocked back into the street.
    /// </summary>
    private void DissolveBodies(RunSegment segment)
    {
        var root = segment.Suburb.DynamicRoot;
        for (var i = 0; i < root.GetChildCount(); i++)
        {
            if (root.GetChild(i) is not RigidBody3D { ProcessMode: not ProcessModeEnum.Disabled } body || body is PropaneTank) continue;
            if (body.GlobalPosition.X >= Back - 0.5f) continue;
            body.Visible = false;
            body.ProcessMode = ProcessModeEnum.Disabled;
        }
    }

    private void TakeAmmo(Tuning tuning)
    {
        var player = Lab.Player;
        foreach (var pickup in pickups)
        {
            pickup.Visible = pickup.GlobalPosition.X > Back && pickup.GlobalPosition.X < horizon;
            if (!pickup.Visible || !pickup.Available || player.Ammo == null || player.IsRagdolled || runner is not { IsPlaying: true }) continue;
            var offset = player.GlobalPosition - pickup.GlobalPosition;
            if (new Vector2(offset.X, offset.Z).Length() > AmmoPickup.TakeRadius || Mathf.Abs(offset.Y) > 1.6f ||
                player.Ammo.AddReserve(tuning.PickupAmount) == 0)
            {
                continue;
            }

            pickup.Take(); // Once: the street moves on, and the can dissolves with it.
        }
    }

    private string BestKey => Daily ? $"daily-{RunSeed}" : "any";

    private int LoadBest()
    {
        try
        {
            using var file = new ConfigFile();
            return FileAccess.FileExists(BestFile) && file.Load(BestFile) == Error.Ok ? (int)file.GetValue("best", BestKey, 0) : 0;
        }
        catch (Exception) { return 0; }
    }

    private void SaveBest()
    {
        if (Rules is not { } rules || rules.Score <= BestScore) return;
        BestScore = rules.Score;
        NewBest = true;
        try
        {
            using var file = new ConfigFile();
            if (FileAccess.FileExists(BestFile)) file.Load(BestFile);
            file.SetValue("best", BestKey, BestScore);
            file.Save(BestFile);
        }
        catch (Exception) { }
    }

    /// <summary>Counts for the scripted runs: built segments, their nodes and bodies.</summary>
    public (int Segments, int Nodes, int Bodies) Census()
    {
        var nodes = 0;
        var bodies = 0;
        foreach (var segment in Source.Segments)
        {
            foreach (var node in segment.Suburb.FindChildren("*", "", owned: false))
            {
                nodes++;
                if (node is PhysicsBody3D) bodies++;
            }
        }
        return (Source.Segments.Count, nodes, bodies);
    }
}

/// <summary>Run-space conventions the source and arena share: the street runs along world +X.</summary>
public static class RunAxis
{
    /// <summary>A player's yaw facing down the run (+X).</summary>
    public static float FacingYaw => World.Plan.SuburbGenerator.YawFacing(new Vector2(1, 0)) + Mathf.Pi;
}
