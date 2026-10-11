using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.UI;

namespace Propane.Dev;

/// <summary>Render the production lobby/results presenters at 2, 8 or 16 entries without starting a match.</summary>
public partial class CapacityUiTests : Node
{
    public override async void _Ready()
    {
        var net = new NetClient();
        var menus = new Menus { Net = net, HostingNote = "Host on this computer · UDP lobby" };
        var results = new ResultsScreen();
        var capture = new CaptureDirector();
        AddChild(net);
        AddChild(menus);
        AddChild(results);
        AddChild(capture);
        var background = new ColorRect { Color = new Color(.95f, .95f, .96f), MouseFilter = Control.MouseFilterEnum.Ignore };
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var canvas = new CanvasLayer { Layer = -1 };
        AddChild(canvas); canvas.AddChild(background);
        try
        {
            InputSetup.Register();
            var count = (int)DevArgs.GetFloat("players", 16);
            var profiles = Enumerable.Range(0, count).Select(i => new PlayerProfile(1001 + i,
                i == 0 ? "Creator" : $"Participant {i + 1:00}", AppearanceRecord.FromPalette(i)) { OwnerPeerId = 2 + i, AllocationSlot = i }).ToArray();
            net.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
            net.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyList, 0, 0, new LobbyListMessage(16, Array.Empty<LobbySummary>())), true));
            net.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, new LobbyInfo(1, 2, 180,
                LobbyServer.LobbyPhase.Waiting, profiles, count)), true));
            menus.Show(Menus.Screen.Lobby);
            await Frames(12).ConfigureAwait(true);
            AssertPanels(menus);
            TestRunnerCount(menus, count);
            capture.Screenshot($"lobby_{count}");
            await Frames(12).ConfigureAwait(true);
            menus.Show(Menus.Screen.None);
            var standings = profiles.Select((p, i) => new Standing(p.Id, p.Name, p.Appearance, count - i,
                i != count - 1)).ToArray();
            results.Show(standings, profiles[0].Id, 30);
            await Frames(12).ConfigureAwait(true);
            AssertPanels(results);
            foreach (var name in results.FindChildren("*", "Label", owned: false).OfType<Label>().Where(l => l.Text.Contains("Participant") || l.Text.Contains("Creator")))
                UnitTests.TestRunner.Assert(name.Size.X >= 200, "standing name has no readable width");
            TestRunnerCount(results, count);
            capture.Screenshot($"results_{count}");
            await Frames(12).ConfigureAwait(true);
            GD.Print($"[capacity UI] PASS {count} lobby/results entries at window {GetWindow().Size}, logical viewport {GetViewport().GetVisibleRect().Size}");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[capacity UI] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }
    private static void TestRunnerCount(Node root, int count) => UnitTests.TestRunner.Assert(
        root.FindChildren("*", "ColorRect", owned: false).OfType<ColorRect>().Count(c => c.HasMeta("appearance_player")) == count,
        "presenter lost roster entries");
    private void AssertPanels(Node root)
    {
        var size = GetViewport().GetVisibleRect().Size;
        foreach (var panel in root.FindChildren("*", "PanelContainer", owned: false).OfType<PanelContainer>())
        {
            var rect = panel.GetGlobalRect();
            UnitTests.TestRunner.Assert(rect.Position.X >= 0 && rect.Position.Y >= 0 && rect.End.X <= size.X && rect.End.Y <= size.Y,
                $"panel overflow: {rect}, viewport {size}");
        }
    }
    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
