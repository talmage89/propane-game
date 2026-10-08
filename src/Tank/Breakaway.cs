using Godot;
using Propane.Core;

namespace Propane.Tank;

/// <summary>
/// A rigid body that holds still (frozen) until a blast pushes it harder than <see cref="Tuning.BreakawaySpeed"/>,
/// then tears loose. Used for fence panels and other things that are fixed in place in real life.
/// </summary>
public partial class Breakaway : RigidBody3D
{
    public bool Anchored => Freeze;

    public override void _Ready()
    {
        FreezeMode = FreezeModeEnum.Static;
        Freeze = true;
    }

    public void Release()
    {
        Freeze = false;
        Sleeping = false;
    }
}
