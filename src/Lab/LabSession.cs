using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Player;
using Propane.Session;
using Propane.Tank;
using Propane.UI;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Lab;

/// <summary>A shipped offline session. Reset replaces all scoped services through ordered session retirement.</summary>
public partial class LabSession : Node3D
{
    public required LabScenario Scenario { get; init; }
    public LabEnvironment EnvironmentKind { get; init; }
    public Tuning Template { get; init; } = Tuning.SavedTemplate;
    public bool PauseOnFocusLoss { get; set; } = true;
    internal ulong? TestRandomSeed { get; init; }
    public bool CreateLocalView { get; init; } = true;
    public SessionContext Session { get; private set; } = null!;
    public WorldContext World => Session.World;
    public PlayerCharacter Player { get; private set; } = null!;
    public PlayerCharacter? Opponent { get; private set; }
    public LocalBank? Bank { get; private set; }
    public TuningPanel TuningPanel { get; private set; } = null!;
    public LabOverlay Overlay { get; private set; } = null!;
    public bool Initialized { get; private set; }
    public int ResetCount { get; private set; }
    public event Action? ExitRequested;
    private readonly List<PropaneTank> tanks = new();
    internal IEnumerable<Vector3>? AmmoPositions => suburb?.Plan.AmmoSpots.Select(p => Suburb.ToWorld(p, 0.05f));
    public IEnumerable<PropaneTank> LiveTanks => tanks.Where(t => IsInstanceValid(t) && t.State != PropaneTank.TankState.Exploded);
    private VoidEnvironment environment = null!;
    private Suburb? suburb;
    private ISessionWorldSource source = null!;
    private Vector3 start;
    private float startYaw;
    private bool resetQueued;
    private bool tuningVisited;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        if (!Scenario.Environments.Contains(EnvironmentKind)) throw new ArgumentException("Scenario does not allow this environment");
        Assemble(Template);
        Initialized = true;
    }

    private void Assemble(Tuning template)
    {
        tuningVisited = false;
        using var settings = template.Copy();
        // Reject corrupted/local out-of-range layout inputs before constructing anything.
        foreach (var property in typeof(Tuning).GetProperties())
        {
            if (Attribute.GetCustomAttribute(property, typeof(LabDialAttribute)) is not LabDialAttribute dial) continue;
            var value = Convert.ToDouble(property.GetValue(settings), System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsFinite(value) || value < dial.Min || value > dial.Max)
                throw new ArgumentOutOfRangeException(property.Name);
        }
        source = EnvironmentKind == LabEnvironment.Void ? new VoidWorldSource() :
            new SuburbWorldSource(Scenario.Banking ? Scenario.Definition.Supply.Suburb(settings, 2) with { MapSize = settings.LabSuburbSize } :
                Scenario.Definition.Supply.Suburb(settings, 1), settings.FixedSeed != 0 ? settings.FixedSeed : 23);
        SessionFactory.Build(this, settings, source, new Mode(this), session => { Session = session; if (TestRandomSeed is { } seed) World.Random.Seed = seed; }, present: CreateLocalView);
        resetQueued = false;
    }

    public void RequestReset()
    {
        if (!World.IsAlive || resetQueued) return;
        resetQueued = true;
        World.Defer(Reset, whilePaused: true);
    }

    /// <summary>Also used by lifecycle probes. Event handlers request a deferred reset instead.</summary>
    public void Reset()
    {
        using var tuning = World.Tuning.Copy();
        Session.Dispose();
        tanks.Clear();
        suburb = null;
        Bank = null;
        Opponent = null;
        Assemble(tuning);
        ResetCount++;
    }

    public void RequestExit() => World.Defer(() => ExitRequested?.Invoke(), whilePaused: true);
    public override void _ExitTree() => Session?.Dispose();

    public override void _UnhandledInput(InputEvent e)
    {
        if (Session is not { IsAlive: true } || !CreateLocalView) return;
        if (e.IsActionPressed(InputSetup.ToggleFullscreen))
        {
            var full = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
            DisplayServer.WindowSetMode(full ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
        }
        else if (e.IsActionPressed(InputSetup.Pause))
        {
            if (TuningPanel.Visible) CloseTuning();
            else SetMenu(!Overlay.MenuVisible);
        }
        else if (e.IsActionPressed(InputSetup.ToggleTuning))
        {
            OpenTuning();
        }
        else if ((!World.Paused || Overlay.MenuVisible) && e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Backspace })
        {
            // Also in the controls panel, where it is the keyboard's way to go again once a duel's clock has ended.
            RequestReset();
        }
        else if (!World.Paused && !Scenario.Banking && e.IsActionPressed(InputSetup.NewSuburb))
        {
            RequestReset();
        }
        else if (e is InputEventMouseButton { Pressed: true } && !World.Paused)
        {
            Capture(true);
        }
        else
        {
            return;
        }

        GetViewport().SetInputAsHandled();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut && PauseOnFocusLoss && Initialized && CreateLocalView && Session.IsAlive)
            SetMenu(true);
    }

    public void SetMenu(bool visible)
    {
        visible |= Bank is { IsPlaying: false };
        TuningPanel.Visible = false;
        World.SetPaused(visible);
        Overlay.ShowMenu(visible);
        Player.View?.Input?.Reset();
        Capture(!visible);
    }

    public void OpenTuning()
    {
        tuningVisited = true;
        if (TuningPanel.Visible) { CloseTuning(); return; }
        World.SetPaused(true);
        Overlay.ShowMenu(false);
        TuningPanel.Toggle();
        Player.View?.Input?.Reset();
        Capture(false);
    }

    private void CloseTuning()
    {
        TuningPanel.Visible = false;
        SetMenu(false);
    }

    private static void Capture(bool captured) => Input.MouseMode = captured ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;

    public override void _Process(double delta)
    {
        if (!Initialized || !Session.IsAlive || !CreateLocalView) return;
        var view = Player.View!;
        var camera = view.CameraRig.Camera;
        var radius = Mathf.Tan(Mathf.DegToRad(Player.SpreadDegrees)) / Mathf.Tan(Mathf.DegToRad(camera.Fov / 2)) *
            GetViewport().GetVisibleRect().Size.Y / 2;
        view.Hud.SetCrosshair(radius, !World.Paused && !Player.IsRagdolled, Player.AimAmount);
        view.Hud.SetTanks(LiveTanks.Count(), animate: false);
        if (suburb != null) view.Hud.SetTankArrows(camera, suburb.TankGroupCenters(), World.Tuning.TankArrows);
        Overlay.UpdateStatus();
    }

    internal void BuildStack()
    {
        start = new Vector3(0, 0.05f, World.Tuning.LabStackDistance);
        startYaw = 0;
        AddPile(Vector3.Zero, World.Tuning.LabStackCount);
        Marker(Vector3.Zero, "HUGE STACK", tanks.Max(t => t.Position.Y) + PileBuilder.Height + 0.5f, scale: World.Tuning.LabStackDistance / 10);
    }

    internal void BuildRange()
    {
        start = new Vector3(0, 0.05f, 4);
        startYaw = 0;
        var tuning = World.Tuning;
        for (var lane = 0; lane < tuning.LabRangeLanes; lane++)
        {
            var distance = tuning.LabRangeStep * (lane + 1);
            var at = new Vector3((lane - (tuning.LabRangeLanes - 1) / 2f) * tuning.LabLaneSpacing, 0, start.Z - distance);
            AddPile(at, lane % 2 == 0 ? 1 : tuning.LabRangePileCount);
            // Labels grow with distance so every lane reads at about the same size from the firing line.
            Marker(at, $"{distance:0} m", tanks.Where(t => t.Position.DistanceTo(at) < 2).Select(t => t.Position.Y).DefaultIfEmpty(0).Max() + PileBuilder.Height + 0.5f,
                scale: Mathf.Max(1, distance / 10));
        }
        World.ContentRoot.AddChild(new MeshInstance3D
        {
            Position = new Vector3(0, 0.01f, start.Z),
            Mesh = new BoxMesh { Size = new Vector3((tuning.LabRangeLanes - 1) * tuning.LabLaneSpacing + 2, 0.015f, 0.1f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = UiStyle.Accent, Roughness = 1 },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        });
        Marker(new Vector3(0, 0, start.Z + 2), "FIRING LINE", 0.02f, floorText: true);
    }

    internal void BuildTuning()
    {
        if (suburb != null) return;
        BuildRange();
    }

    internal void BuildDuel()
    {
        var tuning = World.Tuning;
        if (suburb != null) return;
        start = new Vector3(0, 0.05f, tuning.LabArenaRadius);
        startYaw = 0;
        for (var i = 0; i < tuning.LabDuelPiles; i++)
        {
            var angle = (i + 0.5f) * Mathf.Tau / tuning.LabDuelPiles;
            AddPile(new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * tuning.LabArenaRadius * 0.55f, tuning.LabDuelPileCount);
        }
        // Half a step off the start points, so neither actor begins standing on a marker.
        for (var i = 0; i < 8; i++)
        {
            var angle = (i + 0.5f) * Mathf.Tau / 8;
            Marker(new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * tuning.LabArenaRadius, null, 0, floorText: true);
        }
    }

    private void AddPile(Vector3 at, int count)
    {
        var tuning = World.Tuning;
        var spacing = new Vector3(tuning.PileSpacing, tuning.PileLayerHeight, tuning.PileDepthSpacing);
        foreach (var transform in PileBuilder.Build(PileFormation.Square, count, spacing, tuning.PileUprightLayerHeight))
        {
            var tank = PropaneTank.Create(World);
            tank.Transform = new Transform3D(transform.Basis, transform.Origin + at);
            World.ContentRoot.AddChild(tank);
            tank.MarkPile();
            tanks.Add(tank);
        }
    }

    /// <summary>An orange disk on the floor, with a label unless <paramref name="text"/> is null.</summary>
    private void Marker(Vector3 at, string? text, float height, bool floorText = false, float scale = 1)
    {
        World.ContentRoot.AddChild(new MeshInstance3D
        {
            Position = at + Vector3.Up * 0.01f,
            Mesh = new CylinderMesh { TopRadius = 0.65f, BottomRadius = 0.65f, Height = 0.015f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = UiStyle.Accent, Roughness = 1 },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        });
        if (text == null) return;
        World.ContentRoot.AddChild(new Label3D
        {
            // A larger label grows about its own center, so lift it by the extra half-height to stay clear of the pile.
            Position = at + Vector3.Up * (floorText ? height : height + 0.19f * (scale - 1)),
            RotationDegrees = floorText ? new Vector3(-90, 0, 0) : Vector3.Zero,
            Text = text,
            FontSize = 64,
            PixelSize = 0.006f * scale,
            Modulate = UiStyle.Ink,
            OutlineModulate = Colors.White,
            Billboard = floorText ? BaseMaterial3D.BillboardModeEnum.Disabled : BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = false
        });
    }

    private sealed class Mode(LabSession lab) : ISessionMode
    {
        public void CreateActors(SessionContext session)
        {
            lab.environment = lab.source is VoidWorldSource empty ? empty.Environment : ((SuburbWorldSource)lab.source).Environment;
            if (lab.source is SuburbWorldSource generated)
            {
                lab.suburb = generated.Suburb;
                lab.tanks.AddRange(lab.suburb.LiveTanks);
                lab.start = Suburb.ToWorld(lab.suburb.Plan.Spawn, 0.05f);
                lab.startYaw = lab.suburb.Plan.SpawnYaw + Mathf.Pi;
                if (lab.Scenario.Banking)
                {
                    var spawn = lab.suburb.Plan.Spawns[0];
                    lab.start = Suburb.ToWorld(spawn.Position, 0.05f);
                    lab.startYaw = spawn.Yaw + Mathf.Pi;
                }
            }
            lab.Scenario.Setup(lab);
            lab.Player = new PlayerCharacter
            {
                World = session.World,
                Name = "Player",
                ActorId = 1,
                OwnerPeerId = 1,
                Ammo = lab.Scenario.Definition.Supply.LimitedAmmo ? AmmoState.Starting(session.World.Tuning) : null
            };
            session.AddActor(lab.Player);
            lab.Player.Teleport(lab.start, lab.startYaw);
            lab.environment.Follow = lab.Player;
            if (lab.Scenario.Banking)
            {
                lab.Opponent = new PlayerCharacter
                {
                    World = session.World,
                    Name = "BankingBot",
                    ActorId = 2,
                    OwnerPeerId = 1,
                    Ammo = AmmoState.Starting(session.World.Tuning),
                    BodyColor = new Color(0.26f, 0.58f, 0.8f)
                };
                session.AddActor(lab.Opponent);
                var at = new Vector3(0, 0.05f, -session.World.Tuning.LabArenaRadius);
                var yaw = Mathf.Pi;
                if (lab.suburb != null)
                {
                    var spawn = lab.suburb.Plan.Spawns[1];
                    at = Suburb.ToWorld(spawn.Position, 0.05f); yaw = spawn.Yaw + Mathf.Pi;
                }
                lab.Opponent.Teleport(at, yaw);
                lab.Bank = new LocalBank { Lab = lab, Name = "LocalBank", ProcessPhysicsPriority = -10 };
                session.World.Root.AddChild(lab.Bank);
            }
            session.World.ContentRoot.AddChild(new TankGather { World = session.World, Tanks = () => lab.LiveTanks, Name = "Gather" });
        }

        public void CreateViews(SessionContext session)
        {
            if (!lab.CreateLocalView) return;
            session.AddLocalView(lab.Player.Runtime, lab, new DeviceInput(session.World.Tuning));
            session.AddView(lab, new Hitstop { World = session.World, Focus = lab.Player, Name = "Hitstop" });
            var tuningPanel = new TuningPanel
            {
                Target = session.World.Tuning,
                IncludeLabDials = true,
                Note = "Session only · reset rebuilds layout",
                Name = "Tuning"
            };
            tuningPanel.CloseRequested += lab.CloseTuning;
            lab.TuningPanel = session.AddView(lab, tuningPanel);
            lab.Overlay = session.AddView(lab, new LabOverlay { Lab = lab, Name = "LabControls" });
        }

        public void Ready(SessionContext session)
        {
            if (!lab.CreateLocalView) return;
            Warmup.RunEffects(session.World, session.World.Root, new Vector3(0, -400, 0));
            Capture(true);
            if (lab.Scenario.OpensTuning)
                session.World.Schedule(lab, session.World.Tuning.LabTuningOpenDelay, () => { if (!lab.tuningVisited) lab.OpenTuning(); });
        }
    }

    internal void TrackDrop(PropaneTank tank) => tanks.Add(tank);
}
