using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.UI;

namespace Propane.Lab;

/// <summary>Scenario registry front end, using the same keyboard/controller navigation as the main menu.</summary>
public partial class LabMenu : CanvasLayer
{
    public Tuning Template { get; init; } = Tuning.SavedTemplate;
    public event Action? BackRequested;
    public event Action<LabScenario, LabEnvironment, Tuning>? StartRequested;
    private VBoxContainer layout = null!;
    private MenuNavigation navigation = null!;
    private LabScenario? selected;
    private LabEnvironment environment;
    private Tuning draft = null!;

    public override void _Ready()
    {
        Layer = 40;
        draft = Template.Copy();
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(0.96f));
        center.AddChild(panel);
        layout = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) };
        layout.AddThemeConstantOverride("separation", 10);
        panel.AddChild(layout);
        navigation = new MenuNavigation { Screen = this, Root = layout, Back = Back };
        AddChild(navigation);
        Rebuild();
    }

    public override void _ExitTree() => draft.Dispose();
    public override void _UnhandledInput(InputEvent e)
    {
        if (Visible && e.IsActionPressed(InputSetup.Pause)) { Back(); GetViewport().SetInputAsHandled(); }
    }

    public void ShowScenarios()
    {
        using var previous = draft;
        draft = Template.Copy();
        draft.CopyLocalTunersFrom(previous);
        Visible = true;
        selected = null;
        Rebuild();
    }

    private void Back()
    {
        if (selected != null) { selected = null; Rebuild(); }
        else
        {
            BackRequested?.Invoke();
        }
    }

    private void Rebuild(string? focus = null)
    {
        foreach (var child in layout.GetChildren()) { layout.RemoveChild(child); child.QueueFree(); }
        layout.AddChild(UiStyle.Title(selected == null ? "THE LAB" : selected.Name.ToUpperInvariant(), 40));
        layout.AddChild(UiStyle.Text(selected == null ? "A clean slate. Try something loud." : selected.Description, 16, UiStyle.SoftInk,
            HorizontalAlignment.Center));
        layout.AddChild(new HSeparator());
        if (selected == null)
        {
            foreach (var scenario in LabScenarios.All.Where(s => !s.Prototype))
                Scenario(scenario);
            // Modes on trial: playable against Lab bots so their rules can be judged before anything ships.
            layout.AddChild(new HSeparator());
            layout.AddChild(UiStyle.Text("PROTOTYPE MODES · RULES ON TRIAL", 18, UiStyle.Accent, HorizontalAlignment.Center));
            foreach (var scenario in LabScenarios.All.Where(s => s.Prototype))
                Scenario(scenario);
            layout.AddChild(new HSeparator());
            Button("Main menu", () => BackRequested?.Invoke());
        }
        else
        {
            var scenario = selected;
            if (scenario.Environments.Count > 1)
            {
                Button($"Environment: {environment}", () =>
                {
                    environment = environment == LabEnvironment.Void ? LabEnvironment.Suburb : LabEnvironment.Void;
                    Rebuild("environment");
                }, "environment");
                layout.AddChild(UiStyle.Text("Void is the default. Suburb adds houses, cover, and loose props.", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
            }
            else
            {
                layout.AddChild(UiStyle.Text($"Environment: {scenario.Environments[0]}", 22, UiStyle.Ink, HorizontalAlignment.Center));
                layout.AddChild(UiStyle.Text(scenario.Environments[0] == LabEnvironment.Void ? "A clear floor, an open sky, and no houses."
                    : scenario.Definition == Modes.ModeRegistry.DissolveRun ? "One endless street, built a segment at a time."
                    : "A new suburb every round, sized for everyone in it.", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
            }
            if (scenario.Id == "stack")
            {
                var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
                layout.AddChild(row);
                Button("−", () => { draft.LabStackCount = Mathf.Max(Tuning.LabStackMinimum, draft.LabStackCount - Tuning.LabStackStep); Rebuild("smaller"); }, "smaller", draft.LabStackCount <= Tuning.LabStackMinimum, row);
                row.AddChild(UiStyle.Text($"{draft.LabStackCount} tanks", 22));
                Button("+", () => { draft.LabStackCount = Mathf.Min(Tuning.LabStackMaximum, draft.LabStackCount + Tuning.LabStackStep); Rebuild("larger"); }, "larger", draft.LabStackCount >= Tuning.LabStackMaximum, row);
                layout.AddChild(UiStyle.Text($"{Tuning.LabStackMinimum}–{Tuning.LabStackMaximum} tanks · bigger stacks cost more during a blast.", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
            }
            if (scenario.Id == "king")
            {
                // A hosted King lobby's two settings, on the lobby's own steps: the mint pace (seconds a lone holder
                // needs for each tank) and the round's length.
                const float step = (float)Modes.KingRules.MintPaceStep, fastest = (float)Modes.KingRules.MintPaceMin, slowest = (float)Modes.KingRules.MintPaceMax;
                var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
                layout.AddChild(row);
                Button("−", () => { draft.KingMintSeconds = Mathf.Max(fastest, draft.KingMintSeconds - step); Rebuild("faster"); }, "faster", draft.KingMintSeconds <= fastest, row);
                row.AddChild(UiStyle.Text($"A tank every {draft.KingMintSeconds:0.0} s", 22));
                Button("+", () => { draft.KingMintSeconds = Mathf.Min(slowest, draft.KingMintSeconds + step); Rebuild("slower"); }, "slower", draft.KingMintSeconds >= slowest, row);
                layout.AddChild(UiStyle.Text("Mint pace while you hold the zone alone · F1 tunes it during play too.", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
                var shortest = draft.MatchLengthMin;
                var longest = Mathf.Max(shortest, draft.MatchLengthMax);
                var lengthStep = Mathf.Max(draft.MatchLengthStep, 1f);
                var round = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
                layout.AddChild(round);
                Button("−", () => { draft.KingRoundSeconds = Mathf.Clamp(draft.KingRoundSeconds - lengthStep, shortest, longest); Rebuild("shorter"); },
                    "shorter", draft.KingRoundSeconds <= shortest, round);
                round.AddChild(UiStyle.Text($"Rounds of {UiStyle.FormatTime(draft.KingRoundSeconds)}", 22));
                Button("+", () => { draft.KingRoundSeconds = Mathf.Clamp(draft.KingRoundSeconds + lengthStep, shortest, longest); Rebuild("longer"); },
                    "longer", draft.KingRoundSeconds >= longest, round);
                layout.AddChild(UiStyle.Text("The same pace and round length a multiplayer lobby sets for King.", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
            }
            if (scenario.Definition == Modes.ModeRegistry.DissolveRun)
            {
                Button(draft.DissolveDaily != 0 ? "Seed: today’s run" : "Seed: a new run", () =>
                {
                    draft.DissolveDaily = draft.DissolveDaily != 0 ? 0 : 1;
                    Rebuild("seed");
                }, "seed");
                layout.AddChild(UiStyle.Text(draft.DissolveDaily != 0 ? "Today’s street is the same for everyone · solo · no server needed"
                    : "A fresh street every run · solo · no server needed", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
            }
            else if (scenario.Banking || scenario.Prototype)
            {
                // The lobby's difficulty profiles; the Lab's skill dial keeps fine control between them.
                var difficulty = Bots.BotProfile.Nearest(draft.LabBotSkill, draft);
                Button($"Difficulty: {difficulty}", () =>
                {
                    draft.LabBotSkill = Bots.BotProfile.For((Bots.BotDifficulty)(((int)difficulty + 1) % Bots.BotProfile.All.Length), draft).Skill;
                    Rebuild("difficulty");
                }, "difficulty");
                layout.AddChild(UiStyle.Text(scenario.Prototype
                    ? $"Prototype · you and {draft.LabPrototypeBots} bot{(draft.LabPrototypeBots == 1 ? "" : "s")} · rules on trial · no server needed"
                    : "Local tank bank · one bot · three minutes · no server needed", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
            }
            layout.AddChild(new HSeparator());
            Button("Enter Lab", () => StartRequested?.Invoke(scenario, environment, draft), "enter");
            Button("Back to scenarios", Back);
            layout.AddChild(UiStyle.Text("Reset and free tuning are available in every scenario.", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
        }
        layout.AddChild(UiStyle.Text("D-pad / stick or arrows: navigate   ·   A / Enter: choose   ·   B / Esc: back", 14, UiStyle.SoftInk,
            HorizontalAlignment.Center));
        // Choosing a scenario lands on Enter Lab, so a second press starts it rather than changing an option.
        navigation.Configure(focus ?? (selected != null ? "enter" : null));
    }

    private void Scenario(LabScenario scenario)
    {
        Button(scenario.Name, () => { selected = scenario; environment = scenario.Environments[0]; Rebuild(); });
        layout.AddChild(UiStyle.Text(scenario.Description, 16, UiStyle.SoftInk, HorizontalAlignment.Center));
    }

    private void Button(string text, Action action, string? key = null, bool disabled = false, Node? parent = null)
    {
        var button = UiStyle.Button(text);
        button.Disabled = disabled;
        if (key != null) button.SetMeta("focus_key", key);
        button.Pressed += action;
        (parent ?? layout).AddChild(button);
    }
}
