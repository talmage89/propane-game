using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using Propane.Modes;

namespace Propane.Dev;

/// <summary>
/// Feeds scripted events into a mode's pure rules and asserts their state: no world, transport or clock. Scripts live
/// in <c>tools/dev/modes/*.mode</c>; <c>--script=NAME</c> runs one. Each line is a command; see the README there.
/// Exits nonzero on the first failed expectation of any script, naming its file and line.
/// </summary>
public partial class ModeTest : Node
{
    private sealed class Run
    {
        public IModeRules Rules = null!;
        public ModeTuners Tuners = ModeTuners.Empty;
        public ModeClock Clock = new(ModePhase.Lobby, 1, 0, 0);
        public RoundInfo Round = RoundInfo.First;
        public IReadOnlyList<ModeCommand> Last = Array.Empty<ModeCommand>();
        public TankBankRules Bank => Rules as TankBankRules ?? throw new InvalidDataException("this expectation needs Tank Bank");
    }

    public override void _Ready()
    {
        DevArgs.MarkReady();
        var directory = ProjectSettings.GlobalizePath("res://tools/dev/modes");
        var only = DevArgs.Get("script");
        var scripts = Directory.GetFiles(directory, "*.mode").Order(StringComparer.Ordinal)
            .Where(path => only == null || Path.GetFileNameWithoutExtension(path) == only).ToArray();
        var failures = 0;
        var steps = 0;
        if (scripts.Length == 0) { GD.PrintErr($"[mode] no scripts matched {only ?? "*"}"); failures++; }
        foreach (var path in scripts)
        {
            var name = Path.GetFileName(path);
            var lines = File.ReadAllLines(path);
            var run = new Run();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Split('#')[0].Trim();
                if (line.Length == 0) continue;
                try { Execute(run, line); steps++; }
                catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or FormatException or IndexOutOfRangeException)
                {
                    GD.PrintErr($"[mode] FAIL {name}:{i + 1}: {line}: {e.Message}");
                    failures++;
                    break;
                }
            }
            GD.Print($"[mode] {name} done");
        }
        GD.Print($"[mode] {scripts.Length} scripts, {steps} steps, {failures} failures");
        CoreTests.Complete(this, failures == 0 ? 0 : 1);
    }

    private static void Execute(Run run, string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var args = words.Skip(1).ToArray();
        switch (words[0])
        {
            case "mode":
                run.Rules = (ModeRegistry.All.FirstOrDefault(m => m.Name.Replace(" ", "", StringComparison.Ordinal)
                    .Equals(args[0], StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"unknown mode {args[0]}")).CreateRules();
                break;
            case "tune":
                run.Tuners = new ModeTuners(Pairs(args).ToDictionary(p => p.Key, p => double.Parse(p.Value, CultureInfo.InvariantCulture)));
                break;
            case "actors":
                run.Rules.Initialize(new ModeSetup(Pairs(args).Select(p => new ModeActor(int.Parse(p.Key, CultureInfo.InvariantCulture), $"P{p.Key}", true,
                    int.Parse(p.Value, CultureInfo.InvariantCulture))).ToArray(), new ModeWorld(ModeWorldSource.SuburbPlan), run.Tuners, 1));
                run.Last = Array.Empty<ModeCommand>();
                break;
            case "retune":
                foreach (var (key, value) in Pairs(args)) run.Tuners = run.Tuners.With(key, double.Parse(value, CultureInfo.InvariantCulture));
                Feed(run, new TunersChanged(run.Tuners));
                break;
            case "phase":
                var phase = Enum.Parse<ModePhase>(args[0], ignoreCase: true);
                var ends = Pairs(args.Skip(1)).TryGetValue("ends", out var end) ? double.Parse(end, CultureInfo.InvariantCulture) : run.Clock.PhaseEnds;
                if (Pairs(args.Skip(1)).TryGetValue("round", out var round)) run.Round = run.Round with { RoundId = uint.Parse(round, CultureInfo.InvariantCulture) };
                run.Clock = run.Clock with { Phase = phase, PhaseEnds = ends, RoundId = run.Round.RoundId };
                Feed(run, new PhaseEntered(phase, run.Round));
                break;
            case "now":
                run.Clock = run.Clock with { Now = double.Parse(args[0], CultureInfo.InvariantCulture) };
                break;
            case "detonate":
                Feed(run, new TankDetonated(Int(args[0]), Enum.Parse<TankOrigin>(args[1], ignoreCase: true)));
                break;
            case "spill":
                Feed(run, new SpillRequested(Ticket(args), Enum.Parse<SpillReason>(args[2], ignoreCase: true), Int(args[3])));
                break;
            case "register":
                Feed(run, new SpillRegistered(Ticket(args)));
                break;
            case "fail":
                Feed(run, new SpillFailed(Ticket(args)));
                break;
            case "leave":
                Feed(run, new ActorLeft(Int(args[0])));
                break;
            case "join":
                var (id, start) = Pairs(args).Select(p => (Int(p.Key), Int(p.Value))).Single();
                Feed(run, new ActorJoined(new ModeActor(id, $"P{id}", true, start)));
                break;
            case "expect":
                Expect(run, args);
                break;
            default:
                throw new InvalidDataException($"unknown command {words[0]}");
        }
    }

    private static void Feed(Run run, AcceptedEvent accepted) => run.Last = run.Rules.Handle(accepted, run.Clock);

    private static void Expect(Run run, string[] args)
    {
        var rest = args.Skip(1).ToArray();
        switch (args[0])
        {
            case "last":
                var actual = string.Join("; ", run.Last.Select(Render));
                var wanted = string.Join(' ', rest);
                if (actual != (wanted == "none" ? "" : wanted)) throw new InvalidDataException($"commands were \"{actual}\"");
                break;
            case "bank":
                foreach (var (key, value) in Pairs(rest))
                    Check(run.Bank.Bank(Int(key)) == Int(value), $"bank {key} is {run.Bank.Bank(Int(key))}");
                break;
            case "reserved":
                foreach (var (key, value) in Pairs(rest))
                    Check(run.Bank.Reserved(Int(key)) == Int(value), $"reserved {key} is {run.Bank.Reserved(Int(key))}");
                break;
            case "stats":
                foreach (var (key, value) in Pairs(rest))
                {
                    var st = run.Bank.StatsFor(Int(key));
                    Check($"{st.Gained}/{st.LostToHits}/{st.LostToThrows}" == value, $"stats {key} are {st.Gained}/{st.LostToHits}/{st.LostToThrows}");
                }
                break;
            case "standings":
                var standings = string.Join(' ', run.Rules.Snapshot().Standings.Select(s => $"{s.ActorId}={s.Score}{(s.Present ? "" : "(left)")}"));
                Check(standings == string.Join(' ', rest), $"standings are \"{standings}\"");
                break;
            case "audit":
                var audit = run.Rules.Snapshot().Report.FirstOrDefault(r => r.Kind == ModeReportKind.Audit)?.Text ?? "";
                Check(audit == "bank audit " + string.Join(' ', rest), $"audit is \"{audit}\"");
                break;
            case "open":
                Check(run.Rules.Permits(ModeGate.World, run.Clock) == bool.Parse(rest[0]), $"world gate is {!bool.Parse(rest[0])}");
                break;
            default:
                throw new InvalidDataException($"unknown expectation {args[0]}");
        }
    }

    private static string Render(ModeCommand command) => command switch
    {
        ScoreChanged c => $"score {c.ActorId} {c.Score} {c.Delta:+0;-0;0}",
        SpillReserved c => $"reserved {c.Ticket.ActorId}/{c.Ticket.EventId} {c.Count}",
        SpillCommitted c => $"committed {c.Ticket.ActorId}/{c.Ticket.EventId} {c.Reason.ToString().ToLowerInvariant()} {c.Count}",
        SpillCanceled c => $"canceled {c.Ticket.ActorId}/{c.Ticket.EventId} {c.Reason.ToString().ToLowerInvariant()}",
        PublishSnapshot => "snapshot",
        EndMatch => "end match",
        NextRound c => $"next round {(c.RebuildWorld ? "rebuilt" : "kept")}",
        _ => command.GetType().Name,
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static int Int(string text) => int.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>"actor eventId ..." with an optional "life=N".</summary>
    private static SpillTicket Ticket(string[] args) => new(Int(args[0]),
        Pairs(args).TryGetValue("life", out var life) ? uint.Parse(life, CultureInfo.InvariantCulture) : 1, uint.Parse(args[1], CultureInfo.InvariantCulture));

    private static Dictionary<string, string> Pairs(IEnumerable<string> args) => args.Where(a => a.Contains('=', StringComparison.Ordinal))
        .Select(a => a.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
}
