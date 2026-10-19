using System.Linq;
using Godot;
using Propane.Core;
using Propane.ReleaseSmoke;

namespace Propane.Dev;

/// <summary>
/// Clicks through the real front end and captures each screen: the main menu, multiplayer, hosting on this
/// computer, the lobby browser and lobby, a solo match (debug builds may start alone) with its Esc menu, leaving it,
/// and single player with its pause menu and the way back to the main menu.
/// Run: godot --resolution 1600x900 res://scenes/dev/menu_test.tscn -- --capture-dir=/tmp/m
/// <c>--scenario=update</c> instead shows the offer of a newer release and of the download a newer server asks for.
/// Pretend to be old and point it at a newer server: <c>-- --scenario=update --update-check --fake-version=0.0.9
/// --server-address=127.0.0.1:24993</c>, with a server started with <c>-- --server --port=24993 --fake-version=9.9.0</c>.
/// </summary>
public partial class MenuTest : Node
{
    private Main main = null!;

    private readonly PlayerSettings settings = new("user://menu_test_settings.cfg");
    private System.IDisposable? settingsBinding;

    public override void _Ready()
    {
        DevArgs.MarkReady();
        DevArgs.Setup();
        // A fresh settings file, so the run neither reads nor changes the player's name, color and server.
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath("user://menu_test_settings.cfg"));
        settingsBinding = PlayerSettings.Bind(settings);
        settings.Name = "Player";
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        main = new Main { Name = "Main" };
        AddChild(main);
        main.GetNode<UI.Menus>("Menus").HostPort = (int)DevArgs.GetFloat("port", Net.Protocol.DefaultPort);
        if (DevArgs.Get("scenario") == "retirement")
        {
            AddChild(new MenuRetirementTest { Main = main });
            return;
        }
        if (DevArgs.Get("scenario") == "input")
        {
            AddChild(new MenuDeviceInputTest { Main = main });
            return;
        }
        if (DevArgs.Get("scenario") == "lifetime")
        {
            AddChild(new MenuLifetimeTest { Main = main });
            return;
        }
        if (DevArgs.Get("scenario") is "gamepad" or "devices" or "mouse" or "bots" or "king" or "lobby")
        {
            AddChild(new MenuGamepadTest { Main = main });
            return;
        }
        var director = new CaptureDirector { QuitAfter = 40f };
        AddChild(director);
        if (DevArgs.Get("scenario") == "rejection")
        {
            director.QuitAfter = 8f;
            director.At(1f, () => Press("Multiplayer"));
            director.At(2f, () => main.GetNode<UI.Menus>("Menus").ConnectTo(
                DevArgs.Get("server-address") ?? "127.0.0.1:24993", "Tester"));
            director.ShotAt(5f, "01_version_rejected");
            director.At(6f, () =>
            {
                var client = main.GetNode<Net.NetClient>("Net");
                var detail = client.LastDisconnect?.Detail;
                var expected = $"This server runs Propane 9.9.x; you have {Net.BuildInfo.GameVersion}. Update Propane to play here.";
                var passed = client.State == Net.NetClient.Status.Offline && client.LastDisconnect?.Kind == Net.DisconnectKind.ApplicationRejected &&
                    client.RejectedBy == "9.9.0" && detail == expected && HasText(GetTree().Root, expected) &&
                    FindButton(GetTree().Root, "Download Propane 9.9.x") != null;
                GD.Print($"[menutest] version rejection UI: {passed}, reason: {detail}");
                if (!passed) { ShutdownGuard.Quit(GetTree(), 1); }
            });
            return;
        }
        if (DevArgs.Get("scenario") == "update")
        {
            director.QuitAfter = 8f;
            director.ShotAt(4f, "01_update_offered");
            director.At(4.5f, () => Press("Multiplayer"));
            director.At(5f, () => main.GetNode<UI.Menus>("Menus").ConnectTo(DevArgs.Get("server-address") ?? "127.0.0.1:24993", "Tester"));
            director.ShotAt(6.5f, "02_server_needs_update");
            director.At(7f, () => GD.Print($"[menutest] download offered: {FindButton(GetTree().Root, "Download Propane 9.9.x") != null}"));
            return;
        }

        if (DevArgs.Get("quit-after") == "single")
        {
            director.ShotAt(1.5f, "01_main_menu");
            SinglePlayer(director, 2f, quit: true);
            return;
        }

        director.ShotAt(1.5f, "01_main_menu");
        director.At(2f, () => Press("Multiplayer"));
        director.ShotAt(2.5f, "02_connect");
        director.At(3f, () => Press("Connect"));
        director.ShotAt(3.3f, "03_connect_empty_address");
        director.At(3.6f, () => Press("Host on this computer"));
        director.ShotAt(4.6f, "04_browser");
        director.At(5f, () => Press("Create lobby"));
        director.ShotAt(5.6f, "05_lobby");
        director.At(6f, () => Press("+"));
        director.At(6.3f, () => ClickSwatch(4));
        director.ShotAt(7f, "06_lobby_changed");
        director.At(7.3f, () => Press("Start match"));
        director.ShotAt(7.5f, "07_loading");
        director.ShotAt(9.5f, "08_materialize");
        director.ShotAt(12f, "09_countdown");
        director.ShotAt(15.5f, "10_playing");
        director.At(16f, () => SendKey(Key.Escape));
        director.ShotAt(16.5f, "11_match_menu");
        director.At(17f, () => SendKey(Key.Escape));
        director.At(17.3f, () => SendKey(Key.F1));
        director.ShotAt(17.8f, "12_tuning_panel");
        director.At(18.2f, () => SendKey(Key.F1));
        director.At(18.5f, () => SendKey(Key.Escape));
        director.At(19f, () => Press("Leave match"));
        director.ShotAt(20f, "13_after_leaving");
        director.At(20.5f, () => Press("Disconnect"));
        director.ShotAt(21f, "14_connect_again");
        director.At(21.5f, () => Press("Back"));
        if (DevArgs.Get("quit-after") == "match")
        {
            director.ShotAt(21.8f, "15_back_to_main_before_quit");
            QuitFromMenu(director, 22f);
            return;
        }
        SinglePlayer(director, 22f);
    }

    private void SinglePlayer(CaptureDirector director, float start, bool quit = false)
    {
        director.At(start, () => Press("Single player"));
        director.ShotAt(start + 4f, "15_single_player");
        director.At(start + 4.5f, () => SendKey(Key.Escape));
        director.ShotAt(start + 5f, "16_pause");
        director.At(start + 5.5f, () => Press("Main menu"));
        director.ShotAt(start + 6.5f, "17_back_to_main");
        director.At(start + 7f, () =>
        {
            if (FindButton(GetTree().Root, "Single player") == null)
            {
                GD.PushError("[menutest] Did not return to the main menu");
                ShutdownGuard.Quit(GetTree(), 1);
                return;
            }
            GD.Print("[menutest] PASS: returned to main menu");
        });
        if (quit) { QuitFromMenu(director, start + 8f); }
    }

    private void QuitFromMenu(CaptureDirector director, float time)
    {
        director.At(time, () => Press("Quit"));
        director.At(time + 2f, () =>
        {
            GD.PushError("[menutest] FAIL main-menu Quit did not finish shutdown");
            ShutdownGuard.Quit(GetTree(), 1);
        });
    }

    private static bool HasText(Node node, string text)
    {
        if (node is Label label && label.Text == text) { return true; }
        return node.GetChildren().Any(child => HasText(child, text));
    }

    private void Press(string text)
    {
        var button = FindButton(GetTree().Root, text);
        GD.Print($"[menutest] press {text}: {(button != null ? "found" : "MISSING")}");
        if (button == null)
        {
            GD.PushError($"[menutest] Missing button: {text}");
            ShutdownGuard.Quit(GetTree(), 1);
            return;
        }
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private static Button? FindButton(Node root, string text) =>
        root.FindChildren("*", "Button", owned: false).OfType<Button>().FirstOrDefault(b => b.Text == text && b.IsVisibleInTree() && !b.Disabled);

    /// <summary>Clicks the n-th color swatch in the lobby (the buttons with no text and a tooltip).</summary>
    private void ClickSwatch(int index)
    {
        var swatches = GetTree().Root.FindChildren("*", "Button", owned: false).OfType<Button>()
            .Where(b => b.Text.Length == 0 && b.TooltipText.Length > 0 && b.IsVisibleInTree()).ToList();
        GD.Print($"[menutest] {swatches.Count} swatches");
        if (index >= swatches.Count)
        {
            GD.PushError($"[menutest] Missing swatch {index}");
            ShutdownGuard.Quit(GetTree(), 1);
            return;
        }
        swatches[index].EmitSignal(BaseButton.SignalName.Pressed);
    }

    private static void SendKey(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = false });
    }
    public override void _ExitTree()
    {
        settingsBinding?.Dispose();
        settingsBinding = null;
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete) return;
        settingsBinding?.Dispose();
        settings.Dispose();
    }
}
