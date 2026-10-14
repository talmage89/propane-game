using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Lab;
using Propane.Modes;
using Propane.Net;
using Propane.Session;
using Propane.UI;

namespace Propane.Dev;

public partial class MenuGamepadTest
{
    /// <summary>The real lobby controls, with device events, observed by an independent guest.</summary>
    private async Task KingLobby()
    {
        await Frames().ConfigureAwait(true);
        await Click("Multiplayer").ConfigureAwait(true);
        await Click("Host on this computer").ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Browser, "King browser opened").ConfigureAwait(true);
        await Click("Create lobby").ConfigureAwait(true);
        await Wait(() => Net.Lobby != null, "King lobby created").ConfigureAwait(true);
        Check(Net.Lobby!.Mode == ModeSettings.Default, "Tank Bank is the default");
        var guest = new NetClient();
        AddChild(guest);
        guest.Connect("127.0.0.1", Menus.HostPort, "King guest", 1);
        await Wait(() => guest.State == NetClient.Status.Connected, "King guest connected").ConfigureAwait(true);
        guest.JoinLobby(Net.Lobby.Id);
        await Wait(() => guest.Lobby != null, "King guest joined").ConfigureAwait(true);

        // Tank supply uses the same device-delivery path in either mode, and survives mode changes.
        await KingClick("tank_more").ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.TankSupply == 2 && guest.Lobby!.Mode == Net.Lobby.Mode, "mouse supply shared in Tank Bank").ConfigureAwait(true);
        await KeyFocus("tank_supply").ConfigureAwait(true);
        await Tap(Key.Right).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.TankSupply == 3, "keyboard increases supply").ConfigureAwait(true);
        CheckFocus("tank_supply");
        await Tap(Key.Left).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.TankSupply == 2, "keyboard decreases supply").ConfigureAwait(true);
        await Navigate("tank_supply").ConfigureAwait(true);
        await Axis(JoyAxis.LeftX, -1).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.TankSupply == 1, "stick reaches minimum supply").ConfigureAwait(true);
        await Press(JoyButton.DpadLeft).ConfigureAwait(true);
        Check(Net.Lobby!.Mode.TankSupply == 1 && !Reachable().ContainsKey("tank_less"), "minimum supply clamps and skips disabled minus");
        for (var supply = 2; supply <= 8; supply++)
        {
            await Press(JoyButton.DpadRight).ConfigureAwait(true);
            var expected = supply;
            await Wait(() => Net.Lobby!.Mode.TankSupply == expected && guest.Lobby!.Mode == Net.Lobby.Mode, "supply step shared").ConfigureAwait(true);
            CheckFocus("tank_supply");
        }
        await Press(JoyButton.DpadRight).ConfigureAwait(true);
        Check(Net.Lobby!.Mode.TankSupply == 8 && !Reachable().ContainsKey("tank_more"), "maximum supply clamps and skips disabled plus");
        await Shot("tank_bank_supply").ConfigureAwait(true);

        // Mouse through the root device delivery path, including a rebuild when mint controls appear.
        await KingClick("mode_4").ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.ModeId == ModeRegistry.KingId && guest.Lobby!.Mode == Net.Lobby.Mode, "mouse selected King for both peers").ConfigureAwait(true);
        Check(Net.Lobby!.Mode.TankSupply == 8, "switching to King remembers supply");
        await KingClick("tank_less").ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.TankSupply == 7 && guest.Lobby!.Mode == Net.Lobby.Mode, "King changes shared supply").ConfigureAwait(true);
        await KingClick("mint_faster").ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.MintSeconds == 1.5f && guest.Lobby!.Mode == Net.Lobby.Mode, "mouse pace acknowledged by both peers").ConfigureAwait(true);
        await Shot("king_lobby_mouse").ConfigureAwait(true);

        // Keyboard Tab reaches the value; Left/Right adjusts rather than losing focus.
        await KeyFocus("mint_pace").ConfigureAwait(true);
        await Tap(Key.Right).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.MintSeconds == 2, "keyboard pace increased").ConfigureAwait(true);
        CheckFocus("mint_pace");
        await Tap(Key.Left).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.MintSeconds == 1.5f, "keyboard pace decreased").ConfigureAwait(true);
        await Shot("king_lobby_keyboard").ConfigureAwait(true);

        // Joypad mode choice and stick/D-pad adjustment, with preserved focus through server rebuilds.
        await Navigate("mode_2").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.ModeId == ModeRegistry.TankBankId, "controller selected Tank Bank").ConfigureAwait(true);
        Check(!Reachable().ContainsKey("mint_pace"), "Tank Bank hides King's pace");
        await Navigate("mode_4").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.ModeId == ModeRegistry.KingId, "controller selected King").ConfigureAwait(true);
        Check(Net.Lobby!.Mode.MintSeconds == 1.5f && Net.Lobby.Mode.TankSupply == 7, "switching modes remembers pace and supply");
        await Navigate("mint_pace").ConfigureAwait(true);
        await Axis(JoyAxis.LeftX, -1).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.MintSeconds == 1, "stick changes pace").ConfigureAwait(true);
        await Press(JoyButton.DpadLeft).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.MintSeconds == .5f, "D-pad reaches fastest pace").ConfigureAwait(true);
        await Press(JoyButton.DpadLeft).ConfigureAwait(true);
        Check(Net.Lobby!.Mode.MintSeconds == .5f && !Reachable().ContainsKey("mint_faster"), "fastest pace clamps and disabled control is skipped");
        for (var step = 1; step <= 19; step++)
        {
            await Press(JoyButton.DpadRight).ConfigureAwait(true);
            var expected = .5f + step * .5f;
            await Wait(() => Net.Lobby!.Mode.MintSeconds == expected, "pace step acknowledged").ConfigureAwait(true);
            CheckFocus("mint_pace");
        }
        await Press(JoyButton.DpadRight).ConfigureAwait(true);
        Check(Net.Lobby!.Mode.MintSeconds == 10 && !Reachable().ContainsKey("mint_slower"), "slowest pace clamps and disabled control is skipped");
        await Navigate("match_length").ConfigureAwait(true);
        var length = Net.Lobby!.MatchLength;
        await Press(JoyButton.DpadRight).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.MatchLength == length + Tuning.SavedTemplate.MatchLengthStep && guest.Lobby!.MatchLength == Net.Lobby.MatchLength,
            "King round length shared").ConfigureAwait(true);
        await Shot("king_lobby_gamepad").ConfigureAwait(true);
        Check(guest.Lobby!.Mode == Net.Lobby!.Mode, "guest sees final mode and pace");
        var guestMenus = new Menus { Net = guest };
        AddChild(guestMenus);
        Menus.Show(Menus.Screen.None);
        guestMenus.Show(Menus.Screen.Lobby);
        await Frames().ConfigureAwait(true);
        Check(Labels().Contains("King of the Cul-de-sac") && Labels().Contains("A tank every 10.0 s") && Labels().Contains("7×"), "guest sees King and mint pace");
        Check(!guestMenus.FindChildren("*", "Button", owned: false).OfType<Button>().Any(b => b.IsVisibleInTree() &&
            MenuNavigation.Key(b) is "mode_2" or "mode_4" or "mint_pace" or "tank_supply" or "tank_more" or "tank_less"), "guest has no creator controls");
        await Shot("king_lobby_guest").ConfigureAwait(true);
        guestMenus.QueueFree();
        guest.Disconnect(); guest.QueueFree();
        await Frames().ConfigureAwait(true);
        Menus.Show(Menus.Screen.Lobby);
        Net.LeaveLobby();
        await Wait(() => Menus.Current == Menus.Screen.Browser, "leave supply lobby").ConfigureAwait(true);
        await Click("Disconnect").ConfigureAwait(true);
        await Click("Back").ConfigureAwait(true);
        await Click("Lab").ConfigureAwait(true);
        await Click("Duel vs a banking bot").ConfigureAwait(true);
        Check(!Reachable().ContainsKey("tank_more"), "void fixture has no suburb supply control");
        await KingClick("environment").ConfigureAwait(true);
        await KingClick("tank_more").ConfigureAwait(true);
        Check(Labels().Contains("2×"), "Lab mouse increases supply");
        await KeyFocus("tank_more").ConfigureAwait(true);
        await Tap(Key.Enter).ConfigureAwait(true);
        Check(Labels().Contains("3×"), "Lab keyboard increases supply");
        await Navigate("tank_more").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        Check(Labels().Contains("4×"), "Lab controller increases supply");
        await Shot("lab_bank_supply").ConfigureAwait(true);
        await Click("Back to scenarios").ConfigureAwait(true);
        await Click("King of the Cul-de-sac").ConfigureAwait(true);
        await KingClick("environment").ConfigureAwait(true);
        Check(Labels().Contains("4×"), "Lab draft carries supply to King");
        await Shot("lab_king_supply").ConfigureAwait(true);
        await Click("Back to scenarios").ConfigureAwait(true);
        await Click("Short Fuse").ConfigureAwait(true);
        Check(Labels().Contains("4×"), "Short Fuse has the same draft supply");
        await Shot("lab_shortfuse_supply").ConfigureAwait(true);
        await KingClick("enter").ConfigureAwait(true);
        await Wait(() => Main.HasNode("Lab"), "Lab scaled suburb launched").ConfigureAwait(true);
        var lab = Main.GetNode<LabSession>("Lab");
        Check(lab.World.Tuning.TankCountMultiplier == 4 && Main.SavedTuning.TankCountMultiplier == 1, "Lab draft reaches session without editing saved tuning");
        Check(lab.Session.Source is SuburbWorldSource source && source.Suburb.Plan.Tanks.Count ==
            lab.Scenario.Definition.Supply.Suburb(lab.World.Tuning, lab.World.Tuning.LabPrototypeBots + 1).TankCount,
            "Lab generated the selected tank supply");
        await Press(JoyButton.Start).ConfigureAwait(true);
        await Navigate("Lab menu").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => !Main.HasNode("Lab"), "leave scaled Lab").ConfigureAwait(true);
        await Click("Short Fuse").ConfigureAwait(true);
        Check(Labels().Contains("4×"), "Lab draft remembers supply after a session");
        while (Reachable().ContainsKey("tank_more")) await KingClick("tank_more").ConfigureAwait(true);
        Check(Labels().Contains("8×"), "Lab supply upper bound");
        while (Reachable().ContainsKey("tank_less")) await KingClick("tank_less").ConfigureAwait(true);
        Check(Labels().Contains("1×"), "Lab supply lower bound");
    }

    private async Task KingClick(string key)
    {
        var button = Main.FindChildren("*", "Button", owned: false).OfType<Button>().Single(b => b.IsVisibleInTree() && MenuNavigation.Key(b) == key);
        var position = GetTree().Root.GetFinalTransform() * button.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = position, GlobalPosition = position });
        Input.FlushBufferedEvents();
        await Frames(1).ConfigureAwait(true);
        Input.ParseInputEvent(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        Input.FlushBufferedEvents();
        await Frames(1).ConfigureAwait(true);
        Input.ParseInputEvent(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = false });
        Input.FlushBufferedEvents();
        await Frames().ConfigureAwait(true);
    }
}
