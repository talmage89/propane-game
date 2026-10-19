using System;
using System.Linq;
using Godot;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev.NetScenarios;

/// <summary>
/// Controlled native acceleration without contact/damping, with optional spin about the offset COM. Fixture-only overrides.
/// Borrows one match-owned tank; retirement clears the reference. Elapsed time uses physics delta while playing.
/// The ordinary NetTest ending and tank checker remain authoritative, including an expected diagnostic failure.
/// </summary>
internal sealed class BallisticScenario(INetScenarioHost host) : NetScenario(host)
{
    private PropaneTank? tank;
    private int tick;
    private float elapsed;
    private bool gravity;
    private float initialVerticalSpeed;
    private float initialSpin;

    protected override void Preparing(Match match)
    {
        tank = null;
        tick = 0;
        elapsed = 0;
        initialVerticalSpeed = 0;
        initialSpin = 0;
        DetachOnRetirement(() => tank = null);
    }

    protected override void Run(Match match, float dt)
    {
        if (!match.IsPlaying || scenarioFailed) { return; }
        tick++;
        elapsed += dt;
        if (tick == 1)
        {
            var option = DevArgs.Get("ballistic-gravity") ?? "1";
            ScenarioAssert(option is "0" or "1", "ballistic: --ballistic-gravity must be 0 or 1");
            gravity = option == "1";
            initialVerticalSpeed = DevArgs.GetFloat("ballistic-vertical-speed", 0);
            initialSpin = DevArgs.GetFloat("ballistic-spin", 0);
            ScenarioAssert(float.IsFinite(initialVerticalSpeed), "ballistic: vertical speed must be finite");
            ScenarioAssert(float.IsFinite(initialSpin) && initialSpin >= 0 && initialSpin <= match.World.Tuning.VentMaxSpin,
                "ballistic: spin must be finite and within the owner spin limit");
            ScenarioAssert(match.World.Tuning.VentThrust == 0, "ballistic: requires --tune.VentThrust=0 to isolate acceleration");
            if (scenarioFailed) { return; }
            tank = match.LiveTanks.OrderBy(t => t.NetId).First();
            match.Player.InputOverride = new ScriptedInput();
            tank.GravityScale = gravity ? 1 : 0;
            tank.LinearDampMode = tank.AngularDampMode = RigidBody3D.DampMode.Replace;
            tank.LinearDamp = tank.AngularDamp = 0;
            tank.CollisionLayer = tank.CollisionMask = 0;
            tank.CanSleep = false;
            tank.MaxContactsReported = 8;
            tank.ContactMonitor = true;
            // Enough height for the entire 20-second match and ordinary results interval, without a floor encounter.
            BodySync.Snap(tank, new Transform3D(Basis.Identity, Vector3.Up * 5000),
                new Vector3(4, initialVerticalSpeed, 0), Vector3.Right * initialSpin);
            Log(FormattableString.Invariant($"ballistic: gravity={gravity} initial-vy={initialVerticalSpeed} initial-spin={initialSpin}, no collisions or damping; ordinary ending"));
        }
        if (tick == 60 && role == "host")
        {
            var body = tank!;
            var hole = PropaneTank.LocalCenter + Vector3.Right * .15f;
            // A zero impulse direction isolates acceleration without changing the real puncture/authority path.
            body.TakeShot(body.ToGlobal(hole), body.GlobalBasis.X, Vector3.Zero, match.Player.ActorId);
        }
        if (!scenarioPassed && elapsed >= 6)
        {
            ScenarioAssert(tank != null && GodotObject.IsInstanceValid(tank), "ballistic: fixture tank disappeared");
            if (tank == null || !GodotObject.IsInstanceValid(tank)) { return; }
            ScenarioAssert(tank.State == PropaneTank.TankState.Venting && match.Bodies.Get(tank.NetId) is { Epoch: 2, ClaimPending: false },
                "ballistic: canonical vent or stable authority missing");
            ScenarioAssert(tank.GetContactCount() == 0 && tank.CollisionMask == 0 && tank.CollisionLayer == 0,
                "ballistic: contact contaminated free flight");
            ScenarioAssert(tank.LinearDampMode == RigidBody3D.DampMode.Replace &&
                tank.AngularDampMode == RigidBody3D.DampMode.Replace && tank.LinearDamp == 0 && tank.AngularDamp == 0,
                "ballistic: damping contaminated acceleration isolation");
            // Observer steering legitimately changes velocity, including in a constant-velocity control.
            // Check physical acceleration on the owner; check the selected native mode on every copy.
            var isOwner = match.Bodies.Get(tank.NetId)?.Owner == match.Net.MyId;
            ScenarioAssert(initialSpin == 0 ? tank.AngularVelocity.Length() < .01f :
                !isOwner || Mathf.Abs(tank.AngularVelocity.Length() - initialSpin) < .01f,
                "ballistic: native spin selection did not take effect");
            ScenarioAssert(tank.GravityScale == (gravity ? 1 : 0) && (!isOwner || (gravity ?
                tank.LinearVelocity.Y < initialVerticalSpeed - 5 : Mathf.Abs(tank.LinearVelocity.Y - initialVerticalSpeed) < .1f)),
                "ballistic: native gravity selection did not take effect");
            scenarioPassed = !scenarioFailed;
            Log(FormattableString.Invariant($"ballistic: native fixture assertions passed={scenarioPassed} time={TankCheckTime():0.000000} id={tank.NetId:x} vy={tank.LinearVelocity.Y:0.000} spin={tank.AngularVelocity.Length():0.000}"));
        }
    }
}
