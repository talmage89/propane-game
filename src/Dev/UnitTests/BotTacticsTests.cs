using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using Propane.Bots;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Player;

namespace Propane.Dev.UnitTests;

/// <summary>Same source runs on the parent; opt-in diagnostics never instrument production thinks.</summary>
[UnitSuite]
internal static class BotTacticsTests
{
    private const float Delta = 1f / 120;
    private static readonly string[] BankFields = { "target", "seekingRival", "roamGoal", "roamUntil", "retarget", "blocked", "unaimed", "unaimedAnchor",
        "vantage", "cover", "fightTarget", "fightTime", "burstClock", "strafeTimer", "strafeSide", "coverTimer", "vantageTimer", "sighted", "sightedVisible",
        "sightTimer", "coverObserver", "shooterSeen", "watcher", "watchTimer", "shooterVisible", "shooterTimer", "protecting", "clock" };
    private static readonly string[] KingFields = { "standZone", "standOffset", "standCheck", "fightTime", "burstClock", "prize", "prizeTimer", "rival", "watched",
        "watchedVisible", "blocker", "sightTimer", "sightStarted", "wasDown", "regroupTime", "regroupGoal", "regroupTurn", "crowdTimer", "crowdRivals", "firstToZone",
        "contestedTime", "contestPatience", "raidTime", "raidCommitted", "pointsTimer", "pointsTarget", "protectingPoints", "protectZone", "pointsShot", "pointsShotWait" };

    public static void Register(TestRunner runner)
    {
        var path = DevArgs.Get("tactics-transcript");
        var only = DevArgs.Get("tactics-only")?.Split(',').ToHashSet(StringComparer.Ordinal);
        using var transcript = path == null ? null : new StreamWriter(path);
        foreach (var king in new[] { false, true })
        {
            foreach (var difficulty in BotProfile.All)
            {
                foreach (var scenario in king ? new[] { "contest", "regroup", "prize", "points" } : new[] { "cover", "combat", "demolition", "scout" })
                {
                    var name = $"{(king ? "king" : "bank")}/{difficulty}/{scenario}";
                    if (only != null && !only.Contains(name)) continue;
                    runner.Run($"tactics/{name} decisions, edges and warm allocation", () =>
                    {
                        using var world = new TacticalWorld(runner.World, scenario);
                        using IBotGoalPolicy brain = king ? new KingBot(23) : new BankingBot(23);
                        brain.Skill = BotProfile.For(difficulty, world.World.Tuning).Skill;
                        brain.PointsDriven = difficulty == BotDifficulty.Hard;
                        brain.Prepare(world);
                        var statuses = new HashSet<string>();
                        for (var tick = 0; tick < 960; tick++)
                        {
                            world.Step(tick);
                            brain.Think(world, Delta);
                            if (scenario == "prize" && tick == 0)
                            {
                                // Hard can prefer zone income and use the ordinary prize fallback on this tick.
                                var selected = (difficulty == BotDifficulty.Hard ? Field(brain, "pointsTarget") : null) ?? Field(brain, "prize");
                                TestRunner.Assert(ReferenceEquals(selected, world.Tanks[0]), "equal prizes changed their encounter-order tie");
                            }
                            var intent = brain.Read();
                            var consumed = brain.Read();
                            TestRunner.Assert(!consumed.FirePressed && !consumed.JumpPressed && !consumed.ReloadPressed && !consumed.MeleePressed,
                                "input edges survived a second read");
                            TestRunner.Assert(consumed.Move == intent.Move && consumed.FireHeld == intent.FireHeld && consumed.Aim == intent.Aim && consumed.Sprint == intent.Sprint,
                                "reading edges changed held intent");
                            statuses.Add(brain.StatusKind);
                            if (transcript != null) Write(transcript, name, tick, world, brain, intent);
                        }
                        TestRunner.Assert(statuses.Count > 0 && !statuses.Contains("") && !statuses.SetEquals(new[] { "waiting" }), "fixture never made a tactical decision");
                        if (scenario == "cover") TestRunner.Assert(statuses.Any(s => s.StartsWith("taking cover", StringComparison.Ordinal)), "cover fixture found no cover");
                        if (scenario == "demolition") TestRunner.Assert(statuses.Any(s => s.StartsWith("moving to a vantage", StringComparison.Ordinal)), "demolition fixture never searched a runway");
                        if (scenario == "points" && difficulty == BotDifficulty.Hard)
                            TestRunner.Assert(statuses.Contains("points: protecting the bank outside the zone") && statuses.Contains("points: spilling {0}"), "points fixture missed protection or combat");
                        if (scenario == "contest") TestRunner.Assert(statuses.Any(s => s.StartsWith("siege:", StringComparison.Ordinal)), "contested fixture never broke off");
                        if (scenario == "regroup") TestRunner.Assert(statuses.Contains("regrouping outside the zone"), "recovery fixture never regrouped");
                        // Refill shared query/search budgets each tick: exhausted budgets must not hide allocations.
                        // Native observations remain fixed while the tactical timers and searches keep advancing.
                        for (var i = 0; i < 1200; i++)
                        {
                            world.Services.BeginTick(world.Tanks);
                            brain.Think(world, Delta);
                            brain.Read();
                        }
                        TestRunner.AssertNoAllocation(() =>
                        {
                            for (var i = 0; i < 600; i++)
                            {
                                world.Services.BeginTick(world.Tanks);
                                brain.Think(world, Delta);
                                brain.Read();
                            }
                        }, "warmed tactical thinks allocated");
                        GD.Print($"[tactics] {name} statuses={string.Join('|', statuses.Order(StringComparer.Ordinal))}");
                        if (DevArgs.Get("tactics-bench") != null) Measure(name, world, brain);
                    });
                }
            }
        }
    }

    private static void Measure(string name, TacticalWorld world, IBotGoalPolicy brain)
    {
        // Timing is opt-in: settle tiered JIT and sample long enough to distinguish call overhead from noise.
        for (var i = 0; i < 120000; i++)
        {
            world.Services.BeginTick(world.Tanks);
            brain.Think(world, Delta);
            brain.Read();
        }
        const int ticks = 60000;
        var whole = new double[ticks];
        var think = new double[ticks];
        var rays = world.Services.RayLines;
        var queries = world.Services.NavigationQueries;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ticks; i++)
        {
            var start = Stopwatch.GetTimestamp();
            world.Services.BeginTick(world.Tanks);
            var thinking = Stopwatch.GetTimestamp();
            brain.Think(world, Delta);
            think[i] = Stopwatch.GetElapsedTime(thinking).TotalMicroseconds;
            brain.Read();
            whole[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var meanWhole = whole.Average();
        var meanThink = think.Average();
        Array.Sort(whole);
        Array.Sort(think);
        GD.Print(FormattableString.Invariant($"[tactics-cost] {name} ticks={ticks} bytes={bytes} rays={world.Services.RayLines - rays} queries={world.Services.NavigationQueries - queries} think_us={meanThink:F3}/{think[(int)(ticks * .95) - 1]:F3}/{think[(int)(ticks * .99) - 1]:F3} whole_us={meanWhole:F3}/{whole[(int)(ticks * .95) - 1]:F3}/{whole[(int)(ticks * .99) - 1]:F3}"));
    }

    private static void Write(StreamWriter output, string name, int tick, TacticalWorld world, IBotGoalPolicy brain, PlayerIntent intent)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = brain.Status,
            ["intent"] = Intent(intent),
            ["yaw"] = Bits(world.Player.Aim.Yaw),
            ["pitch"] = Bits(world.Player.Aim.Pitch),
            ["rays"] = world.Services.RayLines.ToString(CultureInfo.InvariantCulture),
            ["queries"] = world.Services.NavigationQueries.ToString(CultureInfo.InvariantCulture),
            ["rng"] = ((RandomNumberGenerator)Field(brain, "rng")!).State.ToString(CultureInfo.InvariantCulture),
            ["navigation"] = brain.NavigationDiagnostics,
        };
        foreach (var field in brain is KingBot ? KingFields : BankFields) fields[field] = Value(Field(brain, field), world);
        foreach (var field in brain is KingBot ? new[] { "pointsBlocked" } : new[] { "blacklist" })
        {
            var entries = (System.Collections.IDictionary)Field(brain, field)!;
            var blocks = new List<string>();
            foreach (System.Collections.DictionaryEntry entry in entries)
            {
                var node = GodotObject.InstanceFromId((ulong)entry.Key) as Godot.Node3D;
                blocks.Add($"{world.NodeName(node)}:{Value(entry.Value, world)}");
            }
            fields[field] = string.Join(',', blocks.Order(StringComparer.Ordinal));
        }
        output.WriteLine(JsonSerializer.Serialize(new { name, tick, fields }));
    }

    // Semantic field names survive an ownership move. Recurse only through tactical owners, never through native
    // objects, motor/navigation, services or rules. Missing and duplicate fields fail instead of hiding lost state.
    private static object? Field(object owner, string name)
    {
        var matches = new List<(object Owner, FieldInfo Field)>();
        Find(owner, name, matches);
        TestRunner.Assert(matches.Count == 1, $"expected one tactical field {name}, got {matches.Count}");
        return matches[0].Field.GetValue(matches[0].Owner);
    }

    private static void Find(object owner, string name, List<(object, FieldInfo)> matches)
    {
        foreach (var field in owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (field.Name == name) matches.Add((owner, field));
            if (field.FieldType.Namespace == "Propane.Bots" && (field.FieldType.Name.StartsWith("Banking", StringComparison.Ordinal) ||
                field.FieldType.Name.StartsWith("King", StringComparison.Ordinal)) && field.GetValue(owner) is { } child)
            {
                Find(child, name, matches);
            }
        }
    }

    private static string Intent(PlayerIntent i) => $"{Bits(i.Move.X)},{Bits(i.Move.Y)};{i.Aim},{i.Sprint},{i.FirePressed},{i.FireHeld},{i.JumpPressed},{i.MeleePressed},{i.ReloadPressed};{Bits(i.Look.X)},{Bits(i.Look.Y)},{Bits(i.LookStick.X)},{Bits(i.LookStick.Y)}";

    private static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture);
    private static string Value(object? value, TacticalWorld world) => value switch
    {
        null => "none",
        float scalar => Bits(scalar),
        double scalar => BitConverter.DoubleToInt64Bits(scalar).ToString("X16", CultureInfo.InvariantCulture),
        Vector3 v => $"{Bits(v.X)},{Bits(v.Y)},{Bits(v.Z)}",
        Node3D node => world.NodeName(node),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };
}
