using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

using ProcessModeEnum = Godot.Node.ProcessModeEnum;

namespace Propane.Dev.NetScenarios;

internal sealed class BodyStreamScenario(INetScenarioHost host) : NetScenario(host)
{

    protected override void Run(Match match, float dt) => BodyStream(match);

    // Preserve the original connection lifetime; the documented lane fixture runs one match.
    private PropaneTank? streamTank;
    private Vector3 streamOrigin;
    private float streamStarted;

    // Native replication fixture: straight flight isolates packet delivery from collisions/vent chaos.
    // Only the host drives the owner; observers must learn motion through the actual transport.
    private void BodyStream(Match current)
    {
        var clock = MatchClock;
        if (streamTank == null)
        {
            streamTank = current.LiveTanks.OrderBy(t => t.NetId).First();
            streamOrigin = streamTank.GlobalPosition;
            streamStarted = clock;
            streamTank.GravityScale = 0;
            streamTank.LinearDamp = streamTank.AngularDamp = 0;
            streamTank.CollisionMask = 0;
        }
        if (role == "host")
        {
            var flight = Mathf.Min(clock - streamStarted, 15);
            streamTank.GlobalPosition = streamOrigin + Vector3.Right * (flight * 8);
            streamTank.LinearVelocity = flight < 15 ? Vector3.Right * 8 : Vector3.Zero;
            streamTank.AngularVelocity = Vector3.Zero;
            streamTank.Sleeping = flight >= 15;
        }
        if (!scenarioPassed && clock - streamStarted >= 10)
        {
            ScenarioAssert(streamTank.GlobalPosition.DistanceTo(streamOrigin) > 40,
                "body-stream: observer did not receive owner movement");
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed) { Log("body-stream: authoritative movement received"); }
        }
    }

}
