using static Godot.GodotObject;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Fx;
using Propane.Lab;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;
using Propane.UI;
using static Propane.Dev.UnitTests.TestRunner;

using static Propane.Dev.LabCases.LabScenarioTools;

namespace Propane.Dev.LabCases;

internal sealed class BankScenario(ILabScenarioHost host, bool soak = false) : LabScenario(host)
{
    protected override Task RunCore() => Duel(soak);

    private async Task Duel(bool soak = false)
    {
        if (DevArgs.Get("duel-seconds") != null) template.LabDuelSeconds = DevArgs.GetFloat("duel-seconds", 180);
        template.LabBankBots = (int)DevArgs.GetFloat("bots", 1);
        DevArgs.ApplyTuningOverrides(template);
        var lab = Enter(LabScenarios.Get("duel"), DevArgs.Get("environment") == "suburb" ? LabEnvironment.Suburb : LabEnvironment.Void);
        Assert(lab.Session.Actors.Count == template.LabBankBots + 1, "banking roster ignored the bot count");
        Assert(lab.Session.Actors.Select(a => a.Character.GlobalPosition).Distinct().Count() == lab.Session.Actors.Count, "banking actors share a start");
        Assert(lab.Session.Actors.Where(a => a.Character != lab.Player).All(a => a.Character.View == null && a.Character.InputOverride is IBotGoalPolicy),
            "banking bots need owned brains without local views");
        if (soak) lab.Bank!.AttachBot(lab.Player);
        else lab.Player.InputOverride = new ScriptedPlayer(lab);
        // The scripted player taps visible tanks through the actor intent/aim path; neither bank is granted.
        var seconds = (int)DevArgs.GetFloat("seconds", soak ? 180 : 45);
        var shots = (DevArgs.Get("shots") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var shot = 0;
        var frame = 0;
        while (lab.Bank!.IsPlaying && lab.World.Clock.Now < seconds)
        {
            Assert(frame++ < seconds * 120, "duel simulation stalled");
            if (DevArgs.Get("trace") != null && frame % 120 == 0) GD.Print($"[lab trace] {lab.World.Clock.Now:F1}s {lab.Bank.BotTrace}");
            if (shot < shots.Length && lab.World.Clock.Now >= shots[shot]) capture.Screenshot($"duel_{shots[shot++]:000}s");
            await Frames(1).ConfigureAwait(true);
        }
        var bank = lab.Bank!;
        CheckIdle("duel", bank.Activity);
        ReportPoints(lab, "bank", template.FixedSeed, bank.Scores, bank.Earned);
        ReportSkills(lab, template.FixedSeed, bank.Scores);
        var botEarned = bank.Earned.Where(s => s.Key != lab.Player.ActorId).Sum(s => s.Value);
        GD.Print($"[lab duel] seconds={seconds} simulation={lab.World.Clock.Now:F3} banked={bank.BankedTotal} spilled={bank.SpilledTotal} hits={bank.Hits} you={bank.Scores[1]} bot={bank.Scores[2]} earned_you={bank.Earned[1]} earned_bot={bank.Earned[2]} live={lab.LiveTanks.Count()} earned_bots={botEarned}");
        // In a larger roster the first rival can finish empty while the other bots bank.
        Assert(bank.BankedTotal > 0 && botEarned > 0, "banking bots earned no bank");
        // A bot that freezes for most of the round (the suburb once banked one tank in 180 s) must not pass.
        var earned = bank.Earned.Values.Sum();
        if (soak) Assert(earned >= 10, $"banking bots earned only {earned} tanks");
        Assert(bank.Scores.Values.Sum() + lab.LiveTanks.Count() == bank.InitialSupply, "bank conservation");
        if (!bank.IsPlaying)
        {
            await Frames(2).ConfigureAwait(true);
            var best = bank.Scores.Where(s => s.Key != lab.Player.ActorId).OrderByDescending(s => s.Value).First();
            var mine = bank.Scores[lab.Player.ActorId];
            var name = template.LabBankBots == 1 ? "BOT" : $"BOT {best.Key - 1}";
            var expected = mine > best.Value ? "YOU WIN" : mine == best.Value ? "TIED" : $"{name} WINS";
            Assert(lab.Overlay.ResultTitle == expected, "banking result ignored the strongest bot");
            Assert(lab.Overlay.StatusText.Contains($"  {best.Value}    ·", StringComparison.Ordinal), "banking HUD omitted the best opponent's score");
        }
        Assert(lab.Opponent!.View == null && !lab.World.Networked, "duel needs camera/network");
        capture.Screenshot(soak ? "duel_soak_end" : "duel_bot_bank");
        if (!bank.IsPlaying)
        {
            await Frames(20).ConfigureAwait(true);
            capture.Screenshot("duel_round_end");
            Assert(lab.World.Paused && lab.Overlay.MenuVisible, "round end must pause and show the Lab menu");
        }
        lab.Session.Dispose(); lab.Free();
    }

    private sealed class ScriptedPlayer(LabSession lab) : IPlayerInputSource
    {
        private readonly BotAim aim = new();
        private PropaneTank? target;
        private float cooldown;
        public PlayerIntent Read()
        {
            if (!lab.Bank!.IsPlaying) return default;
            cooldown -= 1f / 60;
            if (target == null || !IsInstanceValid(target) || target.State == PropaneTank.TankState.Exploded)
                target = lab.LiveTanks.OrderBy(t => t.GlobalPosition.DistanceSquaredTo(lab.Player.GlobalPosition)).FirstOrDefault();
            if (target == null) return default;
            aim.Track(lab.Player, target.GlobalTransform * PropaneTank.LocalCenter, 1f / 60, 1, smooth: false);
            var fire = cooldown <= 0;
            if (fire) cooldown = .25f;
            return new PlayerIntent { Aim = true, FirePressed = fire, ReloadPressed = lab.Player.Ammo!.Magazine == 0 };
        }
    }
}
