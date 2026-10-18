using System;
using System.IO;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Opt-in packet observation, injected only into scratch builds by server_equivalence.py.</summary>
internal static class ServerObservation
{
    public static void Packet(string direction, int peer, byte[] bytes, TrafficClass? traffic)
    {
        if (DevArgs.Get("server-transcript") is not { } path) return;
        File.AppendAllText(path, $"{direction} {peer} {traffic?.ToString() ?? "unspecified"} {Convert.ToBase64String(bytes)}\n");
    }
}
