using System;
using System.Linq;
using Godot;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev;

public partial class NetTest
{
    private int ventContactTick;

    // Eight independently fluttering vents on clear ground isolate contact tracking from blasts,
    // local shot predictions and missed grants. Only the confirmed owner initiates each vent.
    private void VentContactScenario(Match current)
    {
        if (!current.IsPlaying) { return; }
        ventContactTick++;
        var tanks = current.LiveTanks.OrderBy(t => t.NetId).Take(8).ToArray();
        ScenarioAssert(tanks.Length == 8, "vent-contact: a fixture tank disappeared");
        if (ventContactTick == 1)
        {
            current.Player.InputOverride = new ScriptedInput();
            var edge = current.Suburb.Plan.Bounds.End.X + 40;
            current.Player.Teleport(new Vector3(edge, 0, -20), 0);
            for (var i = 0; i < tanks.Length; i++)
            {
                BodySync.Snap(tanks[i], new Transform3D(Basis.Identity, new Vector3(edge, 0, i * 12)), Vector3.Zero, Vector3.Zero);
                tanks[i].MaxContactsReported = 8;
                tanks[i].ContactMonitor = true;
            }
        }
        if (ventContactTick == 240 && role == "host")
        {
            for (var i = 0; i < tanks.Length; i++)
            {
                var tank = tanks[i];
                var hole = PropaneTank.LocalCenter + Vector3.Right * .15f;
                tank.TakeShot(tank.ToGlobal(hole), tank.GlobalBasis.X, -tank.GlobalBasis.X);
                var axis = DevArgs.Get("vent-contact-axis") == "mixed" && i % 2 == 1 ? Vector3.Right : Vector3.Up;
                tank.AngularVelocity = axis * current.World.Tuning.VentMaxSpin;
            }
        }
        if (ventContactTick == 1200)
        {
            ScenarioAssert(tanks.All(t => t.State == PropaneTank.TankState.Venting &&
                current.Bodies.Get(t.NetId) is { ClaimPending: false, Epoch: 2 }),
                "vent-contact: missing canonical vents or unresolved ownership");
            scenarioPassed = !scenarioFailed;
            Log("vent-contact: eight canonical floor vents; assertions passed");
        }
        if (ventContactTick >= 720 && ventContactTick % 30 == 0)
        {
            foreach (var tank in tanks)
            {
                var contacts = string.Join(",", tank.GetCollidingBodies().Select(body => body.Name));
                Log(FormattableString.Invariant($"vent-contact time {TankCheckTime():0.000000} id {tank.NetId:x} contacts={contacts} speed={tank.LinearVelocity.Length():0.000} spin={tank.AngularVelocity.Length():0.000}"));
            }
        }
    }
}
