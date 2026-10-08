using Godot;
using Propane.Core;

namespace Propane.Fx;

/// <summary>Freezes time for a few frames when a blast goes off close to the player, for weight.</summary>
public partial class Hitstop : Node
{
    private ulong releaseAt;
    private bool active;

    /// <summary>The node whose distance to a blast decides whether it stops time (the player).</summary>
    public Node3D? Focus { get; set; }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        GameEvents.Explosion += OnExplosion;
    }

    public override void _ExitTree()
    {
        GameEvents.Explosion -= OnExplosion;
        Engine.TimeScale = 1.0;
    }

    public override void _Process(double delta)
    {
        if (active && Time.GetTicksMsec() >= releaseAt)
        {
            active = false;
            Engine.TimeScale = 1.0;
        }
    }

    private void OnExplosion(Vector3 position)
    {
        var tuning = Tuning.Current;
        if (Focus == null || tuning.HitstopDuration <= 0 || Focus.GlobalPosition.DistanceTo(position) > tuning.HitstopRadius)
        {
            return;
        }
        active = true;
        Engine.TimeScale = tuning.HitstopTimeScale;
        releaseAt = Time.GetTicksMsec() + (ulong)(tuning.HitstopDuration * 1000f);
    }
}
