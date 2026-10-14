using System;
using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Navigation;
using Propane.Player;
using Propane.Tank;

namespace Propane.Bots;

/// <summary>
/// What every bot one process runs in a world shares, refreshed once per physics tick before they think: the tanks in
/// play, which of them vent, how big a pile each belongs to, the hazard circles routes keep out of, and the navigation
/// lattice (<see cref="NavigationGrid"/>) with its per-tick budgets. Brains read it instead of each scanning the world,
/// so route searches share observations and reading them allocates nothing.
/// </summary>
public sealed class BotServices : IDisposable
{
    private readonly WorldContext world;
    private readonly Rect2? bounds;
    private readonly List<PropaneTank> tanks = new(512);
    private readonly List<PropaneTank> venting = new(512);
    private readonly Dictionary<PropaneTank, int> indices = new(512);
    private int[] piles = new int[512];
    private float[] spreads = new float[512];
    private readonly Dictionary<long, int> pileBuckets = new(512);
    private int[] pileNext = new int[512];
    private bool pilesKnown;
    private NavigationWorld? physics;
    private NavigationGrid? grid;
    private RayCast3D? sight;
    private float gridSpacing;
    private float gridMinimumJump;
    private float gridMargin;
    private bool disposed;

    public BotServices(WorldContext world, Rect2? bounds = null)
    {
        this.world = world;
        this.bounds = bounds;
        world.Events.Explosion += Disturb;
    }

    /// <summary>The tanks still in play this tick. Index it: enumerating an interface list allocates.</summary>
    public IReadOnlyList<PropaneTank> Tanks => tanks;
    /// <summary>The venting tanks among <see cref="Tanks"/>.</summary>
    public IReadOnlyList<PropaneTank> Venting => venting;
    /// <summary>Intact tank piles and venting tanks, as circles to keep routes out of.</summary>
    public NavigationHazards Hazards { get; } = new();
    /// <summary>Physics ticks begun; brains stagger periodic work by it.</summary>
    public long Tick { get; private set; }

    /// <summary>
    /// Starts a tick: takes the tanks in play from <paramref name="live"/> (anything freed or exploded is skipped) and
    /// rebuilds the hazards and the navigation budgets. The owner calls this once per physics tick, before any of its
    /// bots think. (Concrete collections, so walking them allocates nothing.)
    /// </summary>
    public void BeginTick(List<PropaneTank> live)
    {
        Clear();
        foreach (var tank in live) Add(tank);
        Finish();
    }

    /// <inheritdoc cref="BeginTick(List{PropaneTank})"/>
    public void BeginTick<TKey>(Dictionary<TKey, PropaneTank>.ValueCollection live) where TKey : notnull
    {
        Clear();
        foreach (var tank in live) Add(tank);
        Finish();
    }

    private void Clear()
    {
        Tick++;
        tanks.Clear();
        venting.Clear();
        indices.Clear();
        Hazards.Clear();
        pilesKnown = false;
    }

    private void Add(PropaneTank tank)
    {
        if (!GodotObject.IsInstanceValid(tank) || tank.State == PropaneTank.TankState.Exploded) return;
        var tuning = world.Tuning;
        indices[tank] = tanks.Count;
        tanks.Add(tank);
        var vents = tank.State == PropaneTank.TankState.Venting;
        if (vents) venting.Add(tank);
        Hazards.Add(tank.GlobalPosition, vents ? tuning.RagdollRadius + tuning.BotVentClearance : tuning.BotTankClearance);
    }

    private void Finish()
    {
        Hazards.Build();
        grid?.BeginTick(world.Clock.Now);
    }

    /// <summary>The lattice for this body, made (or remade, when tuning changed the body or the dials) on first use.</summary>
    public NavigationGrid Navigation(NavigationCapabilities capabilities)
    {
        var tuning = world.Tuning;
        if (grid == null || grid.Capabilities != capabilities || gridSpacing != tuning.BotRouteSpacing ||
            gridMinimumJump != tuning.BotJumpMinimumRange || gridMargin != tuning.BotJumpMargin)
        {
            retiredQueries += physics?.Queries ?? 0;
            physics?.Dispose();
            physics = new NavigationWorld(world.Root, capabilities, tuning.BotJumpMargin);
            grid = new NavigationGrid(physics, capabilities, tuning.BotRouteSpacing, tuning.BotJumpMinimumRange);
            if (bounds is { } known) grid.Reserve(known.Grow(capabilities.JumpRange * 2));
            gridSpacing = tuning.BotRouteSpacing;
            gridMinimumJump = tuning.BotJumpMinimumRange;
            gridMargin = tuning.BotJumpMargin;
            grid.BeginTick(world.Clock.Now);
        }
        grid.QueryBudget = Math.Max(physics!.FlightQueries + 2, tuning.BotQueriesPerTick);
        grid.ExpansionBudget = Math.Max(1, tuning.BotExpansionsPerTick);
        return grid;
    }

    /// <summary>The lattice in use, if any bot has planned yet: for measurements.</summary>
    public NavigationGrid? Grid => grid;

    /// <summary>Navigation physics queries made so far, across every lattice this world has had.</summary>
    public long NavigationQueries => retiredQueries + (physics?.Queries ?? 0);
    private long retiredQueries;

    /// <summary>The body and movement a bot plans for, from its capsule and the world's tuning.</summary>
    public static NavigationCapabilities Capabilities(CapsuleShape3D capsule, Tuning tuning) =>
        new(capsule.Radius, capsule.Height, tuning.RunSpeed, tuning.SprintSpeed, tuning.JumpVelocity, tuning.PlayerGravity,
            tuning.Acceleration, tuning.AirControl);

    /// <summary>An actor's capsule, found once: it never changes.</summary>
    public static CapsuleShape3D Capsule(PlayerCharacter player)
    {
        foreach (var child in player.GetChildren())
        {
            if (child is CollisionShape3D { Shape: CapsuleShape3D capsule }) return capsule;
        }
        throw new InvalidOperationException("an actor without a capsule cannot navigate");
    }

    /// <summary>
    /// The first thing a ray from one point to another hits on <paramref name="mask"/>, through one reused ray node,
    /// so sight lines and ground probes allocate nothing. Rays starting inside a body ignore it, as physics rays do.
    /// </summary>
    public bool Ray(Vector3 from, Vector3 to, uint mask, out RayHit hit)
    {
        RayLines++;
        if (sight == null || !GodotObject.IsInstanceValid(sight))
        {
            sight = new RayCast3D { Name = "BotSight", TopLevel = true, Enabled = false };
            world.Root.AddChild(sight);
        }
        sight.CollisionMask = mask;
        sight.Position = from;
        sight.TargetPosition = to - from;
        sight.ForceRaycastUpdate();
        if (!sight.IsColliding())
        {
            hit = default;
            return false;
        }
        hit = new RayHit(sight.GetCollisionPoint(), sight.GetCollisionNormal(), sight.GetCollider());
        return true;
    }

    /// <summary>Nothing on <paramref name="mask"/> lies between two points.</summary>
    public bool Clear(Vector3 from, Vector3 to, uint mask) => !Ray(from, to, mask, out _);

    /// <summary>Rays cast for brains so far, for measurements.</summary>
    public long RayLines { get; private set; }

    /// <summary>Tanks within the chain radius of this one, itself included: how big a pile it sets off.</summary>
    public int PileCount(int tank)
    {
        CountPiles();
        return piles[tank];
    }

    /// <summary>How far this tank's chain-radius neighbors reach from it (0 alone).</summary>
    public float PileSpread(int tank)
    {
        CountPiles();
        return spreads[tank];
    }

    /// <summary>A tank's index in <see cref="Tanks"/> this tick, or -1.</summary>
    public int IndexOf(PropaneTank tank) => indices.GetValueOrDefault(tank, -1);

    /// <summary>The nearest venting tank within a distance of a point, or null.</summary>
    public PropaneTank? NearestVenting(Vector3 point, float within)
    {
        PropaneTank? best = null;
        var bestDistance = within;
        foreach (var tank in venting)
        {
            var distance = tank.GlobalPosition.DistanceTo(point);
            if (distance < bestDistance) { best = tank; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>Counts every tank's chain-radius neighbors once per tick, on a bucket grid of that radius.</summary>
    private void CountPiles()
    {
        if (pilesKnown) return;
        pilesKnown = true;
        var radius = world.Tuning.ChainRadius;
        var count = tanks.Count;
        if (piles.Length < count)
        {
            piles = new int[Math.Max(count, piles.Length * 2)];
            spreads = new float[piles.Length];
            pileNext = new int[piles.Length];
        }
        pileBuckets.Clear();
        var size = Math.Max(radius, 0.5f);
        for (var i = 0; i < count; i++)
        {
            var key = BucketKey(tanks[i].GlobalPosition, size, 0, 0);
            pileNext[i] = pileBuckets.GetValueOrDefault(key, -1);
            pileBuckets[key] = i;
        }
        for (var i = 0; i < count; i++)
        {
            var at = tanks[i].GlobalPosition;
            var neighbors = 0;
            var spread = 0f;
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var j = pileBuckets.GetValueOrDefault(BucketKey(at, size, dx, dz), -1); j >= 0; j = pileNext[j])
                    {
                        var distance = tanks[j].GlobalPosition.DistanceTo(at);
                        if (distance >= radius) continue;
                        neighbors++;
                        spread = Math.Max(spread, distance);
                    }
                }
            }
            piles[i] = neighbors;
            spreads[i] = spread;
        }
    }

    private static long BucketKey(Vector3 at, float size, int dx, int dz) =>
        ((long)((int)MathF.Floor(at.X / size) + dx) << 32) | (uint)((int)MathF.Floor(at.Z / size) + dz);

    private void Disturb(Vector3 center) => grid?.Disturb(center, world.Tuning.BlastRadius, world.Tuning.BotDisturbSettle);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        world.Events.Explosion -= Disturb;
        physics?.Dispose();
        physics = null;
        grid = null;
        if (sight != null && GodotObject.IsInstanceValid(sight)) sight.QueueFree();
        sight = null;
    }
}

/// <summary>Where a ray hit, the surface's normal there and what it hit.</summary>
public readonly record struct RayHit(Vector3 Point, Vector3 Normal, GodotObject? Collider);
