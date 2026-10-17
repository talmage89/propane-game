using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Modes;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.NetScenarios;

internal sealed class KingLateScenario(INetScenarioHost host) : NetScenario(host)
{
    private int kingStage = -1;
    protected override void Preparing(Match match) => kingStage = -1;
    protected override void Run(Match match, float dt) => KingLate(match);

    /// <summary>A quiet holder and outside observers let one builder catch up after a move within the inbox budget.</summary>
    private void KingLate(Match current)
    {
        if (!current.IsPlaying || current.ModeView is not KingState state) return;
        if (kingStage < 0) current.Player.InputOverride = new ScriptedInput();
        if (kingStage != state.Zone)
        {
            kingStage = state.Zone;
            var spot = new Vector3((float)state.Spot.X, (float)state.Spot.Y, (float)state.Spot.Z);
            current.Player.Teleport(spot + Vector3.Left * (float)state.Radius * (role == "host" ? .7f : 1.8f) + Vector3.Up * .05f, 0);
        }
        if (!scenarioPassed && state.Elapsed >= 80)
        {
            ScenarioAssert(state.Zone >= 1 && state.Holder != 0 && state.Minted.Count > 0,
                "King late builder did not receive the server's moved zone, holder and mints");
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed) Log("King late builder: current zone, holder and minted bank passed");
        }
    }

}
