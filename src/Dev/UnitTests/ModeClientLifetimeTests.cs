using System;
using System.Linq;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

/// <summary>Repeated native sessions retire mode state and borrowed bindings while Match itself stays reachable.</summary>
[UnitSuite]
public static class ModeClientLifetimeTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("mode client/lifetime repeated Bank, King and roaming sessions release every adapter", () =>
        {
            using var bank = new ServerFixture();
            using var king = new ServerFixture(mode: ModeRegistry.King);
            using var lobby = new LobbyWorldFixture(3);
            foreach (var setup in new[] { bank.Setup, king.Setup, lobby.World() })
            {
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    using var client = new MatchClient(runner, setup, bank.Clock, 2);
                    if (setup.ModeId == ModeRegistry.LobbyWorldId)
                        ModeClientLifetimeFixture.PopulateLobby(client, lobby.Lobby(), 40 + cycle);
                    var references = ModeClientLifetimeFixture.Retire(client.Match);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Assert(references.All(reference => !reference.IsAlive), $"mode {setup.ModeId} cycle {cycle} retained a retired mode owner/state");
                    GC.KeepAlive(client.Match);
                }
            }
        });
        runner.Run("mode client/lifetime configuration failure rolls back each real Match adapter", () =>
        {
            using var bank = new ServerFixture();
            using var king = new ServerFixture(mode: ModeRegistry.King);
            using var lobby = new LobbyWorldFixture(3);
            foreach (var setup in new[] { bank.Setup, king.Setup, lobby.World() })
            {
                var references = ModeClientLifetimeFixture.FailedAssembly(runner, setup, bank.Clock);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert(references.Length > 0 && references.All(reference => !reference.IsAlive), "failed construction retained an adapter/composed owner");
            }
        });
    }
}
