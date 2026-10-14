using System;
using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.Navigation;

public readonly record struct NavigationState(Vector3 Position, Vector3 Velocity, bool Grounded);
public readonly record struct NavigationIntent(Vector3 Direction, bool Sprint, bool Jump, bool Arrived, bool Failed);
public readonly record struct NavigationStep(Vector3 From, Vector3 To, bool Jump);

/// <summary>Where a bot's mode lets it set foot beyond what the geometry allows, such as Ring Out's dissolving lots.</summary>
public interface INavigationArea
{
    bool Allows(Vector3 feet);
}

/// <summary>
/// Persistent, bounded A* routes over the shared lattice (<see cref="NavigationGrid"/>), with ballistic jump links.
/// Brains supply a goal; only after alternate routes have failed does Failed ask them to reconsider it. Searches,
/// routes and recovery allocate nothing once their buffers have grown. No scene, player facade, target selection or
/// Match dependencies.
/// </summary>
public sealed class BotNavigator
{
    // Shortcut tests along a route grow 1, 2, 4... steps ahead and stop at the first that fails.
    private const int ShortcutTries = 4;

    private readonly RouteSearch search = new();
    private readonly List<NavigationStep> route = new();
    private readonly List<Vector3> avoided = new();
    private Vector3 goal;
    private Vector3 direction;
    private Vector3 progressPosition;
    private Vector3 flightDirection;
    private Vector3 runway;
    private float progressTime;
    private float validationTime;
    private float flightTime;
    private float retryTime;
    private int retries;
    private bool searching;
    private bool hasGoal;
    private bool flying;
    private bool sawAir;
    private bool preparing;
    private bool runwayChosen;
    private int preparedBudget;

    public int StuckEvents { get; private set; }
    public int Replans { get; private set; }
    public int Jumps { get; private set; }
    public IReadOnlyList<NavigationStep> Route => route;
    public string Diagnostics => $"searching={searching} replans={Replans} jumps={Jumps} stuck={StuckEvents} retries={retries} goal={goal} route={route.Count} search={search.Diagnostics}";

    public void Reset()
    {
        route.Clear();
        searching = false;
        avoided.Clear();
        hasGoal = flying = sawAir = preparing = runwayChosen = false;
        progressTime = validationTime = retryTime = 0;
        retries = 0;
        direction = Vector3.Zero;
    }

    /// <summary>Reserves bounded search buffers during actor setup, before measured physics ticks.</summary>
    public void Prepare(Tuning tuning)
    {
        if (preparedBudget != tuning.BotSearchBudget)
        {
            search.Reserve(tuning.BotSearchBudget);
            route.EnsureCapacity(tuning.BotSearchBudget + 2);
            avoided.EnsureCapacity(tuning.BotRouteRetries + 2);
            preparedBudget = tuning.BotSearchBudget;
        }
    }

    private static Vector3 Flat(Vector3 point) => new(point.X, 0, point.Z);

    /// <summary>
    /// One physics tick of movement toward <paramref name="requestedGoal"/>. The grid supplies the body and the static
    /// world; <paramref name="hazards"/> are this tick's circles to keep out of; <paramref name="area"/>, if any, the
    /// mode's own limits on where to step.
    /// </summary>
    public NavigationIntent Navigate(NavigationGrid grid, NavigationHazards hazards, NavigationState state,
        Vector3 requestedGoal, Tuning tuning, float dt, INavigationArea? area = null)
    {
        Prepare(tuning);
        var capabilities = grid.Capabilities;
        // Once committed, finish the jump even if tactical targeting changes. Held aim cannot slow the runway
        // or alter this flight; the caller suppresses aim when Jumping is true.
        if (flying)
        {
            flightTime += dt;
            sawAir |= !state.Grounded;
            if ((!sawAir || !state.Grounded) && flightTime < capabilities.FlightTime * tuning.BotFlightTimeoutFactor)
            {
                return new NavigationIntent(flightDirection, false, false, false, false);
            }
            flying = false;
            runwayChosen = false;
            route.Clear();
            progressPosition = state.Position;
            progressTime = 0;
        }
        if (!state.Grounded)
        {
            return new NavigationIntent(direction, false, false, false, false);
        }
        if (!hasGoal || Flat(goal - requestedGoal).Length() > tuning.BotRouteSpacing)
        {
            goal = requestedGoal;
            hasGoal = true;
            route.Clear();
            searching = false;
            runwayChosen = false;
            avoided.Clear();
            retries = 0;
            preparing = false;
            progressPosition = state.Position;
            progressTime = 0;
        }
        if (Flat(state.Position - requestedGoal).Length() < tuning.BotGoalRadius)
        {
            route.Clear();
            direction = Vector3.Zero;
            progressTime = 0;
            return new NavigationIntent(Vector3.Zero, false, false, true, false);
        }

        var layer = grid.LayerAt(state.Position.Y);
        if (!searching) { progressTime += dt; }
        if (progressTime >= tuning.BotProgressWindow)
        {
            if (Flat(state.Position - progressPosition).Length() < tuning.BotProgressDistance)
            {
                StuckEvents++;
                if (route.Count > 0) { avoided.Add(route[0].Jump ? route[0].To : state.Position + direction * tuning.BotRouteSpacing); }
                route.Clear();
                preparing = false;
                retries++;
                // The world disagrees with what the lattice remembers here (a prop moved): ask it again.
                grid.Invalidate(state.Position, grid.Spacing * 2);
            }
            else { retries = 0; }
            progressPosition = state.Position;
            progressTime = 0;
        }
        if (retries >= tuning.BotRouteRetries)
        {
            return new NavigationIntent(Vector3.Zero, false, false, false, true);
        }
        retryTime -= dt;
        if (route.Count == 0)
        {
            if (retryTime > 0) { return new NavigationIntent(Vector3.Zero, false, false, false, false); }
            if (!searching)
            {
                Replans++;
                runwayChosen = false;
                searching = true;
                search.Start(layer, state.Position, goal, avoided);
            }
            search.Advance(grid, hazards, area, tuning, tuning.BotSearchPerTick);
            if (!search.Complete) { return new NavigationIntent(Vector3.Zero, false, false, false, false); }
            searching = false;
            route.AddRange(search.Result);
            progressTime = 0;
            progressPosition = state.Position;
            validationTime = 0; // Recheck against the current world after a multi-tick search.
            if (route.Count == 0)
            {
                retries++;
                retryTime = tuning.BotReplanInterval;
                return new NavigationIntent(Vector3.Zero, false, false, false, retries >= tuning.BotRouteRetries);
            }
        }

        while (route.Count > 1 && !route[0].Jump && Flat(route[0].To - state.Position).Length() < tuning.BotWaypointRadius)
        {
            route.RemoveAt(0);
        }
        if (validationTime <= 0) { Shortcut(grid, hazards, area, layer, state.Position, tuning); }
        var step = route[0];
        validationTime -= dt;
        if (validationTime <= 0 && !step.Jump)
        {
            validationTime = tuning.BotReplanInterval;
            if (Walkable(grid, hazards, area, layer, state.Position, step.To, tuning) == NavigationAnswer.No)
            {
                // Something now stands where the lattice remembers open ground: forget it there and plan again.
                grid.Invalidate((state.Position + step.To) / 2, Flat(step.To - state.Position).Length() / 2 + grid.Spacing);
                route.Clear();
                return new NavigationIntent(Vector3.Zero, false, false, false, false);
            }
        }
        if (step.Jump)
        {
            var jumpDirection = Flat(step.To - step.From).Normalized();
            var runup = capabilities.Speed * capabilities.Speed / (2 * capabilities.Acceleration) + capabilities.Radius * 2;
            if (!runwayChosen)
            {
                runway = step.From - jumpDirection * runup;
                var clear = Walkable(grid, hazards, area, layer, step.From, runway, tuning);
                if (clear == NavigationAnswer.Pending) { return new NavigationIntent(Vector3.Zero, false, false, false, false); }
                if (clear == NavigationAnswer.No)
                {
                    avoided.Add(step.To);
                    route.Clear();
                    retries++;
                    return new NavigationIntent(Vector3.Zero, false, false, false, false);
                }
                runwayChosen = true;
                // Already approaching at the correct speed and angle: keep that momentum.
                preparing = (Flat(state.Velocity) - jumpDirection * capabilities.Speed).Length() > capabilities.Speed * tuning.BotLaunchSpeedTolerance;
            }
            if (preparing && Flat(state.Position - runway).Length() > tuning.BotWaypointRadius)
            {
                direction = Flat(runway - state.Position).Normalized();
                return new NavigationIntent(direction, false, false, false, false);
            }
            preparing = false;
            direction = jumpDirection;
            var distanceToLaunch = Flat(step.From - state.Position).Dot(jumpDirection);
            if (distanceToLaunch <= capabilities.Speed * dt + tuning.BotJumpMargin &&
                (Flat(state.Velocity) - jumpDirection * capabilities.Speed).Length() <= capabilities.Speed * tuning.BotLaunchSpeedTolerance &&
                Flat(state.Position - step.From).Length() < capabilities.Radius * 2)
            {
                // Dynamic props/tanks can invalidate a previously safe jump. Check immediately before takeoff, live.
                var takeoff = Takeoff(grid, hazards, area, state.Position, jumpDirection);
                if (takeoff == NavigationAnswer.Pending) return new NavigationIntent(Vector3.Zero, false, false, false, false);
                if (takeoff == NavigationAnswer.No)
                {
                    avoided.Add(step.To);
                    route.Clear();
                    retries++;
                    return new NavigationIntent(Vector3.Zero, false, false, false, false);
                }
                flying = true;
                flightDirection = jumpDirection;
                flightTime = 0;
                sawAir = false;
                Jumps++;
                return new NavigationIntent(direction, false, true, false, false);
            }
            if (distanceToLaunch < -capabilities.Radius * 2)
            {
                // A collision or a turn can carry us past a takeoff before alignment. Try a fresh runway,
                // never keep running along a stale jump link until its target has to be abandoned.
                runwayChosen = false;
            }
            return new NavigationIntent(direction, false, false, false, false);
        }
        var toward = Flat(step.To - state.Position);
        var wanted = toward.Normalized();
        var turn = 1 - Mathf.Exp(-tuning.BotTurnSharpness * dt);
        direction = direction.LengthSquared() < 0.01f ? wanted : direction.Lerp(wanted, turn).Normalized();
        return new NavigationIntent(direction, capabilities.SprintSpeed > capabilities.Speed && toward.Length() > tuning.BotSprintDistance, false, false, false);
    }

    public bool Jumping => flying || route.Count > 0 && route[0].Jump;

    /// <summary>
    /// Skips walk-only corners when the body can reach further along the route in a straight line. Never skips a
    /// runway. Each test is one live sweep, so they grow 1, 2, 4... steps ahead and stop at the first that fails.
    /// </summary>
    private void Shortcut(NavigationGrid grid, NavigationHazards hazards, INavigationArea? area, NavigationGrid.Layer layer, Vector3 position, Tuning tuning)
    {
        var walks = 0;
        while (walks < route.Count && !route[walks].Jump) { walks++; }
        for (int ahead = 1, tries = 0; ahead < walks && tries < ShortcutTries; ahead *= 2, tries++)
        {
            if (Walkable(grid, hazards, area, layer, position, route[ahead].To, tuning) != NavigationAnswer.Yes) { break; }
            route.RemoveRange(0, ahead);
            walks -= ahead;
        }
    }

    /// <summary>A straight walk from where the bot stands, through the hazards it may already be inside.</summary>
    private NavigationAnswer Walkable(NavigationGrid grid, NavigationHazards hazards, INavigationArea? area, NavigationGrid.Layer layer,
        Vector3 from, Vector3 to, Tuning tuning)
    {
        if (!Avoids(from, to, avoided, tuning.BotRouteSpacing * 0.75f) || !hazards.SafeAlong(from, to, grid.Capabilities.Radius, from) ||
            area != null && !area.Allows(to)) { return NavigationAnswer.No; }
        return grid.WalkSegment(layer, from, to);
    }

    /// <summary>The jump about to be taken, asked live: the arc and landing clear of everything, hazards included.</summary>
    private static NavigationAnswer Takeoff(NavigationGrid grid, NavigationHazards hazards, INavigationArea? area, Vector3 from, Vector3 direction)
    {
        var landing = from + direction * grid.Capabilities.JumpRange;
        if (!hazards.SafeAlong(from, landing, JumpHazardStep, null) || area != null && !area.Allows(landing)) return NavigationAnswer.No;
        return grid.Takeoff(from, direction);
    }

    /// <summary>Hazards along a jump are tested this often (meters): no sample may fall inside one.</summary>
    private const float JumpHazardStep = 0.25f;

    private static bool Avoids(Vector3 from, Vector3 to, List<Vector3> avoided, float radius)
    {
        var segment = Flat(to - from);
        foreach (var point in avoided)
        {
            var t = segment.LengthSquared() > 0 ? Mathf.Clamp(Flat(point - from).Dot(segment) / segment.LengthSquared(), 0, 1) : 0;
            if (Flat(point - from - segment * t).Length() < radius) { return false; }
        }
        return true;
    }

    /// <summary>Synchronous entry point for tests and offline callers; runtime navigation budgets work per tick.</summary>
    public static IReadOnlyList<NavigationStep> Plan(NavigationGrid grid, NavigationHazards hazards, Vector3 start, Vector3 goal, Tuning tuning,
        IReadOnlyList<Vector3>? avoided = null, INavigationArea? area = null)
    {
        var search = new RouteSearch();
        var list = new List<Vector3>();
        if (avoided != null) list.AddRange(avoided);
        search.Start(grid.LayerAt(start.Y), start, goal, list);
        while (!search.Complete)
        {
            grid.Refill();
            search.Advance(grid, hazards, area, tuning, tuning.BotSearchBudget);
        }
        return search.Result.ToArray();
    }

    /// <summary>
    /// One bot's A* over the lattice, reused for every search. The start and goal need not lie on lattice points: the
    /// search first tries walking straight there, then joins the lattice at the points around the start and leaves it
    /// from any point near the goal. Waiting on the grid's budget resumes exactly where it stopped.
    /// </summary>
    private sealed class RouteSearch
    {
        private const long Origin = long.MinValue;

        private struct Node
        {
            public float Cost;
            public long Parent;
            public bool Jump;
            public bool Closed;
        }

        private readonly PriorityQueue<long, float> frontier = new();
        private readonly Dictionary<long, Node> nodes = new();
        private readonly List<Vector3> avoided = new();
        private NavigationGrid.Layer layer = null!;
        private Vector3 start;
        private Vector3 goal;
        private int expansions;
        private int stage;
        private int seed;

        public string Diagnostics => $"stage{stage}/nodes{nodes.Count}/expansions{expansions}";
        public bool Complete { get; private set; }
        public List<NavigationStep> Result { get; } = new();

        private static long Key(Vector2I cell) => ((long)cell.X << 32) | (uint)cell.Y;
        private static Vector2I Cell(long key) => new((int)(key >> 32), (int)key);

        public void Reserve(int budget)
        {
            if (budget <= 0) throw new ArgumentOutOfRangeException(nameof(budget));
            // Each closed node introduces at most sixteen links. Reserve once, rather than grow in a hot tick.
            var capacity = checked(budget * 16 + 16);
            nodes.EnsureCapacity(capacity);
            frontier.EnsureCapacity(capacity);
            Result.EnsureCapacity(budget + 2);
            avoided.EnsureCapacity(budget + 2);
        }

        public void Start(NavigationGrid.Layer on, Vector3 from, Vector3 to, List<Vector3> avoid)
        {
            layer = on;
            start = from;
            // A ground navigator: goals are reached on the lattice's own plane.
            goal = new Vector3(to.X, on.Y, to.Z);
            avoided.Clear();
            avoided.AddRange(avoid);
            frontier.Clear();
            nodes.Clear();
            Result.Clear();
            expansions = stage = seed = 0;
            Complete = false;
        }

        public void Advance(NavigationGrid grid, NavigationHazards hazards, INavigationArea? area, Tuning tuning, int budget)
        {
            if (Complete) { return; }
            if (tuning.BotRouteSpacing <= 0 || tuning.BotSearchBudget <= 0 || tuning.BotSearchPerTick <= 0 || budget <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tuning), "navigation needs positive spacing and search budgets");
            }
            var spacing = grid.Spacing;
            var radius = grid.Capabilities.Radius;
            var speed = grid.Capabilities.Speed;
            if (stage == 0)
            {
                // Straight there, if nothing is in the way.
                var direct = Walk(grid, hazards, area, start, goal, tuning);
                if (direct == NavigationAnswer.Pending) { return; }
                if (direct == NavigationAnswer.Yes)
                {
                    Result.Add(new NavigationStep(start, goal, false));
                    Complete = true;
                    return;
                }
                stage = 1;
            }
            if (stage == 1)
            {
                // Join the lattice at the four points around the start, else at the twelve around those.
                var corner = grid.CellBelow(start);
                for (; seed < 16; seed++)
                {
                    if (seed == 4 && frontier.Count > 0) { break; }
                    var offset = seed < 4 ? new Vector2I(seed & 1, seed >> 1) : Ring[seed - 4];
                    var cell = corner + offset;
                    var allowed = Allowed(grid, hazards, area, cell, tuning);
                    if (allowed == NavigationAnswer.Pending) { return; }
                    if (allowed == NavigationAnswer.No) { continue; }
                    var point = grid.Point(layer, cell);
                    var walk = Walk(grid, hazards, area, start, point, tuning);
                    if (walk == NavigationAnswer.Pending) { return; }
                    if (walk == NavigationAnswer.No) { continue; }
                    var cost = Flat(point - start).Length() / speed;
                    nodes[Key(cell)] = new Node { Cost = cost, Parent = Origin };
                    frontier.Enqueue(Key(cell), cost + Flat(point - goal).Length() / speed);
                }
                stage = 2;
            }
            var worked = 0;
            while (worked < budget && expansions < tuning.BotSearchBudget && frontier.TryPeek(out var key, out var priority) && grid.TryExpand())
            {
                frontier.Dequeue();
                worked++;
                var node = nodes[key];
                if (node.Closed) { continue; }
                var cell = Cell(key);
                var from = grid.Point(layer, cell);
                if (Flat(from - goal).Length() < spacing * 2)
                {
                    var finish = Walk(grid, hazards, area, from, goal, tuning);
                    if (finish == NavigationAnswer.Pending) { frontier.Enqueue(key, priority); return; }
                    if (finish == NavigationAnswer.Yes)
                    {
                        Finish(grid, key);
                        return;
                    }
                }
                for (var direction = 0; direction < NavigationGrid.Steps.Length; direction++)
                {
                    var step = NavigationGrid.Steps[direction];
                    if (!Explore(grid, hazards, area, tuning, key, node.Cost, cell, from, cell + step, direction, false) ||
                        !Explore(grid, hazards, area, tuning, key, node.Cost, cell, from, cell + step * grid.JumpCells(direction), direction, true))
                    {
                        // The budget ran out mid-expansion: come back to this point next tick. Links already taken
                        // are kept and will not be taken again.
                        frontier.Enqueue(key, priority);
                        return;
                    }
                }
                node.Closed = true;
                nodes[key] = node;
                expansions++;
            }
            Complete = frontier.Count == 0 || expansions >= tuning.BotSearchBudget;
        }

        /// <summary>Relaxes one link; false when an answer is pending on the budget.</summary>
        private bool Explore(NavigationGrid grid, NavigationHazards hazards, INavigationArea? area, Tuning tuning,
            long parent, float parentCost, Vector2I cell, Vector3 from, Vector2I next, int direction, bool jump)
        {
            if (jump && grid.JumpCells(direction) == 0) { return true; }
            var key = Key(next);
            var known = nodes.TryGetValue(key, out var old);
            if (known && old.Closed) { return true; }
            var to = grid.Point(layer, next);
            var cost = parentCost + Flat(to - from).Length() / grid.Capabilities.Speed + (jump ? tuning.BotJumpCost : 0);
            if (known && cost >= old.Cost) { return true; }
            var allowed = Allowed(grid, hazards, area, next, tuning);
            if (allowed != NavigationAnswer.Yes) { return allowed != NavigationAnswer.Pending; }
            if (!Avoids(from, to, avoided, grid.Spacing * 0.75f)) { return true; }
            NavigationAnswer link;
            if (jump)
            {
                link = grid.Jump(layer, cell, direction);
                if (link == NavigationAnswer.Yes)
                {
                    var landing = from + Flat(to - from).Normalized() * grid.Capabilities.JumpRange;
                    if (!hazards.SafeAlong(from, landing, JumpHazardStep, null) || area != null && !area.Allows(landing)) { return true; }
                }
            }
            else
            {
                link = grid.Walk(layer, cell, direction);
                if (link == NavigationAnswer.Yes && !hazards.SafeAlong(from, to, grid.Capabilities.Radius, start)) { return true; }
            }
            if (link != NavigationAnswer.Yes) { return link != NavigationAnswer.Pending; }
            nodes[key] = new Node { Cost = cost, Parent = parent, Jump = jump };
            frontier.Enqueue(key, cost + Flat(to - goal).Length() / grid.Capabilities.Speed);
            return true;
        }

        private NavigationAnswer Allowed(NavigationGrid grid, NavigationHazards hazards, INavigationArea? area, Vector2I cell, Tuning tuning)
        {
            var point = grid.Point(layer, cell);
            if (!hazards.Safe(point, start) || area != null && !area.Allows(point)) { return NavigationAnswer.No; }
            foreach (var blocked in avoided)
            {
                if (Flat(point - blocked).Length() < tuning.BotRouteSpacing * 0.75f) { return NavigationAnswer.No; }
            }
            return grid.Standable(layer, cell);
        }

        private NavigationAnswer Walk(NavigationGrid grid, NavigationHazards hazards, INavigationArea? area, Vector3 from, Vector3 to, Tuning tuning)
        {
            if (!Avoids(from, to, avoided, tuning.BotRouteSpacing * 0.75f) || !hazards.SafeAlong(from, to, grid.Capabilities.Radius, start) ||
                area != null && !area.Allows(to)) { return NavigationAnswer.No; }
            return grid.WalkSegment(layer, from, to);
        }

        private void Finish(NavigationGrid grid, long key)
        {
            Result.Add(new NavigationStep(grid.Point(layer, Cell(key)), goal, false));
            while (true)
            {
                var node = nodes[key];
                var to = grid.Point(layer, Cell(key));
                if (node.Parent == Origin)
                {
                    Result.Add(new NavigationStep(start, to, false));
                    break;
                }
                Result.Add(new NavigationStep(grid.Point(layer, Cell(node.Parent)), to, node.Jump));
                key = node.Parent;
            }
            Result.Reverse();
            Complete = true;
        }

        private static readonly Vector2I[] Ring =
        {
            new(-1, -1), new(0, -1), new(1, -1), new(2, -1), new(-1, 0), new(2, 0),
            new(-1, 1), new(2, 1), new(-1, 2), new(0, 2), new(1, 2), new(2, 2),
        };
    }
}
