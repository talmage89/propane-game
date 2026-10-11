using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.UI;
using Propane.World;
using Propane.World.Plan;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Enter and synchronously retire real facade worlds through their menu buttons, checking exact counts.</summary>
public partial class MenuLifetimeTest : Node
{
    public required Main Main { get; init; }
    private Menus Menus => Main.GetNode<Menus>("Menus");
    private NetClient Net => Main.GetNode<NetClient>("Net");
    private CaptureDirector capture = null!;

    public override async void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        capture = new CaptureDirector(); AddChild(capture);
        try
        {
            // Single-player repeats a layout; server-owned match seeds still vary. Warm every immutable catalog variant.
            Main.SavedTuning.FixedSeed = 23;
            WarmCatalog();
            // The first two entries also settle lazy input/UI resource bindings.
            await SinglePlayer().ConfigureAwait(true);
            await Collect().ConfigureAwait(true);
            await SinglePlayer().ConfigureAwait(true);
            await Collect().ConfigureAwait(true);
            var single = Counts();
            for (var i = 0; i < 10; i++)
            {
                await SinglePlayer(i + 1).ConfigureAwait(true);
                await Collect().ConfigureAwait(true);
                Assert(Counts() == single, $"single-player retirement {i + 1}: {single} -> {Counts()}");
            }
            Press("Multiplayer"); Press("Host on this computer");
            await Wait(() => Menus.Current == Menus.Screen.Browser, "host browser").ConfigureAwait(true);
            await SoloMatch().ConfigureAwait(true);
            await Collect().ConfigureAwait(true);
            var match = Counts();
            for (var i = 0; i < 10; i++)
            {
                await SoloMatch(i + 1).ConfigureAwait(true);
                await Collect().ConfigureAwait(true);
                Assert(Counts() == match, $"match retirement {i + 1}: {match} -> {Counts()}");
            }
            capture.Screenshot("menu_after_10");
            GD.Print($"[menu lifetime] PASS: 10 single-player cycles {single}; 10 solo-match cycles {match}; no orphans");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError($"[menu lifetime] FAIL {error}");
            GetTree().Quit(1);
        }
    }

    private void WarmCatalog()
    {
        using var world = new WorldContext(this, Main.SavedTuning);
        var plan = new SuburbPlan { Seed = 23, Bounds = new Rect2(-100, -100, 200, 200) };
        for (var house = 0; house < Catalog.Houses.Count; house++)
        {
            for (var palette = 0; palette < Catalog.HousePalettes.Length; palette++)
            {
                plan.Lots.Add(new Lot
                {
                    Seed = 23,
                    Bounds = new OrientedRect(Vector2.Zero, Vector2.One, 0),
                    HouseModel = house,
                    HousePalette = palette,
                    House = new OrientedRect(Vector2.Zero, Vector2.One, 0)
                });
            }
        }
        var loose = Catalog.CarModels.Distinct().Select(name => $"car:{name}")
            .Concat(new[] { "table", "tableRound", "chair", "chairCushion", "loungeChair", "loungeChairRelax" }.Select(name => $"furniture:{name}"))
            .Concat(new[] { "mailbox", "wheelie_bin", "gas_grill", "patio_umbrella", "patio_heater", "kiddie_pool", "cooler", "fire_hydrant", "stop_sign" }.Select(name => $"prop:{name}"));
        foreach (var key in loose) { plan.Props.Add(new PropPlacement(key, Vector2.Zero, 0, 1, PropBody.Loose, 1, Colors.White)); }
        var anchored = Catalog.TreeModels.Concat(Catalog.BushModels).Select(name => $"nature:{name}")
            .Concat(new[] { "prop:shed", "prop:street_lamp" });
        foreach (var key in anchored) { plan.Props.Add(new PropPlacement(key, Vector2.Zero, 0, 1, PropBody.Static, 0, Colors.White)); }
        world.Root.AddChild(Suburb.Build(world, plan, SuburbPlans.SinglePlayer(world.Tuning)));
    }

    private async Task SinglePlayer(int cycle = 0)
    {
        Press("Single player");
        var game = Main.GetNode<Game>("Game");
        game.PauseOnFocusLoss = false;
        var retired = game.World;
        await Frames(6).ConfigureAwait(true);
        if (cycle is 1 or 10) { capture.Screenshot($"single_player_{cycle:00}"); }
        await KeyEscape().ConfigureAwait(true);
        Assert(retired.Paused && !GetTree().Paused, "single-player pause affected application");
        Press("Main menu");
        await Wait(() => !Main.HasNode("Game") && Menus.Current == Menus.Screen.Main, "single-player exit").ConfigureAwait(true);
        Assert(!retired.IsAlive && retired.Clock.PendingCount == 0 && retired.BlastReceivers.Count == 0,
            "single-player world remained live after exit");
    }

    private async Task SoloMatch(int cycle = 0)
    {
        Press("Create lobby");
        await Wait(() => Menus.Current == Menus.Screen.Lobby, "solo lobby").ConfigureAwait(true);
        Press("Start match");
        await Wait(() => Main.HasNode("Match") && Main.GetNode<Match>("Match").World.IsAlive, "solo match").ConfigureAwait(true);
        var retired = Main.GetNode<Match>("Match").World;
        await Frames(6).ConfigureAwait(true);
        if (cycle is 1 or 10) { capture.Screenshot($"solo_match_{cycle:00}"); }
        await KeyEscape().ConfigureAwait(true); Press("Leave match");
        await Wait(() => !Main.HasNode("Match") && Net.Lobby == null && Menus.Current == Menus.Screen.Browser,
            "solo match exit").ConfigureAwait(true);
        Assert(!retired.IsAlive && retired.Clock.PendingCount == 0 && retired.BlastReceivers.Count == 0,
            "match world remained live after exit");
    }

    private void Press(string text)
    {
        var button = GetTree().Root.FindChildren("*", "Button", owned: false).OfType<Button>()
            .FirstOrDefault(b => b.Text == text && b.IsVisibleInTree() && !b.Disabled);
        Assert(button != null, $"missing enabled button {text}");
        button!.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private async Task KeyEscape()
    {
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Escape, Keycode = Key.Escape, Pressed = true });
        await Frames(1).ConfigureAwait(true);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Escape, Keycode = Key.Escape, Pressed = false });
        await Frames().ConfigureAwait(true);
    }

    private async Task Wait(Func<bool> ready, string outcome)
    {
        var deadline = Time.GetTicksMsec() + 15000;
        while (!ready())
        {
            if (Time.GetTicksMsec() >= deadline) { throw new TimeoutException(outcome); }
            await Frames().ConfigureAwait(true);
        }
    }

    private async Task Frames(int count = 3)
    {
        for (var i = 0; i < count; i++) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    }

    private async Task Collect()
    {
        // Godot's managed RefCounted wrappers can release other wrappers during finalization.
        for (var i = 0; i < 6; i++)
        {
            await Frames().ConfigureAwait(true);
            GC.Collect(); GC.WaitForPendingFinalizers();
        }
        await Frames().ConfigureAwait(true);
    }

    private (int Nodes, int Objects, int Orphans) Counts() => (GetTree().GetNodeCount(),
        (int)Performance.GetMonitor(Performance.Monitor.ObjectCount), (int)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount));
}
