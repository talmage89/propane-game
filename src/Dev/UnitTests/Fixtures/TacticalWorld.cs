using System;
using System.Collections.Generic;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Controlled native observations, without advancing Jolt or running an actor's input source.</summary>
internal sealed class TacticalWorld : IBotWorld, IDisposable
{
    public WorldContext World { get; }
    public PlayerCharacter Player { get; }
    public PlayerCharacter Enemy { get; }
    public bool IsPlaying { get; set; } = true;
    public float TimeLeft => 180;
    public int MyScore { get; set; }
    public Dictionary<int, int> Banks { get; } = new() { [1] = 0, [2] = 8 };
    public IReadOnlyDictionary<int, int> Scores => Banks;
    public double Now { get; private set; }
    public double LastHitTime { get; set; } = double.NegativeInfinity;
    public int LastHitBy => 2;
    public IReadOnlyList<PlayerCharacter> RemoteBodies { get; private set; }
    public IReadOnlyList<AmmoPickup> Pickups => Array.Empty<AmmoPickup>();
    public KingBotView? King { get; private set; }
    public BotServices Services { get; }
    public List<PropaneTank> Tanks { get; } = new();
    private readonly Node3D root = new();
    private readonly List<Shape3D> shapes = new();
    private readonly string scenario;

    public TacticalWorld(WorldContext world, string scenario)
    {
        World = world;
        this.scenario = scenario;
        world.Root.AddChild(root);
        Player = new PlayerCharacter { World = world, ActorId = 1, Ammo = AmmoState.Starting(world.Tuning), Position = new Vector3(2, 0, 0) };
        Enemy = new PlayerCharacter { World = world, ActorId = 2, Position = new Vector3(18, 0, 0) };
        root.AddChild(Player);
        root.AddChild(Enemy);
        RemoteBodies = scenario is "regroup" or "points" ? new[] { Enemy, Enemy, Enemy, Enemy, Enemy, Enemy, Enemy } :
            scenario == "contest" ? new[] { Enemy, Enemy, Enemy } : new[] { Enemy };
        Box(new Vector3(0, -0.5f, 0), new Vector3(160, 1, 160));
        if (scenario == "cover") Box(new Vector3(4, 1.5f, 4), new Vector3(2, 3, 5));
        if (scenario is "demolition" or "prize")
        {
            for (var i = 0; i < (scenario == "prize" ? 2 : 3); i++)
            {
                var tank = PropaneTank.Create(world);
                // Equal-distance prizes exercise encounter-order ties; demolition retains its close pile.
                tank.Position = scenario == "prize" ? new Vector3(16, 0.5f, i == 0 ? -8 : 8) : new Vector3(16 + i * 2, 0.5f, -8);
                tank.Freeze = true;
                root.AddChild(tank);
                Tanks.Add(tank);
            }
            Enemy.Teleport(Vector3.Right * 100, 0);
        }
        if (scenario == "cover") { MyScore = 8; Banks[2] = 1; LastHitTime = 0; }
        if (scenario == "scout") Banks[2] = 0;
        Services = new BotServices(world);
        Step(0);
    }

    private void Box(Vector3 position, Vector3 size)
    {
        var shape = new BoxShape3D { Size = size };
        shapes.Add(shape);
        var body = new StaticBody3D { Position = position, CollisionLayer = Layers.World | Layers.Floor };
        body.AddChild(new CollisionShape3D { Shape = shape });
        root.AddChild(body);
    }

    public void Step(int tick)
    {
        Now = tick / 120d;
        if (scenario == "cover") LastHitTime = Now;
        if (scenario == "demolition")
        {
            if (tick == 120) Player.Teleport(new Vector3(14, 0, -8), 0);
            if (tick == 480) Player.Teleport(new Vector3(2, 0, 0), 0);
        }
        if (scenario == "points")
        {
            if (tick == 240) { MyScore = 8; Banks[2] = 0; Enemy.Teleport(Vector3.Right * 4, 0); }
            if (tick == 480) { Banks[2] = 12; Enemy.Teleport(Vector3.Right * 14, 0); }
        }
        if (scenario is "contest" or "regroup" or "prize" or "points")
        {
            King = new KingBotView(tick < 840 ? 0 : 1, new ModeSpot(0, 0, 0), new ModeSpot(20, 0, 0), 6,
                scenario == "prize" ? 1 : 0, tick is >= 720 and < 840, tick < 840 ? (840 - tick) / 120d : 45);
            if (scenario == "contest")
            {
                if (tick == 0) { Banks[2] = 0; Enemy.Teleport(Vector3.Right * 4, 0); }
                if (tick == 480) { Player.Teleport(Vector3.Right * 10, 0); Enemy.Teleport(Vector3.Right * 8, 0); }
                if (tick == 600) Enemy.Teleport(Vector3.Right * 100, 0);
            }
            if (scenario == "regroup")
            {
                if (tick == 0) { Enemy.Teleport(Vector3.Right * 100, 0); Player.RemoteRagdoll(Player.GlobalPosition, Vector3.Zero, Vector3.Zero); }
                if (tick == 1) Player.Teleport(Vector3.Right * 2, 0);
            }
        }
        Services.BeginTick(Tanks);
    }

    public string NodeName(Node3D? node)
    {
        if (node == null) return "none";
        if (node == Player) return "self";
        if (node == Enemy) return "enemy";
        if (node is PropaneTank tank && Tanks.IndexOf(tank) is var index && index >= 0) return $"tank{index}";
        throw new InvalidOperationException("unknown decision target");
    }

    public void RemoveEnemy(IBotGoalPolicy brain)
    {
        // Identical harness on the parent: its invalid cached wrapper is the behavioral oracle.
        brain.GetType().GetMethod("ForgetActor")?.Invoke(brain, new object[] { Enemy });
        typeof(BotServices).GetMethod("RemoveActor")?.Invoke(Services, new object[] { Enemy });
        RemoteBodies = Array.Empty<PlayerCharacter>();
        Enemy.Free();
    }

    public void Dispose()
    {
        Services.Dispose();
        root.Free();
        foreach (var shape in shapes) shape.Dispose();
    }
}
