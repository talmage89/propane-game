using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Lab;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

internal sealed class FramesScenario(ILabScenarioHost host) : LabScenario(host)
{
    protected override Task RunCore() => FramesReview();

    private async Task FramesReview()
    {
        template.LabStackCount = Tuning.LabStackMaximum;
        var main = new Main { SavedTuning = template };
        AddChild(main);
        await Frames(60).ConfigureAwait(true);
        capture.Screenshot("main_menu");
        Press("Lab");
        await Frames(12).ConfigureAwait(true);
        capture.Screenshot("lab_menu");
        foreach (var scenario in LabScenarios.All)
        {
            Press(scenario.Name);
            await Frames(12).ConfigureAwait(true);
            capture.Screenshot($"options_{scenario.Id}");
            if (scenario.Environments.Count > 1)
            {
                Press("Environment: Void");
                await Frames(12).ConfigureAwait(true);
                capture.Screenshot($"options_{scenario.Id}_suburb");
            }
            Press("Back to scenarios");
            await Frames(6).ConfigureAwait(true);
        }
        main.Free();
        if (DevArgs.Get("menus-only") != null) return;
        foreach (var scenario in LabScenarios.All)
        {
            foreach (var environment in scenario.Environments)
            {
                var lab = Enter(scenario, environment);
                await Frames(60).ConfigureAwait(true);
                if (lab.TuningPanel.Visible)
                {
                    capture.Screenshot($"{scenario.Id}_{environment}_panel");
                    lab.SetMenu(false);
                }
                await Frames(30).ConfigureAwait(true);
                capture.Screenshot($"{scenario.Id}_{environment}_player");
                lab.SetMenu(true);
                await Frames(8).ConfigureAwait(true);
                capture.Screenshot($"{scenario.Id}_{environment}_controls");
                if (environment == LabEnvironment.Void)
                {
                    // The controls follow the last device used: a controller press switches every hint.
                    Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = JoyButton.DpadDown, Pressed = true });
                    Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = JoyButton.DpadDown, Pressed = false });
                    await Frames(8).ConfigureAwait(true);
                    capture.Screenshot($"{scenario.Id}_{environment}_controls_pad");
                    lab.SetMenu(false);
                    await Frames(8).ConfigureAwait(true);
                    capture.Screenshot($"{scenario.Id}_{environment}_player_pad");
                    // Back to the keyboard hints before the next scenario, which the live overlay must see.
                    Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.A, Keycode = Key.A, Pressed = true });
                    Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.A, Keycode = Key.A, Pressed = false });
                    await Frames(3).ConfigureAwait(true);
                }
                lab.Session.Dispose(); lab.Free();
                await Collect().ConfigureAwait(true);
            }
        }
    }

    private void Press(string text)
    {
        var button = GetTree().Root.FindChildren("*", "Button", owned: false).OfType<Button>()
            .FirstOrDefault(b => b.IsVisibleInTree() && b.Text == text && !b.Disabled);
        Assert(button != null, $"missing button {text}");
        button!.EmitSignal(BaseButton.SignalName.Pressed);
    }
}
