using System.Linq;
using Godot;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// Dissolve Run's goal policy, kept simple for the prototype and its scripted runs: keep heading down the street
/// (world +X), never stopping while the front is close; on the way, shoot tanks ahead that stand outside a blast's
/// throw, finishing the ones it punctured, and take ammo cans it passes when it runs low. It reads the front from the
/// rules, as a pure function of the clock.
/// </summary>
public sealed class DissolveRunBot : IBotGoalPolicy
{
    /// <summary>A gap under this many meters means run, whatever else is ahead.</summary>
    private const float Flee = 9f;
    private const float Reach = 28f;

    private readonly BotMotor motor = new();
    private PropaneTank? target;
    private float retarget;

    public string Status { get; private set; } = "";
    public float Skill { get => motor.Skill; set => motor.Skill = value; }

    public DissolveRunBot(ulong? seed = null) => _ = seed;

    public void Dispose() => motor.Dispose();

    public PlayerIntent Read() => motor.Read();

    public void Think(IBotWorld world, float dt)
    {
        motor.Begin(dt);
        var player = world.Player;
        if (!world.IsPlaying || world.Rules is not DissolveRunRules rules || rules.Caught || player.IsRagdolled)
        {
            motor.Reset();
            Status = world.Rules is DissolveRunRules { Caught: true } ? "caught" : player.IsRagdolled ? "down" : "waiting";
            return;
        }
        var tuning = world.World.Tuning;
        var here = player.GlobalPosition;
        var gap = here.X - rules.Front;
        // Down the street, drifting back to its crown so parked cars and yards do not trap it.
        var ahead = new Vector3(here.X + 14f, here.Y, here.Z * 0.4f);
        var ammo = player.Ammo;
        if (ammo is { Magazine: 0, CanReload: true }) motor.Intent.ReloadPressed = true;

        // A can close ahead when ammo runs low.
        if (ammo != null && ammo.Magazine + ammo.Reserve < tuning.MagazineSize * 2 && gap > Flee &&
            world.Pickups.Where(p => GodotObject.IsInstanceValid(p) && p.Available && p.GlobalPosition.X > here.X - 1f &&
                p.GlobalPosition.DistanceTo(here) < 18f).OrderBy(p => p.GlobalPosition.DistanceTo(here)).FirstOrDefault() is { } can)
        {
            ahead = can.GlobalPosition;
            Status = "taking ammo";
        }
        else Status = gap < Flee ? $"running, front {gap:0.0} m behind" : "heading down the street";

        retarget -= dt;
        if (target != null && (!GodotObject.IsInstanceValid(target) || target.State == PropaneTank.TankState.Exploded)) target = null;
        if (retarget <= 0 || target == null)
        {
            retarget = 0.4f;
            var safe = tuning.RagdollRadius + 1.5f;
            target = gap < Flee || ammo is { Magazine: 0 } ? null : world.LiveTanks
                .Where(t => !t.Invulnerable && t.GlobalPosition.X > here.X - 3f && t.GlobalPosition.DistanceTo(here) is var d && d > safe && d < Reach &&
                    BotMotor.CanSee(player, t.GlobalTransform * PropaneTank.LocalCenter))
                .OrderByDescending(t => t.State == PropaneTank.TankState.Venting)
                .ThenBy(t => t.GlobalPosition.DistanceTo(here)).FirstOrDefault();
        }
        if (target != null)
        {
            motor.Face(player, target.GlobalTransform * PropaneTank.LocalCenter, dt);
            motor.Intent.Aim = true;
            if (BotMotor.OnTarget(player, target)) motor.Tap();
            Status = $"shooting a {target.State} tank, front {gap:0.0} m behind";
            // Keep a walking pace toward the street while shooting.
            motor.MoveTo(ahead, run: false);
        }
        else
        {
            motor.Face(player, ahead + Vector3.Up * 1.4f, dt);
            motor.MoveTo(ahead, run: true);
        }
        motor.Navigate(player, world.LiveTanks.Where(t => t.GlobalPosition.DistanceTo(here) < 30f), dt);
    }
}
