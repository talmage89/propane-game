using System;
using System.Globalization;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.UI;
using Propane.World;
using Propane.World.Plan;
using ProcessModeEnum = Godot.Node.ProcessModeEnum;

namespace Propane.Dev.NetScenarios;

internal sealed class AppearanceScenario(INetScenarioHost host) : NetScenario(host)
{
    private Menus? appearanceMenus;
    private float appearanceTime = -1;
    private bool appearanceDropped;
    private bool appearanceGlowChecked;
    private bool appearanceResultsChecked;

    public override void Initialize()
    {
        appearanceMenus = new Menus { Net = net, Visible = false };
        Attach(appearanceMenus);
    }
    public override void LobbyChanged(LobbyInfo lobby)
    {
        if (lobby.Phase == LobbyPhase.Waiting && lobby.Members.Count == players)
        {
            CheckAppearances(lobby.Members);
            appearanceMenus!.Show(Menus.Screen.Lobby);
            GetTree().CreateTimer(0.15).Timeout += GuardConnection(() => director.Screenshot($"{net.PlayerName}_appearance_lobby"));
        }
    }
    public override void MatchStarting(MatchSetup setup)
    {
        appearanceMenus?.Show(Menus.Screen.None);
        CheckAppearances(setup.Players.Select(p => p.Player));
    }
    protected override void Assembled(Match match)
    {
        appearanceTime = -1;
        appearanceDropped = appearanceGlowChecked = appearanceResultsChecked = false;
    }
    protected override void Run(Match match, float dt) => Appearance(match, dt);

    private void CheckAppearances(System.Collections.Generic.IEnumerable<PlayerProfile> profiles)
    {
        var expected = (DevArgs.Get("expected-appearances") ?? throw new ArgumentException("appearance scenario requires -c colors")).Split(',');
        foreach (var profile in profiles)
        {
            var index = int.Parse(profile.Name.AsSpan(3), CultureInfo.InvariantCulture) - 1;
            ScenarioAssert(index >= 0 && index < expected.Length && profile.Appearance == NetScenarioTools.ParseAppearance(expected[index]),
                $"appearance: accepted profile differs for {profile.Name}");
        }
    }

    /// <summary>
    /// A stationary lineup in a real match. Each client sends one bounded drop request from its own bank, without
    /// any color field; every copy must render the dropper's frozen MatchStart profile. Uses the actual results UI.
    /// </summary>
    private void Appearance(Match match, float dt)
    {
        if (match.IsOver)
        {
            if (appearanceResultsChecked) { return; }
            var results = match.GetNode<ResultsScreen>("Results");
            foreach (var (profile, _) in match.Setup.Players)
            {
                var key = profile.Id.ToString(CultureInfo.InvariantCulture);
                ScenarioAssert(results.FindChildren("*", "ColorRect", owned: false).OfType<ColorRect>()
                    .Any(c => c.HasMeta("appearance_player") && c.GetMeta("appearance_player").AsString() == key && c.Color == profile.Appearance.Color),
                    $"appearance: results sample differs for {profile.Name}");
            }
            appearanceResultsChecked = true;
            scenarioPassed = appearanceGlowChecked && !scenarioFailed;
            GetTree().CreateTimer(0.2).Timeout += Guard(() => director.Screenshot($"{net.PlayerName}_appearance_results"));
            Log($"appearance: assertions passed={scenarioPassed}, profiles/bodies/drops/results {match.Setup.Players.Count}");
            return;
        }
        if (!match.IsPlaying) { return; }
        if (appearanceTime < 0)
        {
            appearanceTime = 0;
            match.Player.InputOverride = new ScriptedInput();
            var slot = match.Setup.Players.Single(p => p.Player.OwnerPeerId == net.MyId).Spawn;
            var road = match.Suburb.Plan.Roads.OrderByDescending(r => r.Length).First();
            var center = PlanCoordinates.ToWorld((road.A + road.B) / 2, 0.02f);
            var along = new Vector3(road.Direction.X, 0, road.Direction.Y);
            var side = new Vector3(-along.Z, 0, along.X);
            var position = center + along * ((slot - (players - 1) / 2f) * 2.5f);
            match.Player.Teleport(position, Mathf.Atan2(-side.X, -side.Z));
            if (match.Player.View is { } view)
            {
                var camera = view.CameraRig.Camera;
                view.CameraRig.ProcessMode = ProcessModeEnum.Disabled;
                camera.GlobalPosition = center + side * 7 + Vector3.Up * 3.4f;
                camera.LookAt(center + Vector3.Up);
                camera.Fov = 60;
            }
        }
        appearanceTime += dt;
        var own = match.Setup.Players.Single(p => p.Player.OwnerPeerId == net.MyId);
        ScenarioAssert(match.Player.BodyColor == own.Player.Appearance.Color, "appearance: local body differs");
        foreach (var remote in match.RemoteBodies)
        {
            var profile = match.Setup.Players.Single(p => p.Player.Id == remote.ActorId).Player;
            ScenarioAssert(remote.BodyColor == profile.Appearance.Color, $"appearance: remote body differs for {profile.Name}");
        }
        if (!appearanceDropped && appearanceTime >= 2)
        {
            appearanceDropped = true;
            var toward = (match.Player.View?.CameraRig.Camera.GlobalPosition ?? match.Player.Aim.EyeOrigin) - match.Player.GlobalPosition;
            toward.Y = 0;
            var at = match.Player.GlobalPosition + toward.Normalized() * 1.3f;
            var body = new BodyState(BodyIds.DropTankId(own.Player.AllocationSlot, 900000), new Transform3D(Basis.Identity, at), Vector3.Zero, Vector3.Zero);
            net.Send(MessageCodec.Encode(new NetMessage(Msg.ModeEvent, match.Setup.Id, 0, new ModeEnvelope(1, 1, Modes.ModeRegistry.TankBankId, 1, match.Player.ActorId,
                match.Player.LifeId, checked(++match.Player.Runtime.ActionSequence), ModeSchemas.TankBankKinds.Spill, new TankBankSpill(Modes.SpillReason.Hit, new[] { body }))), false));
        }
        if (!appearanceGlowChecked && appearanceTime >= 2.5f)
        {
            var drops = match.LiveTanks.Where(IsDrop).ToArray();
            if (drops.Length == players)
            {
                foreach (var (profile, slot) in match.Setup.Players)
                {
                    var tank = drops.Single(t => t.NetId == BodyIds.DropTankId(slot, 900000));
                    var glow = tank.GetNode<MeshInstance3D>("Mesh").GetInstanceShaderParameter("shield_color").AsColor();
                    ScenarioAssert(tank.Invulnerable && glow.A > 0 && new Color(glow, 1) == profile.Appearance.Color,
                        $"appearance: shield differs for {profile.Name}");
                }
                appearanceGlowChecked = !scenarioFailed;
                director.Screenshot($"{net.PlayerName}_appearance_tags_glow");
                Log($"appearance: {drops.Length} accepted drop colors match on this client");
            }
            else if (appearanceTime > 4) { ScenarioAssert(false, "appearance: missing accepted drops"); }
        }
    }
}
