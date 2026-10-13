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
using Propane.Player;
using Propane.Tank;
using Propane.UI;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Real Lab sessions: retirement, fixture counts, session-only tuning, local banks and player-camera frames.</summary>
public partial class LabTest : Node
{
    private CaptureDirector capture = null!;
    private Tuning template = null!;
    public override async void _Ready()
    {
        DevArgs.MarkReady();
        capture = new CaptureDirector(); AddChild(capture);
        template = Tuning.LoadBaked();
        template.FixedSeed = 23;
        template.LabStackCount = 32;
        if (DevArgs.Get("bot-skill") != null) template.LabBotSkill = DevArgs.GetFloat("bot-skill", template.LabBotSkill);
        try
        {
            switch (DevArgs.Get("scenario") ?? "lifetime")
            {
                case "frames": await FramesReview().ConfigureAwait(true); break;
                case "rules": await BankRules().ConfigureAwait(true); break;
                case "duel": await Duel().ConfigureAwait(true); break;
                case "soak": await Duel(soak: true).ConfigureAwait(true); break;
                case "effects": await EffectFrames().ConfigureAwait(true); break;
                case "measure": await Measure().ConfigureAwait(true); break;
                case "stackfire": await StackFire().ConfigureAwait(true); break;
                case "aimstall": await AimStall().ConfigureAwait(true); break;
                case "dials": await Dials().ConfigureAwait(true); break;
                case "prototype": await Prototypes().ConfigureAwait(true); break;
                case "shortfuse": await ShortFuseMatches().ConfigureAwait(true); break;
                default: await Lifetime().ConfigureAwait(true); break;
            }
            template.Dispose();
            GD.Print("[lab] PASS");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[lab] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private LabSession Enter(LabScenario scenario, LabEnvironment environment)
    {
        var lab = new LabSession
        {
            Scenario = scenario,
            EnvironmentKind = environment,
            Template = template,
            PauseOnFocusLoss = false,
            TestRandomSeed = (ulong)DevArgs.GetFloat("effects-seed", 23),
            Name = "Lab"
        };
        AddChild(lab);
        Assert(lab.Initialized, $"{scenario.Id}/{environment} initialization");
        return lab;
    }

    private async Task Lifetime()
    {
        using var snapshot = template.Snapshot();
        Assert(!snapshot.ContainsKey(nameof(Tuning.LabStackCount)) && !snapshot.ContainsKey(nameof(Tuning.LabBotSkill)),
            "Lab dials entered a shared tuning snapshot");
        foreach (var scenario in LabScenarios.All)
        {
            foreach (var environment in scenario.Environments)
            {
                await Cycle(scenario, environment).ConfigureAwait(true);
                await Collect().ConfigureAwait(true);
                await Cycle(scenario, environment).ConfigureAwait(true);
                await Collect().ConfigureAwait(true);
                var baseline = Counts();
                for (var cycle = 1; cycle <= (int)DevArgs.GetFloat("cycles", 10); cycle++)
                {
                    await Cycle(scenario, environment).ConfigureAwait(true);
                    await Collect().ConfigureAwait(true);
                    Assert(Counts() == baseline, $"{scenario.Id}/{environment} cycle {cycle}: {baseline} -> {Counts()}");
                }
                GD.Print($"[lab lifetime] {scenario.Id}/{environment}: {(int)DevArgs.GetFloat("cycles", 10)} entry/reset/exit cycles stable {baseline}");
            }
        }
    }

    /// <summary>
    /// A player can drag any layout slider to either end and press Reset: each end, and every dial at once, must
    /// still build a playable fixture in every scenario and environment, and survive a reset.
    /// </summary>
    private async Task Dials()
    {
        var dials = new System.Collections.Generic.List<(System.Reflection.PropertyInfo Property, LabDialAttribute Dial)>();
        foreach (var property in typeof(Tuning).GetProperties())
        {
            if (Attribute.GetCustomAttribute(property, typeof(LabDialAttribute)) is LabDialAttribute dial) dials.Add((property, dial));
        }
        Assert(dials.Count > 0, "no Lab dials found");
        var cases = new System.Collections.Generic.List<(string Name, (System.Reflection.PropertyInfo Property, double Value)[] Settings)>();
        foreach (var (property, dial) in dials)
        {
            cases.Add((property.Name, [(property, dial.Min)]));
            cases.Add((property.Name, [(property, dial.Max)]));
        }
        cases.Add(("all minimums", dials.Select(entry => (entry.Property, entry.Dial.Min)).ToArray()));
        cases.Add(("all maximums", dials.Select(entry => (entry.Property, entry.Dial.Max)).ToArray()));
        var sessions = 0;
        foreach (var (name, settings) in cases)
        {
            // A prototype's own dials only lay out its scenario; the shared ones and all-at-once go everywhere.
            var only = settings.Length == 1 ? ((LabDialAttribute)Attribute.GetCustomAttribute(settings[0].Property, typeof(LabDialAttribute))!).Scenario : null;
            foreach (var scenario in LabScenarios.All.Where(s => only == null || s.Id == only))
            {
                foreach (var environment in scenario.Environments)
                {
                    using var tuning = template.Copy();
                    foreach (var (property, value) in settings)
                        property.SetValue(tuning, Convert.ChangeType(value, property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
                    var lab = new LabSession
                    {
                        Scenario = scenario,
                        EnvironmentKind = environment,
                        Template = tuning,
                        PauseOnFocusLoss = false,
                        TestRandomSeed = 23,
                        Name = "Lab"
                    };
                    AddChild(lab);
                    var label = $"{scenario.Id}/{environment} with {name}";
                    Assert(lab.Initialized && lab.LiveTanks.Any(), $"{label} built no fixture");
                    await Frames(20).ConfigureAwait(true);
                    lab.Reset();
                    await Frames(10).ConfigureAwait(true);
                    Assert(lab.Player.IsOnFloor() && lab.LiveTanks.Any(), $"{label} did not survive a reset");
                    lab.Session.Dispose(); lab.Free();
                    sessions++;
                }
            }
        }
        GD.Print($"[lab dials] {sessions} sessions at both slider ends and all-at-once, each built and reset");
    }

    private async Task Cycle(LabScenario scenario, LabEnvironment environment)
    {
        var lab = Enter(scenario, environment);
        var tankCount = lab.LiveTanks.Count();
        Assert(tankCount > 0, "fixture has no tanks");
        Assert(!lab.World.Networked && lab.Session.Actors.Count == (scenario.Banking ? 2 : scenario.Prototype ? template.LabPrototypeBots + 1 : 1),
            "local actors/world");
        Assert(lab.Opponent?.View == null, "bot acquired a camera");
        if (scenario.Id == "stack") Assert(tankCount == template.LabStackCount && lab.LiveTanks.All(t => t.Transform.Basis.Y.Dot(Vector3.Up) > .99f), "upright stack count");
        if (scenario.Id == "range") Assert(tankCount == 12, "range fixture count");
        lab.World.Tuning.RunSpeed = 7;
        lab.World.Tuning.LabRangeStep = 15;
        Assert(template.RunSpeed != 7 && template.LabRangeStep == 10, "Lab edited template");
        await Frames(8).ConfigureAwait(true);
        var retired = lab.Session;
        lab.RequestReset();
        await Frames(6).ConfigureAwait(true);
        Assert(lab.ResetCount == 1 && lab.Session != retired, "reset did not replace session");
        Assert(!retired.IsAlive && retired.Actors.Count == 0 && retired.World.Clock.PendingCount == 0 &&
            retired.World.BlastReceivers.Count == 0, "reset left old services live");
        Assert(lab.World.Tuning.RunSpeed == 7 && lab.World.Tuning.LabRangeStep == 15, "reset lost session tuning");
        Assert(lab.LiveTanks.Count() == tankCount, "reset lost fixtures");
        retired = lab.Session;
        lab.Session.Dispose(); lab.Free();
        Assert(!retired.IsAlive && retired.Actors.Count == 0 && retired.World.BlastReceivers.Count == 0, "exit retained services");
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
        var lab = Enter(LabScenarios.Get("duel"), DevArgs.Get("environment") == "suburb" ? LabEnvironment.Suburb : LabEnvironment.Void);
        if (soak) lab.Bank!.AttachBot(lab.Player);
        else lab.Player.InputOverride = new ScriptedPlayer(lab);
        // The scripted player taps visible tanks through the actor intent/aim path; neither bank is granted.
        var seconds = (int)DevArgs.GetFloat("seconds", soak ? 180 : 45);
        var frame = 0;
        while (lab.Bank!.IsPlaying && lab.World.Clock.Now < seconds)
        {
            Assert(frame++ < seconds * 120, "duel simulation stalled");
            await Frames(1).ConfigureAwait(true);
        }
        var bank = lab.Bank!;
        GD.Print($"[lab duel] seconds={seconds} simulation={lab.World.Clock.Now:F3} banked={bank.BankedTotal} spilled={bank.SpilledTotal} hits={bank.Hits} you={bank.Scores[1]} bot={bank.Scores[2]} earned_you={bank.Earned[1]} earned_bot={bank.Earned[2]} live={lab.LiveTanks.Count()}");
        Assert(bank.BankedTotal > 0 && bank.Earned[2] > 0, "banking bot earned no bank");
        // A bot that freezes for most of the round (the suburb once banked one tank in 180 s) must not pass.
        if (soak) Assert(bank.Earned[1] + bank.Earned[2] >= 10, $"two banking bots earned only {bank.Earned[1] + bank.Earned[2]} tanks");
        Assert(bank.Scores.Values.Sum() + lab.LiveTanks.Count() == bank.InitialSupply, "bank conservation");
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
    /// clock ran out, the bank is conserved with every mint counted, and each zone move materialized a pile.
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
                DrivePlayer = DevArgs.Get("watch") == null,
                Name = "Lab"
            };
            AddChild(lab);
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
            runner.Decided += command => { if (command is ZoneMoved { Zone: > 0 }) moves++; };
            var limit = (id == "king" ? lab.World.Tuning.KingRoundSeconds : lab.World.Tuning.RingOutRoundLimit) + 5;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var shot = 0;
            for (var frame = 0; runner.IsPlaying && runner.Elapsed < until; frame++)
            {
                Assert(frame < limit * 60, $"{id} seed {seed}: the round never ended");
                if (DevArgs.Get("trace") != null && frame % 120 == 0)
                {
                    GD.Print($"[lab trace] {runner.Elapsed:F1}s " + string.Join(" | ", lab.Session.Actors.Select(a =>
                        $"{a.Character.ActorId} {a.Character.GlobalPosition.X:F1},{a.Character.GlobalPosition.Y:F1},{a.Character.GlobalPosition.Z:F1} {runner.BotStatus(a.Character.ActorId)}")));
                }

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
            var state = runner.Rules.Snapshot();
            foreach (var line in state.Report) GD.Print($"[lab prototype] {id} seed {seed}: {line.Text}");
            var brains = runner.Thinks / System.Math.Max(1, runner.Elapsed * 120);
            GD.Print($"[lab prototype] {id} seed={seed} environment={environment} round={runner.Elapsed:F1}s wall={wall:F1}s " +
                $"brains={brains:F1} think_ms_per_tick={runner.ThinkSeconds * 1000 / System.Math.Max(1, runner.Elapsed * 120):F3}");
            switch (runner.Rules)
            {
                case RingOutRules ring:
                {
                    var starters = runner.Roster.Select(a => a.ActorId).ToArray();
                    var winners = starters.Where(a => ring.Place(a) == 1).ToArray();
                    Assert(ring.Over && ring.PlacementsComplete && winners.Length >= 1, $"ring out seed {seed}: no winner or incomplete placements");
                    Assert(starters.All(a => ring.Place(a) is >= 1 and var p && p <= starters.Length), "placements out of range");
                    Assert(ring.Eliminations.Count == starters.Length - winners.Length &&
                        ring.Eliminations.All(e => runner.Spectating(e.ActorId) && !winners.Contains(e.ActorId)), "eliminations and spectators disagree");
                    // Elimination order is placement order: anyone out later never places worse.
                    Assert(ring.Eliminations.Zip(ring.Eliminations.Skip(1)).All(p => p.Second.Place <= p.First.Place), "placements out of elimination order");
                    Assert(ring.Eliminations.Sum(e => e.CreditedTo != 0 ? 1 : 0) == starters.Sum(ring.Credits), "credit totals");
                    Assert(runner.Elapsed < ring.Plan.Collapsed + 1 || lab.RingOut!.SolidLots == 1, "the rim must have dissolved to one lot");
                    break;
                }
                case KingRules king:
                {
                    var bank = king.Bank;
                    Assert(runner.Elapsed >= lab.World.Tuning.KingRoundSeconds - 0.01, "king ended before its clock");
                    Assert(bank.Conserved && bank.Minted == runner.Roster.Sum(a => king.MintedBy(a.ActorId)), $"king seed {seed}: bank not conserved");
                    Assert(runner.Roster.All(a => runner.Scores.GetValueOrDefault(a.ActorId) == bank.Bank(a.ActorId)), "scores disagree with the bank");
                    Assert(moves == king.Zone && lab.King!.Materialized == moves, $"zone moves {moves}, zone {king.Zone}, piles {lab.King!.Materialized}");
                    minted += bank.Minted;
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
        if (id == "king" && DevArgs.Get("quick") == null && until == float.MaxValue)
            Assert(minted > 0, $"king: nobody minted across {seeds} rounds");
    }

    /// <summary>
    /// Whole Short Fuse matches with a bot on every actor (the player's too), headless, across seeds: rounds rebuild
    /// the suburb, and each must end legally. Every death is a starter's, once, credited to someone else or nobody;
    /// the dead spectate and leave the round; the winner was alive; the match winner reached the wins; kills add up;
    /// every life advanced with its round; and the Lab's reports, arriving at once, were never late, early, duplicated
    /// or naming an unknown actor or blast. --quick shortens rounds and the match; --capture-events screenshots a
    /// blast-then-bullet kill, the kill feed, spectating and the round-win banner.
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
            AddChild(lab);
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
                        $"{a.Character.ActorId} {((ShortFuseRules)traced.Rules).Health.Health(a.Character.ActorId, traced.Clock.Now):0} {traced.BotStatus(a.Character.ActorId)}")));
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
        GD.Print($"[lab shortfuse] {seeds} matches, {roundsPlayed} rounds ({limits} at the limit), {deaths} deaths, {combos} finished while down");
        // Bot play, not legality: across whole matches a thrown player must sometimes be shot dead.
        if (DevArgs.Get("quick") == null) Assert(combos > 0, "no blast-then-bullet kill across the matches");
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
        // Reproduce the first physics shot after ammo becomes ready, with the prior render's bolt pose.
        var rig = lab.Player.FindChildren("*", owned: false).OfType<RifleRig>().Single();
        Assert(rig.AimAmount >= .999f, "post-reload contact fixture must be fully aimed");
        var rifle = rig.Rifle!;
        var skeleton = rig.GetParent();
        var parentAdvance = new ShotParentProbe { Rig = rig };
        skeleton.AddChild(parentAdvance);
        skeleton.MoveChild(parentAdvance, rig.GetIndex());
        var contacts = new ShotParentProbe { Rig = rig, Source = parentAdvance };
        skeleton.AddChild(contacts);
        rig.Reload = .8f;
        rig.AdvanceReload(0, remote: false);
        rifle.PreviewReloadContact(.8f);
        var firedWithForegrip = false;
        void CheckFiringContact(ShotReport shot)
        {
            Assert(rig.Reload == 0 && rifle.MagazineHold == 0 && rifle.BoltHold == 0,
                "first post-reload shot fitted an obsolete magazine/bolt contact");
            firedWithForegrip = true;
            parentAdvance.Pending = true;
        }
        lab.Player.ShotImpacting += CheckFiringContact;
        await FireAt(lab.Player, () => tanks[0].GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
        lab.Player.ShotImpacting -= CheckFiringContact;
        Assert(firedWithForegrip, "post-reload firing contact was not exercised");
        Assert(contacts.Samples == 1, "animation-parent firing contacts were not checked");
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
        lab.Session.Dispose(); lab.Free();
        GD.Print("[lab aimstall] PASS the bot left a pile whose shots an obstruction stopped");
    }

    private static async Task FireAt(PlayerCharacter actor, Func<Vector3> point)
    {
        actor.InputOverride = new OneShot(actor, point);
        for (var i = 0; i < 15; i++) await actor.ToSignal(actor.GetTree(), SceneTree.SignalName.ProcessFrame);
        actor.InputOverride = new IdleInput();
    }

    private sealed class IdleInput(bool aim = false) : IPlayerInputSource { public PlayerIntent Read() => new() { Aim = aim }; }
    private sealed class OneShot(PlayerCharacter actor, Func<Vector3> point) : IPlayerInputSource
    {
        private readonly BotAim aim = new();
        private bool fired;
        public PlayerIntent Read()
        {
            if (fired) return new PlayerIntent { Aim = true };
            aim.Track(actor, point(), 1f / Engine.PhysicsTicksPerSecond, 1, smooth: false);
            fired = true;
            return new PlayerIntent { Aim = true, FirePressed = true };
        }
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
        var count = (int)DevArgs.GetFloat("count", Tuning.LabStackMaximum);
        Assert(count >= Tuning.LabStackMinimum && count <= 1024, "measurement count must be 24..1024");
        template.LabStackCount = Math.Min(count, Tuning.LabStackMaximum);
        // Profile beyond the product ceiling through the registry callback, using the same fixture builder.
        var scenario = LabScenarios.Get("stack") with { Setup = lab => { lab.World.Tuning.LabStackCount = count; lab.BuildStack(); } };
        var lab = Enter(scenario, LabEnvironment.Void);
        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        await Frames(180).ConfigureAwait(true);
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
        var last = Time.GetTicksUsec();
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
        for (var i = 0; i < 600; i++)
        {
            await Frames(1).ConfigureAwait(true);
            var now = Time.GetTicksUsec(); Sample("wall", (now - last) / 1000d); last = now;
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
        foreach (var (name, values) in series)
        {
            values.Sort();
            GD.Print($"[lab profile] {name} p50={values[300]:F3} p95={values[570]:F3} worst={values[^1]:F3}");
        }
        capture.Screenshot($"stack_{count}");
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
