using System;
using System.Runtime.CompilerServices;
using Godot;

namespace Propane.Core;

/// <summary>Keep this first in the autoload list: Godot deletes root children in reverse insertion order.</summary>
public partial class ShutdownGuard : Node
{
    private bool requested;
    private int exitCode;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetPhysicsProcess(false);
        GetTree().AutoAcceptQuit = false;
        GetWindow().CloseRequested += CloseRequested;
    }

    public override void _ExitTree() => GetWindow().CloseRequested -= CloseRequested;

    private void CloseRequested() => Quit(GetTree());

    /// <summary>Free the scene and drain its finalizers, then allow physics to flush their queued RID frees.</summary>
    public static void Quit(SceneTree tree, int code = 0)
    {
        var guard = tree.Root.GetNode<ShutdownGuard>("ShutdownGuard");
        // Preserve an assertion failure if another component also requests a successful quit.
        if (code != 0 || !guard.requested)
        {
            guard.exitCode = code;
        }
        if (guard.requested)
        {
            return;
        }
        guard.requested = true;
        Callable.From(guard.BeginShutdown).CallDeferred();
    }

    private void BeginShutdown()
    {
        FreeScenes();
        DrainFinalizers();
        SetPhysicsProcess(true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FreeScenes()
    {
        // End this stack frame before collecting, so its node wrappers cannot keep scene resources alive.
        var children = GetTree().Root.GetChildren();
        foreach (var child in children)
        {
            if (child != this)
            {
                child.Free();
            }
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        // PhysicsServer3D::sync has now flushed commands queued by resource finalizers on the CLR thread.
        SetPhysicsProcess(false);
        GetTree().Quit(exitCode);
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete)
        {
            return;
        }

        // The scene and later autoloads have been freed, but the native servers and C# bindings still exist.
        // Godot's shutdown tracker uses short weak references, so it cannot dispose wrappers already queued
        // for finalization. Drain those wrappers before CSharpLanguage::finalize tears down their bindings.
        DrainFinalizers();
    }

    private static void DrainFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
