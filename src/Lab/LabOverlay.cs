using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.UI;

namespace Propane.Lab;

/// <summary>Persistent scenario reference and a controller-accessible pause/reset/tuning menu.</summary>
public partial class LabOverlay : CanvasLayer
{
    public required LabSession Lab { get; init; }
    private Label status = null!;
    private Label hint = null!;
    private Label title = null!;
    private Label result = null!;
    private Label ammo = null!;
    private Button resume = null!;
    private string fixtureSummary = "";
    private MenuNavigation navigation = null!;
    private CanvasLayer menuLayer = null!;
    public CanvasLayer MenuLayer => menuLayer;
    public bool MenuVisible => menuLayer.Visible;

    public override void _Ready()
    {
        fixtureSummary = Lab.Scenario.Id == "stack" ? $"{Lab.World.Tuning.LabStackCount} tanks · upright pyramid" :
            Lab.Scenario.Id == "range" ? $"{Lab.World.Tuning.LabRangeStep:0}–{Lab.World.Tuning.LabRangeStep * Lab.World.Tuning.LabRangeLanes:0} m" : "Your session · your dials";
        Layer = 12;
        var inset = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        inset.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        inset.AddThemeConstantOverride("margin_left", 28);
        inset.AddThemeConstantOverride("margin_top", 22);
        inset.AddThemeConstantOverride("margin_right", 28);
        AddChild(inset);
        var text = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        inset.AddChild(text);
        text.AddChild(UiStyle.Text($"LAB / {Lab.Scenario.Name.ToUpperInvariant()} / {Lab.EnvironmentKind.ToString().ToUpperInvariant()}", 18));
        status = UiStyle.Text("", 26);
        status.AddThemeFontOverride("font", UiStyle.BoldFont);
        text.AddChild(status);
        hint = UiStyle.Text("", 16, UiStyle.SoftInk);
        text.AddChild(hint);

        ammo = UiStyle.Text("", 24, UiStyle.Ink, HorizontalAlignment.Right);
        ammo.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        ammo.OffsetLeft = -360;
        ammo.OffsetRight = -28;
        ammo.OffsetTop = -74;
        ammo.OffsetBottom = -28;
        AddChild(ammo);
        menuLayer = new CanvasLayer { Layer = 30, Visible = false };
        AddChild(menuLayer);
        var shade = new ColorRect { Color = new Color(0.95f, 0.95f, 0.96f, 0.55f) };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        menuLayer.AddChild(shade);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        menuLayer.AddChild(center);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(0.96f));
        center.AddChild(panel);
        var layout = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        layout.AddThemeConstantOverride("separation", 10);
        panel.AddChild(layout);
        title = UiStyle.Title(Lab.Scenario.Name.ToUpperInvariant(), 32);
        layout.AddChild(title);
        result = UiStyle.Text("Paused", 18, UiStyle.SoftInk, HorizontalAlignment.Center);
        layout.AddChild(result);
        layout.AddChild(new HSeparator());
        layout.AddChild(new ControlsReference { Compact = true, ResetAction = Lab.Scenario.Reloads ? "Reload" : "Reset scenario" });
        layout.AddChild(UiStyle.Text(Lab.Scenario.Banking ? "R / X reloads · Backspace resets the duel" :
            Lab.Scenario.Reloads ? "R / X reloads · Backspace starts a new run" :
            "R / X or Backspace resets this scenario", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
        resume = AddButton(layout, "Resume Lab", () => Lab.SetMenu(false));
        AddButton(layout, Lab.Scenario.Id == "range" ? "Respawn targets" : "Reset scenario", Lab.RequestReset);
        AddButton(layout, "Free tuning", Lab.OpenTuning);
        AddButton(layout, "Lab menu", Lab.RequestExit);
        navigation = new MenuNavigation { Screen = menuLayer, Root = layout, Back = () => Lab.SetMenu(false) };
        menuLayer.AddChild(navigation);
        UpdateStatus();
    }

    internal string ResultTitle => title.Text;
    internal string StatusText => status.Text;

    /// <summary>A prototype's status line, and once decided its result title and the full order.</summary>
    private (string Line, string Winner, string Summary) Prototype(PrototypeRunner prototype)
    {
        var me = Lab.Player.ActorId;
        string Name(int actor) => actor == me ? "You" : prototype.Roster.First(a => a.ActorId == actor).Name;
        switch (prototype.View)
        {
            case Modes.RingOutState ring:
            {
                var plan = ring.Plan;
                var next = plan.NextDissolve(prototype.Elapsed) - prototype.Elapsed;
                var line = $"{ring.AliveCount} standing" + (double.IsInfinity(next) ? " · one lot left" : $" · rim dissolves in {Mathf.CeilToInt(next)} s");
                var order = prototype.Roster.Select(a => a.ActorId).OrderBy(a => ring.Place(a) ?? 0).ToArray();
                var winners = order.Where(a => ring.Place(a) == 1).ToArray();
                var winner = winners.Contains(me) ? (winners.Length > 1 ? "SHARED WIN" : "YOU WIN") :
                    winners.Length == 0 ? "NOBODY STANDS" : $"{string.Join(" & ", winners.Select(Name)).ToUpperInvariant()} WINS";
                var summary = string.Join("  ·  ", order.Select(a => $"{Modes.RingOutHud.Ordinal(ring.Place(a) ?? 0).ToLowerInvariant()} {Name(a)}"));
                return (line, winner, summary);
            }
            case Modes.DissolveRunState run:
            {
                var line = $"{run.Meters} m · {run.Tanks} tanks · score {run.Score}" + (run.Caught ? "" : $" · front {Mathf.FloorToInt((float)run.Gap)} m behind");
                var dissolve = Lab.Dissolve!;
                var seed = dissolve.Daily ? $"today’s run ({dissolve.RunSeed})" : $"seed {dissolve.RunSeed}";
                var best = dissolve.NewBest ? "a new best run" : $"best run {dissolve.BestScore}";
                var summary = $"{run.Meters} m + {run.Tanks} tanks × {Lab.World.Tuning.DissolveTankPoints} + chains {run.Bonus} · best chain {run.BestChain}\n{best} · {seed}";
                return (line, $"RUN OVER · {run.Score}", summary);
            }
            case Modes.ShortFuseState fuse:
            {
                var line = $"Round {fuse.Round} · {fuse.AliveCount} standing · " +
                    string.Join("  ·  ", prototype.Roster.Select(a => $"{Name(a.ActorId)} {fuse.Wins(a.ActorId)}"));
                var champion = fuse.MatchWinner;
                var winner = champion == me ? "YOU WIN THE MATCH" : champion is { } other ? $"{Name(other).ToUpperInvariant()} WINS THE MATCH" : "NO WINNER";
                var summary = $"{fuse.Rounds.Count} rounds · " + string.Join(", ", prototype.Roster.OrderByDescending(a => fuse.Wins(a.ActorId))
                    .Select(a => $"{Name(a.ActorId)} {fuse.Wins(a.ActorId)} won, {fuse.Kills(a.ActorId)} kills"));
                return (line, winner, summary);
            }
            default:
            {
                var scores = prototype.Scores;
                var clock = $"{Mathf.CeilToInt(prototype.TimeLeft) / 60}:{Mathf.CeilToInt(prototype.TimeLeft) % 60:00}";
                var line = string.Join("  ·  ", prototype.Roster.Select(a => $"{Name(a.ActorId)} {scores.GetValueOrDefault(a.ActorId)}")) + $"  ·  {clock}";
                var best = scores.Values.DefaultIfEmpty(0).Max();
                var top = scores.Where(s => s.Value == best).Select(s => s.Key).ToArray();
                var winner = top.Length > 1 ? (top.Contains(me) ? "TIED FOR THE WIN" : "TIED") : top[0] == me ? "YOU WIN" : $"{Name(top[0]).ToUpperInvariant()} WINS";
                var summary = "Time’s up · " + string.Join(", ", prototype.Roster.OrderByDescending(a => scores.GetValueOrDefault(a.ActorId))
                    .Select(a => $"{Name(a.ActorId)} {scores.GetValueOrDefault(a.ActorId)}"));
                return (line, winner, summary);
            }
        }
    }

    private static Button AddButton(Node parent, string text, System.Action action)
    {
        var button = UiStyle.Button(text);
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }

    public void ShowMenu(bool visible)
    {
        UpdateStatus();
        menuLayer.Visible = visible;
        if (visible) navigation.Configure();
    }

    public void UpdateStatus()
    {
        if (Lab.Player.Ammo is { } rounds)
        {
            ammo.Text = $"{rounds.Magazine} / {rounds.Reserve}  ·  " + (rounds.Reloading ? "Reloading" :
                MenuNavigation.Gamepad ? "X: reload" : "R: reload");
        }

        if (Lab.Bank is { } bank)
        {
            resume.Disabled = !bank.IsPlaying;
            var best = 2;
            foreach (var (actor, score) in bank.Scores)
                if (actor != Lab.Player.ActorId && score > bank.Scores[best]) best = actor;
            var mine = bank.Scores[Lab.Player.ActorId];
            var other = bank.Scores[best];
            var name = bank.Scores.Count == 2 ? "Bot" : $"Bot {best - 1}";
            status.Text = $"You  {mine}    ·    {name}  {other}    ·    {Mathf.CeilToInt(bank.TimeLeft) / 60}:{Mathf.CeilToInt(bank.TimeLeft) % 60:00}";
            // Compare every opponent: the first bot need not be the strongest in a larger Lab session.
            title.Text = bank.IsPlaying ? Lab.Scenario.Name.ToUpperInvariant() : mine == other ? "TIED" :
                mine > other ? "YOU WIN" : $"{name.ToUpperInvariant()} WINS";
            result.Text = bank.IsPlaying ? "Paused · the clock is stopped" :
                $"Time’s up · you banked {mine}, {(bank.Scores.Count == 2 ? "the bot" : name)} {other}";
        }
        else if (Lab.Prototype is { } prototype)
        {
            resume.Disabled = prototype.Finished;
            var (line, winner, summary) = Prototype(prototype);
            status.Text = line;
            title.Text = !prototype.Finished ? Lab.Scenario.Name.ToUpperInvariant() : winner;
            result.Text = !prototype.Finished ? "Paused · the round is stopped" : summary;
        }
        else
        {
            status.Text = Lab.Scenario.Id == "range" ? $"{Lab.LiveTanks.Count()} targets · {fixtureSummary}" : fixtureSummary;
        }

        hint.Text = MenuNavigation.Gamepad ? (Lab.Scenario.Reloads ? "Start: Lab controls / reset / tuning   ·   X: reload" :
            "X: reset   ·   Start: Lab controls / tuning") : (Lab.Scenario.Reloads ?
            "Esc: Lab controls   ·   Backspace: reset   ·   R: reload   ·   F1: tuning" :
            "R / Backspace: reset   ·   Esc: Lab controls   ·   F1: tuning");
    }
}
