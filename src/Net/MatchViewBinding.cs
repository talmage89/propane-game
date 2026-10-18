using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Modes;
using Propane.Player;
using Propane.Session;
using Propane.Tank;
using Propane.UI;
using Propane.World;

namespace Propane.Net;

/// <summary>One physical session's selected actor and presentation bindings. SessionContext owns native view free;
/// this owner clears selection at the original roster notifications and retires during replication stop.
/// Reveal, notice and objective refresh clocks consume Match's render delta; devices remain LocalView-owned.</summary>
internal sealed class MatchViewBinding : IDisposable
{
    private const float RevealMaxRadius = 320f;
    private const float MaterializeShare = 0.58f;
    private readonly SessionContext Session;
    private WorldContext World => Session.World;
    private readonly Node parent;
    private readonly NetClient Net;
    private readonly MatchSetup Setup;
    private readonly IReadOnlyDictionary<int, PlayerProfile> profiles;
    private readonly bool CreateLocalView;
    private readonly bool Returning;
    private readonly int me;
    private readonly Func<bool> playing;
    private readonly Func<IModeView?> modeView;
    private readonly Action leave;
    private readonly Action<NetWriter, TrafficClass> send;
    private ModeDefinition mode = null!;
    private IModeHudPresenter hudPresenter = null!;
    private IModeClient? clientMode;
    private IModeClient ClientMode => clientMode ?? throw new InvalidOperationException("view mode is not attached");
    private static readonly IReadOnlyDictionary<int, int> EmptyScores = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, int> Scores { get; private set; } = EmptyScores;
    private ModeClientDisplay modeDisplay;
    public ModeClientDisplay Display => modeDisplay;
    private VoidEnvironment environment = null!;
    private Suburb suburb = null!;
    private PlayerCharacter? player;
    private LocalView? localView;
    private Hud hud = null!;
    private MatchHud matchHud = null!;
    private MatchMenu menu = null!;
    private ResultsScreen results = null!;
    private TuningPanel? tuningPanel;
    private KingZone? kingZone;
    private LobbyFloor? lobbyFloor;
    private float revealTime;
    private bool revealing = true;
    private float groupRefresh;
    private List<Vector3> tankGroups = new();
    private string noticeText = "";
    private float noticeLeft;
    private readonly List<LeaderboardEntry> leaderboardRoster = new();
    private bool IsLobby => Setup.ModeId == ModeRegistry.LobbyWorldId;
    private bool Staging => Net.Lobby is { Phase: LobbyPhase.Staging };
    public PlayerCharacter? Player => player;
    public bool MenuVisible => menu.Visible;
    public int LobbyRefills => lobbyFloor?.Materialized ?? 0;

    public MatchViewBinding(SessionContext session, Node parent, NetClient net, MatchSetup setup,
        IReadOnlyDictionary<int, PlayerProfile> profiles, bool createLocalView, bool returning, int peer,
        Func<bool> playing, Func<IModeView?> modeView, Action leave, Action<NetWriter, TrafficClass> send)
    {
        Session = session;
        this.parent = parent;
        Net = net;
        Setup = setup;
        this.profiles = profiles;
        CreateLocalView = createLocalView; Returning = returning;
        me = peer;
        this.playing = playing;
        this.modeView = modeView;
        this.leave = leave;
        this.send = send;
    }
    public void Configure(ModeDefinition definition) { mode = definition; hudPresenter = mode.CreateHud(); }
    public void BindMode(IModeClient adapter)
    {
        clientMode = adapter;
        // The adapter guarantees a stable live score container and immutable display choices.
        // Sample both at configuration, before constructing the physical replica, as Match did.
        Scores = adapter.Scores;
        modeDisplay = adapter.Display;
    }
    public void AttachContent(VoidEnvironment environment, Suburb suburb) { this.environment = environment; this.suburb = suburb; }
    public void AddRosterEntry(PlayerProfile profile) => leaderboardRoster.Add(new LeaderboardEntry(profile.Id, profile.Name, profile.Appearance.Color));
    private void OnLocalViewExiting() => localView = null;
    public void Prepare()
    {
        suburb.SetRevealSide(1f);
        if (player != null) World.Reveal.Center = player.GlobalPosition;
        World.Reveal.Radius = 0f;
        Warmup.Run(World, World.Root, new Vector3(0, -400f, 0));
        CaptureMouse(true);
        if (modeDisplay.WaitingNotice.Length > 0) ShowNotice(modeDisplay.WaitingNotice, 30f);
    }
    public void Reveal(float dt)
    {
        if (!revealing) return;
        var tuning = World.Tuning;
        revealTime += dt;
        var t = Mathf.Clamp(revealTime / Mathf.Max(tuning.TransitionTime * MaterializeShare, 0.01f), 0, 1);
        World.Reveal.Radius = Mathf.Lerp(0f, RevealMaxRadius, t * t);
        if (t >= 1f)
        {
            revealing = false;
            suburb.SetRevealSide(0f);
            World.Reveal.Radius = 100000f;
        }
    }
    public void TickHud(float dt, ModePhase phase, float timeLeft)
    {
        noticeLeft -= dt;
        matchHud.SetNotice(noticeLeft > 0 ? noticeText : "");
        UpdateHud(dt, phase, timeLeft);
    }
    public ModeHud PresentHud(ModePhase phase, float timeLeft, IReadOnlyList<Vector3> objectives, bool showObjectives) =>
        hudPresenter.Present(new ModeHudInput(player?.ActorId ?? 0, phase, timeLeft, Scores, objectives, showObjectives, View: modeView()));
    public void ShowCountdown(string text) => matchHud.ShowCountdown(text);
    public void ScoreChanged(int delta, ModePhase phase, float timeLeft)
    {
        matchHud.Show(PresentHud(phase, timeLeft, tankGroups, World.Tuning.TankArrows), delta > 0);
        if (delta > 0) matchHud.AddScorePopup(delta);
    }
    public void HitMarker(PlayerCharacter actor) { if (actor == player) matchHud.ShowHitMarker(); }
    public void HitDirection(PlayerCharacter actor, Vector3 from) { if (actor == player) matchHud.AddHitDirection(from); }
    public void AmmoPopup(PlayerCharacter actor, int amount) { if (actor == player) matchHud.AddAmmoPopup(amount); }
    public void TuningChanged(PlayerCharacter? remote, string name)
    {
        tuningPanel?.Refresh();
        if (remote != null) ShowNotice($"{profiles[remote.ActorId].Name} changed {name}", 2.5f);
    }
    public void ShowResults(IReadOnlyList<Standing> standings, float seconds, float timeLeft)
    {
        if (player != null) results.Show(standings, player.ActorId, seconds, PresentHud(ModePhase.Results, timeLeft, Array.Empty<Vector3>(), false).Columns);
    }
    public void MaterializePile(ClientPileView view, IReadOnlyList<PropaneTank> built, IReadOnlyList<BodyState> placements)
    {
        if (view == ClientPileView.Zone) kingZone?.Materialize(built, PileSite.Center(placements.Select(t => t.Transform).ToArray()));
        else lobbyFloor?.Materialize(built, PileSite.Center(placements.Select(t => t.Transform).ToArray()));
    }
    public void TitleChanged(LobbyInfo lobby) { if (lobbyFloor != null) lobbyFloor.Title = LobbyText.Title(lobby); }
    public void FaceLobby() { if (lobbyFloor != null && player != null) lobbyFloor.Facing = player.FacingYaw; }
    public void AddLobbyCard()
    {
        if (CreateLocalView)
        {
            Session.AddView(parent, new LobbyCard
            {
                Net = Net,
                Name = "LobbyCard",
                Arrival = Returning ? "Back in the lobby, ready for another" : "Roam, shoot and throw tanks until the match starts",
            });
        }
    }
    public void PanelChanged(bool open) { if (World.IsAlive && !menu.Visible) CaptureMouse(!open && !Staging); }
    public void StageChanged(bool staging, bool panelOpen)
    {
        if (staging) menu.Visible = false;
        if (CreateLocalView) CaptureMouse(!staging && !panelOpen);
    }
    private static void CaptureMouse(bool captured) => Input.MouseMode = captured ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
    public void Dispose()
    {
        clientMode = null;
        Scores = EmptyScores;
        modeDisplay = default;
        player = null;
        localView = null;
    }

    public void RefreshPlayer()
    {
        player = null;
        if (localView != null && !Session.Actors.Any(actor => actor.Character == localView.Actor)) localView = null;
        if (!Session.IsAlive) return;
        foreach (var actor in Session.Actors)
        {
            if (actor.Character.IsRemote) continue;
            player ??= actor.Character;
            if (profiles[actor.Character.ActorId].Kind != ActorKind.Human) continue;
            player = actor.Character;
            break;
        }
    }

    public void BuildViews()
    {
        if (CreateLocalView && player != null)
        {
            localView = Session.AddLocalView(player.Runtime, parent, new DeviceInput(World.Tuning));
            environment.Follow = localView.CameraRig.Camera;
            localView.TreeExiting += OnLocalViewExiting;
            hud = localView.Hud;
            hud.ShowTankCount(false);
        }
        matchHud = new MatchHud { World = World, Name = "MatchHud" };
        Session.AddView(parent, matchHud);
        matchHud.Show(PresentHud(ModePhase.Loading, -1, tankGroups, false));
        BuildKingZone();
        results = new ResultsScreen { Name = "Results" };
        Session.AddView(parent, results);
        menu = new MatchMenu { Name = "Menu", Lobby = IsLobby };
        menu.ResumeRequested += CloseMenu;
        menu.LeaveRequested += () => World.Defer(() => leave(), whilePaused: true);
        Session.AddView(parent, menu);
        if (OS.IsDebugBuild() && Setup.CreatorDebug && Setup.CreatorId == me)
        {
            tuningPanel = new TuningPanel
            {
                Target = World.Tuning,
                Name = "Tuning",
                Note = "Changes apply to everyone in the match",
                UseDeclaredRanges = true,
                LockedProperties = new[] { nameof(Tuning.TankCountMultiplier) }
            };
            tuningPanel.ValueChanged += (name, value) => send(new NetWriter(Msg.TuningSet, Setup.Id).String(name).Scalar(value), TrafficClass.ReliableOrdered);
            Session.AddView(parent, tuningPanel);
        }

    }

    public void UpdateHud(float dt, ModePhase phase, float timeLeft)
    {
        if (player == null) return;
        var view = localView?.Actor == player ? localView : LocalView.Find(player);
        if (view == null) return;
        var tuning = World.Tuning;
        var camera = view.CameraRig.Camera;
        var viewportHeight = parent.GetViewport().GetVisibleRect().Size.Y;
        var radius = Mathf.Tan(Mathf.DegToRad(player.SpreadDegrees)) / Mathf.Tan(Mathf.DegToRad(camera.Fov / 2f)) * viewportHeight / 2f;
        // The lobby world's stage screen stands in for the HUD: no tags, notices or crosshair through its lineup.
        var menuOpen = menu.Visible || tuningPanel?.Visible == true || ClientMode.SuppressHud;
        hud.SetCrosshair(radius, !player.IsRagdolled && Input.MouseMode == Input.MouseModeEnum.Captured && phase != ModePhase.Results, player.AimAmount);

        groupRefresh -= dt;
        if (groupRefresh <= 0)
        {
            groupRefresh = 0.25f;
            tankGroups = phase is ModePhase.Round or ModePhase.Countdown ? suburb.TankGroupCenters() : new List<Vector3>();
        }
        var shown = PresentHud(phase, timeLeft, tankGroups, tuning.TankArrows);
        hud.SetTankArrows(camera, shown.Objectives, shown.ObjectivesVisible);
        hud.Visible = !menuOpen;
        matchHud.Visible = !menuOpen;

        matchHud.Show(shown);
        matchHud.SetAmmo(player.Ammo);
        matchHud.SetView(camera, player.GlobalPosition);
        matchHud.SetTags(ScoreTags(camera));
        for (var i = 0; i < leaderboardRoster.Count; i++)
        {
            if (leaderboardRoster[i].Present && Session.Actor(leaderboardRoster[i].Id) == null) leaderboardRoster[i] = leaderboardRoster[i] with { Present = false };
        }
        matchHud.SetStandings(leaderboardRoster, Scores, player.ActorId, phase is ModePhase.Countdown or ModePhase.Round);
    }

    public List<ScoreTag> ScoreTags(Camera3D camera)
    {
        var tags = new List<ScoreTag>();
        var tuning = World.Tuning;
        var space = World.Physics.DirectSpaceState;
        var from = camera.GlobalPosition;
        foreach (var remote in Session.Actors.Where(a => a.Character != player))
        {
            var at = remote.Character.TagPosition;
            var distance = from.DistanceTo(at);
            if (distance > tuning.ScoreTagRange || camera.IsPositionBehind(at))
            {
                continue;
            }
            var ray = PhysicsRayQueryParameters3D.Create(from, at, Layers.World | Layers.Props);
            if (space.IntersectRay(ray).Count > 0)
            {
                continue;
            }
            var scale = tuning.ScoreTagScale * Mathf.Lerp(1.15f, 0.6f, Mathf.Clamp(distance / Mathf.Max(tuning.ScoreTagRange, 1f), 0, 1));
            var profile = profiles[remote.Character.ActorId];
            // The lobby world has no scores: everyone's name floats over them instead.
            tags.Add(new ScoreTag(camera.UnprojectPosition(at), Scores.GetValueOrDefault(remote.Character.ActorId), profile.Appearance.Color, scale,
                modeDisplay.NameTags ? profile.Name : null));
        }
        return tags;
    }

    public void InputEvent(InputEvent @event)
    {
        if (World is not { IsAlive: true }) { return; }
        if (@event.IsActionPressed(InputSetup.Pause))
        {
            if (tuningPanel?.Visible == true)
            {
                tuningPanel.Toggle();
                CaptureMouse(true);
            }
            else if (menu.Visible)
            {
                CloseMenu();
            }
            else
            {
                menu.Visible = true;
                CaptureMouse(false);
            }
            parent.GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed(InputSetup.ToggleTuning) && tuningPanel != null && !menu.Visible)
        {
            tuningPanel.Toggle();
            CaptureMouse(!tuningPanel.Visible);
        }
        else if (@event.IsActionPressed(InputSetup.ToggleFullscreen))
        {
            var fullscreen = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
            DisplayServer.WindowSetMode(fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
        }
        else if (@event is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured &&
                 !menu.Visible && tuningPanel?.Visible != true)
        {
            CaptureMouse(true);
        }
    }

    public void CloseMenu()
    {
        menu.Visible = false;
        CaptureMouse(true);
    }

    public void ShowNotice(string text, float seconds)
    {
        noticeText = text;
        noticeLeft = seconds;
    }

    public void BuildLobbyContent()
    {
        var tuning = World.Tuning;
        lobbyFloor = new LobbyFloor
        {
            World = World,
            Name = "LobbyFloor",
            Spots = LobbyLayout.Spots(tuning.LobbyPiles, tuning.LobbyPileRing),
            Title = Net.Lobby is { } lobby ? LobbyText.Title(lobby) : "",
        };
        suburb.AddChild(lobbyFloor);
    }

    public void BuildKingZone()
    {
        if (ClientMode is not KingClientAdapter || !CreateLocalView) return;
        kingZone = new KingZone
        {
            World = World,
            Name = "KingZone",
            State = () => playing() ? modeView() as KingState : null,
            ActorColor = id => profiles.TryGetValue(id, out var profile) ? profile.Appearance.Color : null,
        };
        suburb.AddChild(kingZone);
    }
}
