using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Propane.Net;

namespace Propane.Dev;

/// <summary>Engine frame cost, resident process memory and transport/inbox pressure for repeatable capacity runs.</summary>
public static class CapacityMetrics
{
    public static string Server(IEnumerable<TransportDiagnostics> connections)
    {
        var records = connections.ToArray();
        return Sample("server", diagnostics: new TransportDiagnostics() with
        {
            BytesIn = records.Sum(d => d.BytesIn),
            BytesOut = records.Sum(d => d.BytesOut),
            QueuedPackets = records.Sum(d => d.QueuedPackets),
            QueuedBytes = records.Sum(d => d.QueuedBytes),
            Drops = records.Sum(d => d.Drops),
        });
    }
    public static string Sample(string role, NetClient? net = null, TransportDiagnostics diagnostics = default)
    {
        using var process = Process.GetCurrentProcess();
        var d = net?.Diagnostics ?? diagnostics;
        var inbox = net?.InboxPressure ?? default;
        return FormattableString.Invariant($"metrics {role} process={Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:0.000}ms physics={Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:0.000}ms rss={process.WorkingSet64}B in={d.BytesIn}B out={d.BytesOut}B queue={d.QueuedPackets}/{d.QueuedBytes}B drops={d.Drops} inbox={inbox.Packets}/{inbox.Bytes}B overflows={inbox.Overflows}");
    }
}
