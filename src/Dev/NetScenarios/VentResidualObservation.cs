using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Tank;

namespace Propane.Dev.NetScenarios;

/// <summary>
/// Opt-in observer called only by tools/dev/vent_residual.py's isolated source build. One writer per
/// process/match, closed at completion/replacement/exit. Network times use the match's monotonic clock;
/// physics frame numbers describe callback order, never cross-process time. No ordinary runtime hooks.
/// </summary>
internal static class VentResidualObservation
{
    private static Match? match;
    private static StreamWriter? writer;
    private static bool configurationWritten;
    private static bool preserveContacts;

    internal static void Attach(Match current)
    {
        Close();
        if (DevArgs.Get("vent-residual-dir") is not { } directory) { return; }
        Directory.CreateDirectory(directory);
        match = current;
        preserveContacts = DevArgs.Get("vent-residual-preserve-contacts") != null;
        writer = new StreamWriter(Path.Combine(directory, $"peer-{current.Net.MyId}-match-{current.Setup.Id}.jsonl"), false, new System.Text.UTF8Encoding(false), 65536);
        Write(new { kind = "start", time = NetScenarioTools.TankCheckTime(), network = current.Clock.Seconds, peer = current.Net.MyId, name = current.Net.PlayerName, match = current.Setup.Id, schema = 4, preserveContacts });
        GD.Print($"[vent-residual] enabled peer {current.Net.MyId} match {current.Setup.Id}");
    }

    internal static void Close()
    {
        writer?.Dispose();
        writer = null;
        match = null;
        configurationWritten = false;
        preserveContacts = false;
    }

    private static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private static float[] Q(Quaternion q) => new[] { q.X, q.Y, q.Z, q.W };
    private static object Pose(Transform3D pose) => new { p = V(pose.Origin), q = Q(pose.Basis.Orthonormalized().GetRotationQuaternion()) };
    private static object State(BodyState state) => new { id = state.Id.ToString("x"), state.Epoch, state.Sequence, pose = Pose(state.Transform), v = V(state.Linear), w = V(state.Angular) };

    private static void Write(object record)
    {
        writer?.WriteLine(JsonSerializer.Serialize(record));
    }

    internal static void Receipt(BodySync sync, int sender, BodyState state, bool rest, double now, double transit)
    {
        if (writer == null || match == null) { return; }
        var entry = sync.Get(state.Id);
        var disposition = entry == null ? "unknown" : state.Epoch < entry.Epoch ? "old-epoch" :
            state.Epoch > entry.Epoch ? "future-epoch" : entry.Owner != sender ? "nonowner" : sender == sync.Me ? "self" :
            state.Sequence <= entry.LastSequence ? "old-sequence" : "accepted";
        Write(new { kind = "receipt", time = NetScenarioTools.TankCheckTime(), network = now, match = match.Setup.Id, peer = sync.Me, sender, rest, transit, disposition, sample = State(state), owner = entry?.Owner, epoch = entry?.Epoch, last = entry?.LastSequence });
    }

    internal static void Authority(BodySync sync, BodyAuthority authority)
    {
        if (writer == null || match == null) { return; }
        Write(new { kind = "authority", time = NetScenarioTools.TankCheckTime(), network = match.Clock.Seconds, match = match.Setup.Id, peer = sync.Me, authority.Owner, authority.Rest, state = authority.State.ToString(), sample = State(authority.Body) });
    }

    internal static void Record(BodySync.Entry entry, string reason, object? detail = null)
    {
        if (writer == null || match == null || !entry.IsTank || !GodotObject.IsInstanceValid(entry.Body)) { return; }
        var body = entry.Body;
        if (reason == "register" && !preserveContacts) { body.MaxContactsReported = 8; }
        if ((reason.StartsWith("decision-", StringComparison.Ordinal) || reason == "after-correct") &&
            body is not PropaneTank { State: PropaneTank.TankState.Venting }) { return; }
        var direct = body.IsInsideTree() ? PhysicsServer3D.BodyGetDirectState(body.GetRid()) : null;
        var contacts = direct == null ? Array.Empty<object>() : Enumerable.Range(0, direct.GetContactCount()).Select(i =>
            (object)new { collider = (direct.GetContactColliderObject(i) as Node)?.Name.ToString(), normal = V(direct.GetContactLocalNormal(i)), impulse = V(direct.GetContactImpulse(i)) }).ToArray();
        Write(new
        {
            kind = reason,
            time = NetScenarioTools.TankCheckTime(),
            network = match.Clock.Seconds,
            frame = Engine.GetPhysicsFrames(),
            peer = match.Net.MyId,
            match = match.Setup.Id,
            id = entry.Id.ToString("x"),
            owner = entry.Owner,
            epoch = entry.Epoch,
            sequence = entry.Sequence,
            last = entry.LastSequence,
            pending = entry.ClaimPending,
            observePending = entry.ObserveWhilePending,
            state = (body as PropaneTank)?.State.ToString(),
            sleeping = body.Sleeping,
            gathering = (body as PropaneTank)?.Gathering,
            correcting = (body as PropaneTank)?.CorrectingMotion,
            pose = Pose(body.GlobalTransform),
            center = V(direct?.CenterOfMassLocal ?? body.CenterOfMass),
            v = V(body.LinearVelocity),
            w = V(body.AngularVelocity),
            contacts,
            contactLimit = body.MaxContactsReported,
            hasTarget = entry.HasTarget,
            rest = entry.TargetRest,
            sampled = entry.TargetReceived,
            updated = entry.TargetUpdated,
            target = entry.HasTarget ? Pose(entry.Target) : null,
            tv = V(entry.TargetLinear),
            tw = V(entry.TargetAngular),
            detail
        });
    }

    internal static void Decision(BodySync.Entry entry, string reason, Vector3 targetCenter, Quaternion targetRotation, Vector3 error, float angle, float lead)
    {
        if (writer == null || entry.Body is not PropaneTank { State: PropaneTank.TankState.Venting }) { return; }
        Record(entry, "decision-" + reason, new { goalCenter = V(targetCenter), goalRotation = Q(targetRotation), error = V(error), angle, lead });
    }

    internal static void Measurement(Match current, double time, string kind)
    {
        if (writer == null) { return; }
        if (!configurationWritten)
        {
            var tuning = current.World.Tuning;
            foreach (var (name, flag) in new[] { ("BodyCheckpointInterval", "expect-checkpoint"), ("CorrectionAccelerationMaxSpin", "expect-acceleration-spin"), ("CorrectionVentBlendTime", "expect-vent-blend") })
            {
                if (DevArgs.Get(flag) == null) { continue; }
                var expected = DevArgs.GetFloat(flag, float.NaN);
                var actual = typeof(Tuning).GetProperty(name)?.GetValue(tuning) is float value ? value : float.NaN;
                if (!float.IsFinite(expected) || !Mathf.IsEqualApprox(actual, expected))
                {
                    GD.PushError($"effective {name}={actual} differs from requested {expected}");
                    ShutdownGuard.Quit(current.GetTree(), 1);
                    return;
                }
            }
            Write(new
            {
                kind = "configuration",
                time = NetScenarioTools.TankCheckTime(),
                peer = current.Net.MyId,
                match = current.Setup.Id,
                tuning.CorrectionBlendTime,
                tuning.SnapDistance,
                tuning.TankSendRate,
                tuning.VentThrust,
                tuning.VentMaxSpin,
                checkpointInterval = typeof(Tuning).GetProperty("BodyCheckpointInterval")?.GetValue(tuning),
                maxAcceleration = typeof(Tuning).GetProperty("CorrectionMaxAcceleration")?.GetValue(tuning),
                accelerationMaxSpin = typeof(Tuning).GetProperty("CorrectionAccelerationMaxSpin")?.GetValue(tuning),
                ventBlend = typeof(Tuning).GetProperty("CorrectionVentBlendTime")?.GetValue(tuning),
                hull = (GameAssets.TankShape as ConvexPolygonShape3D)?.Points.Select(V).ToArray()
            });
            configurationWritten = true;
        }
        var detail = new { checkerTime = time, worldAlive = current.World.IsAlive, worldPaused = current.World.Paused, simulation = current.World.Clock.Now };
        foreach (var entry in current.Bodies.Entries) { Record(entry, "measurement-" + kind, detail); }
        writer.Flush();
    }
}
