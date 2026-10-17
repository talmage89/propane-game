using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.NetScenarios;

internal sealed class KingScenario(INetScenarioHost host) : NetScenario(host)
{
    private Camera3D? kingCamera;
    private readonly HashSet<string> kingCaptured = new();
    private int kingStage = -1;
    private int kingInitialTanks;
    private bool kingHeld;
    private bool kingContested;
    private bool kingWarned;
    private bool kingMoved;
    private bool kingPiled;

    protected override void Preparing(Match match)
    {
        kingStage = -1; kingCamera = null; kingCaptured.Clear();
        kingHeld = kingContested = kingWarned = kingMoved = kingPiled = false;
    }
    protected override void Run(Match match, float dt) => King(match);

    /// <summary>
    /// Two real network peers stage their owner-authoritative positions, while the server alone decides King.
    /// Captures and assertions follow received state, including a moved zone's newly registered world pile.
    /// </summary>
    private void King(Match current)
    {
        if (current.IsOver)
        {
            KingShot("results");
            return;
        }
        if (!current.IsPlaying || current.ModeView is not KingState state) return;
        if (kingStage < 0)
        {
            current.Player.InputOverride = new ScriptedInput();
            kingInitialTanks = PlanCodec.Decode(current.Setup.Plan).Tanks.Count;
            if (DisplayServer.GetName() != "headless")
            {
                kingCamera = new Camera3D { Current = true, Fov = 55 };
                current.AddChild(kingCamera);
            }
        }
        var elapsed = state.Elapsed;
        var stage = state.Zone > 0 ? 3 : elapsed >= 10 ? 2 : elapsed >= 6 ? 1 : 0;
        var spot = new Vector3((float)state.Spot.X, (float)state.Spot.Y, (float)state.Spot.Z);
        if (stage != kingStage)
        {
            kingStage = stage;
            // From 6 to 10 both stand inside; otherwise only the creator does. Keep clear of the original pile.
            var inside = role == "host" || stage == 1;
            var offset = new Vector3(role == "host" ? -1 : 1, 0, 0) * (float)state.Radius * (inside ? .7f : 1.8f);
            current.Player.Teleport(spot + offset + Vector3.Up * .05f, 0);
        }
        if (state.Warning)
        {
            var next = new Vector3((float)state.Next.X, (float)state.Next.Y, (float)state.Next.Z);
            var center = (spot + next) / 2;
            kingCamera?.LookAtFromPosition(center + new Vector3(24, 20, 28), center + Vector3.Up);
        }
        else if (elapsed >= 48 && elapsed < 50 && current.LiveTanks.Where(t => t.NetId >= BodySync.MapTankId(kingInitialTanks) &&
                     t.NetId < BodySync.DropTankBase).ToArray() is { Length: > 0 } fresh)
        {
            var center = fresh.Aggregate(Vector3.Zero, (sum, t) => sum + t.GlobalPosition) / fresh.Length;
            kingCamera?.LookAtFromPosition(center + new Vector3(5, 4, 6), center);
        }
        else
        {
            kingCamera?.LookAtFromPosition(spot + new Vector3(14, 10, 16), spot + Vector3.Up);
        }

        kingHeld |= state.Holder == current.Setup.Players.Single(p => p.Player.OwnerPeerId == current.Setup.CreatorId).Player.Id;
        kingContested |= state.Contested;
        kingWarned |= state.Warning;
        kingMoved |= state.Zone > 0;
        kingPiled |= current.LiveTanks.Any(t => t.NetId >= BodySync.MapTankId(kingInitialTanks) && t.NetId < BodySync.DropTankBase);
        if (state.Holder != 0 && state.Minted.Count > 0 && state.Zone == 0) KingShot("hold");
        if (state.Contested) KingShot("contest");
        if (state.Warning) KingShot("warning");
        if (kingPiled) KingShot("moved_pile");
        if (kingPiled && elapsed >= 48) KingShot("settled_pile");
        if (!scenarioPassed && elapsed >= 50)
        {
            ScenarioAssert(kingHeld && kingContested && kingWarned && kingMoved && kingPiled && state.Minted.Count > 0,
                "King: missing authoritative holder, contest, mint, warning, move or registered fresh pile");
            scenarioPassed = !scenarioFailed;
            if (scenarioPassed) Log("King: snapshot-driven hold, contest, mint, warning, move and fresh pile passed");
        }
    }

    private async void KingShot(string label)
    {
        if (DisplayServer.GetName() == "headless" || DevArgs.Get("capture-dir") == null || !kingCaptured.Add(label)) return;
        var captured = Epoch;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (IsCurrent(captured) && IsInsideTree()) director.ScreenshotAsync($"{net.PlayerName}_king_{label}");
    }
}
