using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Dev.LabCases;
using Propane.Fx;
using Propane.Lab;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;
using Propane.UI;
using static Propane.Dev.LabCases.LabScenarioTools;
using static Propane.Dev.UnitTests.TestRunner;
using LabCase = Propane.Dev.LabCases.LabScenario;

namespace Propane.Dev;

/// <summary>Real Lab sessions: retirement, fixture counts, session-only tuning, local banks and player-camera frames.</summary>
public partial class LabTest : Node, ILabScenarioHost
{
    internal static bool RingEliminationsAgree(RingOutRules ring, IReadOnlyList<int> starters, Func<int, bool> spectating) =>
        LabScenarioTools.RingEliminationsAgree(ring, starters, spectating);

    private CaptureDirector capture = null!;
    private Tuning template = null!;
    private Equivalence.LabTrace? equivalence;
    private LabCase? activeScenario;

    Tuning ILabScenarioHost.Template => template;
    CaptureDirector ILabScenarioHost.Capture => capture;
    Equivalence.LabTrace? ILabScenarioHost.Trace => equivalence;
    SceneTree ILabScenarioHost.Tree => GetTree();
    Viewport ILabScenarioHost.Viewport => GetViewport();
    void ILabScenarioHost.Attach(Node node) => AddChild(node);
    void ILabScenarioHost.AddLab(LabSession lab) => AddLab(lab);
    LabSession ILabScenarioHost.Enter(Propane.Lab.LabScenario scenario, LabEnvironment environment) => Enter(scenario, environment);
    Task ILabScenarioHost.Frames(int count) => Frames(count);
    Task ILabScenarioHost.Collect() => Collect();
    (int, int, int) ILabScenarioHost.Counts() => Counts();

    public override void _ExitTree() => activeScenario?.Dispose();

    private void AddLab(LabSession lab)
    {
        if (DevArgs.Get("equivalence-trace") != null)
        {
            Assert(equivalence == null, "trace fixtures must use one Lab session");
            equivalence = new Equivalence.LabTrace { Lab = lab, ProcessPhysicsPriority = int.MaxValue };
            equivalence.OpenDetail();
            lab.TestObserveCommand = equivalence.Command;
            lab.TestObserveTank = equivalence.Track;
        }
        AddChild(lab);
        if (equivalence != null) { AddChild(equivalence); equivalence.Begin(); }
    }
    public override async void _Ready()
    {
        DevArgs.MarkReady();
        capture = new CaptureDirector { CollectFrameMetrics = DevArgs.Get("scenario") != "measure-play" }; AddChild(capture);
        template = Tuning.LoadBaked();
        if (DevArgs.Get("equivalence-trace") != null) template.HitstopDuration = 0;
        template.FixedSeed = 23;
        template.LabStackCount = 32;
        if (DevArgs.Get("bot-skill") != null) template.LabBotSkill = DevArgs.GetFloat("bot-skill", template.LabBotSkill);
        try
        {
            var selected = DevArgs.Get("scenario") ?? "lifetime";
            activeScenario = LabScenarioRegistry.Create(selected, this);
            if (activeScenario != null)
            {
                await activeScenario.Execute().ConfigureAwait(true);
                activeScenario.CheckCompletion();
            }
            else
            {
                switch (selected)
                {
                    case "frames": await FramesReview().ConfigureAwait(true); break;
                    case "rules": await BankRules().ConfigureAwait(true); break;
                    case "duel": await Duel().ConfigureAwait(true); break;
                    case "soak": await Duel(soak: true).ConfigureAwait(true); break;
                    case "effects": await EffectFrames().ConfigureAwait(true); break;
                    case "measure": await Measure().ConfigureAwait(true); break;
                    case "measure-play": await MeasurePlay().ConfigureAwait(true); break;
                    case "stackfire": await StackFire().ConfigureAwait(true); break;
                    case "aimstall": await AimStall().ConfigureAwait(true); break;
                    case "prototype": await Prototypes().ConfigureAwait(true); break;
                    case "shortfuse": await ShortFuseMatches().ConfigureAwait(true); break;
                    case "shortfuse-combo": await ShortFuseCombo().ConfigureAwait(true); break;
                    case "reference-retirement": await ReferenceRetirement().ConfigureAwait(true); break;
                    case "dissolve": await DissolveRun().ConfigureAwait(true); break;
                    default: throw new ArgumentException($"[lab] unknown scenario '{selected}'");
                }
            }

            activeScenario?.Dispose();
            equivalence?.Finish();
            template.Dispose();
            GD.Print("[lab] PASS");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            activeScenario?.Dispose();
            GD.PushError($"[lab] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private LabSession Enter(Propane.Lab.LabScenario scenario, LabEnvironment environment)
    {
        var lab = new LabSession
        {
            Scenario = scenario,
            EnvironmentKind = environment,
            Template = template,
            PauseOnFocusLoss = false,
            TestRandomSeed = (ulong)DevArgs.GetFloat("effects-seed", 23),
            TestSeedPhysicsRandom = DevArgs.Get("replay-random") != null,
            Name = "Lab"
        };
        AddLab(lab);
        Assert(lab.Initialized, $"{scenario.Id}/{environment} initialization");
        return lab;
    }




    private async Task FramesReview()
    {
        template.LabStackCount = Tuning.LabStackMaximum;
        var main = new Main { SavedTuning = template };
        AddChild(main);
        await Frames(60).ConfigureAwait(true);
        capture.Screenshot("main_menu");
        Press("Lab");
        await Frames(12).ConfigureAwait(true);
        capture.Screenshot("lab_menu");
        foreach (var scenario in LabScenarios.All)
        {
            Press(scenario.Name);
            await Frames(12).ConfigureAwait(true);
            capture.Screenshot($"options_{scenario.Id}");
            if (scenario.Environments.Count > 1)
            {
                Press("Environment: Void");
                await Frames(12).ConfigureAwait(true);
                capture.Screenshot($"options_{scenario.Id}_suburb");
            }
            Press("Back to scenarios");
            await Frames(6).ConfigureAwait(true);
        }
        main.Free();
        if (DevArgs.Get("menus-only") != null) return;
        foreach (var scenario in LabScenarios.All)
        {
            foreach (var environment in scenario.Environments)
            {
                var lab = Enter(scenario, environment);
                await Frames(60).ConfigureAwait(true);
                if (lab.TuningPanel.Visible)
                {
                    capture.Screenshot($"{scenario.Id}_{environment}_panel");
                    lab.SetMenu(false);
                }
                await Frames(30).ConfigureAwait(true);
                capture.Screenshot($"{scenario.Id}_{environment}_player");
                lab.SetMenu(true);
                await Frames(8).ConfigureAwait(true);
                capture.Screenshot($"{scenario.Id}_{environment}_controls");
                if (environment == LabEnvironment.Void)
                {
                    // The controls follow the last device used: a controller press switches every hint.
                    Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = JoyButton.DpadDown, Pressed = true });
                    Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = JoyButton.DpadDown, Pressed = false });
                    await Frames(8).ConfigureAwait(true);
                    capture.Screenshot($"{scenario.Id}_{environment}_controls_pad");
                    lab.SetMenu(false);
                    await Frames(8).ConfigureAwait(true);
                    capture.Screenshot($"{scenario.Id}_{environment}_player_pad");
                    // Back to the keyboard hints before the next scenario, which the live overlay must see.
                    Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.A, Keycode = Key.A, Pressed = true });
                    Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.A, Keycode = Key.A, Pressed = false });
                    await Frames(3).ConfigureAwait(true);
                }
                lab.Session.Dispose(); lab.Free();
                await Collect().ConfigureAwait(true);
            }
        }
    }




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
            runner.Decided += command => { if (command is ZoneMoved { Zone: > 0 }) moves++; };
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
            await Collect().ConfigureAwait(true);
        }
        // Bot play, not legality: across full rounds somebody must hold the zone long enough to mint.
        if (id == "king" && DevArgs.Get("quick") == null && DevArgs.Get("compare-points") == null && until == float.MaxValue)
            Assert(minted >= DevArgs.GetFloat("min-king-mints", 1), $"king: insufficient mints ({minted}) across {seeds} rounds");
    }

    /// <summary>
    /// Whole Short Fuse matches with a bot on every actor (the player's too), headless, across seeds: rounds rebuild
    /// the suburb, and each must end legally. Every death is a starter's, once, credited to someone else or nobody;
    /// the dead spectate and leave the round; the winner was alive; the match winner reached the wins; kills add up;
    /// every life advanced with its round; and the Lab's reports, arriving at once, were never late, early, duplicated
    /// or naming an unknown actor or blast. --quick shortens rounds and the match; --capture-events screenshots a
    /// blast-then-bullet kill, the kill feed, spectating and the round-win banner as they first happen.
    /// </summary>
    private async Task ShortFuseMatches()
    {
        var scenario = LabScenarios.Get("shortfuse");
        var seeds = (int)DevArgs.GetFloat("seeds", 2);
        var firstSeed = (int)DevArgs.GetFloat("first-seed", 1);
        template.LabPrototypeBots = (int)DevArgs.GetFloat("bots", 2);
        if (DevArgs.Get("quick") != null)
        {
            template.ShortFuseRoundSeconds = 60;
            template.ShortFuseWinsToMatch = 2;
            template.ShortFuseRoundEndSeconds = 1;
        }
        DevArgs.ApplyTuningOverrides(template);
        var events = DevArgs.Get("capture-events") != null;
        int combos = 0, deaths = 0, roundsPlayed = 0, limits = 0;
        for (var seed = firstSeed; seed < firstSeed + seeds; seed++)
        {
            template.FixedSeed = 100 + seed * 37;
            var lab = new LabSession
            {
                Scenario = scenario,
                EnvironmentKind = LabEnvironment.Suburb,
                Template = template,
                PauseOnFocusLoss = false,
                TestRandomSeed = (ulong)seed,
                DrivePlayer = DevArgs.Get("watch") == null,
                Name = "Lab"
            };
            AddLab(lab);
            var match = lab.Match!;
            var tuning = lab.World.Tuning;
            var starters = lab.Session.Actors.Select(a => a.Character.ActorId).ToArray();
            Assert(starters.Length == template.LabPrototypeBots + 1 && lab.LiveTanks.Any() && lab.ShortFuse!.Spawns.Count == starters.Length, "roster or fixture");
            var maxRounds = starters.Length * (tuning.ShortFuseWinsToMatch - 1) + 1;
            var limit = maxRounds * (tuning.ShortFuseRoundSeconds + tuning.ShortFuseRoundEndSeconds + 10) * 60;
            PrototypeRunner? watched = null;
            var captured = new HashSet<string>();
            var pending = new List<(int Frame, string Name)>();
            var frame = 0;
            void Capture(string name, int delay)
            {
                if (events && captured.Add(name)) pending.Add((frame + delay, $"shortfuse_{seed}_{name}"));
            }
            void Watch(PrototypeRunner runner)
            {
                watched = runner;
                var rules = (ShortFuseRules)runner.Rules;
                var spawns = lab.ShortFuse!.Spawns.Select(p => p.Position).ToArray();
                Assert(spawns.SelectMany(a => spawns.Where(b => b != a), (a, b) => a.DistanceTo(b)).DefaultIfEmpty(99).Min() > 10, $"seed {seed}: crowded starts");
                Assert(lab.Session.Actors.All(a => a.Character.LifeId == match.Round && rules.Health.Health(a.Character.ActorId, runner.Clock.Now) == rules.MaxHealth),
                    $"seed {seed} round {match.Round}: lives must advance with the round, at full health");
                runner.Decided += command =>
                {
                    switch (command)
                    {
                        case ActorDied died:
                            deaths++;
                            Assert(starters.Contains(died.VictimId) && died.CreditedTo != died.VictimId && (died.CreditedTo == 0 || starters.Contains(died.CreditedTo)),
                                $"seed {seed}: illegal death {died}");
                            Assert(rules.RoundDeaths.Count(d => d.VictimId == died.VictimId) == 1, $"seed {seed}: {died.VictimId} died twice");
                            var body = lab.Session.Actor(died.VictimId)!.Character;
                            if (died.Kind == DamageKind.Bullet && body.IsRagdolled) { combos++; Capture("blast_then_bullet", 2); }
                            if (died.VictimId == lab.Player.ActorId) Capture("spectating", (int)((tuning.ShortFuseCorpseSeconds + 1.5f) * 60));
                            if (rules.RoundDeaths.Count >= 2) Capture("kill_feed", 10);
                            break;
                        case RoundWon won:
                        {
                            roundsPlayed++;
                            var alive = starters.Where(rules.IsAlive).ToArray();
                            var timedOut = runner.Elapsed >= tuning.ShortFuseRoundSeconds - 0.01;
                            if (timedOut) limits++;
                            Assert(alive.Length + rules.RoundDeaths.Count == starters.Length, $"seed {seed}: alive {alive.Length} + dead {rules.RoundDeaths.Count}");
                            Assert(timedOut || alive.Length <= 1, $"seed {seed}: the round ended early with {alive.Length} alive");
                            Assert(won.WinnerId == 0 || alive.Contains(won.WinnerId), $"seed {seed}: the winner {won.WinnerId} was not alive");
                            Assert(rules.RoundDeaths.All(d => runner.Spectating(d.VictimId)) && alive.All(a => !runner.Spectating(a)), $"seed {seed}: spectators disagree");
                            Capture("round_win", 30);
                            GD.Print($"[lab shortfuse] seed={seed} round={won.RoundId} winner={won.WinnerId} time={runner.Elapsed:F1}s deaths=" +
                                string.Join(",", rules.RoundDeaths.Select(d => $"{d.VictimId}:{d.Kind}:{d.KilledBy}>{d.CreditedTo}")) +
                                $" bots={string.Join(" | ", starters.Select(a => $"{a} {runner.BotStatus(a)}"))}");
                            break;
                        }
                    }
                };
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (; lab.Prototype is not { Finished: true }; frame++)
            {
                Assert(frame < limit, $"seed {seed}: the match never ended");
                if (lab.Prototype is { } runner && runner != watched) Watch(runner);
                foreach (var shot in pending.Where(p => p.Frame <= frame).ToArray())
                {
                    capture.Screenshot(shot.Name);
                    pending.Remove(shot);
                }
                if (DevArgs.Get("trace") != null && frame % 300 == 0 && lab.Prototype is { } traced)
                {
                    GD.Print($"[lab trace] {traced.Elapsed:F1}s " + string.Join(" | ", lab.Session.Actors.Select(a =>
                        $"{a.Character.ActorId} {((ShortFuseRules)traced.Rules).Health.Health(a.Character.ActorId, traced.Clock.Now):0} {traced.BotStatus(a.Character.ActorId)} pos={a.Character.GlobalPosition} nav={traced.BotNavigation(a.Character.ActorId)}")));
                }
                await Frames(1).ConfigureAwait(true);
            }
            var fuse = (ShortFuseRules)match.Rules!;
            foreach (var line in fuse.Snapshot().Report) GD.Print($"[lab shortfuse] seed {seed}: {line.Text}");
            var health = fuse.Health;
            Assert(fuse.MatchOver && fuse.MatchWinner is { } champion && fuse.Wins(champion) == tuning.ShortFuseWinsToMatch &&
                starters.Count(a => fuse.Wins(a) >= tuning.ShortFuseWinsToMatch) == 1, $"seed {seed}: no single match winner");
            Assert(fuse.Rounds.Count == match.Round && fuse.Rounds.Count(r => r.WinnerId != 0) == starters.Sum(fuse.Wins), $"seed {seed}: round wins disagree");
            Assert(health.Deaths.Count(d => d.CreditedTo != 0) == starters.Sum(fuse.Kills) && health.Deaths.Count == starters.Sum(fuse.DeathsOf), $"seed {seed}: kills disagree");
            Assert(new[] { DamageRefusal.Late, DamageRefusal.Future, DamageRefusal.Duplicate, DamageRefusal.UnknownAttacker, DamageRefusal.UnknownVictim,
                DamageRefusal.UnknownBlast, DamageRefusal.SelfHit }.All(r => health.Refused(r) == 0), $"seed {seed}: the Lab sent a bad report: {health.Audit()}");
            Assert(health.Accepted > 0 && health.Deaths.Count > 0, $"seed {seed}: nobody fought");
            GD.Print($"[lab shortfuse] seed={seed} rounds={fuse.Rounds.Count} winner={fuse.MatchWinner} wall={watch.Elapsed.TotalSeconds:F1}s");
            await Frames(20).ConfigureAwait(true);
            capture.Screenshot($"shortfuse_{seed}_end");
            Assert(lab.World.Paused && lab.Overlay.MenuVisible, "a decided match must pause and show the Lab menu");
            lab.Session.Dispose(); lab.Free();
            await Collect().ConfigureAwait(true);
        }
        // Bot play, not legality: how often a thrown player was shot dead is reported, not required (shortfuse-combo proves it).
        GD.Print($"[lab shortfuse] {seeds} matches, {roundsPlayed} rounds ({limits} at the limit), {deaths} deaths, {combos} finished while down");
    }

    /// <summary>
    /// A scripted Short Fuse round through the real reports, for review captures and as an integration check: Bot 2
    /// shoots the player dead (the player then spectates Bot 2), sets off a pile beside Bot 1, and finishes Bot 1 with
    /// bullets while it is still down; Bot 2 wins the round. Asserts each death's kind, killer and credit.
    /// </summary>
    private async Task ShortFuseCombo()
    {
        template.LabPrototypeBots = 2;
        template.FixedSeed = (int)DevArgs.GetFloat("seed", 23);
        DevArgs.ApplyTuningOverrides(template);
        var lab = new LabSession
        {
            Scenario = LabScenarios.Get("shortfuse"),
            EnvironmentKind = LabEnvironment.Suburb,
            Template = template,
            PauseOnFocusLoss = false,
            TestRandomSeed = 23,
            TestSeedPhysicsRandom = DevArgs.Get("replay-random") != null,
            Name = "Lab"
        };
        AddLab(lab);
        var runner = lab.Prototype!;
        var rules = (ShortFuseRules)runner.Rules;
        var player = lab.Player;
        var victim = lab.Session.Actor(2)!.Character;
        var shooter = lab.Session.Actor(3)!.Character;
        foreach (var actor in lab.Session.Actors) actor.Character.InputOverride = new IdleInput();
        var space = lab.World.Physics.DirectSpaceState;
        bool Clear(Vector3 at) => space.IntersectShape(new PhysicsShapeQueryParameters3D
        {
            Shape = new SphereShape3D { Radius = 0.5f },
            Transform = new Transform3D(Basis.Identity, at + Vector3.Up * 1f),
            CollisionMask = Layers.World | Layers.Props | Layers.Pushable,
        }, 1).Count == 0;
        // Nothing a bullet would stop at in between: the line ends at the target or short of it only by the target itself.
        bool Sees(Vector3 from, Vector3 to, Node? target = null)
        {
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from + Vector3.Up * 1.5f, to + Vector3.Up * 1.2f, Layers.ShotMask));
            return hit.Count == 0 || target != null && hit["collider"].As<GodotObject>() == target;
        }
        // A pile with room beside it for Bot 1, and an open line from 15 m off for Bot 2, and from 9 m off that for the player.
        (PropaneTank Tank, Vector3 Victim, Vector3 Shooter, Vector3 Player)? layout = null;
        foreach (var tank in lab.LiveTanks.OrderBy(t => t.GlobalPosition.Length()))
        {
            var ground = tank.GlobalPosition with { Y = 0.05f };
            for (var a = 0; a < 12 && layout == null; a++)
            {
                var out1 = new Vector3(Mathf.Cos(a * Mathf.Tau / 12), 0, Mathf.Sin(a * Mathf.Tau / 12));
                // Bot 1 beside the pile, off Bot 2's lane to it, so the shot reaches the tank.
                var across = new Vector3(-out1.Z, 0, out1.X);
                var at = ground + across * 3.2f;
                var from = ground + out1 * 17f;
                var side = from + (out1 - across).Normalized() * 9f;
                if (Clear(at) && Clear(from) && Clear(side) && Sees(from, at) && Sees(from, tank.GlobalTransform * PropaneTank.LocalCenter - Vector3.Up * 1.2f, tank) && Sees(side, from) &&
                    lab.LiveTanks.All(t => t.GlobalPosition.DistanceTo(from) > 8 && t.GlobalPosition.DistanceTo(side) > 8))
                {
                    layout = (tank, at, from, side);
                }
            }
            if (layout != null) break;
        }
        Assert(layout != null, "no pile with a clear shooting lane");
        var (target, victimAt, shooterAt, playerAt) = layout!.Value;
        static float Facing(Vector3 from, Vector3 to) => Mathf.Atan2(from.X - to.X, from.Z - to.Z);
        victim.Teleport(victimAt, Facing(victimAt, shooterAt));
        shooter.Teleport(shooterAt, Facing(shooterAt, victimAt));
        player.Teleport(playerAt, Facing(playerAt, shooterAt));
        var deaths = new List<ActorDied>();
        var blasts = new List<DamageApplied>();
        runner.Decided += command =>
        {
            if (command is ActorDied died) deaths.Add(died);
            if (command is DamageApplied { Kind: DamageKind.Blast } blast) blasts.Add(blast);
        };
        await Frames(60).ConfigureAwait(true);
        capture.Screenshot("shortfuse_combo_start");
        for (var shot = 0; shot < 20 && rules.IsAlive(player.ActorId); shot++)
        {
            await FireAt(shooter, () => BotMotor.AimPoint(player)).ConfigureAwait(true);
            if (shot == 4) capture.Screenshot("shortfuse_combo_chip");
        }
        Assert(deaths.SingleOrDefault() is { VictimId: 1, Kind: DamageKind.Bullet, KilledBy: 3, CreditedTo: 3 }, $"the player's death: {string.Join("; ", deaths)}");
        await Frames((int)((lab.World.Tuning.ShortFuseCorpseSeconds + 0.5f) * 60)).ConfigureAwait(true);
        // The camera behind Bot 2, looking down its lane at the pile.
        player.Aim.SetOrientation(Mathf.RadToDeg(Facing(shooterAt, victimAt)), -12f);
        await Frames(30).ConfigureAwait(true);
        Assert(lab.ShortFuse!.Parked.Contains(player) && player.Spectating == shooter, "the dead player must watch their killer");
        capture.Screenshot("shortfuse_spectating");
        await FireAt(shooter, () => target.GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
        // The shot punctures whichever tank of the pile faces the lane; that one goes up next.
        var vent = lab.LiveTanks.Where(t => t.State == PropaneTank.TankState.Venting).OrderBy(t => t.GlobalPosition.DistanceTo(target.GlobalPosition)).FirstOrDefault();
        Assert(vent != null && vent.GlobalPosition.DistanceTo(victimAt) < 6, "the first shot punctures a tank of the pile");
        await FireAt(shooter, () => vent!.GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
        Assert(victim.IsRagdolled && blasts.Any(b => b.VictimId == 2 && b.AttackerId == 3), $"the blast must throw and hurt Bot 1: {string.Join("; ", blasts)}");
        capture.Screenshot("shortfuse_combo_thrown");
        for (var shot = 0; shot < 12 && rules.IsAlive(victim.ActorId); shot++)
        {
            await FireAt(shooter, () => BotMotor.AimPoint(victim)).ConfigureAwait(true);
            if (deaths.Count == 2) capture.Screenshot("shortfuse_blast_then_bullet");
        }
        Assert(deaths.Count == 2 && deaths[1] is { VictimId: 2, Kind: DamageKind.Bullet, KilledBy: 3, CreditedTo: 3 } && victim.IsRagdolled,
            $"Bot 1 must be finished by bullets while down: {string.Join("; ", deaths)}");
        Assert(rules.Over && rules.Rounds.Single() == new RoundWon(1, 3) && !runner.IsPlaying, "Bot 2 wins the round");
        await Frames(20).ConfigureAwait(true);
        capture.Screenshot("shortfuse_round_win");
        var hud = runner.Present(Array.Empty<Vector3>());
        Assert(hud.Banner == "BOT 2 WINS THE ROUND" && hud.Feed!.SequenceEqual(new[] { "Bot 2 shot Player", "Bot 2 shot Bot 1" }), $"banner {hud.Banner}, feed {string.Join("|", hud.Feed!)}");
        GD.Print($"[lab shortfuse-combo] {rules.Health.Audit()}");
        await Frames((int)(lab.World.Tuning.ShortFuseRoundEndSeconds * 60) + 30).ConfigureAwait(true);
        Assert(lab.Prototype != runner && lab.Match!.Round == 2 && lab.Player.LifeId == 2, "the next round must build a new suburb with new lives");
        capture.Screenshot("shortfuse_round_two");
        lab.Session.Dispose(); lab.Free();
    }

    /// <summary>Real shots into the shipped maximum stack, through the whole chain, with frame times and frames.</summary>
    private async Task StackFire()
    {
        template.LabStackCount = Tuning.LabStackMaximum;
        var lab = Enter(LabScenarios.Get("stack"), LabEnvironment.Void);
        await Frames(90).ConfigureAwait(true);
        capture.Screenshot("stack_before");
        var start = lab.LiveTanks.Count();
        Assert(start == Tuning.LabStackMaximum, "stack must start at the shipped maximum");
        var target = lab.LiveTanks.OrderBy(t => t.GlobalPosition.DistanceSquaredTo(lab.Player.GlobalPosition)).First();
        await FireAt(lab.Player, () => target.GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
        Assert(target.State == PropaneTank.TankState.Venting, "first shot must puncture the stack tank");
        capture.Screenshot("stack_punctured");
        await FireAt(lab.Player, () => target.GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
        var frames = new System.Collections.Generic.List<double>();
        var last = Time.GetTicksUsec();
        async Task Run(int count, string shots)
        {
            for (var i = 0; i < count; i++)
            {
                await Frames(1).ConfigureAwait(true);
                var now = Time.GetTicksUsec(); frames.Add((now - last) / 1000d); last = now;
                if (shots.Length > 0 && i is 10 or 60 or 240) capture.Screenshot($"{shots}_{i:000}");
            }
        }
        await Run(180, "stack_blast").ConfigureAwait(true);
        Assert(lab.LiveTanks.Count() < start && lab.LiveTanks.Count() > 0, "the first blast should leave a venting stack");
        // Shoot venting tanks until the rest go up, as a player would.
        for (var shot = 0; shot < 12 && lab.LiveTanks.Count() > start / 4; shot++)
        {
            var next = lab.LiveTanks.OrderBy(t => t.GlobalPosition.DistanceSquaredTo(lab.Player.GlobalPosition)).First();
            await FireAt(lab.Player, () => next.GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
            await Run(90, shot == 0 ? "stack_chain" : "").ConfigureAwait(true);
        }
        await Run(240, "stack_end").ConfigureAwait(true);
        frames.Sort();
        GD.Print($"[lab stackfire] tanks {start} -> {lab.LiveTanks.Count()} median_ms={frames[frames.Count / 2]:F1} p95_ms={frames[(int)(frames.Count * .95)]:F1} max_ms={frames[^1]:F1}");
        Assert(lab.LiveTanks.Count() <= start / 4, "shooting the stack should chain most of it");
        lab.Session.Dispose(); lab.Free();
    }

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




    private async Task EffectFrames()
    {
        var count = (int)DevArgs.GetFloat("count", 128);
        Assert(count is >= 1 and <= 1024, "effect review count must be 1..1024");
        template.LabStackDistance = count <= 8 ? 10 : 24;
        var scenario = LabScenarios.Get("stack") with { Setup = lab => { lab.World.Tuning.LabStackCount = count; lab.BuildStack(); } };
        uint particleSeed = 0;
        void Seed(Node node)
        {
            if (node is GpuParticles3D particle) { particle.UseFixedSeed = true; particle.Seed = ++particleSeed; }
        }
        GetTree().NodeAdded += Seed;
        var lab = Enter(scenario, LabEnvironment.Void);
        await Frames(180).ConfigureAwait(true);
        particleSeed = 0;
        var tank = lab.LiveTanks.First();
        tank.TakeShot(tank.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
        await Frames(60).ConfigureAwait(true);
        capture.Screenshot($"vent_{count}");
        await Frames(120).ConfigureAwait(true);
        capture.Screenshot($"smoke_{count}");
        lab.Session.Dispose(); lab.Free();
        lab = Enter(scenario, LabEnvironment.Void);
        await Frames(180).ConfigureAwait(true);
        particleSeed = 0;
        foreach (var primed in lab.LiveTanks.ToArray())
            primed.TakeShot(primed.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
        lab.LiveTanks.First().Detonate();
        var previous = 0;
        foreach (var frame in new[] { 1, 6, 15, 30, 60, 120, 240 })
        {
            await Frames(frame - previous).ConfigureAwait(true); previous = frame;
            capture.Screenshot($"chain_{count}_{frame:000}");
        }
        Assert(lab.LiveTanks.Count() < count, "effect review must detonate a tank");
        lab.Session.Dispose(); lab.Free();
        if (count >= 32)
        {
            lab = Enter(scenario, LabEnvironment.Void);
            await Frames(180).ConfigureAwait(true);
            particleSeed = 0;
            var target = lab.LiveTanks.First();
            target.TakeShot(target.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
            target.TakeShot(target.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
            previous = 0;
            foreach (var frame in new[] { 60, 180, 600 })
            {
                await Frames(frame - previous).ConfigureAwait(true); previous = frame;
                capture.Screenshot($"ordinary_{count}_{frame:000}");
            }
            lab.Session.Dispose(); lab.Free();
        }
        GetTree().NodeAdded -= Seed;
    }

    private async Task Measure()
    {
        // The dummy headless renderer otherwise sleeps 6.9 ms even with low-processor mode disabled.
        OS.LowProcessorUsageMode = false;
        OS.LowProcessorUsageModeSleepUsec = 0;
        Engine.MaxFps = 0;
        ConfigurePacingProbe();
        var count = (int)DevArgs.GetFloat("count", Tuning.LabStackMaximum);
        Assert(count >= Tuning.LabStackMinimum && count <= 1024, "measurement count must be 24..1024");
        template.LabStackCount = Math.Min(count, Tuning.LabStackMaximum);
        // Profile beyond the product ceiling through the registry callback, using the same fixture builder.
        var scenario = LabScenarios.Get("stack") with { Setup = lab => { lab.World.Tuning.LabStackCount = count; lab.BuildStack(); } };
        var lab = Enter(scenario, LabEnvironment.Void);
        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        if (DevArgs.Get("measure-physics-cap") != null) lab.World.Tuning.DenseChainPhysicsSteps = 0;
        await Frames(180).ConfigureAwait(true);
        var warmup = DevArgs.GetFloat("measure-warmup", 0);
        var seconds = DevArgs.GetFloat("measure-seconds", 0);
        Assert(float.IsFinite(warmup) && (warmup == 0 || warmup is >= .1f and <= 120), "measurement warm-up must be 0 or 0.1..120 seconds");
        Assert(float.IsFinite(seconds) && (seconds == 0 || seconds is >= .1f and <= 120), "measurement duration must be 0 or 0.1..120 seconds");
        if (warmup > 0)
        {
            var warmUntil = lab.World.Clock.Now + warmup;
            while (lab.World.Clock.Now < warmUntil) await Frames(1).ConfigureAwait(true);
        }
        var phase = DevArgs.Get("phase") ?? "idle";
        Assert(phase is "idle" or "blast" or "chain", "unknown measurement phase");
        EffectProfile.Enabled = true;
        foreach (var system in Enum.GetValues<EffectProfile.System>().Where(s => s != EffectProfile.System.Count))
        { EffectProfile.Drain(system); EffectProfile.DrainBytes(system); }
        var allocated = GC.GetTotalAllocatedBytes();
        var gcPause = GC.GetTotalPauseDuration().TotalMilliseconds;
        var pipelineMesh = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh);
        var pipelineSurface = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface);
        var pipelineDraw = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw);
        var pipelineSpecialization = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization);
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var hide = DevArgs.Get("profile-hide");
        Assert(hide is null or "particles" or "smoke" or "lights" or "shadows" or "decals", "unknown diagnostic exclusion");
        var excludedLights = new LightExclusion { ProcessPriority = int.MaxValue };
        if (hide == "lights") AddChild(excludedLights);
        void Hide(Node node)
        {
            if (node is GpuParticles3D particle && hide == "particles") particle.Visible = false;
            if (node is GpuParticles3D puff && hide == "smoke" && (puff.DrawPass1 as PrimitiveMesh)?.Material is ShaderMaterial material && material.Shader.ResourcePath == "res://shaders/fx_smoke.gdshader") puff.Visible = false;
            if (node is OmniLight3D light && hide == "lights")
            {
                excludedLights.Lights.Add(light);
                light.TreeExiting += () => excludedLights.Lights.Remove(light);
                light.Visible = false;
            }
            if (node is OmniLight3D shadow && hide == "shadows") shadow.ShadowEnabled = false;
            if (node is Decal decal && hide == "decals") decal.Visible = false;
        }
        if (hide != null) GetTree().NodeAdded += Hide;
        var simulationStarted = lab.World.Clock.Now;
        var wallStarted = Time.GetTicksUsec();
        var last = wallStarted;
        if (phase == "blast")
        {
            var target = lab.LiveTanks.First();
            target.TakeShot(target.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
            target.TakeShot(target.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
        }
        else if (phase == "chain")
        {
            foreach (var tank in lab.LiveTanks.ToArray())
                tank.TakeShot(tank.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
            lab.LiveTanks.First().Detonate();
        }
        var series = new Dictionary<string, List<double>>();
        var rows = new List<string>();
        void Sample(string name, double value)
        {
            if (!series.TryGetValue(name, out var values)) series[name] = values = new List<double>();
            values.Add(value);
        }
        var measureUntil = lab.World.Clock.Now + seconds;
        for (var i = 0; seconds > 0 ? lab.World.Clock.Now < measureUntil : i < 600; i++)
        {
            await Frames(1).ConfigureAwait(true);
            var now = Time.GetTicksUsec(); Sample("wall", (now - last) / 1000d); last = now;
            Sample("simulation_time", lab.World.Clock.Now - simulationStarted);
            Sample("physics_cap", Engine.MaxPhysicsStepsPerFrame);
            Sample("gpu", RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()));
            Sample("render_cpu", RenderingServer.ViewportGetMeasuredRenderTimeCpu(GetViewport().GetViewportRid()) + RenderingServer.GetFrameSetupTimeCpu());
            Sample("process", Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000);
            Sample("physics", Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000);
            Sample("active_bodies", Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects));
            Sample("contacts", Performance.GetMonitor(Performance.Monitor.Physics3DCollisionPairs));
            Sample("draw_calls", Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame));
            Sample("objects", Performance.GetMonitor(Performance.Monitor.ObjectCount));
            Sample("resources", Performance.GetMonitor(Performance.Monitor.ObjectResourceCount));
            Sample("nodes", Performance.GetMonitor(Performance.Monitor.ObjectNodeCount));
            Sample("gc_pause", GC.GetTotalPauseDuration().TotalMilliseconds - gcPause);
            Sample("pipeline_mesh", Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh) - pipelineMesh);
            Sample("pipeline_surface", Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface) - pipelineSurface);
            Sample("pipeline_draw", Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw) - pipelineDraw);
            Sample("pipeline_specialization", Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization) - pipelineSpecialization);
            foreach (var system in Enum.GetValues<EffectProfile.System>().Where(s => s != EffectProfile.System.Count))
            {
                Sample(system.ToString(), EffectProfile.Drain(system));
                Sample(system + "_bytes", EffectProfile.DrainBytes(system));
            }
            rows.Add(string.Join(",", series.Values.Select(v => v[^1].ToString("R", CultureInfo.InvariantCulture))));
        }
        EffectProfile.Enabled = false;
        if (hide != null) GetTree().NodeAdded -= Hide;
        excludedLights.Free();
        if (DevArgs.Get("metrics") is { } path)
            File.WriteAllLines(path, new[] { string.Join(",", series.Keys) }.Concat(rows));
        GD.Print($"[lab measure] tanks={count} phase={phase} live={lab.LiveTanks.Count()} allocated_bytes={GC.GetTotalAllocatedBytes() - allocated} gc={string.Join(",", collections.Select((v, i) => GC.CollectionCount(i) - v))}");
        GD.Print($"[lab pacing] simulation={lab.World.Clock.Now - simulationStarted:R} wall={(last - wallStarted) / 1000000d:R} cap={Engine.MaxPhysicsStepsPerFrame}");
        foreach (var (name, values) in series)
        {
            values.Sort();
            GD.Print($"[lab profile] {name} p50={values[(int)(values.Count * .5)]:F3} p95={values[(int)(values.Count * .95)]:F3} worst={values[^1]:F3}");
        }
        capture.Screenshot($"stack_{count}");
        lab.Session.Dispose(); lab.Free();
    }

    // These flags affect only diagnostic scenes, never the product's pacing policy.
    private static void ConfigurePacingProbe()
    {
        if (DevArgs.Get("measure-physics-cap") != null)
        {
            var cap = DevArgs.GetFloat("measure-physics-cap", 0);
            Assert(cap is 2 or 4 or 6 or 12, "measurement physics cap must be 2, 4, 6 or 12");
            Engine.MaxPhysicsStepsPerFrame = (int)cap;
        }
        if (DevArgs.Get("measure-max-fps") != null)
        {
            var fps = DevArgs.GetFloat("measure-max-fps", 0);
            Assert(fps is 0 or 30, "measurement frame cap must be 0 or 30");
            Engine.MaxFps = (int)fps;
        }
    }

    /// <summary>The owner's primary Lab view with four in-process banking bots, measured against wall time.</summary>
    private async Task MeasurePlay()
    {
        OS.LowProcessorUsageMode = false;
        OS.LowProcessorUsageModeSleepUsec = 0;
        Engine.MaxFps = 0;
        ConfigurePacingProbe();
        var seconds = DevArgs.GetFloat("measure-seconds", 30);
        Assert(float.IsFinite(seconds) && seconds is >= .1f and <= 120, "play measurement must be 0.1..120 wall seconds");
        Assert(DevArgs.Get("frame-metrics") != null, "play measurement requires frame metrics");
        var lab = new LabSession
        {
            Scenario = LabScenarios.Get("duel"),
            EnvironmentKind = LabEnvironment.Suburb,
            Template = template,
            TestRandomSeed = 23,
            TestBankBots = 4,
            TestSuburbSettings = Propane.World.SuburbPlans.Match(template, 5) with { MapSize = template.LabSuburbSize },
            PauseOnFocusLoss = false,
            Name = "Lab"
        };
        AddLab(lab);
        Assert(lab.Initialized && lab.Session.Actors.Count == 5 && lab.Player.View != null, "play fixture needs four bots and the primary view");
        var count = lab.LiveTanks.Count();
        AddChild(new FrameMetrics { ProcessPriority = int.MaxValue, WarmupFrames = 0 });
        // Equal simulation age keeps the bot/pile workload comparable when a tested cap slows the world.
        var warmUntil = lab.World.Clock.Now + 3;
        while (lab.World.Clock.Now < warmUntil) await Frames(1).ConfigureAwait(true);
        var start = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        var simulation = lab.World.Clock.Now;
        double end;
        do
        {
            await Frames(1).ConfigureAwait(true);
            Assert(lab.Bank!.IsPlaying && !lab.World.Paused, "play measurement ended or paused early");
            end = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        } while (end - start < seconds);
        GD.Print($"[lab play pacing] start={start:R} end={end:R} simulation={lab.World.Clock.Now - simulation:R} wall={end - start:R} cap={Engine.MaxPhysicsStepsPerFrame} tanks={count}");
        capture.Screenshot("play-pacing");
        lab.Session.Dispose(); lab.Free();
    }

    // Apply after effects and the clustering service, which update light visibility each frame.
    private sealed partial class LightExclusion : Node
    {
        public List<OmniLight3D> Lights { get; } = new();
        public override void _Process(double delta)
        {
            foreach (var light in Lights) light.Visible = false;
        }
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

    private void Press(string text)
    {
        var button = GetTree().Root.FindChildren("*", "Button", owned: false).OfType<Button>()
            .FirstOrDefault(b => b.IsVisibleInTree() && b.Text == text && !b.Disabled);
        Assert(button != null, $"missing button {text}");
        button!.EmitSignal(BaseButton.SignalName.Pressed);
    }
    private async Task Collect()
    {
        await Frames(3).ConfigureAwait(true);
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Frames(3).ConfigureAwait(true); }
    }
    private (int, int, int) Counts() => (GetTree().GetNodeCount(), (int)Performance.GetMonitor(Performance.Monitor.ObjectCount),
        (int)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount));
    private async Task Frames(int count = 3)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
