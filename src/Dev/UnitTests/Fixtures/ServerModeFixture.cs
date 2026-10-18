using System;
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
    private readonly Func<ServerMode, Tuning, int, SuburbSettings> supply;
    private readonly Func<ServerMode, Tuning, ModeTuners> tuners;

    public ServerModeFixture(ModeDefinition? initial = null)
    {
        server = ModeRegistry.CreateServer(initialMode: initial);
        T Field<T>(string name) => (T)typeof(LobbyServer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        Modes = Field<ServerModes>("modes");
        supply = Field<Func<ServerMode, Tuning, int, SuburbSettings>>("supply");
        tuners = Field<Func<ServerMode, Tuning, ModeTuners>>("copyModeTuners");
    }

    public SuburbSettings Supply(ServerMode mode, Tuning tuning, int players) => supply(mode, tuning, players);
    public ModeTuners Tuners(ServerMode mode, Tuning tuning) => tuners(mode, tuning);
    public void Dispose() => server.Free();
}
