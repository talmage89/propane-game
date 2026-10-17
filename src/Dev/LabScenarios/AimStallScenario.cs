using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Lab;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;
using static Propane.Dev.LabCases.LabScenarioTools;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

internal sealed class AimStallScenario(ILabScenarioHost host) : LabScenario(host)
{
    protected override Task RunCore() => AimStall();

    /// <summary>
    /// The bot's vantage search ignores props that still stop the rifle's ray. It must give a pile up rather than
    /// stand in front of it, aimed at the obstruction, for the rest of the round.
    /// </summary>
    private async Task AimStall()
    {
        template.LabDuelPiles = 2;
        template.LabDuelPileCount = 1;
        template.LabDuelSeconds = 60;
        var lab = Enter(LabScenarios.Get("duel"), LabEnvironment.Void);
        var tanks = lab.LiveTanks.ToArray();
        tanks[0].Position = new Vector3(-1, 0, -16);
        tanks[1].Position = new Vector3(1, 0, -16);
        foreach (var tank in tanks) tank.Freeze = true;
        lab.Player.InputOverride = new IdleInput();
        lab.Player.Teleport(new Vector3(0, .05f, 25), 0);
        lab.Opponent!.Teleport(new Vector3(0, .05f, 0), 0);
        // Debris stops shots but not the vantage search's sight test or a walking player.
        var wall = new StaticBody3D { CollisionLayer = Layers.Debris, CollisionMask = 0, Position = new Vector3(0, 3, -5) };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60, 6, .3f) } });
        lab.World.ContentRoot.AddChild(wall);
        await Frames(3 * 60).ConfigureAwait(true);
        Assert(lab.Bank!.BotStatus.StartsWith("shooting", StringComparison.Ordinal) && lab.Opponent.Ammo!.Magazine == lab.Opponent.Ammo.Tuning.MagazineSize,
            $"fixture must stall the bot on its aim first ({lab.Bank.BotStatus})");
        await Frames(6 * 60).ConfigureAwait(true);
        Assert(lab.Bank.BotBlacklisted >= 2, $"bot never gave up a pile with no clear shot ({lab.Bank.BotStatus})");
        // Hard King's points estimate must validate the rifle path, including shot-only debris. Start with a
        // clear prize, then put the blocker back after selection: the periodic choice must abandon it too.
        lab.Bank.SetPhysicsProcess(false);
        using var king = new KingBot(1) { PointsDriven = true };
        using var view = new BlockedKingWorld(lab);
        lab.Opponent.InputOverride = king;
        wall.CollisionLayer = 0;
        await Frames(1).ConfigureAwait(true);
        king.Prepare(view);
        view.Services.BeginTick(lab.BotTanks);
        king.Think(view, 1f / 60);
        Assert(king.Status.StartsWith("points: banking", StringComparison.Ordinal), "King did not choose the clear prize");
        wall.CollisionLayer = Layers.Debris;
        for (var frame = 0; frame < 3 * 60; frame++)
        {
            view.Services.BeginTick(lab.BotTanks);
            king.Think(view, 1f / 60);
            await Frames(1).ConfigureAwait(true);
        }
        Assert(!king.Status.StartsWith("points: banking", StringComparison.Ordinal) && tanks.All(t => t.State == PropaneTank.TankState.Intact),
            $"Hard King kept choosing prizes behind shot-only debris ({king.Status})");
        // A valid route with no actual movement must eventually fail, even while the prototype motor tries its
        // direct walking recovery. Lock the actor (gravity/collision remain live) to hold that condition exactly.
        lab.Opponent.InputOverride = new IdleInput();
        lab.Opponent.InputLocked = true;
        await Frames(60).ConfigureAwait(true); // Let the previous zone run lose its momentum before measuring a stall.
        var motor = new BotMotor();
        motor.Prepare(view);
        var anchor = lab.Opponent.GlobalPosition;
        var goal = anchor + Vector3.Right * 10;
        var recoveryFailed = false;
        for (var frame = 0; frame < 6 * 60 && !recoveryFailed; frame++)
        {
            view.Services.BeginTick(lab.BotTanks);
            motor.Begin(1f / 60);
            motor.MoveTo(goal);
            motor.Navigate(view, 1f / 60);
            recoveryFailed = motor.Failed;
            await Frames(1).ConfigureAwait(true);
        }
        Assert(recoveryFailed && BotMotor.Flat(lab.Opponent.GlobalPosition - anchor).Length() < lab.World.Tuning.BotProgressDistance,
            $"direct walking recovery failed={recoveryFailed} travel={BotMotor.Flat(lab.Opponent.GlobalPosition - anchor).Length():F3}: {motor.NavigationDiagnostics}");
        lab.Opponent.InputOverride = null;
        lab.Session.Dispose(); lab.Free();
        GD.Print("[lab aimstall] PASS bankers and Hard King leave blocked shots; walking recovery preserves bounded route failure");
    }

    private sealed class BlockedKingWorld(LabSession lab) : IBotWorld, IDisposable
    {
        public WorldContext World => lab.World;
        public PlayerCharacter Player => lab.Opponent!;
        public bool IsPlaying => true;
        public float TimeLeft => 60;
        public int MyScore => 0;
        public IReadOnlyDictionary<int, int> Scores { get; } = new Dictionary<int, int>();
        public double Now => lab.World.Clock.Now;
        public double LastHitTime => double.NegativeInfinity;
        public int LastHitBy => 0;
        public IReadOnlyList<PlayerCharacter> RemoteBodies => Array.Empty<PlayerCharacter>();
        public IReadOnlyList<AmmoPickup> Pickups => Array.Empty<AmmoPickup>();
        public BotServices Services { get; } = new(lab.World);
        public KingBotView? King => new KingBotView(0, new ModeSpot(100, 0, 0), new ModeSpot(200, 0, 0), 6, 0, false, 0.1);
        public void Dispose() => Services.Dispose();
    }
}
