using Godot;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;
using Propane.World;

namespace Propane.Bots;

/// <summary>
/// Dissolve Run's goal policy, kept simple for the prototype and its scripted runs: keep heading down the street
/// (world +X), never stopping while the front is close; on the way, shoot tanks ahead that stand outside a blast's
/// throw, finishing the ones it punctured, and take ammo cans it passes when it runs low. It reads the already
/// integrated front from a plain observation of the latest accepted step.
/// </summary>
public sealed class DissolveRunBot : IBotGoalPolicy
{
    /// <summary>A gap under this many meters means run, whatever else is ahead.</summary>
    private const float Flee = 9f;
    private const float Reach = 28f;

    private readonly BotMotor motor = new();
    private PropaneTank? target;
    private float retarget;

    private readonly BotStatus status = new();
    public string Status => status.ToString();
    public string StatusKind => status.Format;
    public string NavigationDiagnostics => motor.NavigationDiagnostics;
    public float Skill { get => motor.Skill; set => motor.Skill = value; }

    public DissolveRunBot(ulong? seed = null) => _ = seed;

    public void Dispose() { }

    public void Prepare(IBotWorld world) => motor.Prepare(world);

    public PlayerIntent Read() => motor.Read();

    public void Think(IBotWorld world, float dt)
    {
        motor.Begin(dt);
        var player = world.Player;
        if (!world.IsPlaying || world.DissolveRun is not { } observation || observation.Caught || player.IsRagdolled)
        {
            motor.Reset();
            status.Set(world.DissolveRun is { Caught: true } ? "caught" : player.IsRagdolled ? "down" : "waiting");
            return;
        }
        var tuning = world.World.Tuning;
        var here = player.GlobalPosition;
        var gap = here.X - observation.Front;
        // Down the street, drifting back to its crown so parked cars and yards do not trap it.
        var ahead = new Vector3(here.X + 14f, here.Y, here.Z * 0.4f);
        var ammo = player.Ammo;
        if (ammo is { Magazine: 0, CanReload: true }) motor.Intent.ReloadPressed = true;

        // A can close ahead when ammo runs low.
        AmmoPickup? can = null;
        if (ammo != null && ammo.Magazine + ammo.Reserve < tuning.MagazineSize * 2 && gap > Flee)
        {
            var pickups = world.Pickups;
            var nearest = 18f;
            for (var i = 0; i < pickups.Count; i++)
            {
                var p = pickups[i];
                if (!GodotObject.IsInstanceValid(p) || !p.Available || p.GlobalPosition.X <= here.X - 1f) continue;
                var distance = p.GlobalPosition.DistanceTo(here);
                if (distance < nearest) { can = p; nearest = distance; }
            }
        }
        if (can != null)
        {
            ahead = can.GlobalPosition;
            status.Set("taking ammo");
        }
        else
        {
            status.Set(gap < Flee ? "running, front {1:0.0} m behind" : "heading down the street", first: gap);
        }

        retarget -= dt;
        if (target != null && (!GodotObject.IsInstanceValid(target) || target.State == PropaneTank.TankState.Exploded)) { target = null; retarget = 0; }
        if (retarget <= 0)
        {
            retarget = 0.4f;
            var safe = tuning.RagdollRadius + 1.5f;
            target = null;
            if (gap >= Flee && ammo is not { Magazine: 0 })
            {
                var tanks = world.LiveTanks;
                var nearest = Reach;
                for (var i = 0; i < tanks.Count; i++)
                {
                    var t = tanks[i];
                    var distance = t.GlobalPosition.DistanceTo(here);
                    if (t.Invulnerable || t.GlobalPosition.X <= here.X - 3f || distance <= safe || distance >= Reach) continue;
                    var vents = t.State == PropaneTank.TankState.Venting;
                    var preferred = target == null || vents && target.State != PropaneTank.TankState.Venting ||
                        vents == (target.State == PropaneTank.TankState.Venting) && distance < nearest;
                    if (preferred && BotMotor.CanSee(world, player, t.GlobalTransform * PropaneTank.LocalCenter))
                    {
                        target = t;
                        nearest = distance;
                    }
                }
            }
        }
        if (target != null)
        {
            motor.Face(player, target.GlobalTransform * PropaneTank.LocalCenter, dt);
            motor.Intent.Aim = true;
            if (BotMotor.OnTarget(world, player, target)) motor.Tap();
            status.Set("shooting a {4} tank, front {1:0.0} m behind", first: gap, word: BotStatus.Name(target.State));
            // Keep a walking pace toward the street while shooting.
            motor.MoveTo(ahead, run: false);
        }
        else
        {
            motor.Face(player, ahead + Vector3.Up * 1.4f, dt);
            motor.MoveTo(ahead, run: true);
        }
        motor.Navigate(world, dt);
    }
}
