using System;
using System.Linq;
using System.Reflection;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.World.Plan;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Observes the actual composition-selected startup callbacks without generating hundreds of suburbs.</summary>
internal sealed class ServerModeFixture : IDisposable
{
    private readonly LobbyServer server;
    public readonly ServerModes Modes;
    private readonly IServerSettings settings;

    public ServerModeFixture(ModeDefinition? initial = null)
    {
        server = ModeRegistry.CreateServer(initialMode: initial);
        T Field<T>(string name) => (T)typeof(LobbyServer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        Modes = Field<ServerModes>("modes");
        settings = Field<IServerSettings>("settings");
    }

    // Exercise the composition-selected adapter with an owned copy of the caller's current values. The
    // caller keeps its template; disposing each sample must not release it. Live reads inside a surviving
    // adapter are separately asserted by ServerModeTests' wire observations and ApplicationServerTests.
    public SuburbSettings Supply(ServerMode mode, Tuning tuning, int players)
    {
        using var sample = settings.FromSnapshot(mode, tuning.Snapshot());
        return sample.Supply(players);
    }
    public ModeTuners Tuners(ServerMode mode, Tuning tuning)
    {
        using var sample = settings.FromSnapshot(mode, tuning.Snapshot());
        return sample.ReadModeTuners();
    }
    public bool CapturesCatalog => settings.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Any(member => member.FieldType == typeof(ModeCatalog) || member.FieldType == typeof(ModeDefinition) || member.FieldType == typeof(ModeBundle));
    public void Dispose() => server.Free();
}
