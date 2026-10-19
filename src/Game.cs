using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Player;
using Propane.Session;
using Propane.Tank;
using Propane.UI;
using Propane.World;
using Propane.World.Plan;

namespace Propane;

/// <summary>
/// Top of the game: the void, the player, the current suburb, the HUD and menus, and the loop. Clearing every tank
/// (after a short beat) or pressing R swaps in a new suburb: the old one dissolves in toward the player, the player
/// moves to the new spawn while only the void is visible, and the new suburb materializes outward around them.
/// </summary>
public partial class Game : Node3D
{
    public SessionContext Session { get; private set; } = null!;
    public WorldContext World => Session.World;
    public Tuning Template { get; init; } = Tuning.SavedTemplate;
    internal ulong? TestRandomSeed { get; init; }

    private enum Phase
    {
        Playing,
        Celebrating,
        Dissolving,
        Materializing,
    }

    private const float RevealMaxRadius = 320f;
    private const float DissolveShare = 0.42f;

    private VoidEnvironment environment = null!;
    private PlayerCharacter player = null!;
    private LocalView? localView;
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

    public bool InTransition => phase is Phase.Dissolving or Phase.Materializing;

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

    public override void _Ready() => SessionFactory.Build(this, Template, new SandboxSource(this), new SandboxMode(this),
        session =>
        {
            Session = session;
            if (TestRandomSeed is { } seed) World.Random.Seed = seed;
        });

    public override void _ExitTree() => Session?.Dispose();

    public override void _UnhandledInput(InputEvent @event)
    {
        if (World is not { IsAlive: true }) { return; }
        if (@event.IsActionPressed(InputSetup.Pause))
        {
            if (tuningPanel.Visible)
            {
                tuningPanel.Toggle();
                CaptureMouse(true);
            }
            else if (World.Paused)
            {
                Resume();
            }
            else
            {
                Pause();
            }
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed(InputSetup.ToggleTuning) && !World.Paused)
        {
            tuningPanel.Toggle();
            CaptureMouse(!tuningPanel.Visible);
        }
        else if (@event.IsActionPressed(InputSetup.ToggleFullscreen))
        {
            var fullscreen = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
            DisplayServer.WindowSetMode(fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
        }
        else if (@event.IsActionPressed(InputSetup.NewSuburb) && !World.Paused && phase is Phase.Playing or Phase.Celebrating)
        {
            BeginDissolve();
        }
        else if (@event is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured &&
                 !tuningPanel.Visible && !World.Paused)
        {
            CaptureMouse(true);
        }
    }

    public override void _Notification(int what)
    {
        // Losing focus pauses rather than leaving the mouse captured behind another window.
        // macOS can report focus loss before the game is ready, when it launches behind another window.
        if (what == NotificationApplicationFocusOut && PauseOnFocusLoss && IsNodeReady() && World is { IsAlive: true, Paused: false } && !tuningPanel.Visible)
        {
            Pause();
        }
    }

    public override void _Process(double delta)
    {
        if (World is not { IsAlive: true }) { return; }
        var dt = (float)delta;
        UpdateHud();
        if (World.Paused)
        {
            return;
        }
        phaseTime += dt;
        var tuning = World.Tuning;
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
                World.Reveal.Center = revealCenter;
                World.Reveal.Radius = radius;
                outgoing?.HideOutsideRing(revealCenter, radius);
                Suburb.HideOutside(effects, revealCenter, radius);
                if (t >= 1f)
                {
                    FinishDissolve();
                }
                break;
            }
            case Phase.Materializing:
            {
                var duration = tuning.TransitionTime * (1f - DissolveShare);
                var t = Mathf.Clamp(phaseTime / duration, 0, 1);
                World.Reveal.Radius = Mathf.Lerp(0f, RevealMaxRadius, t * t);
                if (t >= 1f)
                {
                    current?.SetRevealSide(0f);
                    World.Reveal.Radius = 100000f;
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
        if (localView == null) return;
        var camera = localView.CameraRig.Camera;
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
        hud.SetTankArrows(camera, tankGroups, World.Tuning.TankArrows && phase == Phase.Playing);
    }

    // ------------------------------------------------------------------ Suburbs

    private void LoadSuburb()
    {
        var tuning = World.Tuning;
        var settings = SuburbPlans.SinglePlayer(tuning);
        var seed = tuning.FixedSeed != 0 ? tuning.FixedSeed : (int)seeds.Randi();
        // Occasionally a layout comes out sparse; a few rerolls keep every suburb worth exploring.
        var plan = SuburbPlans.Generate(settings, seed, tuning.FixedSeed == 0 ? () => (int)seeds.Randi() : null);
        current = Suburb.Build(World, plan, settings);
        World.Root.AddChild(current);
        World.SetContentRoot(current.DynamicRoot);
        var gatheringWorld = current;
        current.AddChild(new TankGather
        {
            World = World,
            Name = "Gather",
            Tanks = () => gatheringWorld.LiveTanks,
            Enabled = () => phase == Phase.Playing
        });
        current.TankDestroyed += OnTankDestroyed;
        hud?.SetTanks(current.TanksRemaining, animate: false);

        var spawn = PlanCoordinates.ToWorld(plan.Spawn, 0.05f);
        player?.Teleport(spawn, plan.SpawnYaw + Mathf.Pi);
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
        World.Reveal.Center = revealCenter;
        World.Reveal.Radius = RevealMaxRadius;
        phase = Phase.Dissolving;
        phaseTime = 0;
    }

    private void FinishDissolve()
    {
        // Only the void is visible now, so the player can move to the new spawn without a visible cut.
        if (outgoing != null) { World.RetireContent(outgoing); }
        outgoing = null;
        foreach (var child in effects.GetChildren())
        {
            child.Free();
        }
        World.Reveal.Radius = 0f;
        LoadSuburb();
        BeginMaterialize();
    }

    private void BeginMaterialize()
    {
        current?.SetRevealSide(1f);
        revealCenter = player.GlobalPosition;
        World.Reveal.Center = revealCenter;
        World.Reveal.Radius = 0f;
        phase = Phase.Materializing;
        phaseTime = 0;
    }

    // ------------------------------------------------------------------ Pause and mouse

    private void Pause()
    {
        World.SetPaused(true);
        pauseMenu.Visible = true;
        hud.ShowHud(false);
        CaptureMouse(false);
    }

    private void Resume()
    {
        World.SetPaused(false);
        pauseMenu.Visible = false;
        hud.ShowHud(true);
        CaptureMouse(true);
    }

    private static void CaptureMouse(bool captured) =>
        Input.MouseMode = captured ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;

    private static float EaseInOut(float t) => t * t * (3f - 2f * t);
}
