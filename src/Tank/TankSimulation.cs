using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using Propane.Core;
using Propane.Diagnostics;

namespace Propane.Tank;

/// <summary>One physics callback per world, retaining every tank's between-scan disturbance observations.</summary>
public partial class TankSimulation : Node
{
    private static readonly ConditionalWeakTable<WorldContext, TankSimulation> worlds = new();
    private readonly List<PropaneTank> tanks = new();
    private readonly HashSet<PropaneTank> pendingFuses = new();
    private ProcessPhysicsPolicies.CatchupLease catchup = null!;
    private double denseUntil;
    private int limitedSteps;
    internal Func<double> CatchupClock { get; set; } = static () => Time.GetTicksUsec() / 1000000d;
    public required WorldContext World { get; init; }
    internal int Count => tanks.Count;

    internal static TankSimulation Register(WorldContext world, PropaneTank tank)
    {
        world.EnsureAlive();
        var simulation = worlds.GetValue(world, static owner =>
        {
            // Poll the lease even when its world pauses; tank ticks still require their ordinary eligibility.
            var node = new TankSimulation { World = owner, Name = "TankSimulation", ProcessMode = ProcessModeEnum.Always };
            owner.EffectsRoot.AddChild(node);
            node.SetProcess(!owner.Networked);
            node.catchup = ProcessPhysicsPolicies.RegisterCatchup(owner);
            owner.OnStopping(node.ReleaseCatchup);
            ProcessPhysicsPolicies.RequestCatchup(node.catchup, 0);
            return node;
        });
        simulation.tanks.Add(tank);
        return simulation;
    }

    internal void Remove(PropaneTank tank) => tanks.Remove(tank);

    internal void CancelFuse(PropaneTank tank) => pendingFuses.Remove(tank);

    internal void LightFuse(PropaneTank tank)
    {
        if (World.Networked || !tank.CanProcess()) return;
        pendingFuses.Add(tank);
        if (!World.Networked && limitedSteps == 0 && pendingFuses.Count >= World.Tuning.DenseChainFuseThreshold) UpdateCatchup();
    }

    public override void _Process(double delta) => UpdateCatchup();

    private void UpdateCatchup()
    {
        pendingFuses.RemoveWhere(t => !IsInstanceValid(t) || !t.ChainFusePending);
        var eligible = 0;
        foreach (var tank in pendingFuses)
        {
            // A retiring source must not lend its catch-up limit to the incoming suburb.
            if (tank.CanProcess()) eligible++;
            else denseUntil = 0;
        }
        var tuning = World.Tuning;
        limitedSteps = 0;
        if (World.IsAlive && !World.Networked && !World.Paused && World.Root.CanProcess() && tuning.DenseChainPhysicsSteps > 0)
        {
            var now = CatchupClock();
            if (eligible >= tuning.DenseChainFuseThreshold) denseUntil = now + tuning.DenseChainCatchupHold;
            if (eligible >= tuning.DenseChainFuseThreshold || now < denseUntil)
                limitedSteps = Math.Clamp(tuning.DenseChainPhysicsSteps, 1, 12);
        }
        else
        {
            denseUntil = 0;
        }

        ProcessPhysicsPolicies.RequestCatchup(catchup, limitedSteps);
    }

    private void ReleaseCatchup()
    {
        pendingFuses.Clear();
        limitedSteps = 0;
        catchup.Dispose();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!World.IsAlive || World.Paused || !World.Root.CanProcess()) return;
        using var profile = EffectProfile.Measure(EffectProfile.System.TankPhysics);
        foreach (var tank in tanks)
        {
            // Staged sources disable bodies before freeing them; match their former native callback eligibility.
            if (tank.State == PropaneTank.TankState.Exploded || !tank.CanProcess()) continue;
            if (tank.NeedsTick) tank.Tick(delta);
            else tank.ObserveRest();
        }
    }

    public override void _ExitTree()
    {
        ReleaseCatchup();
        tanks.Clear();
        if (worlds.TryGetValue(World, out var current) && ReferenceEquals(current, this)) worlds.Remove(World);
    }
}
