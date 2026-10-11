using System;
using System.Diagnostics;
using Godot;

namespace Propane.Dev;

/// <summary>Runs compatible suites sequentially in one engine; standalone scenes still exit on completion.</summary>
public partial class CoreTests : Node
{
    // Keep the synchronous unit suites last: they build/free worlds without advancing physics.
    private static readonly string[] Scenes = { "platform_tests", "movement_tests", "unit_tests" };
    private int next;
    private Node? suite;
    private readonly Stopwatch timer = new();
    private bool completing;
    private bool failed;

    public override void _Ready()
    {
        DevArgs.MarkReady();
        Callable.From(StartNext).CallDeferred();
    }

    private void StartNext()
    {
        if (failed) return;
        try
        {
            if (suite != null)
            {
                RemoveChild(suite);
                suite.Free();
                suite = null;
                foreach (var action in InputMap.GetActions()) Input.ActionRelease(action);
            }
            if (next == Scenes.Length)
            {
                GD.Print($"[core] PASS {next} suites");
                GetTree().Quit();
                return;
            }
            completing = false;
            var name = Scenes[next++];
            GD.Print($"[core] BEGIN {name}");
            timer.Restart();
            suite = ResourceLoader.Load<PackedScene>($"res://scenes/dev/{name}.tscn").Instantiate();
            AddChild(suite);
        }
        catch (Exception error)
        {
            failed = true;
            GD.PushError($"[core] FAIL setup/cleanup: {error}");
            GetTree().Quit(1);
        }
    }

    private void Finish(int code)
    {
        // A failure must never be overwritten by a later successful completion.
        if (code != 0)
        {
            failed = true;
            GD.PushError($"[core] FAIL {Scenes[next - 1]} after {timer.Elapsed.TotalSeconds:0.000}s");
            GetTree().Quit(code);
            completing = true;
            return;
        }
        if (completing) return;
        completing = true;
        GD.Print($"[core] PASS {Scenes[next - 1]} in {timer.Elapsed.TotalSeconds:0.000}s");
        Callable.From(StartNext).CallDeferred();
    }

    public static void Complete(Node node, int code = 0)
    {
        if (node.GetParent() is CoreTests runner) runner.Finish(code);
        else node.GetTree().Quit(code);
    }
}
