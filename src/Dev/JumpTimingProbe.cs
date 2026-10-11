using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Compare first visible air/landing weights to accepted jumps and actual post-move floor contact.
/// Works with the baseline graph too: its stationary-only landing exposes a missing moving landing.</summary>
public partial class JumpTimingProbe : SkeletonModifier3D
{
    public PlayerCharacter Player { get; init; } = null!;
    public bool RequireJumps { get; init; }
    private sealed class Event
    {
        public string Kind = "";
        public ulong PhysicsTick;
        public ulong? PoseTick;
    }
    private readonly List<Event> events = new();
    private Event? takeoff;
    private Event? landing;
    private AnimationTree tree = null!;
    private bool landingBlend;
    private bool inAir;
    private bool supported = true;

    public static JumpTimingProbe Attach(PlayerCharacter player, bool requireJumps = false)
    {
        var skeleton = player.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single(s => s.FindBone("foot_l") >= 0);
        var probe = new JumpTimingProbe { Player = player, RequireJumps = requireJumps };
        skeleton.AddChild(probe);
        return probe;
    }

    public override void _Ready()
    {
        ProcessPhysicsPriority = 10;
        tree = Player.FindChildren("*", "AnimationTree", owned: false).OfType<AnimationTree>().Single();
        landingBlend = tree.GetPropertyList().Any(p => p["name"].AsString() == "parameters/land/blend_amount");
    }

    public override void _PhysicsProcess(double delta)
    {
        var state = Player.ResolvedState;
        var tick = Engine.GetPhysicsFrames();
        if (supported && !state.Grounded && (state.Support == PlayerSupport.Jumping || (Player.IsRemote && Player.Velocity.Y > 1)))
        {
            takeoff = new Event { Kind = "takeoff", PhysicsTick = tick };
            events.Add(takeoff);
            inAir = true;
            landing = null;
        }
        var floor = Player.IsRemote ? state.Grounded : Player.IsOnFloor();
        if (inAir && floor)
        {
            landing = new Event { Kind = "landing", PhysicsTick = tick };
            events.Add(landing);
            inAir = false;
        }
        supported = state.Grounded;
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (delta <= 0) return;
        var tick = Engine.GetPhysicsFrames();
        if (takeoff?.PoseTick == null && tree.Get("parameters/air/blend_amount").AsSingle() > 0.001f)
        {
            if (takeoff != null) takeoff.PoseTick = tick;
        }
        var visibleLand = landingBlend ? tree.Get("parameters/land/blend_amount").AsSingle() > 0.001f
            : tree.Get("parameters/land/active").AsBool();
        if (landing?.PoseTick == null && visibleLand)
        {
            if (landing != null) landing.PoseTick = tick;
        }
    }

    public void Finish()
    {
        var failed = RequireJumps && events.Count < 4;
        var csv = new StringBuilder("kind,physics_tick,pose_tick,latency_ticks\n");
        foreach (var e in events)
        {
            var latency = e.PoseTick.HasValue ? (long)(e.PoseTick.Value - e.PhysicsTick) : -1;
            GD.Print($"[feet timing] {e.Kind} physics={e.PhysicsTick} pose={e.PoseTick?.ToString() ?? "missing"} latency={latency} ticks");
            csv.AppendLine($"{e.Kind},{e.PhysicsTick},{e.PoseTick?.ToString() ?? ""},{latency}");
            failed |= latency < 0 || latency > 2;
        }
        var directory = DevArgs.Get("capture-dir");
        if (directory != null)
        {
            DirAccess.MakeDirRecursiveAbsolute(directory);
            using var file = FileAccess.Open($"{directory}/jump-timing.csv", FileAccess.ModeFlags.Write);
            file.StoreString(csv.ToString());
        }
        Active = false;
        SetPhysicsProcess(false);
        if (DevArgs.Get("assert-feet") != null && failed)
        {
            GD.PushError("[feet timing] FAIL missing jump/landing or more than one 60 Hz frame of latency");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }
}
