using System.Threading;
using Godot;

namespace Propane.Dev;

/// <summary>Test-only root child that checks scene garbage was drained before final engine teardown.</summary>
public partial class ShutdownWitness : Node
{
    public SceneTree Tree { get; set; } = null!;
    public ShutdownTestState State { get; } = new();

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete)
        {
            return;
        }

        var alive = 0;
        foreach (var reference in State.Garbage)
        {
            if (reference.TryGetTarget(out _))
            {
                alive++;
            }
        }
        var physicsAdvanced = Engine.GetPhysicsFrames() > State.DeletedAtPhysicsFrame;
        if (!State.SceneDeleted || !State.SceneSawSiblingExit || !physicsAdvanced || Volatile.Read(ref State.Finalized) != 1 || alive != 0)
        {
            GD.PushError($"[shutdown] FAIL scene deleted {State.SceneDeleted}, reverse order {State.SceneSawSiblingExit}, physics advanced {physicsAdvanced}, finalizers {State.Finalized}, live wrappers {alive}");
            // Final teardown is already running: set its failure status directly.
            Tree.Quit(1);
            return;
        }
        GD.Print($"[shutdown] PASS reverse teardown, scene destroyed and {State.Garbage.Count} wrappers collected; finalizer drained, physics advanced");
    }
}
