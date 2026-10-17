using Godot;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.NetScenarios;

internal sealed class PickupScenario(INetScenarioHost host) : NetScenario(host)
{
    private float duelTime = -1;
    private ScriptedInput duelInput = new();
    private float pickupTakenAt = -1;
    private bool pickupMovedAway;

    private bool Once(float at, float dt) => duelTime >= at && duelTime - dt < at;

    protected override void Assembled(Match match)
    {
        duelTime = -1; duelInput = new ScriptedInput();
        pickupTakenAt = -1; pickupMovedAway = false;
    }

    protected override void Run(Match match, float dt) => Pickup(match, dt);

    /// <summary>Walks into the nearest ammo pickup and films it vanishing and building back up.</summary>
    private void Pickup(Match match, float dt)
    {
        var player = match.Player;
        if (!match.IsPlaying || match.Pickups.Count == 0)
        {
            return;
        }
        if (duelTime < 0)
        {
            duelTime = 0;
            player.InputOverride = duelInput;
        }
        duelTime += dt;
        duelInput.Advance(dt);
        var pickup = match.Pickups[0];
        if (Once(0.2f, dt))
        {
            // Spend some rounds first so the pickup has room to add them.
            player.Ammo!.Reserve = 10;
            var at = pickup.GlobalPosition;
            var start = at + new Vector3(3.5f, 0.05f, 0);
            player.Teleport(start, Mathf.Atan2(-(at - start).X, -(at - start).Z));
            AimAt(player, at + Vector3.Up * 0.3f);
        }
        if (Once(0.6f, dt))
        {
            AimAt(player, pickup.GlobalPosition + Vector3.Up * 0.3f);
            director.Screenshot("pickup_0_before");
            duelInput.Hold(duelInput.Now, duelInput.Now + 5f, new PlayerIntent { Move = new Vector2(0, 1f) });
        }
        if (duelTime >= 0.6f && !pickupMovedAway)
        {
            AimAt(player, pickup.GlobalPosition + Vector3.Up * 0.3f);
            if (pickup.ClaimPending || !pickup.Available)
            {
                // Stop only after actually reaching the can, and move out before it can respawn. The old fixed
                // walking interval could end short of the take radius, yet still capture a "taken" frame.
                pickupMovedAway = true;
                duelInput = new ScriptedInput();
                player.InputOverride = duelInput;
                player.Teleport(pickup.GlobalPosition + new Vector3(3.5f, 0.05f, 0), player.FacingYaw);
                AimAt(player, pickup.GlobalPosition + Vector3.Up * 0.3f);
            }
        }
        if (!pickup.Available && pickupTakenAt < 0)
        {
            pickupTakenAt = duelTime;
            ScenarioAssert(player.Ammo!.Reserve == 10 + match.World.Tuning.PickupAmount,
                "pickup: can did not add its rounds exactly once");
        }
        if (Once(5f, dt)) { ScenarioAssert(pickupTakenAt >= 0, "pickup: player never took the can"); }
        if (pickupTakenAt < 0) { return; }
        foreach (var offset in new[] { 0.15f, 1f })
        {
            if (Once(pickupTakenAt + offset, dt))
            {
                director.Screenshot($"pickup_1_taken_{offset:0.00}");
                Log($"pickup available {pickup.Available}, ammo {player.Ammo!.Magazine}/{player.Ammo.Reserve}");
                ScenarioAssert(!pickup.Available && player.Ammo.Reserve == 10 + match.World.Tuning.PickupAmount,
                    "pickup: can returned early or changed ammo while absent");
            }
        }
        var respawn = match.World.Tuning.PickupRespawnTime;
        var reveal = match.World.Tuning.PickupRevealTime;
        foreach (var f in new[] { 0.15f, 0.4f, 0.65f, 0.9f, 1.2f })
        {
            var t = pickupTakenAt + respawn + reveal * f;
            if (Once(t, dt))
            {
                director.Screenshot($"pickup_2_reveal_{f:0.00}");
                if (f == 1.2f)
                {
                    ScenarioAssert(pickup.Available, "pickup: can did not respawn");
                    scenarioPassed = !scenarioFailed;
                    if (scenarioPassed) { Log("pickup: assertions passed"); }
                }
            }
        }
    }

}
