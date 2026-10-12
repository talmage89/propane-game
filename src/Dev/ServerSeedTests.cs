using System;
using Godot;
using Propane.Core;
using Propane.Net;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>The debug server seed must select the actual transmitted plan, independently of creator tuners.</summary>
public partial class ServerSeedTests : Node
{
    public override async void _Ready()
    {
        DevArgs.MarkReady();
        try
        {
            Assert(int.TryParse(DevArgs.Get("match-seed"), out var expected), "native seed fixture needs --match-seed");
            AddChild(new Main { Name = "Main" });
            var client = new NetClient { Name = "Client", Template = Tuning.LoadBaked() };
            AddChild(client);
            client.Connect("127.0.0.1", (int)DevArgs.GetFloat("port", Protocol.DefaultPort), "Seed", 0);
            for (var step = 0; step < 120 && client.State != NetClient.Status.Connected; step++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            }
            Assert(client.State == NetClient.Status.Connected, "native seed fixture did not connect");
            client.CreateLobby();
            for (var step = 0; step < 120 && client.Lobby == null; step++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            }
            Assert(client.Lobby != null, "native seed fixture did not create a lobby");
            MatchSetup? setup = null;
            client.MatchStarting += value => setup = value;
            client.StartMatch();
            for (var step = 0; step < 120 && setup == null; step++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            }
            Assert(setup != null, "native seed fixture did not receive a plan");
            var actual = PlanCodec.Decode(setup!.Plan).Seed;
            Assert(actual == expected, $"server ignored --match-seed: expected {expected}, transmitted {actual}");
            GD.Print($"[server seed] PASS actual transmitted plan seed {actual}");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[server seed] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }
}
