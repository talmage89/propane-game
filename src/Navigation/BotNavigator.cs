using System;
using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.Navigation;

public readonly record struct NavigationState(Vector3 Position, Vector3 Velocity, bool Grounded);
public readonly record struct NavigationIntent(Vector3 Direction, bool Sprint, bool Jump, bool Arrived, bool Failed);
public readonly record struct NavigationStep(Vector3 From, Vector3 To, bool Jump);

/// <summary>
/// Persistent, bounded A* routes with ballistic links. Brains supply a goal; only after alternate routes have
/// failed does Failed ask them to reconsider it. No scene, player facade, target selection or Match dependencies.
/// </summary>
public sealed class BotNavigator
{
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
    private bool hasGoal;
    private bool flying;
    private bool sawAir;
    private bool preparing;
    private bool runwayChosen;

    public int StuckEvents { get; private set; }
    public int Replans { get; private set; }
    public int Jumps { get; private set; }
    public IReadOnlyList<NavigationStep> Route => route;

    public void Reset()
    {
        route.Clear();
        avoided.Clear();
        hasGoal = flying = sawAir = preparing = runwayChosen = false;
        progressTime = validationTime = retryTime = 0;
        retries = 0;
        direction = Vector3.Zero;
    }

    private static Vector3 Flat(Vector3 point) => new(point.X, 0, point.Z);

    public NavigationIntent Navigate(INavigationWorld world, NavigationState state, NavigationCapabilities capabilities,
        Vector3 requestedGoal, Tuning tuning, float dt)
    {
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

        progressTime += dt;
        if (progressTime >= tuning.BotProgressWindow)
        {
            if (Flat(state.Position - progressPosition).Length() < tuning.BotProgressDistance)
            {
                StuckEvents++;
                if (route.Count > 0) { avoided.Add(route[0].Jump ? route[0].To : state.Position + direction * tuning.BotRouteSpacing); }
                route.Clear();
                preparing = false;
                retries++;
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
            Replans++;
            runwayChosen = false;
            route.AddRange(Plan(world, state.Position, goal, capabilities, tuning, avoided));
            validationTime = tuning.BotReplanInterval;
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
        // Skip walk-only corners whenever the full capsule can reach further along the route. Never skip a runway.
        for (var i = 1; validationTime <= 0 && i < route.Count && !route[i - 1].Jump && !route[i].Jump; i++)
        {
            if (!Avoids(state.Position, route[i].To, avoided, tuning.BotRouteSpacing * 0.75f) || !world.Walk(state.Position, route[i].To)) { break; }
            route.RemoveRange(0, i);
            i = 0;
        }
        var step = route[0];
        validationTime -= dt;
        if (validationTime <= 0 && !step.Jump)
        {
            validationTime = tuning.BotReplanInterval;
            if (!world.Walk(state.Position, step.To))
            {
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
                if (!world.Walk(step.From, runway))
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
                // Dynamic props/tanks can invalidate a previously safe jump. Check immediately before takeoff.
                if (!world.Jump(state.Position, state.Position + jumpDirection * capabilities.JumpRange))
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
        return new NavigationIntent(direction, toward.Length() > tuning.BotSprintDistance, false, false, false);
    }

    public bool Jumping => flying || route.Count > 0 && route[0].Jump;

    private static bool Avoids(Vector3 from, Vector3 to, IReadOnlyList<Vector3>? avoided, float radius)
    {
        if (avoided == null) { return true; }
        var segment = Flat(to - from);
        foreach (var point in avoided)
        {
            var t = segment.LengthSquared() > 0 ? Mathf.Clamp(Flat(point - from).Dot(segment) / segment.LengthSquared(), 0, 1) : 0;
            if (Flat(point - from - segment * t).Length() < radius) { return false; }
        }
        return true;
    }

    /// <summary>Algorithmic grid resolution and expansion bound are local tuners, not suburb construction.</summary>
    public static IReadOnlyList<NavigationStep> Plan(INavigationWorld world, Vector3 start, Vector3 goal,
        NavigationCapabilities capabilities, Tuning tuning, IReadOnlyList<Vector3>? avoided = null)
    {
        var result = new List<NavigationStep>();
        if (Avoids(start, goal, avoided, tuning.BotRouteSpacing * 0.75f) && world.Walk(start, goal))
        {
            result.Add(new NavigationStep(start, goal, false));
            return result;
        }
        var spacing = tuning.BotRouteSpacing;
        var frontier = new PriorityQueue<Vector2I, float>();
        var costs = new Dictionary<Vector2I, float> { [Vector2I.Zero] = 0 };
        var parents = new Dictionary<Vector2I, (Vector2I Parent, bool Jump)>();
        var closed = new HashSet<Vector2I>();
        var points = new Dictionary<Vector2I, bool>();
        Vector3 Point(Vector2I cell) => start + new Vector3(cell.X * spacing, 0, cell.Y * spacing);
        bool Allowed(Vector2I cell)
        {
            if (points.TryGetValue(cell, out var allowed)) { return allowed; }
            var point = Point(cell);
            allowed = world.Standable(point);
            if (avoided != null)
            {
                foreach (var blocked in avoided)
                {
                    if (Flat(point - blocked).Length() < spacing * 0.75f) { allowed = false; break; }
                }
            }
            points[cell] = allowed;
            return allowed;
        }
        frontier.Enqueue(Vector2I.Zero, 0);
        var jumpCells = (int)(capabilities.JumpRange / spacing);
        var expansions = 0;
        while (frontier.TryDequeue(out var cell, out _) && expansions < tuning.BotSearchBudget)
        {
            if (!closed.Add(cell)) { continue; }
            expansions++;
            var from = Point(cell);
            if (Flat(from - goal).Length() < spacing * 2 && Avoids(from, goal, avoided, spacing * 0.75f) && world.Walk(from, goal))
            {
                result.Add(new NavigationStep(from, goal, false));
                while (parents.TryGetValue(cell, out var parent))
                {
                    result.Add(new NavigationStep(Point(parent.Parent), Point(cell), parent.Jump));
                    cell = parent.Parent;
                }
                result.Reverse();
                return result;
            }
            for (var x = -1; x <= 1; x++)
            {
                for (var z = -1; z <= 1; z++)
                {
                    if (x == 0 && z == 0) { continue; }
                    var offset = new Vector2I(x, z);
                    Explore(cell + offset, false);
                    if (jumpCells >= 2)
                    {
                        var length = x != 0 && z != 0 ? (int)(jumpCells / Mathf.Sqrt(2)) : jumpCells;
                        if (length >= 2) { Explore(cell + offset * length, true); }
                    }
                }
            }
            void Explore(Vector2I next, bool jump)
            {
                if (closed.Contains(next) || !Allowed(next)) { return; }
                var to = Point(next);
                var cost = costs[cell] + from.DistanceTo(to) / capabilities.Speed + (jump ? tuning.BotJumpCost : 0);
                if (costs.TryGetValue(next, out var old) && cost >= old) { return; }
                if (!Avoids(from, to, avoided, spacing * 0.75f) || (jump ? !world.Jump(from, to) : !world.Walk(from, to))) { return; }
                costs[next] = cost;
                parents[next] = (cell, jump);
                frontier.Enqueue(next, cost + Flat(to - goal).Length() / capabilities.Speed);
            }
        }
        return result;
    }
}
