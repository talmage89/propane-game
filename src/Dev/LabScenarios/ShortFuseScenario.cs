using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Lab;
using Propane.Modes;
using Propane.Tank;
using static Propane.Dev.LabCases.LabScenarioTools;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

internal sealed class ShortFuseScenario(ILabScenarioHost host, bool combo = false) : LabScenario(host)
{
    protected override Task RunCore() => combo ? ShortFuseCombo() : ShortFuseMatches();

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
            IDisposable? observation = null;
            var captured = new HashSet<string>();
            var pending = new List<(int Frame, string Name)>();
            var frame = 0;
            void Capture(string name, int delay)
            {
                if (events && captured.Add(name)) pending.Add((frame + delay, $"shortfuse_{seed}_{name}"));
            }
            void Watch(PrototypeRunner runner)
            {
                observation?.Dispose();
                watched = runner;
                var rules = (ShortFuseRules)runner.Rules;
                var spawns = lab.ShortFuse!.Spawns.Select(p => p.Position).ToArray();
                Assert(spawns.SelectMany(a => spawns.Where(b => b != a), (a, b) => a.DistanceTo(b)).DefaultIfEmpty(99).Min() > 10, $"seed {seed}: crowded starts");
                Assert(lab.Session.Actors.All(a => a.Character.LifeId == match.Round && rules.Health.Health(a.Character.ActorId, runner.Clock.Now) == rules.MaxHealth),
                    $"seed {seed} round {match.Round}: lives must advance with the round, at full health");
                var observe = Guard<ModeCommand>(command =>
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
                });
                runner.Decided += observe;
                observation = DetachOnDisposal(() => runner.Decided -= observe);
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
            observation?.Dispose();
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
        var observe = Guard<ModeCommand>(command =>
        {
            if (command is ActorDied died) deaths.Add(died);
            if (command is DamageApplied { Kind: DamageKind.Blast } blast) blasts.Add(blast);
        });
        runner.Decided += observe;
        using var observation = DetachOnDisposal(() => runner.Decided -= observe);
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
}
