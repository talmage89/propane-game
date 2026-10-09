using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Player;
using Propane.UI;
using Propane.World;
using Propane.World.Plan;

namespace Propane;

/// <summary>
/// Top of the game: the void, the player, the current suburb, the HUD and menus, and the loop. Clearing every tank
/// (after a short beat) or pressing R swaps in a new suburb: the old one dissolves in toward the player, the player
/// moves to the new spawn while only the void is visible, and the new suburb materialises outward around them.
/// </summary>
public partial class Game : Node3D
{
    private enum Phase
    {
        Playing,
        Celebrating,
        Dissolving,
        Materialising,
    }

    private const float RevealMaxRadius = 320f;
    private const float DissolveShare = 0.42f;

    private VoidEnvironment environment = null!;
    private PlayerCharacter player = null!;
    private Hud hud = null!;
    private TuningPanel tuningPanel = null!;
    private PauseMenu pauseMenu = null!;
    private Hitstop hitstop = null!;
    private Node3D effects = null!;
    private Suburb? current;
    private Suburb? outgoing;
    private Phase phase = Phase.Playing;
    private float phaseTime;
    private Vector3 revealCenter;
    private readonly RandomNumberGenerator seeds = new();
    private List<Vector3> tankGroups = new();
    private float groupRefresh;

    public PlayerCharacter Player => player;

    public Suburb? Current => current;

    public bool InTransition => phase is Phase.Dissolving or Phase.Materialising;

    /// <summary>Leave single player for the main menu (from the pause menu). Subscribe before adding to the tree.</summary>
    public event System.Action? ExitRequested;

    /// <summary>Pause when the window loses focus. Scripted dev runs turn this off, since they run unfocused.</summary>
    public bool PauseOnFocusLoss { get; set; } = true;

    /// <summary>Starts the swap to a new suburb, as the R key does.</summary>
    public void RequestNewSuburb()
    {
        if (phase is Phase.Playing or Phase.Celebrating)
        {
            BeginDissolve();
        }
    }

    public override void _Ready()
    {
        InputSetup.Register();
        _ = GameAssets.Debris;
        ExplosionEffect.Preload();
        ProcessMode = ProcessModeEnum.Always;
        seeds.Randomize();

        environment = new VoidEnvironment { Name = "Void" };
        AddChild(environment);
        effects = new Node3D { Name = "Effects", ProcessMode = ProcessModeEnum.Pausable };
        AddChild(effects);
        Spawn.SetEffectsRoot(effects);

        player = new PlayerCharacter { Name = "Player", ProcessMode = ProcessModeEnum.Pausable };
        AddChild(player);
        environment.Follow = player.CameraRig.Camera;

        hitstop = new Hitstop { Name = "Hitstop", Focus = player };
        AddChild(hitstop);
        hud = new Hud { Name = "Hud" };
        AddChild(hud);
        tuningPanel = new TuningPanel { Name = "Tuning" };
        AddChild(tuningPanel);
        pauseMenu = new PauseMenu { Name = "Pause" };
        pauseMenu.ResumeRequested += Resume;
        if (ExitRequested != null)
        {
            pauseMenu.MainMenuRequested += () => ExitRequested?.Invoke();
        }
        AddChild(pauseMenu);

        // The first suburb materialises around the player, like every later one.
        RenderingServer.GlobalShaderParameterSet("reveal_radius", 0f);
        LoadSuburb();
        BeginMaterialise();
        Warmup.Run(this, new Vector3(0, -400f, 0));
        CaptureMouse(true);
        // Played for real, the game fills the screen; dev harnesses that host it keep their requested window.
        // Deferred, because resizing the window runs a frame before everything above is ready.
        if (GetTree().CurrentScene == this && DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Windowed)
        {
            Callable.From(() => DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized)).CallDeferred();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed(InputSetup.Pause))
        {
            if (tuningPanel.Visible)
            {
                tuningPanel.Toggle();
                CaptureMouse(true);
            }
            else if (GetTree().Paused)
            {
                Resume();
            }
            else
            {
                Pause();
            }
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed(InputSetup.ToggleTuning) && !GetTree().Paused)
        {
            tuningPanel.Toggle();
            CaptureMouse(!tuningPanel.Visible);
        }
        else if (@event.IsActionPressed(InputSetup.ToggleFullscreen))
        {
            var fullscreen = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
            DisplayServer.WindowSetMode(fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
        }
        else if (@event.IsActionPressed(InputSetup.NewSuburb) && !GetTree().Paused && phase is Phase.Playing or Phase.Celebrating)
        {
            BeginDissolve();
        }
        else if (@event is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured &&
                 !tuningPanel.Visible && !GetTree().Paused)
        {
            CaptureMouse(true);
        }
    }

    public override void _Notification(int what)
    {
        // Losing focus pauses rather than leaving the mouse captured behind another window.
        // macOS can report focus loss before the game is ready, when it launches behind another window.
        if (what == NotificationApplicationFocusOut && PauseOnFocusLoss && IsNodeReady() && !GetTree().Paused && !tuningPanel.Visible)
        {
            Pause();
        }
    }

    public override void _Process(double delta)
    {
        var dt = (float)(delta / Mathf.Max(Engine.TimeScale, 0.0001));
        UpdateHud();
        if (GetTree().Paused)
        {
            return;
        }
        phaseTime += dt;
        var tuning = Tuning.Current;
        switch (phase)
        {
            case Phase.Celebrating:
                if (phaseTime >= tuning.ClearCelebrationTime)
                {
                    BeginDissolve();
                }
                break;
            case Phase.Dissolving:
            {
                var duration = tuning.TransitionTime * DissolveShare;
                var t = Mathf.Clamp(phaseTime / duration, 0, 1);
                revealCenter = player.GlobalPosition;
                var radius = Mathf.Lerp(RevealMaxRadius, 0f, EaseInOut(t));
                RenderingServer.GlobalShaderParameterSet("reveal_center", revealCenter);
                RenderingServer.GlobalShaderParameterSet("reveal_radius", radius);
                outgoing?.HideOutsideRing(revealCenter, radius);
                Suburb.HideOutside(effects, revealCenter, radius);
                if (t >= 1f)
                {
                    FinishDissolve();
                }
                break;
            }
            case Phase.Materialising:
            {
                var duration = tuning.TransitionTime * (1f - DissolveShare);
                var t = Mathf.Clamp(phaseTime / duration, 0, 1);
                RenderingServer.GlobalShaderParameterSet("reveal_radius", Mathf.Lerp(0f, RevealMaxRadius, t * t));
                if (t >= 1f)
                {
                    current?.SetRevealSide(0f);
                    RenderingServer.GlobalShaderParameterSet("reveal_radius", 100000f);
                    // The last tank may already have gone up while the suburb was still assembling.
                    phase = current?.TanksRemaining == 0 ? Phase.Celebrating : Phase.Playing;
                    phaseTime = 0;
                }
                break;
            }
        }
    }

    private void UpdateHud()
    {
        var camera = player.CameraRig.Camera;
        var viewportHeight = GetViewport().GetVisibleRect().Size.Y;
        var radius = Mathf.Tan(Mathf.DegToRad(player.SpreadDegrees)) / Mathf.Tan(Mathf.DegToRad(camera.Fov / 2f)) * viewportHeight / 2f;
        hud.SetCrosshair(radius, !player.IsRagdolled && Input.MouseMode == Input.MouseModeEnum.Captured, player.AimAmount);

        // Tank groups shift as tanks vent and blow up, so they are regrouped a few times a second, not every frame.
        groupRefresh -= (float)GetProcessDeltaTime();
        if (groupRefresh <= 0)
        {
            groupRefresh = 0.25f;
            tankGroups = phase == Phase.Playing && current != null ? current.TankGroupCenters() : new List<Vector3>();
        }
        hud.SetTankArrows(camera, tankGroups, Tuning.Current.TankArrows && phase == Phase.Playing);
    }

    // ------------------------------------------------------------------ Suburbs

    private void LoadSuburb()
    {
        var tuning = Tuning.Current;
        var settings = SuburbPlans.SinglePlayer(tuning);
        var seed = tuning.FixedSeed != 0 ? tuning.FixedSeed : (int)seeds.Randi();
        // Occasionally a layout comes out sparse; a few rerolls keep every suburb worth exploring.
        var plan = SuburbPlans.Generate(settings, seed, tuning.FixedSeed == 0 ? () => (int)seeds.Randi() : null);
        current = Suburb.Build(plan, settings);
        current.ProcessMode = ProcessModeEnum.Pausable;
        AddChild(current);
        Spawn.SetWorldRoot(current.DynamicRoot);
        current.TankDestroyed += OnTankDestroyed;
        hud.SetTanks(current.TanksRemaining, animate: false);

        var spawn = Suburb.ToWorld(plan.Spawn, 0.05f);
        player.Teleport(spawn, plan.SpawnYaw + Mathf.Pi);
        GD.Print($"[game] suburb seed {plan.Seed}: {plan.Lots.Count} lots, {plan.Tanks.Count} tanks");
    }

    private void OnTankDestroyed(int remaining)
    {
        hud.SetTanks(remaining, animate: true);
        if (remaining == 0 && phase == Phase.Playing)
        {
            phase = Phase.Celebrating;
            phaseTime = 0;
        }
    }

    private void BeginDissolve()
    {
        if (current == null)
        {
            return;
        }
        outgoing = current;
        current = null;
        outgoing.TankDestroyed -= OnTankDestroyed;
        outgoing.SetRevealSide(1f);
        revealCenter = player.GlobalPosition;
        RenderingServer.GlobalShaderParameterSet("reveal_center", revealCenter);
        RenderingServer.GlobalShaderParameterSet("reveal_radius", RevealMaxRadius);
        phase = Phase.Dissolving;
        phaseTime = 0;
    }

    private void FinishDissolve()
    {
        // Only the void is visible now, so the player can move to the new spawn without a visible cut.
        outgoing?.QueueFree();
        outgoing = null;
        foreach (var child in effects.GetChildren())
        {
            child.QueueFree();
        }
        RenderingServer.GlobalShaderParameterSet("reveal_radius", 0f);
        LoadSuburb();
        BeginMaterialise();
    }

    private void BeginMaterialise()
    {
        current?.SetRevealSide(1f);
        revealCenter = player.GlobalPosition;
        RenderingServer.GlobalShaderParameterSet("reveal_center", revealCenter);
        RenderingServer.GlobalShaderParameterSet("reveal_radius", 0f);
        phase = Phase.Materialising;
        phaseTime = 0;
    }

    // ------------------------------------------------------------------ Pause and mouse

    private void Pause()
    {
        GetTree().Paused = true;
        pauseMenu.Visible = true;
        hud.ShowHud(false);
        CaptureMouse(false);
    }

    private void Resume()
    {
        GetTree().Paused = false;
        pauseMenu.Visible = false;
        hud.ShowHud(true);
        CaptureMouse(true);
    }

    private static void CaptureMouse(bool captured) =>
        Input.MouseMode = captured ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;

    private static float EaseInOut(float t) => t * t * (3f - 2f * t);
}
