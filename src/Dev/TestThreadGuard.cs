using System;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
using Environment = System.Environment;

namespace Propane.Dev;

/// <summary>Turn Godot's rejected scene-thread calls into fatal, named test assertions at their caller.</summary>
public partial class TestThreadGuard : Logger
{
    // Retain the logger through scene destruction and finalizer draining, not just suite execution.
    private static TestThreadGuard? installed;
    private readonly int mainThread = Environment.CurrentManagedThreadId;
    private int failing;

    public static void Install()
    {
        if (installed != null) { return; }
        installed = new TestThreadGuard();
        OS.AddLogger(installed);
    }

    public static void AssertMainThread(string operation)
    {
        if (installed == null) { throw new InvalidOperationException("test thread guard was not installed"); }
        if (Environment.CurrentManagedThreadId != installed.mainThread)
        {
            installed.Fail(operation);
        }
    }

    public override void _LogError(string function, string file, int line, string code, string rationale,
        bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        // Node thread checks report either an explicit caller-thread rejection or a main-thread requirement.
        // Do not fail on expected malformed-packet warnings or unrelated engine diagnostics.
        var source = file.Replace('\\', '/');
        if ((source.StartsWith("scene/", StringComparison.Ordinal) || source.Contains("/scene/", StringComparison.Ordinal)) &&
            (rationale.Contains("thread", StringComparison.OrdinalIgnoreCase) ||
             code.Contains("is_main_thread", StringComparison.Ordinal) ||
             code.Contains("caller_thread", StringComparison.Ordinal)))
        {
            Fail($"{file}:{line} {function}: {rationale} ({code})");
        }
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void ExitProcess(uint code);

    [DllImport("libc", EntryPoint = "_exit")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void ExitImmediately(int code);

    private void Fail(string operation)
    {
        if (Interlocked.Exchange(ref failing, 1) != 0) { return; }
        // This callback can be on a CLR finalizer/worker thread. Never log through Godot, throw through its
        // native callback, or attempt orderly tree cleanup here. Keep the original managed caller in the log.
        Console.Error.WriteLine($"[thread] FAIL off-main scene access: {operation}; main={mainThread}, caller={Environment.CurrentManagedThreadId}, name={Thread.CurrentThread.Name ?? "unnamed"}");
        Console.Error.WriteLine(Environment.StackTrace);
        Console.Error.Flush();
        // Bypass native atexit destructors too: their services still own the live scene.
        if (OperatingSystem.IsWindows()) { ExitProcess(1); }
        else { ExitImmediately(1); }
    }
}
