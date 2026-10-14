using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using Propane.Core;
using Propane.Fx;

namespace Propane.Tank;

/// <summary>One physics callback per world, retaining every tank's between-scan disturbance observations.</summary>
public partial class TankSimulation : Node
{
    private static readonly ConditionalWeakTable<WorldContext, TankSimulation> worlds = new();
    private readonly List<PropaneTank> tanks = new();
    public required WorldContext World { get; init; }
    internal int Count => tanks.Count;

    internal static TankSimulation Register(WorldContext world, PropaneTank tank)
    {
        world.EnsureAlive();
        var simulation = worlds.GetValue(world, static owner =>
        {
            var node = new TankSimulation { World = owner, Name = "TankSimulation" };
            owner.EffectsRoot.AddChild(node);
            return node;
        });
        simulation.tanks.Add(tank);
        return simulation;
    }

    internal void Remove(PropaneTank tank) => tanks.Remove(tank);

    public override void _PhysicsProcess(double delta)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.TankPhysics);
        foreach (var tank in tanks)
        {
            if (tank.State == PropaneTank.TankState.Exploded) continue;
            if (tank.NeedsTick) tank.Tick(delta);
            else tank.ObserveRest();
        }
    }

    public override void _ExitTree()
    {
        tanks.Clear();
        if (worlds.TryGetValue(World, out var current) && ReferenceEquals(current, this)) worlds.Remove(World);
    }
}
