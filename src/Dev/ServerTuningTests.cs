using System;
using Godot;
using Propane.Core;
using Propane.Net;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Server command-line tuners must reach native lobby admission, not only dev client scenes.</summary>
public partial class ServerTuningTests : Node
{
    public override async void _Ready()
    {
        DevArgs.MarkReady();
        try
        {
            // Deliberately do not call DevArgs.Setup: exercise the production server entry point.
            AddChild(new Main { Name = "Main" });
            var client = new NetClient { Name = "Client" };
            AddChild(client);
            var advertised = false;
            client.LobbiesChanged += () => advertised = true;
            client.Connect("127.0.0.1", (int)DevArgs.GetFloat("port", Protocol.DefaultPort), "Capacity", 0);
            for (var frame = 0; frame < 600 && !advertised; frame++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            Assert(client.State == NetClient.Status.Connected, "native capacity probe did not connect");
            Assert(advertised, "native server did not advertise lobby admission");
            Assert(client.LobbyCapacity == 6, $"server ignored --tune.MaxPlayers=6: advertised {client.LobbyCapacity}");
            GD.Print("[server tuning] PASS native lobby advertises the requested six-player capacity");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[server tuning] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }
}
