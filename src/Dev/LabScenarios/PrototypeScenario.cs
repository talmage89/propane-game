using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Lab;
using Propane.Modes;
using static Propane.Dev.LabCases.LabScenarioTools;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

internal sealed class PrototypeScenario(ILabScenarioHost host) : LabScenario(host)
{
    protected override Task RunCore() => Prototypes();

    /// <summary>
    /// Full prototype rounds with a bot on every actor (the player's too), headless, across seeds: each must end with
    /// a legal outcome. Ring Out: a winner, every starter placed in elimination order, the out spectating. King: the
    /// clock ran out, the bank is conserved with every mint counted, and each zone move materialized a pile (in a
    /// suburb, unless no clear ground was within reach).
    /// --quick shortens the round's dials; --bots sets the Lab bots beside the player (default 2: three brains).
    /// </summary>
    private async Task Prototypes()
    {
        var id = DevArgs.Get("mode") ?? "ringout";
        var scenario = LabScenarios.Get(id);
        var environment = DevArgs.Get("environment") == "suburb" ? LabEnvironment.Suburb : LabEnvironment.Void;
        var seeds = (int)DevArgs.GetFloat("seeds", 3);
        var firstSeed = (int)DevArgs.GetFloat("first-seed", 1);
        template.LabPrototypeBots = (int)DevArgs.GetFloat("bots", 2);
        if (DevArgs.Get("quick") != null)
        {
            template.RingOutLots = 3;
            template.RingOutBandSeconds = 6;
            template.RingOutWarningSeconds = 2;
            template.RingOutRoundLimit = 45;
            template.KingRoundSeconds = 30;
            template.KingMoveSeconds = 12;
        }
        DevArgs.ApplyTuningOverrides(template);
        var until = DevArgs.GetFloat("until", float.MaxValue); // Captures stop early and skip the outcome checks.
        var shots = (DevArgs.Get("shots") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var minted = 0;
        for (var seed = firstSeed; seed < firstSeed + seeds; seed++)
        {
            var lab = new LabSession
            {
                Scenario = scenario,
                EnvironmentKind = environment,
                Template = template,
                PauseOnFocusLoss = false,
                TestRandomSeed = (ulong)seed,
                TestSeedPhysicsRandom = DevArgs.Get("replay-random") != null,
                DrivePlayer = DevArgs.Get("watch") == null,
                Name = "Lab"
            };
            AddLab(lab);
            var runner = lab.Prototype!;
            if (DevArgs.Get("overview") != null)
            {
                // Review captures: a high camera on the whole arena, rather than the bot-driven player's own view.
                var reach = id == "king" ? lab.World.Tuning.KingSpotRing + 8 : lab.RingOut!.HalfWidth;
                var camera = new Camera3D { Name = "Overview", Fov = 50, Far = 2000, Current = true };
                lab.AddChild(camera);
                camera.LookAtFromPosition(new Vector3(0, reach * 1.05f, reach * 1.75f), new Vector3(0, -reach * 0.1f, 0), Vector3.Up);
            }
            Assert(lab.Session.Actors.Count == template.LabPrototypeBots + 1 && lab.LiveTanks.Any(), $"{id} roster or fixture");
            var moves = 0;
            double loneSeconds = 0, sampleTime = 0;
            var observe = Guard<ModeCommand>(command => { if (command is ZoneMoved { Zone: > 0 }) moves++; });
            runner.Decided += observe;
            using var observation = DetachOnDisposal(() => runner.Decided -= observe);
            var limit = (id == "king" ? lab.World.Tuning.KingRoundSeconds : lab.World.Tuning.RingOutRoundLimit) + 5;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var shot = 0;
            var frame = 0;
            for (; runner.IsPlaying && runner.Elapsed < until; frame++)
            {
                // Hitstop slows simulation while process frames keep arriving. Bound the rules' own clock;
                // check_lab's process timeout also catches a stalled clock without weakening the outcome checks.
                Assert(runner.Elapsed < limit, $"{id} seed {seed}: the round never ended at {runner.Elapsed:F1}s");
                if (DevArgs.Get("trace") != null && frame % 120 == 0)
                {
                    GD.Print($"[lab trace] {runner.Elapsed:F1}s " + string.Join(" | ", lab.Session.Actors.Select(a =>
                        $"{a.Character.ActorId} {a.Character.GlobalPosition.X:F1},{a.Character.GlobalPosition.Y:F1},{a.Character.GlobalPosition.Z:F1} {runner.BotStatus(a.Character.ActorId)}")));
                }

                var elapsed = runner.Elapsed;
                if (runner.Rules is KingRules { Holder: not 0 }) loneSeconds += elapsed - sampleTime;
                sampleTime = elapsed;
                if (shot < shots.Length && runner.Elapsed >= shots[shot]) capture.Screenshot($"{id}_{environment}_{seed}_{shots[shot++]:000}s".ToLowerInvariant());
                await Frames(1).ConfigureAwait(true);
            }
            var wall = watch.Elapsed.TotalSeconds;
            if (runner.IsPlaying)
            {
                GD.Print($"[lab prototype] {id} seed={seed} capture stopped at {runner.Elapsed:F1}s");
                lab.Session.Dispose(); lab.Free();
                continue;
            }
            CheckIdle($"{id} seed {seed}", runner.Activity);
            var state = runner.Rules.Snapshot();
            foreach (var line in state.Report) GD.Print($"[lab prototype] {id} seed {seed}: {line.Text}");
            var brains = runner.Thinks / System.Math.Max(1, runner.Elapsed * 120);
            GD.Print($"[lab prototype] {id} seed={seed} environment={environment} round={runner.Elapsed:F1}s wall={wall:F1}s frames={frame} " +
                $"brains={brains:F1} think_ms_per_tick={runner.ThinkSeconds * 1000 / System.Math.Max(1, runner.Elapsed * 120):F3} " +
                $"thinks={runner.Thinks} think_us_per_call={runner.ThinkSeconds * 1e6 / System.Math.Max(1, runner.Thinks):F2}");
            switch (runner.Rules)
            {
                case RingOutRules ring:
                {
                    var starters = runner.Roster.Select(a => a.ActorId).ToArray();
                    var winners = starters.Where(a => ring.Place(a) == 1).ToArray();
                    Assert(ring.Over && ring.PlacementsComplete && winners.Length >= 1, $"ring out seed {seed}: no winner or incomplete placements");
                    Assert(starters.All(a => ring.Place(a) is >= 1 and var p && p <= starters.Length), "placements out of range");
                    Assert(RingEliminationsAgree(ring, starters, runner.Spectating), "eliminations and spectators disagree");
                    // Elimination order is placement order: anyone out later never places worse.
                    Assert(ring.Eliminations.Zip(ring.Eliminations.Skip(1)).All(p => p.Second.Place <= p.First.Place), "placements out of elimination order");
                    Assert(ring.Eliminations.Sum(e => e.CreditedTo != 0 ? 1 : 0) == starters.Sum(ring.Credits), "credit totals");
                    Assert(runner.Elapsed < ring.Plan.Collapsed + 1 || lab.RingOut!.SolidLots == 1, "the rim must have dissolved to one lot");
                    break;
                }
                case KingRules king:
                {
                    var bank = king.Bank;
                    ReportPoints(lab, "king", seed, runner.Scores, runner.Roster.ToDictionary(a => a.ActorId, a => bank.StatsFor(a.ActorId).Gained));
                    Assert(runner.Elapsed >= lab.World.Tuning.KingRoundSeconds - 0.01, "king ended before its clock");
                    Assert(bank.Conserved && bank.Minted == runner.Roster.Sum(a => king.MintedBy(a.ActorId)), $"king seed {seed}: bank not conserved");
                    Assert(runner.Roster.All(a => runner.Scores.GetValueOrDefault(a.ActorId) == bank.Bank(a.ActorId)), "scores disagree with the bank");
                    // Every move builds a fresh pile on clear ground; the open void always has some within reach.
                    var arena = lab.King!;
                    Assert(moves == king.Zone && arena.Materialized + arena.Unplaced == moves && (environment == LabEnvironment.Suburb || arena.Unplaced == 0),
                        $"zone moves {moves}, zone {king.Zone}, piles {arena.Materialized}, unplaced {arena.Unplaced}");
                    minted += bank.Minted;
                    GD.Print(FormattableString.Invariant($"[king crowd] seed={seed} actors={runner.Roster.Count} seconds={runner.Elapsed:F2} minted={bank.Minted} per_minute={bank.Minted * 60 / runner.Elapsed:F2} lone_share={loneSeconds / runner.Elapsed:F4} minters={runner.Roster.Count(a => king.MintedBy(a.ActorId) > 0)}"));
                    break;
                }
            }
            await Frames(20).ConfigureAwait(true);
            capture.Screenshot($"{id}_{environment}_{seed}_end".ToLowerInvariant());
            Assert(lab.World.Paused && lab.Overlay.MenuVisible, "a decided round must pause and show the Lab menu");
            lab.Session.Dispose(); lab.Free();
            observation.Dispose();
            await Collect().ConfigureAwait(true);
        }
        // Bot play, not legality: across full rounds somebody must hold the zone long enough to mint.
        if (id == "king" && DevArgs.Get("quick") == null && DevArgs.Get("compare-points") == null && until == float.MaxValue)
            Assert(minted >= DevArgs.GetFloat("min-king-mints", 1), $"king: insufficient mints ({minted}) across {seeds} rounds");
    }
}
