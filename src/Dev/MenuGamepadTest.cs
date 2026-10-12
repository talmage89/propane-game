using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;
using Propane.UI;

namespace Propane.Dev;

/// <summary>
/// Asserts controller menus and device prompts using injected input; the gamepad scenario uses joypad events only.
/// Every wait and assertion fails with a nonzero exit.
/// </summary>
public partial class MenuGamepadTest : Node
{
    public required Main Main { get; init; }
    private CaptureDirector capture = null!;
    private int assertions;
    private Menus Menus => Main.GetNode<Menus>("Menus");
    private NetClient Net => Main.GetNode<NetClient>("Net");
    private Control Focus => GetViewport().GuiGetFocusOwner() ?? throw new InvalidOperationException("Focus lost");

    public override async void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        capture = new CaptureDirector();
        AddChild(capture);
        try
        {
            if (DevArgs.Get("scenario") == "mouse")
            {
                await MouseAppearance().ConfigureAwait(true);
            }
            else if (DevArgs.Get("scenario") == "devices")
            {
                await Devices().ConfigureAwait(true);
            }
            else
            {
                await Run().ConfigureAwait(true);
            }
            GD.Print($"[gamepadmenu] PASS: {assertions} assertions");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception e)
        {
            GD.PushError($"[gamepadmenu] FAIL: {e}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private async Task Run()
    {
        await Frames().ConfigureAwait(true);
        CheckFocus("Single player");
        await Shot("01_main").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Main.HasNode("Game"), "single player opened").ConfigureAwait(true);
        CheckMouse(Input.MouseModeEnum.Captured);
        await Press(JoyButton.Start).ConfigureAwait(true);
        await Wait(() => Main.HasNode("Game") && Main.GetNode<Game>("Game").World.Paused, "Start pauses single player").ConfigureAwait(true);
        Check(!GetTree().Paused, "world pause leaves application processing active");
        CheckFocus("Resume");
        Check(Labels().Contains("Left stick"), "pause shows gamepad controls");
        await Shot("02_pause").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        Check(!(Main.HasNode("Game") && Main.GetNode<Game>("Game").World.Paused), "A confirms Resume");
        CheckMouse(Input.MouseModeEnum.Captured);
        await Press(JoyButton.Start).ConfigureAwait(true);
        await Press(JoyButton.B).ConfigureAwait(true);
        Check(!(Main.HasNode("Game") && Main.GetNode<Game>("Game").World.Paused), "B resumes single player");
        await Press(JoyButton.Start).ConfigureAwait(true);
        await Press(JoyButton.Start).ConfigureAwait(true);
        Check(!(Main.HasNode("Game") && Main.GetNode<Game>("Game").World.Paused), "Start closes pause");
        await Press(JoyButton.Start).ConfigureAwait(true);
        await Navigate("Main menu").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Main, "return to main").ConfigureAwait(true);
        CheckFocus("Single player");
        await Press(JoyButton.DpadDown).ConfigureAwait(true);
        CheckFocus("Multiplayer");
        await Press(JoyButton.A).ConfigureAwait(true);
        CheckFocus("Name");
        await Shot("03_connect_name").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        Check(Focus is LineEdit && MenuNavigation.Key(Focus) != "Name", "A accepts default name and advances to address");
        await Shot("04_connect_address").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        Check(Menus.Current == Menus.Screen.Connect, "empty address keeps connect screen");
        Check(Focus.IsVisibleInTree(), "validation rebuild retains focus");
        await Navigate("Host on this computer").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Browser, "host browser opened").ConfigureAwait(true);
        CheckFocus("Create lobby");
        await Shot("05_browser_empty").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby, "created lobby").ConfigureAwait(true);
        CheckFocus("Name");
        await Shot("06_lobby_name").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        Check(MenuNavigation.Key(Focus).StartsWith("color_", StringComparison.Ordinal), "name confirmation advances to colors");
        var originalColor = Net.Appearance;
        await Navigate($"color_{(originalColor.LegacyIndex + 1) % Protocol.PlayerColors.Length}").ConfigureAwait(true);
        var colorKey = MenuNavigation.Key(Focus);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Members.Single(m => m.OwnerPeerId == Net.MyId && m.Kind == ActorKind.Human).Appearance != originalColor, "preset update acknowledged").ConfigureAwait(true);
        CheckFocus(colorKey);
        // Cancel is transactional; a separate Apply sends/persists the exact preview bytes.
        await Navigate("custom_color").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        CheckFocus("preset_coral");
        Check(Reachable().Keys.Count(k => k.StartsWith("preset_", StringComparison.Ordinal)) == 12, "all twelve presets are reachable");
        await Navigate("hsv_hue").ConfigureAwait(true);
        var before = Net.Appearance;
        await Press(JoyButton.DpadLeft).ConfigureAwait(true);
        await Press(JoyButton.B).ConfigureAwait(true);
        Check(Net.Appearance == before && PlayerSettings.Appearance == before, "cancel leaves accepted/saved appearance alone");
        CheckFocus("custom_color");
        await Press(JoyButton.A).ConfigureAwait(true);
        await Navigate("preset_sky").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        var picker = Picker;
        Check(picker.Selected == AppearanceRecord.FromColor(Color.FromHtml("48AEDD")), "A selects the sky preset exactly");
        await Shot("07_preset_sky").ConfigureAwait(true);
        await Navigate("hsv_hue").ConfigureAwait(true);
        var hue = ColorSlider("hsv_hue");
        var saturation = ColorSlider("hsv_saturation");
        var brightness = ColorSlider("hsv_value");
        var previousHue = hue.Value;
        await Press(JoyButton.DpadRight).ConfigureAwait(true);
        Check(Mathf.IsEqualApprox(hue.Value, previousHue + 1f / 360), "D-pad adjusts hue by one degree");
        // Sustained stick accelerates, neutral stops it, and a new tap returns to fine adjustment.
        previousHue = hue.Value;
        Input.ParseInputEvent(new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = -1 });
        await ToSignal(GetTree().CreateTimer(1.7), SceneTreeTimer.SignalName.Timeout);
        await Axis(JoyAxis.LeftX, 0).ConfigureAwait(true);
        Check(previousHue - hue.Value > 20f / 360, "held stick accelerates hue movement");
        var stoppedHue = hue.Value;
        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
        Check(hue.Value == stoppedHue, "neutral stops HSV repeat");
        await Press(JoyButton.DpadRight).ConfigureAwait(true);
        Check(Mathf.IsEqualApprox(hue.Value, stoppedHue + 1f / 360), "release resets fine adjustment");
        await Press(JoyButton.DpadDown).ConfigureAwait(true);
        CheckFocus("hsv_saturation");
        var previousSaturation = saturation.Value;
        await Axis(JoyAxis.LeftX, -1).ConfigureAwait(true);
        Check(Mathf.IsEqualApprox(saturation.Value, previousSaturation - 0.01f), "stick adjusts saturation");
        await Press(JoyButton.DpadDown).ConfigureAwait(true);
        CheckFocus("hsv_value");
        var previousValue = brightness.Value;
        await Press(JoyButton.DpadLeft).ConfigureAwait(true);
        Check(Mathf.IsEqualApprox(brightness.Value, previousValue - 0.01f), "D-pad adjusts brightness");
        var selected = AppearanceRecord.FromColor(Color.FromHsv(hue.Value, saturation.Value, brightness.Value));
        Check(picker.Selected == selected && selected != before, "HSV adjustments produce the expected sRGB bytes");
        CheckPreview(selected);
        await Shot("07_custom_color_preview").ConfigureAwait(true);
        await Navigate("Use color").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Members.Single(m => m.OwnerPeerId == Net.MyId && m.Kind == ActorKind.Human).Appearance == selected, "custom color acknowledged").ConfigureAwait(true);
        CheckSaved(selected, "gamepad HSV record saved on disk");
        CheckSample(Menus, Net.Lobby!.Members.First(m => m.OwnerPeerId == Net.MyId).Id, selected, "lobby renders custom color");
        CheckFocus("custom_color");
        await Shot("07_lobby_color").ConfigureAwait(true);
        await Navigate("match_length").ConfigureAwait(true);
        var length = Net.Lobby!.MatchLength;
        await Axis(JoyAxis.LeftX, 1).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.MatchLength == length + Tuning.SavedTemplate.MatchLengthStep, "stick right increases match length").ConfigureAwait(true);
        CheckFocus("match_length");
        await Press(JoyButton.DpadLeft).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.MatchLength == length, "D-pad left decreases match length").ConfigureAwait(true);
        CheckFocus("match_length");
        while (Net.Lobby!.MatchLength > Tuning.SavedTemplate.MatchLengthMin)
        {
            var shorter = Mathf.Max(Tuning.SavedTemplate.MatchLengthMin, Net.Lobby.MatchLength - Tuning.SavedTemplate.MatchLengthStep);
            await Press(JoyButton.DpadLeft).ConfigureAwait(true);
            await Wait(() => Net.Lobby!.MatchLength == shorter, "shorter length acknowledged").ConfigureAwait(true);
        }
        await Press(JoyButton.DpadLeft).ConfigureAwait(true);
        Check(Net.Lobby!.MatchLength == Tuning.SavedTemplate.MatchLengthMin, "length clamps at minimum");
        CheckFocus("match_length");
        Check(!Reachable().ContainsKey("−"), "disabled shorter button is skipped");
        await Shot("08_lobby_length").ConfigureAwait(true);

        // Exercise a populated browser and a Join control using an independent fixture client, not UI shortcuts.
        var extraLobbies = new List<NetClient>();
        for (var i = 0; i < 4; i++)
        {
            var extra = new NetClient();
            AddChild(extra);
            extra.Connect("127.0.0.1", Menus.HostPort, $"Fixture {i + 1}", 0);
            await Wait(() => extra.State == NetClient.Status.Connected, "extra browser fixture connected").ConfigureAwait(true);
            extra.CreateLobby();
            await Wait(() => extra.Lobby != null, "extra browser lobby created").ConfigureAwait(true);
            extraLobbies.Add(extra);
        }
        var fixture = new NetClient { Name = "BrowserFixture" };
        AddChild(fixture);
        fixture.Connect("127.0.0.1", Menus.HostPort, "Menu fixture", (Net.Appearance.LegacyIndex + 1) % Protocol.PlayerColors.Length);
        await Wait(() => fixture.State == NetClient.Status.Connected, "fixture connected").ConfigureAwait(true);
        fixture.CreateLobby();
        await Wait(() => fixture.Lobby != null, "fixture lobby created").ConfigureAwait(true);
        await Press(JoyButton.B).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Browser && Net.Lobbies.Any(l => l.Id == fixture.Lobby!.Id), "B leaves lobby for populated browser").ConfigureAwait(true);
        var joinKey = $"lobby_{fixture.Lobby!.Id}";
        await Navigate(joinKey).ConfigureAwait(true);
        var scroll = Menus.FindChildren("*", "ScrollContainer", owned: false).OfType<ScrollContainer>().Single();
        Check(scroll.ScrollVertical > 0 && scroll.GetGlobalRect().Encloses(Focus.GetGlobalRect().Grow(3)), "last lobby scrolls fully into view");
        await Shot("09_browser_join").ConfigureAwait(true);
        // A server list refresh must retain this exact lobby, not another row with the same Join label.
        fixture.SetProfile("Updated fixture", fixture.Appearance);
        await Wait(() => Net.Lobbies.Any(l => l.Id == fixture.Lobby!.Id && l.CreatorName == "Updated fixture"), "browser list refresh acknowledged").ConfigureAwait(true);
        CheckFocus(joinKey);
        scroll = Menus.FindChildren("*", "ScrollContainer", owned: false).OfType<ScrollContainer>().Single();
        Check(scroll.GetGlobalRect().Encloses(Focus.GetGlobalRect().Grow(3)), "list rebuild reveals restored Join focus");
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby && Net.Lobby!.CreatorId == fixture.MyId, "A joins selected lobby").ConfigureAwait(true);
        CheckFocus("Name");
        await Shot("10_guest_lobby").ConfigureAwait(true);
        Check(Enumerable.Range(0, Protocol.PlayerColors.Length).All(i => Reachable().ContainsKey($"color_{i}")),
            "all preset colors remain available with other players in the lobby");
        await Press(JoyButton.B).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Browser, "B leaves guest lobby").ConfigureAwait(true);
        await Navigate(joinKey).ConfigureAwait(true);
        fixture.Disconnect();
        fixture.QueueFree();
        await Wait(() => !Net.Lobbies.Any(l => $"lobby_{l.Id}" == joinKey), "focused lobby removed").ConfigureAwait(true);
        Check(Focus.IsVisibleInTree() && MenuNavigation.Key(Focus) != joinKey, "removed row has a visible focus fallback");
        foreach (var extra in extraLobbies)
        {
            extra.Disconnect();
            extra.QueueFree();
        }
        await Wait(() => Net.Lobbies.Count == 0, "browser fixtures disconnected").ConfigureAwait(true);
        await Navigate("Create lobby").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby, "created solo debug lobby").ConfigureAwait(true);
        await Navigate("Start match").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Main.HasNode("Match"), "solo debug match started").ConfigureAwait(true);
        await Frames(180).ConfigureAwait(true);
        Check(Main.GetNode<Match>("Match").Player.BodyColor == selected.Color, "match character renders custom color");
        await Shot("11_custom_character").ConfigureAwait(true);
        await Press(JoyButton.Start).ConfigureAwait(true);
        await Wait(() => Main.GetNode<MatchMenu>("Match/Menu").Visible, "Start opens match menu").ConfigureAwait(true);
        CheckFocus("Resume");
        Check(!GetTree().Paused, "match keeps running");
        CheckMouse(Input.MouseModeEnum.Visible);
        Check(Labels().Contains("X / Square"), "match shows gamepad reload binding");
        await Shot("11_match_menu").ConfigureAwait(true);
        await Press(JoyButton.Start).ConfigureAwait(true);
        Check(!Main.GetNode<MatchMenu>("Match/Menu").Visible, "Start resumes match");
        CheckMouse(Input.MouseModeEnum.Captured);
        await Press(JoyButton.Start).ConfigureAwait(true);
        await Navigate("Leave match").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Browser && !Main.HasNode("Match"), "leave match returns to browser").ConfigureAwait(true);
        await Shot("12_after_match").ConfigureAwait(true);
        // Drive the real server to results; assertions inspect the actual MatchEnd UI, not a fabricated standing.
        await Navigate("Create lobby").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Net.Lobby != null, "results lobby created").ConfigureAwait(true);
        await Navigate("Start match").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Main.HasNode("Match") && Main.GetNode<Match>("Match").IsPlaying, "results match playing").ConfigureAwait(true);
        Main.GetNode<LobbyServer>("HostedServer").AdvanceMatches(MonotonicClock.Now + 2000);
        await Wait(() => Main.GetNode<Match>("Match").IsOver, "server ended real match").ConfigureAwait(true);
        var actualResults = Main.GetNode<ResultsScreen>("Match/Results");
        CheckSample(actualResults, Main.GetNode<Match>("Match").Player.ActorId, selected, "real results render custom color");
        await Shot("12_custom_results").ConfigureAwait(true);
        Net.LeaveLobby();
        // The match's results timer owns scene cleanup; keep the next independent overlay test isolated.
        await Wait(() => !Main.HasNode("Match"), "results match cleaned up").ConfigureAwait(true);
        await Press(JoyButton.B).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Connect, "B disconnects").ConfigureAwait(true);
        await Press(JoyButton.B).ConfigureAwait(true);
        Check(Menus.Current == Menus.Screen.Main, "B returns to main");

        // Results are server-timed and read-only: prove their focus and that A/B do not invent an early exit.
        Menus.Show(Menus.Screen.None);
        var results = new ResultsScreen();
        AddChild(results);
        results.Show(new[] { new Standing(1, "Player", AppearanceRecord.FromPalette(0), 7, true), new Standing(2, "Other", AppearanceRecord.FromPalette(1), 3, true) }, 1, 10);
        await Frames().ConfigureAwait(true);
        Check(results.IsAncestorOf(Focus), "results own focus");
        await Shot("13_results").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Press(JoyButton.B).ConfigureAwait(true);
        Check(results.Visible && results.IsAncestorOf(Focus), "read-only results retain focus after confirm/cancel");
        var overlay = new MatchMenu();
        AddChild(overlay);
        overlay.ResumeRequested += () => overlay.Visible = false;
        overlay.Visible = true;
        await Frames().ConfigureAwait(true);
        CheckFocus("Resume");
        await Press(JoyButton.B).ConfigureAwait(true);
        Check(results.IsAncestorOf(Focus), "closing an overlay restores results focus");
        overlay.QueueFree();
        results.Show(Array.Empty<Standing>(), 1, 10);
        await Frames().ConfigureAwait(true);
        Check(results.IsAncestorOf(Focus), "results rebuild retains focus");
        results.QueueFree();
        Menus.Show(Menus.Screen.Main);
        await Frames().ConfigureAwait(true);
        CheckFocus("Single player");

        // Held stick repeat and deadzone: one press, then sustained movement, then neutral must stop repeating.
        Input.ParseInputEvent(new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = 0.2f });
        await Frames().ConfigureAwait(true);
        CheckFocus("Single player");
        Input.ParseInputEvent(new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = 1 });
        await Frames().ConfigureAwait(true);
        CheckFocus("Multiplayer");
        await ToSignal(GetTree().CreateTimer(0.46), SceneTreeTimer.SignalName.Timeout);
        CheckFocus("Lab");
        await ToSignal(GetTree().CreateTimer(0.14), SceneTreeTimer.SignalName.Timeout);
        CheckFocus("Quit");
        await Axis(JoyAxis.LeftY, 0).ConfigureAwait(true);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        CheckFocus("Quit");
        await Shot("14_main_repeat").ConfigureAwait(true);
    }

    private void CheckSample(Node screen, int player, AppearanceRecord appearance, string outcome)
    {
        var key = player.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Check(screen.FindChildren("*", "ColorRect", owned: false).OfType<ColorRect>()
            .Any(c => c.HasMeta("appearance_player") && c.GetMeta("appearance_player").AsString() == key && c.Color == appearance.Color), outcome);
    }

    private async Task MouseAppearance()
    {
        await Frames().ConfigureAwait(true);
        await Click("Multiplayer").ConfigureAwait(true);
        await Click("Host on this computer").ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Browser, "mouse opened host browser").ConfigureAwait(true);
        await Click("Create lobby").ConfigureAwait(true);
        await Wait(() => Net.Lobby != null, "mouse created lobby").ConfigureAwait(true);
        await Click("custom_color").ConfigureAwait(true);
        await Click("preset_coral").ConfigureAwait(true);
        Check(Picker.Selected == AppearanceRecord.FromColor(Color.FromHtml("E85D65")), "mouse selects coral preset exactly");
        await Shot("00_mouse_preset_coral").ConfigureAwait(true);
        await MouseColor("hsv_hue", 0.5f).ConfigureAwait(true);
        await MouseColor("hsv_saturation", 0.75f, drag: true).ConfigureAwait(true);
        await MouseColor("hsv_value", 0.8f).ConfigureAwait(true);
        var hsvColor = AppearanceRecord.FromColor(new Color(0.2f, 0.8f, 0.8f));
        Check(Picker.Selected == hsvColor, "mouse HSV controls produce cyan at 75% saturation and 80% brightness");
        CheckPreview(hsvColor);
        await Shot("00_mouse_hsv_cyan").ConfigureAwait(true);
        // Black must retain both hue and saturation, and gray must retain hue.
        await MouseColor("hsv_value", 0).ConfigureAwait(true);
        await MouseColor("hsv_hue", 0.25f).ConfigureAwait(true);
        Check(Picker.Selected == new AppearanceRecord(0, 0, 0), "hue adjustment keeps black black");
        await MouseColor("hsv_value", 0.8f).ConfigureAwait(true);
        Check(Mathf.IsEqualApprox(ColorSlider("hsv_hue").Value, 0.25f) &&
            Mathf.IsEqualApprox(ColorSlider("hsv_saturation").Value, 0.75f), "raising brightness restores hue and saturation");
        await MouseColor("hsv_saturation", 0).ConfigureAwait(true);
        await MouseColor("hsv_hue", 0.5f).ConfigureAwait(true);
        await MouseColor("hsv_saturation", 0.75f).ConfigureAwait(true);
        Check(Picker.Selected == hsvColor, "raising saturation restores hue from gray");
        await Click("Use color").ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Members.Single(m => m.OwnerPeerId == Net.MyId && m.Kind == ActorKind.Human).Appearance == hsvColor, "mouse HSV color acknowledged").ConfigureAwait(true);
        CheckSaved(hsvColor, "mouse HSV record saved on disk");
        await Click("custom_color").ConfigureAwait(true);
        Check(Picker.Selected == hsvColor, "reopened picker restores accepted HSV color");
        var field = Main.FindChildren("*", "LineEdit", owned: false).OfType<LineEdit>().Single(f => f.PlaceholderText == "Hex color (RRGGBB)");
        await MouseClick(field.GetGlobalRect().GetCenter()).ConfigureAwait(true);
        await TypeHex("12ABEF").ConfigureAwait(true);
        var custom = new AppearanceRecord(0x12, 0xab, 0xef);
        var picker = Main.FindChildren("*", "CanvasLayer", owned: false).OfType<AppearancePicker>().Single();
        Check(picker.Selected == custom, "keyboard hex entry changes preview to exact bytes");
        await Shot("01_mouse_custom_preview").ConfigureAwait(true);
        foreach (var hexColor in new[] { "000000", "FFFFFF", "0000FF", "080C10" })
        {
            await TypeHex(hexColor).ConfigureAwait(true);
            Check(picker.Selected.Color.ToHtml(false).ToUpperInvariant() == hexColor, $"extreme preview retains selected bytes: expected {hexColor}, got {picker.Selected.Color.ToHtml(false)}, focus {MenuNavigation.Key(Focus)}");
            await Shot($"01_mouse_preview_{hexColor}").ConfigureAwait(true);
        }
        await TypeHex("12ABEF").ConfigureAwait(true);
        await Click("Use color").ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Members.Single(m => m.OwnerPeerId == Net.MyId && m.Kind == ActorKind.Human).Appearance == custom, "mouse custom color acknowledged").ConfigureAwait(true);
        CheckSample(Menus, Net.Lobby!.Members.First(m => m.OwnerPeerId == Net.MyId).Id, custom, "mouse lobby renders custom color");
        CheckSaved(custom, "mouse hex record saved on disk");
        await Shot("02_mouse_custom_lobby").ConfigureAwait(true);
        await Click("Start match").ConfigureAwait(true);
        await Wait(() => Main.HasNode("Match") && Main.GetNode<Match>("Match").IsPlaying, "mouse solo match playing").ConfigureAwait(true);
        Check(Main.GetNode<Match>("Match").Player.BodyColor == custom.Color, "mouse match character renders custom color");
        await Shot("03_mouse_custom_character").ConfigureAwait(true);
        Main.GetNode<LobbyServer>("HostedServer").AdvanceMatches(MonotonicClock.Now + 2000);
        await Wait(() => Main.GetNode<Match>("Match").IsOver, "mouse real match ended").ConfigureAwait(true);
        CheckSample(Main.GetNode<ResultsScreen>("Match/Results"), Main.GetNode<Match>("Match").Player.ActorId, custom, "mouse results render custom color");
        await Shot("04_mouse_custom_results").ConfigureAwait(true);
    }

    private AppearancePicker Picker => Main.FindChildren("*", "CanvasLayer", owned: false).OfType<AppearancePicker>().Single();

    private HsvColorSlider ColorSlider(string key) => Picker.FindChildren("*", "Control", owned: false)
        .OfType<HsvColorSlider>().Single(s => MenuNavigation.Key(s) == key);

    private void CheckPreview(AppearanceRecord color) => Check(Picker.FindChildren("Preview", "", owned: false).OfType<PlayerCharacter>().Single().BodyColor == color.Color, "live character preview uses selected bytes");

    private void CheckSaved(AppearanceRecord color, string outcome)
    {
        PlayerSettings.UseFile("user://menu_test_settings.cfg");
        Check(PlayerSettings.Appearance == color, outcome);
    }

    private async Task MouseColor(string key, float value, bool drag = false)
    {
        var slider = ColorSlider(key);
        var target = slider.GetGlobalRect().Position + new Vector2(6 + value * (slider.Size.X - 12), slider.Size.Y / 2);
        if (drag)
        {
            var start = slider.GetGlobalRect().GetCenter();
            GetViewport().PushInput(new InputEventMouseButton { Position = start, ButtonIndex = MouseButton.Left, Pressed = true }, true);
            await Frames(1).ConfigureAwait(true);
            GetViewport().PushInput(new InputEventMouseMotion { Position = target, Relative = target - start, ButtonMask = MouseButtonMask.Left }, true);
            await Frames(1).ConfigureAwait(true);
            GetViewport().PushInput(new InputEventMouseButton { Position = target, ButtonIndex = MouseButton.Left, Pressed = false }, true);
            await Frames().ConfigureAwait(true);
        }
        else { await MouseClick(target).ConfigureAwait(true); }
        Check(Mathf.IsEqualApprox(slider.Value, value), $"mouse sets {key} to {value}");
    }

    private async Task TypeHex(string text)
    {
        var field = Main.FindChildren("*", "LineEdit", owned: false).OfType<LineEdit>().Single(f => f.PlaceholderText == "Hex color (RRGGBB)");
        await MouseClick(field.GetGlobalRect().GetCenter()).ConfigureAwait(true);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.A, PhysicalKeycode = Key.A, CtrlPressed = true, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.A, PhysicalKeycode = Key.A, CtrlPressed = true, Pressed = false });
        foreach (var c in text)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = (Key)c, Unicode = c, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = (Key)c, Unicode = c, Pressed = false });
        }
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, Pressed = false });
        await Frames().ConfigureAwait(true);
    }

    private async Task Click(string key)
    {
        var control = Main.FindChildren("*", "Button", owned: false).OfType<Button>()
            .Single(b => b.IsVisibleInTree() && !b.Disabled && MenuNavigation.Key(b) == key);
        await MouseClick(control.GetGlobalRect().GetCenter()).ConfigureAwait(true);
    }

    private async Task MouseClick(Vector2 position)
    {
        GetViewport().PushInput(new InputEventMouseMotion { Position = position, GlobalPosition = position, Relative = Vector2.One }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        await Frames(1).ConfigureAwait(true);
        GetViewport().PushInput(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Frames().ConfigureAwait(true);
    }

    private async Task Devices()
    {
        await Frames().ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Main.HasNode("Game"), "single player opened").ConfigureAwait(true);
        await Press(JoyButton.Start).ConfigureAwait(true);
        Check(Main.HasNode("Game") && Main.GetNode<Game>("Game").World.Paused && Labels().Contains("Left stick"), "gamepad pause reference");
        Check(Labels().Contains("Right stick click") && !Labels().Contains("F / mouse thumb"), "gamepad melee reference");
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Tab, PhysicalKeycode = Key.Tab, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Tab, PhysicalKeycode = Key.Tab, Pressed = false });
        await Frames().ConfigureAwait(true);
        Check(Labels().Contains("W A S D") && !Labels().Contains("Left stick"), "keyboard replaces gamepad reference while paused");
        Check(Labels().Contains("F / mouse thumb") && !Labels().Contains("Right stick click"), "keyboard melee reference");
        await Shot("01_keyboard_pause").ConfigureAwait(true);
        await Press(JoyButton.DpadLeft).ConfigureAwait(true);
        Check(Labels().Contains("Left stick") && !Labels().Contains("W A S D"), "gamepad replaces keyboard reference while paused");
        await Shot("02_gamepad_pause").ConfigureAwait(true);
        Input.ParseInputEvent(new InputEventMouseMotion { Relative = Vector2.One });
        await Frames().ConfigureAwait(true);
        Check(Labels().Contains("W A S D"), "mouse replaces gamepad reference");
        Input.ParseInputEvent(new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = 0.1f });
        await Frames().ConfigureAwait(true);
        Check(!MenuNavigation.Gamepad, "idle drift does not change prompts");
        await Axis(JoyAxis.RightX, 0.3f).ConfigureAwait(true);
        Check(MenuNavigation.Gamepad && Labels().Contains("Left stick"), "right stick activity above the gameplay deadzone changes prompts");
        await Press(JoyButton.B).ConfigureAwait(true);
        Check(!(Main.HasNode("Game") && Main.GetNode<Game>("Game").World.Paused), "B resumes after switching devices");

        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = false });
        await Frames().ConfigureAwait(true);
        Check(Main.HasNode("Game") && Main.GetNode<Game>("Game").World.Paused && Labels().Contains("W A S D"), "Esc still opens keyboard pause");
        var resume = Main.FindChildren("*", "Button", owned: false).OfType<Button>().Single(b => b.Text == "Resume" && b.IsVisibleInTree());
        var position = resume.GetGlobalRect().GetCenter();
        GetViewport().PushInput(new InputEventMouseMotion { Position = position, GlobalPosition = position, Relative = Vector2.One }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        await Frames(1).ConfigureAwait(true);
        GetViewport().PushInput(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Frames().ConfigureAwait(true);
        Check(!(Main.HasNode("Game") && Main.GetNode<Game>("Game").World.Paused), "mouse still clicks Resume");
        await Press(JoyButton.RightShoulder).ConfigureAwait(true);

        var hud = new MatchHud { World = Main.GetNode<Game>("Game").World };
        AddChild(hud);
        var ammo = new AmmoState { Tuning = Tuning.SavedTemplate, Magazine = 0, Reserve = 1 };
        hud.SetAmmo(ammo);
        Check(Labels().Contains("X / SQUARE TO RELOAD"), "gamepad reload hint");
        Input.ParseInputEvent(new InputEventMouseMotion { Relative = Vector2.One });
        await Frames().ConfigureAwait(true);
        hud.SetAmmo(ammo);
        Check(Labels().Contains("R TO RELOAD"), "mouse reload hint");
        await Shot("03_keyboard_reload_hint").ConfigureAwait(true);
        await Press(JoyButton.RightShoulder).ConfigureAwait(true);
        hud.SetAmmo(ammo);
        Check(Labels().Contains("X / SQUARE TO RELOAD"), "gamepad restores reload hint");
        await Shot("04_gamepad_reload_hint").ConfigureAwait(true);
        hud.QueueFree();
        await Press(JoyButton.Start).ConfigureAwait(true);
        await Navigate("Main menu").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Main, "device fixture returned to main").ConfigureAwait(true);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, AltPressed = true, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, AltPressed = true, Pressed = false });
        await Frames().ConfigureAwait(true);
        Check(Menus.Current == Menus.Screen.Main && !Main.HasNode("Game"), "Alt+Enter remains fullscreen instead of confirming a menu button");
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, AltPressed = true, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, AltPressed = true, Pressed = false });
        await Frames().ConfigureAwait(true);
        Input.ParseInputEvent(new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = 1 });
        await Frames().ConfigureAwait(true);
        CheckFocus("Multiplayer");
        Input.Singleton.EmitSignal(Input.SignalName.JoyConnectionChanged, 0, false);
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
        CheckFocus("Multiplayer");
        await Axis(JoyAxis.LeftY, 0).ConfigureAwait(true);
        GetViewport().GuiReleaseFocus();
        await Frames().ConfigureAwait(true);
        CheckFocus("Single player");
    }

    private IEnumerable<string> Labels() => GetTree().Root.FindChildren("*", "Label", owned: false).OfType<Label>()
        .Where(l => l.IsVisibleInTree()).Select(l => l.Text);

    private void CheckMouse(Input.MouseModeEnum expected)
    {
        // Headless DisplayServer has no cursor; the rendered run proves capture and release.
        if (DisplayServer.GetName() != "headless")
        {
            Check(Input.MouseMode == expected, $"mouse mode is {expected}");
        }
    }

    private void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
        assertions++;
    }

    private void CheckFocus(string key) => Check(MenuNavigation.Key(Focus) == key, $"expected focus {key}, got {MenuNavigation.Key(Focus)}");

    private async Task Frames(int count = 3)
    {
        for (var i = 0; i < count; i++) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    }

    private async Task Wait(Func<bool> predicate, string outcome)
    {
        var deadline = Time.GetTicksMsec() + 15000;
        while (!predicate())
        {
            if (Time.GetTicksMsec() >= deadline) { throw new TimeoutException(outcome); }
            await Frames(1).ConfigureAwait(true);
        }
        await Frames().ConfigureAwait(true);
        Check(predicate(), outcome);
    }

    private async Task Press(JoyButton button)
    {
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = true });
        await Frames(1).ConfigureAwait(true);
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = false });
        await Frames().ConfigureAwait(true);
    }

    private async Task Axis(JoyAxis axis, float value)
    {
        Input.ParseInputEvent(new InputEventJoypadMotion { Axis = axis, AxisValue = value });
        await Frames(1).ConfigureAwait(true);
        Input.ParseInputEvent(new InputEventJoypadMotion { Axis = axis, AxisValue = 0 });
        await Frames().ConfigureAwait(true);
    }

    private async Task Shot(string name)
    {
        await Frames().ConfigureAwait(true);
        capture.Screenshot(name);
        await Frames().ConfigureAwait(true);
    }

    private Dictionary<string, List<JoyButton>> Reachable()
    {
        var seen = new HashSet<Control>();
        var paths = new Dictionary<string, List<JoyButton>>();
        var queue = new Queue<(Control Control, List<JoyButton> Path)>();
        queue.Enqueue((Focus, new List<JoyButton>()));
        while (queue.TryDequeue(out var next))
        {
            if (!seen.Add(next.Control)) { continue; }
            paths[MenuNavigation.Key(next.Control)] = next.Path;
            foreach (var (button, neighbor) in new[]
                     {
                         (JoyButton.DpadUp, next.Control.FocusNeighborTop), (JoyButton.DpadDown, next.Control.FocusNeighborBottom),
                         (JoyButton.DpadLeft, next.Control.FocusNeighborLeft), (JoyButton.DpadRight, next.Control.FocusNeighborRight),
                     })
            {
                // Left/right on match length adjusts the value instead of following a neighbor.
                if ((MenuNavigation.Key(next.Control) == "match_length" || next.Control is HSlider or HsvColorSlider) && button is JoyButton.DpadLeft or JoyButton.DpadRight) { continue; }
                var control = next.Control.GetNodeOrNull<Control>(neighbor);
                if (control != null) { queue.Enqueue((control, next.Path.Append(button).ToList())); }
            }
        }
        return paths;
    }

    private async Task Navigate(string key)
    {
        Check(Reachable().TryGetValue(key, out var path), $"focus target {key} is reachable");
        foreach (var button in path!) { await Press(button).ConfigureAwait(true); }
        CheckFocus(key);
    }
}
