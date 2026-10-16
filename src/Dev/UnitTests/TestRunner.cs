using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        TestThreadGuard.Install();
        TestThreadGuard.AssertMainThread("unit suite setup");
        World = new WorldContext(this, Tuning.SavedTemplate, present: true);
        DevArgs.MarkReady();
        if (DevArgs.Get("test-thread-violation") is { } violation)
        {
            // Separate-process negative probes: the guard must exit 1 before a rejected native call returns.
            var root = GetTree().Root;
            if (violation == "finalizer")
            {
                MakeThreadGarbage(root);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            var worker = new System.Threading.Thread(() =>
            {
                if (violation == "managed") { Run("injected unit callback", () => { }); }
                else if (violation == "node") { root.GetChildCount(); }
                else { TestThreadGuard.AssertMainThread($"unknown thread probe {violation}"); }
            })
            { Name = "TestThreadProbe" };
            worker.Start();
            worker.Join();
            GD.PushError("[unit] FAIL thread guard allowed the injected violation to return");
            CoreTests.Complete(this, 2);
            return;
        }
        try
        {
            if (WorldBuildTests.RunProcessMode(this))
            {
                CoreTests.Complete(this);
                return;
            }
            // --suite=Name runs only the suites whose names contain it, while iterating on one of them.
            var only = DevArgs.Get("suite");
            var suites = typeof(TestRunner).Assembly.GetTypes()
                .Where(type => type.IsDefined(typeof(UnitSuiteAttribute), false) && (only == null || type.Name.Contains(only, StringComparison.Ordinal)))
                .OrderBy(type => type.GetCustomAttribute<UnitSuiteAttribute>()!.Order)
                .ThenBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
            Assert(suites.Length > 0, "no unit suites discovered");
            foreach (var suite in suites)
            {
                var register = suite.GetMethod("Register", BindingFlags.Public | BindingFlags.Static,
                    new[] { typeof(TestRunner) });
                Assert(register != null && register.ReturnType == typeof(void),
                    $"{suite.FullName} must expose public static void Register(TestRunner)");
                GD.Print($"[unit] BEGIN {suite.Name}");
                var before = tests;
                register!.CreateDelegate<Action<TestRunner>>()(this);
                Assert(tests > before, $"{suite.FullName} registered no assertions");
            }
            GD.Print($"[unit] {suites.Length} discovered suites");
        }
        catch (Exception e)
        {
            failures++;
            GD.Print($"[unit] FAIL suite setup: {e}");
        }
        GD.Print($"[unit] {tests} tests, {failures} failures");
        CoreTests.Complete(this, failures == 0 ? 0 : 1);
    }

    public void Run(string name, Action test)
    {
        TestThreadGuard.AssertMainThread($"unit test {name}");
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

    /// <summary>
    /// Runs <paramref name="window"/> up to <paramref name="windows"/> times and passes when one run allocates no managed
    /// bytes on this thread. Steady per-call garbage allocates in every window; a rare one-time runtime allocation (seen
    /// under parallel gate load and on CI, callsite unknown) lands in at most one, so it cannot fail the check alone.
    /// </summary>
    public static void AssertNoAllocation(Action window, string message, int windows = 3)
    {
        var allocated = long.MaxValue;
        for (var i = 0; i < windows && allocated != 0; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            window();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert(allocated == 0, $"{message} ({allocated} bytes in the last window)");
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MakeThreadGarbage(Node root) => _ = new ThreadFinalizerProbe(root);

    private sealed class ThreadFinalizerProbe
    {
        private readonly Node root;
        public ThreadFinalizerProbe(Node root) => this.root = root;
        ~ThreadFinalizerProbe() => root.GetChildCount();
    }
}

/// <summary>Opt a static Register(TestRunner) suite into discovery; negative order is reserved for world setup.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class UnitSuiteAttribute : Attribute
{
    public int Order { get; }

    public UnitSuiteAttribute(int order = 0) => Order = order;
}
