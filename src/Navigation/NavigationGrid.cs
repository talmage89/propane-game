using System;
using System.Collections.Generic;
using Godot;

namespace Propane.Navigation;

/// <summary>A cached navigation answer; Pending when it needs a physics query and this tick's budget is spent.</summary>
public enum NavigationAnswer : byte { Unknown, Yes, No, Pending }

/// <summary>
/// One world's navigation lattice for one actor body, shared by every bot that plans there. Points lie on a
/// world-aligned grid <see cref="Spacing"/> apart; for each it remembers whether the body stands there, and whether it
/// walks or jumps along each of eight links to its neighbors and to the points one jump away. The answers come from
/// the static geometry (<see cref="INavigationWorld"/>: houses, fences, cars, props; never tanks), asked the first time
/// a search needs them and then kept, so a suburb is surveyed once for all bots instead of once per search. Ground
/// support is remembered on a three-times finer grid, which also serves straight walks between arbitrary points.
/// <para>
/// Live questions spend a per-tick query budget (<see cref="QueryBudget"/>); once it is spent an unknown answer is
/// <see cref="NavigationAnswer.Pending"/> and searches resume next tick. Answers near an explosion are forgotten at
/// once and kept only briefly while the debris settles (<see cref="Disturb"/>); a bot that finds the world disagreeing
/// with an answer forgets the answers around it (<see cref="Invalidate"/>).
/// </para>
/// </summary>
public sealed class NavigationGrid
{
    /// <summary>The eight link directions, counterclockwise from +X; odd ones are diagonal.</summary>
    public static readonly Vector2I[] Steps =
    {
        new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1), new(0, -1), new(1, -1),
    };

    /// <summary>Support points per lattice spacing; lattice points and both link kinds land on support points.</summary>
    public const int SupportDivisions = 3;
    private const int ChunkBits = 4;
    private const int ChunkSize = 1 << ChunkBits;
    private const int ChunkMask = ChunkSize - 1;
    private readonly float layerTolerance;

    private readonly INavigationWorld world;
    private readonly float support;
    private readonly int[] jumpCells = new int[2];
    private readonly List<Layer> layers = new();
    private readonly Stack<Layer> spareLayers = new();
    private readonly Stack<Cell[]> spareChunks = new();
    private int reservedChunks;
    private readonly List<(Vector3 Center, float Radius, double Until)> settling = new();
    private double now;
    private long tickStart;

    public NavigationCapabilities Capabilities { get; }
    public float Spacing { get; }
    /// <summary>Physics queries allowed per tick, across every search sharing this grid.</summary>
    public int QueryBudget { get; set; } = int.MaxValue;
    /// <summary>Search expansions allowed per tick, across every search sharing this grid.</summary>
    public int ExpansionBudget { get; set; } = int.MaxValue;
    public int ExpansionsThisTick { get; private set; }
    public long QueriesThisTick => world.Queries - tickStart;
    public long Queries => world.Queries;
    /// <summary>Answers kept so far, including forgotten ones; with <see cref="Queries"/>, how much the cache saves.</summary>
    public long Answers { get; private set; }
    public INavigationWorld World => world;
    private bool CanQuery(int count = 1)
    {
        if (world.Queries - tickStart + count <= QueryBudget) return true;
        queryLimited = true;
        return false;
    }

    private bool queryLimited, expansionLimited;
    /// <summary>Ticks begun, and those in which a search or check waited on the query or expansion budget.</summary>
    public (long Ticks, long QueryLimited, long ExpansionLimited) Saturation { get; private set; }

    public NavigationGrid(INavigationWorld world, NavigationCapabilities capabilities, float spacing, float minimumJumpRange,
        float layerTolerance)
    {
        if (spacing <= 0 || capabilities.Speed <= 0) throw new ArgumentOutOfRangeException(nameof(spacing), "navigation needs a positive spacing and speed");
        this.world = world;
        if (!float.IsFinite(layerTolerance) || layerTolerance < 0) throw new ArgumentOutOfRangeException(nameof(layerTolerance));
        this.layerTolerance = layerTolerance;
        Capabilities = capabilities;
        Spacing = spacing;
        support = spacing / SupportDivisions;
        // A jump link reaches the farthest lattice point inside the jump's range along its direction. It must still
        // cover most of that range, or the landing would lie well past the point the route continues from.
        for (var diagonal = 0; diagonal < 2; diagonal++)
        {
            var step = diagonal == 1 ? spacing * Mathf.Sqrt2 : spacing;
            var cells = capabilities.JumpVelocity > 0 && capabilities.Gravity > 0 ? (int)(capabilities.JumpRange / step) : 0;
            jumpCells[diagonal] = cells >= 1 && cells * step >= capabilities.JumpRange * minimumJumpRange ? cells : 0;
        }
    }

    /// <summary>Starts a physics tick: refills the budgets and lets settled disturbances go.</summary>
    public void BeginTick(double time)
    {
        var saturation = Saturation;
        Saturation = (saturation.Ticks + 1, saturation.QueryLimited + (queryLimited ? 1 : 0), saturation.ExpansionLimited + (expansionLimited ? 1 : 0));
        queryLimited = expansionLimited = false;
        now = time;
        Refill();
        for (var i = settling.Count - 1; i >= 0; i--)
        {
            if (settling[i].Until <= now) settling.RemoveAt(i);
        }
    }

    /// <summary>Refills this tick's budgets, for offline planning that runs many ticks' work at once.</summary>
    public void Refill()
    {
        tickStart = world.Queries;
        ExpansionsThisTick = 0;
    }

    /// <summary>Takes one search expansion from this tick's budget.</summary>
    public bool TryExpand()
    {
        if (ExpansionsThisTick >= ExpansionBudget)
        {
            expansionLimited = true;
            return false;
        }
        ExpansionsThisTick++;
        return true;
    }

    /// <summary>Lattice points per jump link along a direction (0: no jump that way).</summary>
    public int JumpCells(int direction) => jumpCells[direction & 1];

    // ------------------------------------------------------------------ Layers and storage

    /// <summary>A horizontal plane of lattice points at one height.</summary>
    public sealed class Layer
    {
        internal Layer(float y, int capacity) { Y = y; Chunks = new(capacity); }
        public float Y { get; private set; }
        internal void SetHeight(float y) => Y = y;
        internal readonly Dictionary<long, Cell[]> Chunks;
        internal long LastKey = long.MinValue;
        internal Cell[]? LastChunk;
    }

    /// <summary>
    /// One lattice point's answers, two bits each (Unknown, Yes, No): the body fits; ground under the nine support
    /// points of its 3×3 block (the lattice point is the first); walking and jumping along each direction.
    /// </summary>
    internal struct Cell
    {
        public uint Supports;
        public ushort Walks;
        public ushort Jumps;
        public byte Fit;
        /// <summary>When answers made while debris was settling here are forgotten; 0 keeps them.</summary>
        public float Expires;
    }

    /// <summary>The layer for feet at this height, made the first time a bot stands there.</summary>
    public Layer LayerAt(float y)
    {
        foreach (var layer in layers)
        {
            if (Math.Abs(layer.Y - y) <= layerTolerance) return layer;
        }
        if (!spareLayers.TryPop(out var made)) { made = new Layer(y, reservedChunks); }
        else { made.SetHeight(y); }
        layers.Add(made);
        return made;
    }

    /// <summary>Reserves metadata without choosing ground heights; heights still come from grounded actors.</summary>
    public void ReserveLayers(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        layers.EnsureCapacity(count);
        spareLayers.EnsureCapacity(count);
        while (layers.Count + spareLayers.Count < count) spareLayers.Push(new Layer(0, reservedChunks));
    }

    /// <summary>Allocates answer storage for a known map before planning; querying remains lazy and budgeted.</summary>
    public void Reserve(Rect2 bounds)
    {
        var first = CellBelow(new Vector3(bounds.Position.X, 0, bounds.Position.Y));
        var last = CellBelow(new Vector3(bounds.End.X, 0, bounds.End.Y));
        var count = ((last.X >> ChunkBits) - (first.X >> ChunkBits) + 1) * ((last.Y >> ChunkBits) - (first.Y >> ChunkBits) + 1);
        // Unusually fine dev tuning may be better served by growing on demand. Reserve storage only: a layer's
        // height must come from a grounded actor, never the slab (roads can stand above it).
        if (count > 1024) return;
        reservedChunks = count;
        foreach (var layer in layers) layer.Chunks.EnsureCapacity(count);
        foreach (var layer in spareLayers) layer.Chunks.EnsureCapacity(count);
        // One map's answer storage plus one first chunk for each additional standing height.
        var chunks = count + Math.Max(0, layers.Count + spareLayers.Count - 1);
        spareChunks.EnsureCapacity(chunks);
        for (var i = spareChunks.Count; i < chunks; i++) spareChunks.Push(new Cell[ChunkSize * ChunkSize]);
    }

    public Vector3 Point(Layer layer, Vector2I cell) => new(cell.X * Spacing, layer.Y, cell.Y * Spacing);
    private Vector3 SupportPoint(Layer layer, int x, int z) => new(x * support, layer.Y, z * support);

    /// <summary>The lattice cell whose point is nearest below and left of a position.</summary>
    public Vector2I CellBelow(Vector3 position) => new((int)MathF.Floor(position.X / Spacing), (int)MathF.Floor(position.Z / Spacing));

    private static int FloorDiv(int value, int divisor) => value >= 0 ? value / divisor : (value - divisor + 1) / divisor;

    private ref Cell CellAt(Layer layer, int x, int z)
    {
        var key = ((long)(x >> ChunkBits) << 32) | (uint)(z >> ChunkBits);
        if (key != layer.LastKey || layer.LastChunk == null)
        {
            if (!layer.Chunks.TryGetValue(key, out var chunk))
            {
                if (!spareChunks.TryPop(out chunk)) chunk = new Cell[ChunkSize * ChunkSize];
                layer.Chunks.Add(key, chunk);
            }
            layer.LastKey = key;
            layer.LastChunk = chunk;
        }
        ref var cell = ref layer.LastChunk[((z & ChunkMask) << ChunkBits) | (x & ChunkMask)];
        if (cell.Expires > 0 && now >= cell.Expires) cell = default;
        return ref cell;
    }

    private static NavigationAnswer Read(uint bits, int field) => (NavigationAnswer)((bits >> (field * 2)) & 3);
    private static uint Write(uint bits, int field, bool yes) =>
        (bits & ~(3u << (field * 2))) | ((yes ? 1u : 2u) << (field * 2));

    /// <summary>Answers made near settling debris are kept only until it settles.</summary>
    private void Stamp(ref Cell cell, Vector3 at)
    {
        Answers++;
        foreach (var (center, radius, until) in settling)
        {
            var dx = at.X - center.X;
            var dz = at.Z - center.Z;
            if (dx * dx + dz * dz < radius * radius && (cell.Expires == 0 || cell.Expires < until)) cell.Expires = (float)until;
        }
    }

    // ------------------------------------------------------------------ Answers

    /// <summary>Ground lies under the support point (x, z) of the finer grid.</summary>
    public NavigationAnswer Supported(Layer layer, int x, int z)
    {
        var cx = FloorDiv(x, SupportDivisions);
        var cz = FloorDiv(z, SupportDivisions);
        ref var cell = ref CellAt(layer, cx, cz);
        var field = (z - cz * SupportDivisions) * SupportDivisions + (x - cx * SupportDivisions);
        var known = Read(cell.Supports, field);
        if (known != NavigationAnswer.Unknown) return known;
        if (!CanQuery()) return NavigationAnswer.Pending;
        var point = SupportPoint(layer, x, z);
        var yes = world.Supported(point);
        cell.Supports = Write(cell.Supports, field, yes);
        Stamp(ref cell, point);
        return yes ? NavigationAnswer.Yes : NavigationAnswer.No;
    }

    /// <summary>The body stands at a lattice point: ground under it, nothing in it.</summary>
    public NavigationAnswer Standable(Layer layer, Vector2I at)
    {
        var ground = Supported(layer, at.X * SupportDivisions, at.Y * SupportDivisions);
        if (ground != NavigationAnswer.Yes) return ground;
        ref var cell = ref CellAt(layer, at.X, at.Y);
        var known = (NavigationAnswer)cell.Fit;
        if (known != NavigationAnswer.Unknown) return known;
        if (!CanQuery()) return NavigationAnswer.Pending;
        var point = Point(layer, at);
        var yes = world.Fits(point);
        cell.Fit = (byte)(yes ? NavigationAnswer.Yes : NavigationAnswer.No);
        Stamp(ref cell, point);
        return yes ? NavigationAnswer.Yes : NavigationAnswer.No;
    }

    /// <summary>The body walks from a lattice point to its neighbor in a direction, on ground all the way.</summary>
    public NavigationAnswer Walk(Layer layer, Vector2I from, int direction)
    {
        ref var cell = ref CellAt(layer, from.X, from.Y);
        var known = Read(cell.Walks, direction);
        if (known != NavigationAnswer.Unknown) return known;
        var step = Steps[direction];
        var to = from + step;
        var answer = Standable(layer, to);
        for (var k = 1; answer == NavigationAnswer.Yes && k < SupportDivisions; k++)
        {
            answer = Supported(layer, from.X * SupportDivisions + step.X * k, from.Y * SupportDivisions + step.Y * k);
        }
        if (answer == NavigationAnswer.Yes)
        {
            if (!CanQuery()) return NavigationAnswer.Pending;
            answer = world.Sweep(Point(layer, from), Point(layer, to)) ? NavigationAnswer.Yes : NavigationAnswer.No;
        }
        if (answer == NavigationAnswer.Pending) return answer;
        // Still this cell: asking about others may add chunks, but a chunk's array never moves.
        cell.Walks = (ushort)Write(cell.Walks, direction, answer == NavigationAnswer.Yes);
        Stamp(ref cell, Point(layer, from));
        return answer;
    }

    /// <summary>
    /// The body jumps from a lattice point to the one <see cref="JumpCells"/> away in a direction: walking there along
    /// the lattice is blocked (a jump is for fences and hedges, never a shortcut), the arc is clear, and it can stand
    /// at both that point and the landing a full jump away.
    /// </summary>
    public NavigationAnswer Jump(Layer layer, Vector2I from, int direction)
    {
        ref var cell = ref CellAt(layer, from.X, from.Y);
        var known = Read(cell.Jumps, direction);
        if (known != NavigationAnswer.Unknown) return known;
        var cells = JumpCells(direction);
        var answer = cells == 0 ? NavigationAnswer.No : Standable(layer, from);
        var step = Steps[direction];
        if (answer == NavigationAnswer.Yes) answer = Standable(layer, from + step * cells);
        if (answer == NavigationAnswer.Yes)
        {
            var blocked = false;
            for (var k = 0; k < cells && !blocked; k++)
            {
                var walk = Walk(layer, from + step * k, direction);
                if (walk == NavigationAnswer.Pending) return walk;
                blocked = walk == NavigationAnswer.No;
            }
            if (!blocked) answer = NavigationAnswer.No;
        }
        if (answer == NavigationAnswer.Yes)
        {
            if (!CanQuery(world.FlightQueries + 2)) return NavigationAnswer.Pending;
            var takeoff = Point(layer, from);
            var unit = new Vector3(step.X, 0, step.Y).Normalized();
            var landing = takeoff + unit * Capabilities.JumpRange;
            answer = world.Supported(landing) && world.Fits(landing) && world.Flight(takeoff, unit) ? NavigationAnswer.Yes : NavigationAnswer.No;
        }
        if (answer == NavigationAnswer.Pending) return answer;
        cell.Jumps = (ushort)Write(cell.Jumps, direction, answer == NavigationAnswer.Yes);
        Stamp(ref cell, Point(layer, from));
        return answer;
    }

    /// <summary>
    /// A straight walk between any two points on a layer: a live sweep of the body, with ground under it every
    /// support step from the remembered finer grid (each sample at its nearest support point).
    /// </summary>
    public NavigationAnswer WalkSegment(Layer layer, Vector3 from, Vector3 to)
    {
        var flat = new Vector3(to.X - from.X, 0, to.Z - from.Z);
        var samples = Math.Max(1, (int)MathF.Ceiling(flat.Length() / support));
        for (var i = 1; i <= samples; i++)
        {
            var p = from + flat * ((float)i / samples);
            var ground = Supported(layer, (int)MathF.Round(p.X / support), (int)MathF.Round(p.Z / support));
            if (ground != NavigationAnswer.Yes) return ground;
        }
        if (!CanQuery()) return NavigationAnswer.Pending;
        return world.Sweep(from, new Vector3(to.X, layer.Y, to.Z)) ? NavigationAnswer.Yes : NavigationAnswer.No;
    }

    /// <summary>A live landing and flight check, reserving the whole arc so takeoffs share the same query budget.</summary>
    public NavigationAnswer Takeoff(Vector3 from, Vector3 direction)
    {
        if (!CanQuery(world.FlightQueries + 2)) return NavigationAnswer.Pending;
        var landing = from + direction * Capabilities.JumpRange;
        return world.Supported(landing) && world.Fits(landing) && world.Flight(from, direction) ? NavigationAnswer.Yes : NavigationAnswer.No;
    }

    // ------------------------------------------------------------------ Forgetting

    /// <summary>
    /// An explosion: forget every answer it could have changed, and keep answers made nearby only for
    /// <paramref name="settle"/> seconds, while thrown props come to rest.
    /// </summary>
    public void Disturb(Vector3 center, float radius, float settle)
    {
        // Links start up to a jump (or a spacing) away from the points they pass over.
        var reach = radius + Capabilities.JumpRange + Spacing;
        Invalidate(center, reach);
        if (settle > 0) settling.Add((center, reach, now + settle));
    }

    /// <summary>Forgets every answer about lattice points within a radius.</summary>
    public void Invalidate(Vector3 center, float radius)
    {
        var x0 = (int)MathF.Floor((center.X - radius) / Spacing);
        var x1 = (int)MathF.Ceiling((center.X + radius) / Spacing);
        var z0 = (int)MathF.Floor((center.Z - radius) / Spacing);
        var z1 = (int)MathF.Ceiling((center.Z + radius) / Spacing);
        foreach (var layer in layers)
        {
            for (var cz = z0 >> ChunkBits; cz <= z1 >> ChunkBits; cz++)
            {
                for (var cx = x0 >> ChunkBits; cx <= x1 >> ChunkBits; cx++)
                {
                    if (!layer.Chunks.TryGetValue(((long)cx << 32) | (uint)cz, out var chunk)) continue;
                    for (var index = 0; index < chunk.Length; index++)
                    {
                        var x = (cx << ChunkBits) | (index & ChunkMask);
                        var z = (cz << ChunkBits) | (index >> ChunkBits);
                        var dx = x * Spacing - center.X;
                        var dz = z * Spacing - center.Z;
                        if (dx * dx + dz * dz <= radius * radius) chunk[index] = default;
                    }
                }
            }
        }
    }
}
