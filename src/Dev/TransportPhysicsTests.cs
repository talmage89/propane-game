using System;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Native transport must progress between physics steps when client process frames are unavailable.</summary>
public partial class TransportPhysicsTests : Node
{
    public override async void _Ready()
    {
        DevArgs.MarkReady();
        try
        {
            AddChild(new Main { Name = "Main" });
            var client = new NetClient(PacketTransports.CreateClient(), ModeRegistry.Codec) { Name = "Client" };
            AddChild(client);
            client.SetProcess(false);
            client.SetPhysicsProcess(true);
            client.Connect("127.0.0.1", (int)DevArgs.GetFloat("port", Protocol.DefaultPort), "Physics", 0);
            for (var step = 0; step < 120 && client.State != NetClient.Status.Connected; step++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            }
            Assert(client.State == NetClient.Status.Connected, "native handshake required a client process frame");
            client.CreateLobby();
            for (var step = 0; step < 120 && client.Lobby == null; step++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            }
            Assert(client.Lobby?.CreatorId == client.MyId, "native reliable lobby control required a client process frame");
            Assert(!client.IsProcessing(), "native fixture accidentally enabled client process frames");
            GD.Print("[transport physics] PASS native handshake and reliable control without client process frames");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[transport physics] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }
}
