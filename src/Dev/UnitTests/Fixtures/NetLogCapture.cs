using System;
using System.Collections.Generic;
using Godot;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Main-thread scoped observation of the real Godot log; removed before native disposal.</summary>
internal sealed class NetLogCapture : IDisposable
{
    private readonly NetLogSink sink = new();
    public List<string> Lines => sink.Lines;
    public NetLogCapture() => OS.AddLogger(sink);
    public void Dispose() { OS.RemoveLogger(sink); sink.Dispose(); }
}

internal partial class NetLogSink : Logger
{
    internal readonly List<string> Lines = new();
    public override void _LogMessage(string message, bool error) => Lines.Add(message);
    public override void _LogError(string function, string file, int line, string code, string rationale,
        bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces) => Lines.Add(code + rationale);
}
