using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Lab;
using Propane.Tank;
using static Propane.Dev.LabCases.LabScenarioTools;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

internal sealed class BankRulesScenario(ILabScenarioHost host) : LabScenario(host)
{
    protected override Task RunCore() => BankRules();

    private async Task BankRules()
    {
        template.LabDuelPiles = 2;
        template.LabDuelPileCount = 1;
        template.LabDuelSeconds = 30;
        template.PickupRespawnTime = .1f;
        var lab = Enter(LabScenarios.Get("duel"), LabEnvironment.Void);
        var bot = lab.Opponent!;
        Assert(lab.Player.ActorId == 1 && bot.ActorId == 2 && lab.Player.OwnerPeerId == bot.OwnerPeerId,
            "local human and bot need distinct actors on the same peer");
        bot.InputOverride = new IdleInput();
        lab.Player.InputOverride = new IdleInput();
        lab.Player.Teleport(new Vector3(0, .05f, 18), 0);
        bot.Teleport(new Vector3(4, .05f, 18), Mathf.Pi / 2);
        var tanks = lab.LiveTanks.ToArray();
        tanks[0].Position = Vector3.Zero;
        tanks[1].Position = new Vector3(.5f, 0, 0);
        foreach (var tank in tanks) tank.Freeze = true;
        lab.Player.InputOverride = new IdleInput(aim: true);
        await Frames(60).ConfigureAwait(true);
        await FireAt(lab.Player, () => tanks[0].GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
        Assert(tanks[0].State == PropaneTank.TankState.Venting && lab.Bank!.Scores[1] == 0, "first shot must puncture, not bank");
        await FireAt(lab.Player, () => tanks[1].GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
        Assert(tanks[1].State == PropaneTank.TankState.Venting, "second fixture did not puncture");
        await FireAt(lab.Player, () => tanks[0].GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
        await Frames(90).ConfigureAwait(true);
        Assert(lab.Bank!.Scores[1] == 2 && !lab.LiveTanks.Any(), "root and delayed chain must credit the shooter");
        lab.Player.InputOverride = new IdleInput();
        await FireAt(bot, () => lab.Player.GlobalPosition + Vector3.Up * 1.2f).ConfigureAwait(true);
        Assert(lab.Player.Runtime.LastHitBy == bot.ActorId && bot.ActorId != bot.OwnerPeerId,
            "same-peer hit attribution used the peer instead of the bot actor");
        var afterHit = lab.Bank.Scores[1];
        Assert(afterHit == Mathf.Max(0, 2 - lab.World.Tuning.HitDropCount) && lab.LiveTanks.All(t => t.Invulnerable), "hit spill/count/shield");
        await FireAt(bot, () => lab.Player.GlobalPosition + Vector3.Up * 1.2f).ConfigureAwait(true);
        Assert(lab.Bank.Scores[1] == afterHit, "hit grace allowed a second spill");
        bot.InputOverride = new IdleInput();
        lab.Player.ReceiveMelee(Vector3.Forward);
        var afterMelee = lab.Bank.Scores[1];
        Assert(afterMelee == Mathf.Max(0, afterHit - lab.World.Tuning.RagdollDropCount), "melee/ragdoll spill");
        lab.Player.ReceiveMelee(Vector3.Forward);
        Assert(lab.Bank.Scores[1] == afterMelee, "already down player spilled twice");
        Assert(lab.Bank.Scores.Values.Sum() + lab.LiveTanks.Count() == lab.Bank.InitialSupply, "bank conservation after spills");
        bot.Ammo!.Reserve = 0;
        bot.Teleport(lab.Bank.Pickups[0].GlobalPosition, 0);
        await Frames(2).ConfigureAwait(true);
        Assert(bot.Ammo.Reserve == lab.World.Tuning.PickupAmount && !lab.Bank.Pickups[0].Available, "local pickup admission");
        bot.Teleport(new Vector3(4, .05f, 18), 0);
        await Frames(20).ConfigureAwait(true);
        Assert(lab.Bank.Pickups[0].Available, "local ammo did not respawn");
        lab.SetMenu(true);
        var clock = lab.Bank.TimeLeft;
        await Frames(30).ConfigureAwait(true);
        Assert(lab.Bank.TimeLeft == clock, "pause advanced bank clock");
        lab.SetMenu(false);
        await Frames(30 * 60).ConfigureAwait(true);
        Assert(!lab.Bank.IsPlaying && lab.World.Paused && lab.Overlay.MenuVisible, "clock did not end/freeze duel");
        lab.Session.Dispose(); lab.Free();
        GD.Print("[lab rules] PASS two-shot/chain attribution, hit grace, shielded spill, melee, conservation, ammo respawn, pause and round end");
    }
}
