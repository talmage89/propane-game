using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Net;
using Propane.UI;

namespace Propane.Dev;

/// <summary>
/// <c>menu_test --scenario=bots</c>: adds, edits and removes lobby bots with the keyboard, then with a controller,
/// through the real lobby and bot dialog. A second client checks that others see the bots, and fills the lobby while
/// the dialog is open, so the server's refusal reaches the dialog.
/// </summary>
public partial class MenuGamepadTest
{
    private BotEditor? Editor => Main.FindChildren("*", "CanvasLayer", owned: false).OfType<BotEditor>().SingleOrDefault();
    private PlayerProfile? MyBot(string name) => Net.Lobby?.Members.SingleOrDefault(m => m.Kind == ActorKind.Bot && m.OwnerPeerId == Net.MyId && m.Name == name);

    private async Task Bots()
    {
        await Frames().ConfigureAwait(true);
        CheckFocus("Single player");
        await Press(JoyButton.DpadDown).ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Navigate("Host on this computer").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Browser, "host browser opened").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby, "created lobby").ConfigureAwait(true);
        var watcher = new NetClient { Name = "BotWatcher" };
        AddChild(watcher);
        watcher.Connect("127.0.0.1", Menus.HostPort, "Watcher", AppearanceRecord.FromPalette(6));
        await Wait(() => watcher.State == NetClient.Status.Connected && watcher.Lobbies.Count == 1, "watcher sees the lobby").ConfigureAwait(true);
        watcher.JoinLobby(watcher.Lobbies[0].Id);
        await Wait(() => Net.Lobby!.Members.Count == 2, "watcher joined").ConfigureAwait(true);

        // Keyboard: Tab to Add bot, Enter, rename, pick a preset and Hard, save.
        await KeyFocus("add_bot").ConfigureAwait(true);
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        await Wait(() => Editor != null, "Enter opens the bot dialog").ConfigureAwait(true);
        CheckFocus("bot_name");
        var field = (LineEdit)Focus;
        Check(field.Text == "Bot 1" && Editor!.Difficulty == BotDifficulty.Normal, "dialog offers the first free default name at Normal");
        await Shot("01_add_dialog").ConfigureAwait(true);
        await Type("Keyboard Kid", erase: field.Text.Length).ConfigureAwait(true);
        Check(field.Text == "Keyboard Kid", $"typing renames the bot: {field.Text}");
        await KeyFocus("bot_color_3").ConfigureAwait(true);
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        Check(Editor!.Appearance == AppearanceRecord.FromPalette(3), "Enter picks a preset color");
        await KeyFocus("difficulty_hard").ConfigureAwait(true);
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        Check(Editor!.Difficulty == BotDifficulty.Hard, "Enter picks Hard");
        await KeyFocus("bot_save").ConfigureAwait(true);
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        await Wait(() => Editor == null && MyBot("Keyboard Kid") is { } bot && bot.ControllerConfig.Difficulty == 2 &&
            bot.Appearance == AppearanceRecord.FromPalette(3), "keyboard bot accepted").ConfigureAwait(true);
        CheckFocus("add_bot");
        var kid = MyBot("Keyboard Kid")!;
        await Wait(() => watcher.Lobby?.Members.Any(m => m.Id == kid.Id && m.Kind == ActorKind.Bot && m.OwnerPeerId == Net.MyId) == true,
            "another member sees the bot").ConfigureAwait(true);
        Check(Labels().Contains("Keyboard Kid  (Hard bot)"), "lobby labels the bot and its difficulty");
        Check(!Main.FindChildren("*", "Button", owned: false).OfType<Button>().Any(b => b.IsVisibleInTree() && MenuNavigation.Key(b) == $"bot_{Net.MyId}"),
            "the player row has no bot controls");

        // Keyboard edit, Esc cancel and remove.
        await KeyFocus($"bot_{kid.Id}").ConfigureAwait(true);
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        await Wait(() => Editor != null, "Enter opens the edit dialog").ConfigureAwait(true);
        Check(Labels().Contains("EDIT BOT") && ((LineEdit)Focus).Text == "Keyboard Kid" && Editor!.Difficulty == BotDifficulty.Hard,
            "edit dialog starts from the bot's profile");
        await KeyFocus("difficulty_easy").ConfigureAwait(true);
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        await Shot("02_edit_dialog").ConfigureAwait(true);
        await KeyFocus("bot_save").ConfigureAwait(true);
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        await Wait(() => Editor == null && MyBot("Keyboard Kid")?.ControllerConfig.Difficulty == 0, "keyboard edit accepted").ConfigureAwait(true);
        CheckFocus($"bot_{kid.Id}");
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        await Wait(() => Editor != null, "edit dialog reopened").ConfigureAwait(true);
        await Type("Changed", erase: 20).ConfigureAwait(true);
        await Tap(Godot.Key.Escape).ConfigureAwait(true);
        await Wait(() => Editor == null, "Esc cancels the dialog").ConfigureAwait(true);
        Check(MyBot("Keyboard Kid") != null && MyBot("Changed") == null, "a canceled edit changes nothing");
        CheckFocus($"bot_{kid.Id}");
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        await Wait(() => Editor != null, "edit dialog reopened for removal").ConfigureAwait(true);
        await KeyFocus("bot_remove").ConfigureAwait(true);
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        await Wait(() => Editor == null && MyBot("Keyboard Kid") == null && watcher.Lobby!.Members.Count == 2, "keyboard removal").ConfigureAwait(true);

        // Controller: add with the appearance picker, edit, cancel with B, remove.
        await Navigate("add_bot").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Editor != null, "A opens the bot dialog").ConfigureAwait(true);
        CheckFocus("bot_name");
        await Press(JoyButton.A).ConfigureAwait(true);
        Check(MenuNavigation.Key(Focus) != "bot_name", "A accepts the default name and moves on");
        await Navigate("bot_custom_color").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Main.FindChildren("*", "CanvasLayer", owned: false).OfType<AppearancePicker>().Any(), "custom color opens the picker").ConfigureAwait(true);
        Check(Labels().Contains("BOT COLOR"), "the picker names the bot's color");
        await Navigate("preset_sky").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Navigate("Use color").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => !Main.FindChildren("*", "CanvasLayer", owned: false).OfType<AppearancePicker>().Any(), "picker closed").ConfigureAwait(true);
        var sky = AppearanceRecord.FromColor(Color.FromHtml("48AEDD"));
        Check(Editor!.Appearance == sky, "the picker's color is the bot's");
        CheckFocus("bot_custom_color");
        await Navigate("difficulty_hard").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Navigate("bot_save").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Editor == null && MyBot("Bot 1") is { } bot && bot.Appearance == sky && bot.ControllerConfig.Difficulty == 2,
            "controller bot accepted").ConfigureAwait(true);
        CheckFocus("add_bot");
        CheckSample(Menus, MyBot("Bot 1")!.Id, sky, "lobby renders the bot's custom color");
        await Shot("03_lobby_bots").ConfigureAwait(true);
        var pad = MyBot("Bot 1")!;
        await Navigate($"bot_{pad.Id}").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Editor != null, "A opens the edit dialog").ConfigureAwait(true);
        await Navigate("difficulty_normal").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Navigate("bot_save").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Editor == null && MyBot("Bot 1")?.ControllerConfig.Difficulty == 1, "controller edit accepted").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Editor != null, "edit dialog reopened").ConfigureAwait(true);
        await Press(JoyButton.B).ConfigureAwait(true);
        await Wait(() => Editor == null, "B cancels the dialog").ConfigureAwait(true);
        CheckFocus($"bot_{pad.Id}");
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Editor != null, "edit dialog reopened for removal").ConfigureAwait(true);
        await Navigate("bot_remove").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Editor == null && MyBot("Bot 1") == null, "controller removal").ConfigureAwait(true);

        // Refusals: a duplicate name stays in the dialog; a lobby that fills while it is open refuses cleanly.
        await Navigate("add_bot").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Editor != null, "dialog for refusals").ConfigureAwait(true);
        await Type("watcher", erase: 20).ConfigureAwait(true);
        await KeyFocus("bot_save").ConfigureAwait(true);
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        Check(Editor != null && Editor.Message.Contains("already called", StringComparison.Ordinal), "duplicate name is refused in the dialog");
        await KeyFocus("bot_name").ConfigureAwait(true);
        await Type("Late bot", erase: 20).ConfigureAwait(true);
        Net.SetCapacity(3);
        await Wait(() => Net.Lobby!.Capacity == 3, "capacity lowered").ConfigureAwait(true);
        var crowd = new NetClient { Name = "BotCrowd" };
        AddChild(crowd);
        crowd.Connect("127.0.0.1", Menus.HostPort, "Crowd", AppearanceRecord.FromPalette(2));
        await Wait(() => crowd.State == NetClient.Status.Connected && crowd.Lobbies.Count == 1, "crowd sees the lobby").ConfigureAwait(true);
        crowd.JoinLobby(crowd.Lobbies[0].Id);
        await Wait(() => Net.Lobby!.Members.Count == 3, "the lobby filled").ConfigureAwait(true);
        await KeyFocus("bot_save").ConfigureAwait(true);
        await Tap(Godot.Key.Enter).ConfigureAwait(true);
        await Wait(() => Editor?.Message == "That lobby is full.", "the server's refusal reaches the dialog").ConfigureAwait(true);
        await Shot("04_lobby_full").ConfigureAwait(true);
        await Tap(Godot.Key.Escape).ConfigureAwait(true);
        await Wait(() => Editor == null, "refused dialog closes").ConfigureAwait(true);
        Check(!Reachable().ContainsKey("add_bot"), "a full lobby disables Add bot");
        crowd.LeaveLobby();
        watcher.LeaveLobby();
        await Wait(() => Net.Lobby!.Members.Count == 1, "fixtures left").ConfigureAwait(true);
        crowd.Disconnect();
        watcher.Disconnect();

        // A full lobby of bots keeps every row and control on a 720p screen.
        Net.SetCapacity(Protocol.TestedActorCeiling);
        await Wait(() => Net.Lobby!.Capacity == Protocol.TestedActorCeiling, "capacity raised").ConfigureAwait(true);
        for (var i = 1; i < Protocol.TestedActorCeiling; i++)
        {
            var revision = Net.RosterRevision;
            Net.AddActor(ActorKind.Bot, $"Filler {i}", AppearanceRecord.FromPalette(i), ControllerConfig.ForBot((byte)(i % 3)));
            await Wait(() => Net.RosterRevision > revision, "filler bot added").ConfigureAwait(true);
        }
        Check(Net.Lobby!.Members.Count == Protocol.TestedActorCeiling && !Reachable().ContainsKey("add_bot"), "sixteen actors fill the lobby");
        var screen = GetViewport().GetVisibleRect();
        var panel = Menus.FindChildren("*", "PanelContainer", owned: false).OfType<PanelContainer>().First(p => p.IsVisibleInTree());
        Check(screen.Encloses(panel.GetGlobalRect()) || DisplayServer.GetName() == "headless" && panel.Size.Y <= 720,
            $"a full lobby of bots fits the screen: panel {panel.GetGlobalRect()} in {screen}");
        await Navigate($"bot_{Net.Lobby.Members.Last().Id}").ConfigureAwait(true);
        await Shot("05_lobby_full_of_bots").ConfigureAwait(true);
    }

    /// <summary>Moves keyboard focus with Tab until it reaches a control, as a keyboard player would.</summary>
    private async Task KeyFocus(string key)
    {
        for (var i = 0; i < 80 && MenuNavigation.Key(Focus) != key; i++)
        {
            await Tap(Godot.Key.Tab).ConfigureAwait(true);
        }
        CheckFocus(key);
    }

    private async Task Tap(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        await Frames(1).ConfigureAwait(true);
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
        await Frames().ConfigureAwait(true);
    }

    /// <summary>Moves to the end with End, erases characters with Backspace, then types text into the focused field.</summary>
    private async Task Type(string text, int erase)
    {
        Check(Focus is LineEdit, "typing needs a focused field");
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.End, PhysicalKeycode = Godot.Key.End, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.End, PhysicalKeycode = Godot.Key.End, Pressed = false });
        for (var i = 0; i < erase; i++)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.Backspace, PhysicalKeycode = Godot.Key.Backspace, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.Backspace, PhysicalKeycode = Godot.Key.Backspace, Pressed = false });
        }
        foreach (var c in text)
        {
            var key = (Key)char.ToUpperInvariant(c);
            Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = c, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = c, Pressed = false });
        }
        await Frames().ConfigureAwait(true);
    }
}
