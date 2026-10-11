using System;
using Godot;
using Propane.Core;

namespace Propane.Dev.UnitTests;

/// <summary>Small assertion runner: suites register named actions; every failure is printed and exits nonzero.</summary>
public partial class TestRunner : Node
{
    public WorldContext World { get; private set; } = null!;
    public override void _ExitTree() => World.Dispose();

    private int tests;
    private int failures;

    public override void _Ready()
    {
        World = new WorldContext(this, Tuning.SavedTemplate, present: true);
        try
        {
            if (WorldBuildTests.RunProcessMode(this))
            {
                GetTree().Quit();
                return;
            }
            WorldBuildTests.Register(this);
            ProtocolTests.Register(this);
            AppearanceTests.Register(this);
            PlanTests.Register(this);
            ServerTests.Register(this);
            LifecycleTests.Register(this);
            NavigationTests.Register(this);
            AuthorityTests.Register(this);
            LocomotionTests.Register(this);
            GatherTests.Register(this);
            RecoilTests.Register(this);
            MeleeTests.Register(this);
            WorldClockTests.Register(this);
        }
        catch (Exception e)
        {
            failures++;
            GD.Print($"[unit] FAIL suite setup: {e}");
        }
        GD.Print($"[unit] {tests} tests, {failures} failures");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    public void Run(string name, Action test)
    {
        tests++;
        World.SetContentRoot(World.Root);
        try
        {
            test();
        }
        catch (Exception e)
        {
            failures++;
            GD.Print($"[unit] FAIL {name}: {e}");
        }
    }

    public static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }
}
