using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Lab;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

internal sealed class DialsScenario(ILabScenarioHost host) : LabScenario(host)
{
    protected override Task RunCore() => Dials();

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
        var shard = int.Parse(DevArgs.Get("dial-shard") ?? "1", CultureInfo.InvariantCulture);
        var shards = int.Parse(DevArgs.Get("dial-shards") ?? "1", CultureInfo.InvariantCulture);
        Assert(shards > 0 && shard >= 1 && shard <= shards, "invalid dial shard");
        var listing = DevArgs.Get("list-dials") != null;
        var candidates = 0;
        var sessions = 0;
        foreach (var (name, settings) in cases)
        {
            // A prototype's own dials only lay out its scenario; the shared ones and all-at-once go everywhere.
            var only = settings.Length == 1 ? ((LabDialAttribute)Attribute.GetCustomAttribute(settings[0].Property, typeof(LabDialAttribute))!).Scenario : null;
            foreach (var scenario in LabScenarios.All.Where(s => only == null || s.Id == only))
            {
                foreach (var environment in scenario.Environments)
                {
                    var index = candidates++;
                    if (index % shards != shard - 1) continue;
                    sessions++;
                    if (listing)
                    {
                        GD.Print($"[lab dial-case] {scenario.Id}/{environment}/" + string.Join(",", settings.Select(s =>
                            $"{s.Property.Name}={s.Value.ToString("R", CultureInfo.InvariantCulture)}")));
                        continue;
                    }
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
                    AddLab(lab);
                    var label = $"{scenario.Id}/{environment} with {name}";
                    Assert(lab.Initialized && lab.LiveTanks.Any(), $"{label} built no fixture");
                    // This checks layout/retirement, not combat: Hard can detonate a pile before the actors have
                    // settled, triggering hitstop or a throw during the fixed grounding window. Keep gravity and
                    // collisions live, but hold every actor's input on both sides of the reset.
                    foreach (var actor in lab.Session.Actors) actor.Character.InputLocked = true;
                    await Frames(20).ConfigureAwait(true);
                    lab.Reset();
                    foreach (var actor in lab.Session.Actors) actor.Character.InputLocked = true;
                    await Frames(10).ConfigureAwait(true);
                    Assert(lab.Player.IsOnFloor() && lab.LiveTanks.Any(), $"{label} did not survive a reset: floor={lab.Player.IsOnFloor()} down={lab.Player.IsRagdolled} position={lab.Player.GlobalPosition} tanks={lab.LiveTanks.Count()} policy={lab.Prototype?.BotStatus(1)}");
                    lab.Session.Dispose(); lab.Free();
                }
            }
        }
        Assert(sessions > 0, "dial shard selected no sessions");
        if (listing)
        {
            GD.Print($"[lab dials] listed {sessions} of {candidates} sessions");
            return;
        }
        GD.Print($"[lab dials] shard {shard}/{shards}: {sessions} of {candidates} sessions at both slider ends and all-at-once, each built and reset");
    }
}
