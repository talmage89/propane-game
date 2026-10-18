using System;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Dev.LabCases;
using Propane.Lab;
using Propane.ReleaseSmoke;
using static Propane.Dev.UnitTests.TestRunner;
using LabCase = Propane.Dev.LabCases.LabScenario;

namespace Propane.Dev;

/// <summary>Real Lab sessions: retirement, fixture counts, session-only tuning, local banks and player-camera frames.</summary>
public partial class LabTest : Node, ILabScenarioHost
{
    private readonly PlayerSettings applicationSettings = new("res://build/a1-labtest-settings.cfg");
    private System.IDisposable? settingsBinding;

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete) return;
        settingsBinding?.Dispose();
        applicationSettings.Dispose();
    }

    private CaptureDirector capture = null!;
    private Tuning template = null!;
    private Equivalence.LabTrace? equivalence;
    private LabCase? activeScenario;

    Tuning ILabScenarioHost.Template => template;
    CaptureDirector ILabScenarioHost.Capture => capture;
    Equivalence.LabTrace? ILabScenarioHost.Trace => equivalence;
    SceneTree ILabScenarioHost.Tree => GetTree();
    Viewport ILabScenarioHost.Viewport => GetViewport();
    void ILabScenarioHost.Attach(Node node) => AddChild(node);
    void ILabScenarioHost.AddLab(LabSession lab) => AddLab(lab);
    LabSession ILabScenarioHost.Enter(Propane.Lab.LabScenario scenario, LabEnvironment environment) => Enter(scenario, environment);
    Task ILabScenarioHost.Frames(int count) => Frames(count);
    Task ILabScenarioHost.Collect() => Collect();
    (int, int, int) ILabScenarioHost.Counts() => Counts();

    public override void _ExitTree()
    {
        try { activeScenario?.Dispose(); }
        finally { settingsBinding?.Dispose(); settingsBinding = null; }
    }

    private void AddLab(LabSession lab)
    {
        if (DevArgs.Get("equivalence-trace") != null)
        {
            Assert(equivalence == null, "trace fixtures must use one Lab session");
            equivalence = new Equivalence.LabTrace { Lab = lab, ProcessPhysicsPriority = int.MaxValue };
            equivalence.OpenDetail();
            lab.TestObserveCommand = equivalence.Command;
            lab.TestObserveTank = equivalence.Track;
        }
        AddChild(lab);
        if (equivalence != null) { AddChild(equivalence); equivalence.Begin(); }
    }
    public override async void _Ready()
    {
        settingsBinding = PlayerSettings.Bind(applicationSettings);
        DevArgs.MarkReady();
        capture = new CaptureDirector { CollectFrameMetrics = DevArgs.Get("scenario") != "measure-play" }; AddChild(capture);
        template = Tuning.LoadBaked();
        if (DevArgs.Get("equivalence-trace") != null) template.HitstopDuration = 0;
        template.FixedSeed = 23;
        template.LabStackCount = 32;
        if (DevArgs.Get("bot-skill") != null) template.LabBotSkill = DevArgs.GetFloat("bot-skill", template.LabBotSkill);
        try
        {
            activeScenario = LabScenarioRegistry.Create(DevArgs.Get("scenario") ?? "lifetime", this);
            await activeScenario.Execute().ConfigureAwait(true);
            activeScenario.CheckCompletion();
            activeScenario?.Dispose();
            equivalence?.Finish();
            template.Dispose();
            GD.Print("[lab] PASS");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            activeScenario?.Dispose();
            GD.PushError($"[lab] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private LabSession Enter(Propane.Lab.LabScenario scenario, LabEnvironment environment)
    {
        var lab = new LabSession
        {
            Scenario = scenario,
            EnvironmentKind = environment,
            Template = template,
            PauseOnFocusLoss = false,
            TestRandomSeed = (ulong)DevArgs.GetFloat("effects-seed", 23),
            TestSeedPhysicsRandom = DevArgs.Get("replay-random") != null,
            Name = "Lab"
        };
        AddLab(lab);
        Assert(lab.Initialized, $"{scenario.Id}/{environment} initialization");
        return lab;
    }

    private async Task Collect()
    {
        await Frames(3).ConfigureAwait(true);
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Frames(3).ConfigureAwait(true); }
    }
    private (int, int, int) Counts() => (GetTree().GetNodeCount(), (int)Performance.GetMonitor(Performance.Monitor.ObjectCount),
        (int)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount));
    private async Task Frames(int count = 3)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
