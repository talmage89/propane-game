using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;
using Propane.Core;

namespace Propane.Dev;

/// <summary>Creates garbage during scene destruction, after a pre-quit collection would be too early.</summary>
public partial class ShutdownTests : Node
{
    private ShutdownWitness? witness;

    public override void _Ready()
    {
        var tree = GetTree();
        witness = new ShutdownWitness { Tree = tree };
        if (DevArgs.Get("paused") != null)
        {
            tree.Paused = true;
        }
        if (DevArgs.Get("window-close") != null)
        {
            GetWindow().EmitSignal(Window.SignalName.CloseRequested);
        }
        else
        {
            ShutdownGuard.Quit(tree, int.TryParse(DevArgs.Get("exit-code"), out var exitCode) ? exitCode : 0);
        }
        if (DevArgs.Get("repeat-quit") != null)
        {
            ShutdownGuard.Quit(tree);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPredelete && witness != null)
        {
            MakeGarbage(witness.State);
            // Added after the shutdown snapshot: this tiny test-only node survives until final teardown
            // and asserts that scene garbage was already collected by the orderly quit path.
            witness.Tree.Root.AddChild(witness);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MakeGarbage(ShutdownTestState state)
    {
        for (var i = 0; i < 10000; i++)
        {
            state.Garbage.Add(new WeakReference<GodotObject>(new BoxShape3D()));
            state.Garbage.Add(new WeakReference<GodotObject>(new StandardMaterial3D()));
        }
        _ = new FinalizerWitness(state);
        // The native query retains the shape's managed wrapper until the query finalizer releases it.
        var nested = new SphereShape3D();
        state.Garbage.Add(new WeakReference<GodotObject>(nested));
        state.Garbage.Add(new WeakReference<GodotObject>(new PhysicsShapeQueryParameters3D { Shape = nested }));
        state.SceneDeleted = true;
    }

    private sealed class FinalizerWitness
    {
        private readonly ShutdownTestState state;

        public FinalizerWitness(ShutdownTestState state) => this.state = state;

        ~FinalizerWitness() => Interlocked.Increment(ref state.Finalized);
    }
}

public sealed class ShutdownTestState
{
    public readonly List<WeakReference<GodotObject>> Garbage = new();
    public bool SceneDeleted;
    public int Finalized;
}
