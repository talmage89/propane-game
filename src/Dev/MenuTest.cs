using System.Linq;
using Godot;

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

    public override void _Ready()
    {
        DevArgs.Setup();
        // A fresh settings file, so the run neither reads nor changes the player's name, color and server.
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath("user://menu_test_settings.cfg"));
        Core.PlayerSettings.UseFile("user://menu_test_settings.cfg");
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        main = new Main { Name = "Main" };
        AddChild(main);
        var director = new CaptureDirector { QuitAfter = 40f };
        AddChild(director);
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
        director.At(22f, () => Press("Single player"));
        director.ShotAt(26f, "15_single_player");
        director.At(26.5f, () => SendKey(Key.Escape));
        director.ShotAt(27f, "16_pause");
        director.At(27.5f, () => Press("Main menu"));
        director.ShotAt(28.5f, "17_back_to_main");
        director.At(29f, () => GD.Print($"[menutest] done; main menu showing: {FindButton(GetTree().Root, "Single player") != null}"));
    }

    private void Press(string text)
    {
        var button = FindButton(GetTree().Root, text);
        GD.Print($"[menutest] press {text}: {(button != null ? "found" : "MISSING")}");
        button?.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private static Button? FindButton(Node root, string text) =>
        root.FindChildren("*", "Button", owned: false).OfType<Button>().FirstOrDefault(b => b.Text == text && b.IsVisibleInTree() && !b.Disabled);

    /// <summary>Clicks the n-th color swatch in the lobby (the buttons with no text and a tooltip).</summary>
    private void ClickSwatch(int index)
    {
        var swatches = GetTree().Root.FindChildren("*", "Button", owned: false).OfType<Button>()
            .Where(b => b.Text.Length == 0 && b.TooltipText.Length > 0 && b.IsVisibleInTree()).ToList();
        GD.Print($"[menutest] {swatches.Count} swatches");
        if (index < swatches.Count)
        {
            swatches[index].EmitSignal(BaseButton.SignalName.Pressed);
        }
    }

    private static void SendKey(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = false });
    }
}
