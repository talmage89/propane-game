using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using Propane.Core;
using Propane.Lab;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev.Equivalence;

/// <summary>Opt-in post-physics observations. Never calls an input source or draws from a random stream.</summary>
public sealed partial class LabTrace : Node
{
    public required LabSession Lab { get; init; }
    private readonly Dictionary<string, IncrementalHash> fields = new(StringComparer.Ordinal);
    private readonly List<object> windows = new();
    private readonly Dictionary<PropaneTank, int> tankIds = new();
    private readonly Dictionary<int, string> tankStates = new();
    private readonly Dictionary<string, int> commands = new();
    private StreamWriter? detail;
    private int ticks, windowTicks, commandIndex, detonated, punctured;
    private bool finished;
    private object? configuration;

    public void OpenDetail()
    {
        if (DevArgs.Get("equivalence-detail") is { } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            detail = new StreamWriter(new GZipStream(File.Create(path), CompressionLevel.Fastest));
        }
    }

    public void Begin()
    {
        using var snapshot = Lab.World.Tuning.Snapshot();
        configuration = new
        {
            scenario = Lab.Scenario.Id,
            environment = Lab.EnvironmentKind.ToString(),
            seed = Lab.TestRandomSeed,
            replayRandom = Lab.TestSeedPhysicsRandom,
            tuners = typeof(Tuning).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.CanWrite && (p.PropertyType == typeof(float) || p.PropertyType == typeof(int) || p.PropertyType == typeof(bool)))
                .Select(p => new { p.Name, value = ReferenceFile.Value(p.GetValue(Lab.World.Tuning)), shared = snapshot.ContainsKey(p.Name) }).ToArray()
        };
        Observe();
    }

    public void Command(ModeCommand command)
    {
        var kind = command.GetType().Name;
        commands[kind] = commands.GetValueOrDefault(kind) + 1;
        Add("commands", new { tick = ticks, order = commandIndex++, command = ReferenceFile.Value(command) });
    }

    private void Add(string field, object? value)
    {
        var json = JsonSerializer.Serialize(value, ReferenceFile.JsonOptions);
        if (!fields.TryGetValue(field, out var hash)) fields[field] = hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(json + "\n"));
        detail?.WriteLine(JsonSerializer.Serialize(new { tick = ticks, field, value }, ReferenceFile.JsonOptions));
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (!IsInstanceValid(Lab) || !Lab.Initialized || !Lab.World.IsAlive) return;
            ticks++; windowTicks++;
            Observe();
            if (windowTicks == 600) CloseWindow();
        }
        catch (Exception error)
        {
            GD.PushError($"[equivalence] FAIL trace: {error}");
            Core.ShutdownGuard.Quit(GetTree(), 1);
            SetPhysicsProcess(false);
        }
    }

    public void Boundary(string label)
    {
        ticks++; windowTicks++;
        Add("boundary", label);
        Observe();
    }

    private void Observe()
    {
        Add("clock", ReferenceFile.Value(Lab.World.Clock.Now));
        var prototype = Lab.Prototype;
        Add("mode", ReferenceFile.Value(new
        {
            prototype?.Clock,
            prototype?.Phase,
            scores = (prototype?.Scores ?? Lab.Bank!.Scores).Select(p => new { actor = p.Key, score = p.Value }).ToArray(),
            banked = Lab.Bank?.BankedTotal,
            spilled = Lab.Bank?.SpilledTotal
        }));
        foreach (var actor in Lab.Session.Actors.Select(a => a.Character))
        {
            var id = $"actors/{actor.ActorId}";
            // Only device input sets LookAngularVelocity (personal gamepad gain); Lab actors must leave it zero. Assert
            // that, and keep the frozen intent observation without the device-only field.
            var intent = actor.Runtime.Intent;
            if (intent.LookAngularVelocity != Vector2.Zero) throw new InvalidDataException($"{id} has device look velocity");
            var observed = (Dictionary<string, object?>)ReferenceFile.Value(intent)!;
            observed.Remove(nameof(PlayerIntent.LookAngularVelocity));
            Add(id + "/intent", observed);
            Add(id + "/resolved", ReferenceFile.Value(actor.Runtime.ResolvedState));
            Add(id + "/pose", ReferenceFile.Value(new { actor.GlobalTransform, actor.Velocity, actor.Aim.Yaw, actor.Aim.Pitch }));
            Add(id + "/ammo-life", ReferenceFile.Value(new
            {
                actor.Ammo?.Magazine,
                actor.Ammo?.Reserve,
                actor.Ammo?.ReloadLeft,
                actor.LifeId,
                actor.IsRagdolled,
                spectating = actor.Spectating?.ActorId
            }));
        }
        foreach (var tank in Lab.LiveTanks)
        {
            Track(tank);
        }
        var states = new List<object>();
        var poses = new List<object>();
        foreach (var (tank, id) in tankIds)
        {
            var valid = IsInstanceValid(tank);
            var state = valid ? tank.State.ToString() : "retired";
            tankStates[id] = state;
            states.Add(new { id, state, invulnerable = valid && tank.Invulnerable, gathering = valid && tank.Gathering });
            if (valid && tank.IsInsideTree())
            {
                poses.Add(new
                {
                    id,
                    transform = ReferenceFile.Value(tank.GlobalTransform),
                    linear = ReferenceFile.Value(tank.LinearVelocity),
                    angular = ReferenceFile.Value(tank.AngularVelocity)
                });
            }
        }
        Add("tanks/lifecycle", states);
        Add("tanks/transforms", poses);
        if (Lab.Dissolve is { } arena)
        {
            Add("stream", ReferenceFile.Value(new
            {
                arena.Back,
                arena.Horizon,
                arena.DissolvedTanks,
                segments = arena.Source.Segments.Select(s => new { s.Index, s.CutTo, s.Floor.Disabled, s.Floor.Position, s.FloorShape.Size }).ToArray(),
                arena.Source.Retiring,
                arena.Source.Retired
            }));
        }
    }

    public void Track(PropaneTank tank)
    {
        if (tankIds.ContainsKey(tank)) return;
        var id = tankIds.Count;
        tankIds.Add(tank, id);
        tank.Punctured += Punctured;
        tank.Detonated += Detonated;
    }

    private void Punctured(PropaneTank tank)
    {
        punctured++;
        Add("tank-events", new { tick = ticks, id = tankIds[tank], state = "venting" });
    }
    private void Detonated(PropaneTank tank)
    {
        detonated++;
        Add("tank-events", new { tick = ticks, id = tankIds[tank], state = "exploded" });
    }

    private void CloseWindow()
    {
        windows.Add(new
        {
            endTick = ticks,
            samples = windowTicks,
            fields = fields.ToDictionary(p => p.Key,
            p => Convert.ToHexString(p.Value.GetHashAndReset()))
        });
        windowTicks = 0;
    }

    public void Finish()
    {
        if (finished) return;
        finished = true;
        if (windowTicks > 0 || windows.Count == 0) CloseWindow();
        detail?.Dispose(); detail = null;
        var path = DevArgs.Get("equivalence-trace")!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            format = 1,
            configuration,
            windows,
            ticks,
            summary = new { commands, tanks = tankIds.Count, punctured, detonated, lastTankStates = tankStates }
        }, ReferenceFile.JsonOptions));
        foreach (var hash in fields.Values) hash.Dispose();
        foreach (var tank in tankIds.Keys.Where(IsInstanceValid)) { tank.Punctured -= Punctured; tank.Detonated -= Detonated; }
        GD.Print($"[equivalence] trace {ticks} ticks, {windows.Count} windows, {tankIds.Count} tanks");
    }
}
