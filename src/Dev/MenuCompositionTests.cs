using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests;
using Propane.Net;
using Propane.ReleaseSmoke;
using Propane.UI;

namespace Propane.Dev;

/// <summary>Deterministic menu portraits and refresh assertions, using production presenters and decoded packets.</summary>
public partial class MenuCompositionTests : Node
{
    private NetClient net = null!;
    private Menus menus = null!;
    private CaptureDirector capture = null!;

    public override async void _Ready()
    {
        try
        {
            InputSetup.Register();
            PlayerSettings.UseFile("user://menu_composition_settings.cfg");
            PlayerSettings.Name = "Creator";
            PlayerSettings.Server = "";
            PlayerSettings.Appearance = AppearanceRecord.FromPalette(0);
            net = new NetClient();
            menus = new Menus { Net = net };
            capture = new CaptureDirector();
            AddChild(net);
            AddChild(menus);
            AddChild(capture);
            var canvas = new CanvasLayer { Layer = -1 };
            var background = new ColorRect { Color = new Color(.95f, .95f, .96f), MouseFilter = Control.MouseFilterEnum.Ignore };
            background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(canvas);
            canvas.AddChild(background);
            menus.Show(Menus.Screen.Main);
            await Shot("main").ConfigureAwait(true);
            menus.Show(Menus.Screen.Connect);
            await Shot("connect").ConfigureAwait(true);
            menus.ConnectTo("", "Creator");
            await Shot("connect_error").ConfigureAwait(true);
            menus.Show(Menus.Screen.Loading);
            await Shot("loading").ConfigureAwait(true);
            Packet(Msg.Welcome, new WelcomeMessage(2, null));
            Packet(Msg.LobbyList, new LobbyListMessage(16, Array.Empty<LobbySummary>()));
            menus.Show(Menus.Screen.Browser);
            await Shot("browser_empty").ConfigureAwait(true);
            Packet(Msg.LobbyList, new LobbyListMessage(16, new[]
            {
                new LobbySummary(1, "Creator", 2, LobbyPhase.Waiting),
                new LobbySummary(2, "Other creator", 8, LobbyPhase.Playing, 8),
            }));
            await Shot("browser_list").ConfigureAwait(true);
            foreach (var docked in new[] { false, true })
            {
                foreach (var count in new[] { 2, 8, 16 })
                {
                    foreach (var guest in new[] { false, true })
                    {
                        var members = Enumerable.Range(0, count).Select(i => new PlayerProfile(1001 + i,
                            i == 0 ? "Creator" : $"Participant {i + 1:00}", AppearanceRecord.FromPalette(i))
                        {
                            OwnerPeerId = i == 0 ? (guest ? 3 : 2) : i == 1 ? (guest ? 2 : 3) : i == count - 1 ? 2 : 3 + i,
                            ControllerConfig = i > 1 && i == count - 1 ? ControllerConfig.Bot : ControllerConfig.Human,
                            Kind = i > 1 && i == count - 1 ? ActorKind.Bot : ActorKind.Human,
                            AllocationSlot = i
                        }).ToArray();
                        var lobby = new LobbyInfo(1, guest ? 3 : 2, 180, LobbyPhase.Waiting, members, 16);
                        Packet(Msg.LobbyState, lobby);
                        menus.Show(Menus.Screen.Lobby, docked: docked);
                        var prefix = $"{(docked ? "docked" : "classic")}_{count}_{(guest ? "guest" : "host")}";
                        await Shot(prefix + "_name").ConfigureAwait(true);
                        // A real device event selects gamepad presentation; focus identity must survive packets.
                        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = JoyButton.DpadDown, Pressed = true });
                        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = JoyButton.DpadDown, Pressed = false });
                        await Shot(prefix + "_gamepad").ConfigureAwait(true);
                        var name = menus.FindChildren("*", "LineEdit", owned: false).OfType<LineEdit>().Single();
                        name.GrabFocus();
                        name.Text = "Draft name";
                        name.CaretColumn = 5;
                        Packet(Msg.LobbyState, lobby);
                        var rebuilt = menus.FindChildren("*", "LineEdit", owned: false).OfType<LineEdit>().Single();
                        TestRunner.Assert(rebuilt.Text == "Draft name" && rebuilt.CaretColumn == 5, "refresh lost name draft/caret");
                        TestRunner.Assert(GetViewport().GuiGetFocusOwner() == rebuilt, "refresh lost name focus");
                        Press("custom_color");
                        await Shot(prefix + "_appearance").ConfigureAwait(true);
                        Packet(Msg.LobbyState, lobby);
                        var picker = menus.FindChildren("*", "CanvasLayer", owned: false).OfType<AppearancePicker>().Single(p => p.Visible);
                        TestRunner.Assert(picker.IsAncestorOf(GetViewport().GuiGetFocusOwner()), "refresh stole dialog focus");
                        Press("Cancel");
                        Press(count == 16 ? $"bot_{members[^1].Id}" : "add_bot");
                        await Shot(prefix + "_bot").ConfigureAwait(true);
                        Press("bot_custom_color");
                        await Shot(prefix + "_bot_appearance").ConfigureAwait(true);
                        Press("Cancel");
                        Packet(Msg.LobbyState, lobby);
                        TestRunner.Assert(menus.FindChildren("*", "CanvasLayer", owned: false).OfType<BotEditor>().Count() == 1, "refresh lost bot dialog");
                        Press("bot_cancel");
                        TestRunner.Assert(MenuNavigation.Key(GetViewport().GuiGetFocusOwner()) ==
                            (count == 16 ? $"bot_{members[^1].Id}" : "add_bot"), "bot close lost its focus return target");
                        Packet(Msg.LobbyState, lobby with { Mode = new ModeSettings(Modes.ModeRegistry.KingId, 2) });
                        await Shot(prefix + "_king").ConfigureAwait(true);
                    }
                }
            }

            menus.Show(Menus.Screen.Connect);
            var fields = menus.FindChildren("*", "LineEdit", owned: false).OfType<LineEdit>().ToArray();
            fields[0].Text = "Connect draft";
            fields[0].CaretColumn = 4;
            fields[1].Text = "unsubmitted.example:24680";
            fields[1].GrabFocus();
            Packet(Msg.Notice, new TextMessage("Refresh while entering a server"));
            var refreshed = menus.FindChildren("*", "LineEdit", owned: false).OfType<LineEdit>().ToArray();
            TestRunner.Assert(refreshed[0].Text == "Connect draft" && refreshed[0].CaretColumn == 4 &&
                refreshed[1].Text == "unsubmitted.example:24680" && GetViewport().GuiGetFocusOwner() == refreshed[1],
                "connect refresh lost drafts, name caret or server focus");
            GD.Print("[menu composition] PASS screen matrix, drafts, caret, focus and overlay ownership");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[menu composition] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private void Packet(Msg kind, object payload) => net.Handle(MessageCodec.Encode(new NetMessage(kind, 0, 0, payload), true));

    private void Press(string key) => menus.FindChildren("*", "Button", owned: false).OfType<Button>()
        .Single(b => b.IsVisibleInTree() && !b.Disabled && MenuNavigation.Key(b) == key).EmitSignal(BaseButton.SignalName.Pressed);

    private async Task Shot(string name)
    {
        for (var i = 0; i < 12; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        capture.Screenshot(name);
    }
}
