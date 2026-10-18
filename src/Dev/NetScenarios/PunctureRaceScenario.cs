using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Tank;

namespace Propane.Dev.NetScenarios;

internal sealed class PunctureRaceScenario(INetScenarioHost host) : NetScenario(host)
{
    private int raceTick;
    private int raceSamples;
    private float raceWorst;
    private VentParameters? raceVent;
    private Camera3D? raceCamera;

    protected override void Preparing(Match match)
    {
        raceTick = raceSamples = 0;
        raceVent = null; raceCamera = null; raceWorst = 0;
        var captured = Epoch;
        void Resolved(AuthorityMessage result)
        {
            if (!IsCurrent(captured)) return;
            var canonical = result.Bodies.FirstOrDefault(b => b.Body.Id == BodyIds.MapTankId(0));
            if (canonical?.State != BodyState.State.Venting) return;
            if (raceVent != null) ScenarioAssert(raceVent == canonical.Vent, "race: canonical vent changed during reconciliation");
            raceVent = canonical.Vent;
        }
        match.AuthorityResolved += Resolved;
        DetachOnRetirement(() => match.AuthorityResolved -= Resolved);
    }

    protected override void Run(Match match, float dt) => PunctureRace(match);

    /// <summary>
    /// Injects two opposed bullet contacts on the same simulation tick, while both copies are intact. This isolates
    /// the authority race from rifle spread/aim. A third client, when present, observes without shooting.
    /// </summary>
    private void PunctureRace(Match match)
    {
        if (!match.IsPlaying) { return; }
        raceTick++;
        var id = BodyIds.MapTankId(0);
        var tank = match.LiveTanks.SingleOrDefault(t => t.NetId == id);
        ScenarioAssert(tank != null, "race: test tank disappeared");
        if (tank == null) { return; }
        if (raceTick == 1)
        {
            match.Player.InputOverride = new ScriptedInput();
            var road = match.Suburb.Plan.Roads.OrderByDescending(r => r.Length).First();
            var spot = World.Suburb.ToWorld((road.A + road.B) / 2, 8);
            BodySync.Snap(tank, new Transform3D(Basis.Identity, spot), Vector3.Zero, Vector3.Zero);
            match.Player.Teleport(spot + new Vector3(20, -7.9f, 0), 0);
            raceCamera = new Camera3D { Current = true, Fov = 55 };
            match.AddChild(raceCamera);
        }
        if (raceTick == 180 && (int)DevArgs.GetFloat("color", role == "host" ? 0 : 1) < 2)
        {
            ScenarioAssert(tank.State == PropaneTank.TankState.Intact && match.Bodies.Get(id)!.Epoch == 1, "race: contender received a puncture before its simultaneous contact");
            var normal = role == "host" ? Vector3.Right : Vector3.Left;
            var hole = PropaneTank.LocalCenter + normal * 0.15f;
            tank.TakeShot(tank.ToGlobal(hole), tank.GlobalBasis * normal, -(tank.GlobalBasis * normal), match.Player.ActorId);
            Log($"race contact tick {raceTick} hole {Vector(tank.HoleLocal)} swirl {tank.SwirlSign}");
        }
        if (raceCamera != null)
        {
            raceCamera.GlobalPosition = tank.GlobalPosition + new Vector3(2, 1.4f, 3);
            raceCamera.LookAt(tank.GlobalTransform * PropaneTank.LocalCenter, Vector3.Up);
        }
        if (raceTick is 210 or 300 or 600 or 1200) { director.Screenshot($"{net.PlayerName}_race_{raceTick}"); }
        // At 120 physics ticks/s, allow lagged reliable delivery before checking convergence.
        if (raceTick < 720) { return; }
        ScenarioAssert(raceVent != null && tank.State == PropaneTank.TankState.Venting, "race: no canonical puncture after the race");
        if (raceVent == null) { return; }
        ScenarioAssert(tank.HoleLocal == raceVent.Hole && tank.HoleNormalLocal == raceVent.Normal && tank.SwirlSign == raceVent.Swirl,
            "race: contender retained different canonical vent parameters");
        var lean = raceVent.Normal.Rotated(Vector3.Up, Mathf.DegToRad(match.World.Tuning.VentSwirlDegrees) * raceVent.Swirl).Normalized();
        ScenarioAssert(tank.JetDirectionLocal.IsEqualApprox(lean), "race: jet lean differs from canonical vent");
        var entry = match.Bodies.Get(id)!;
        if (entry.Owner != net.MyId && entry.HasTarget)
        {
            var lead = (float)Math.Clamp(MonotonicClock.Now - entry.TargetReceived, 0, 0.3);
            var drift = tank.GlobalPosition.DistanceTo(entry.Target.Origin + entry.TargetLinear * lead);
            raceWorst = Mathf.Max(raceWorst, drift);
            ScenarioAssert(drift <= match.World.Tuning.SnapDistance + 0.5f, $"race: trajectory escaped owner corrections ({drift:0.000} m)");
        }
        if (raceTick % 6 == 0)
        {
            raceSamples++;
            Log($"race path time {MonotonicClock.Now + net.ServerClockOffset:0.000} pos {Vector(tank.GlobalPosition)}");
        }
        if (raceTick == 720)
        {
            Log($"race params owner {entry.Owner} epoch {entry.Epoch} hole {Vector(tank.HoleLocal)} normal {Vector(tank.HoleNormalLocal)} lean {Vector(tank.JetDirectionLocal)} swirl {tank.SwirlSign}");
        }
        if (raceTick == 1320)
        {
            ScenarioAssert(raceSamples >= 100 && !entry.ClaimPending, "race: insufficient trajectory samples or unresolved claim");
            scenarioPassed = !scenarioFailed;
            Log($"puncture-race: assertions passed, {raceSamples} path samples, max correction error {raceWorst:0.000} m");
        }
    }

}
