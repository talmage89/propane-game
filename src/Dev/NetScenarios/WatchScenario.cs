using System.Linq;
using Godot;
using Propane.Net;

namespace Propane.Dev.NetScenarios;

internal sealed class WatchScenario(INetScenarioHost host) : NetScenario(host)
{
    private float duelTime = -1;
    private ScriptedInput duelInput = new();

    private bool Once(float at, float dt) => duelTime >= at && duelTime - dt < at;

    protected override void Assembled(Match match)
    {
        duelTime = -1; duelInput = new ScriptedInput();
    }

    protected override void Run(Match match, float dt) => Watch(match, dt);

    /// <summary>The host follows another (playing) bot at a few meters and films it: running, aiming, shooting, thrown.</summary>
    private void Watch(Match match, float dt)
    {
        var player = match.Player;
        var other = match.RemoteBodies.FirstOrDefault();
        if (!match.IsPlaying || other == null)
        {
            return;
        }
        if (duelTime < 0)
        {
            duelTime = 0;
            player.InputOverride = duelInput;
        }
        duelTime += dt;
        var target = other.IsRagdolled ? other.PelvisPosition : other.GlobalPosition + Vector3.Up;
        if (player.GlobalPosition.DistanceTo(other.GlobalPosition) > 9f)
        {
            // Keep up: drop in a few meters behind and to the side of them.
            var back = other.GlobalPosition + new Vector3(Mathf.Sin(other.FacingYaw), 0, Mathf.Cos(other.FacingYaw)) * 5f + new Vector3(1.5f, 0.3f, 0);
            player.Teleport(back, player.FacingYaw);
        }
        AimAt(player, target);
        if (Mathf.PosMod(duelTime, 1.5f) < dt)
        {
            director.Screenshot($"watch_{duelTime:000.0}");
        }
    }

    public override bool Tick(Match match, float dt) => role == "host" && base.Tick(match, dt);
}
