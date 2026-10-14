using System;
using Godot;
using Propane.Core;

namespace Propane.Navigation;

/// <summary>
/// The actor's upright capsule (feet-based: its center is half its height above the feet) and movement physics,
/// independent of Match.
/// </summary>
public readonly record struct NavigationCapabilities(float Radius, float Height, float Speed, float SprintSpeed,
    float JumpVelocity, float Gravity, float Acceleration, float AirControl)
{
    public float FlightTime => 2 * JumpVelocity / Gravity;
    public float JumpRange => Speed * FlightTime;
    public float JumpHeight => JumpVelocity * JumpVelocity / (2 * Gravity);
}

public readonly record struct NavigationHazard(Vector3 Center, float Radius);

/// <summary>
/// What the static geometry allows the actor's body, without hazards: the questions the shared lattice
/// (<see cref="NavigationGrid"/>) caches. Every answer is about feet positions.
/// </summary>
public interface INavigationWorld
{
    /// <summary>The body fits standing here: it overlaps nothing.</summary>
    bool Fits(Vector3 feet);
    /// <summary>Level ground lies just under these feet.</summary>
    bool Supported(Vector3 feet);
    /// <summary>The body moves from one place to the other without touching anything, start and end included.</summary>
    bool Sweep(Vector3 from, Vector3 to);
    /// <summary>
    /// The body's ballistic jump from these feet along a horizontal unit direction at the run gait touches nothing
    /// before it lands, one <see cref="NavigationCapabilities.JumpRange"/> away. Whether it can stand there is the
    /// caller's question.
    /// </summary>
    bool Flight(Vector3 from, Vector3 direction);
    /// <summary>Physics queries made so far, for budgets and measurements.</summary>
    long Queries { get; }
    /// <summary>Upper bound on the queries in one complete flight check, for atomic budget reservation.</summary>
    int FlightQueries { get; }
}

/// <summary>
/// The physics answers, from the world's live collision shapes: houses, curbs, trees, fences, cars and loose props.
/// Tanks are left out: they move and burn, so navigation treats them as hazards in each search instead. The queries
/// run through one shape cast and one ray cast node, reused for every query, so asking allocates nothing.
/// </summary>
public sealed class NavigationWorld : INavigationWorld, IDisposable
{
    /// <summary>What blocks the body: everything the player's capsule collides with except tanks.</summary>
    public const uint Obstacles = Layers.World | Layers.Props;
    /// <summary>What a foot can stand on.</summary>
    public const uint Ground = Layers.Floor | Layers.World | Layers.Props;
    // A small numerical lift keeps a query off the slab that supports it.
    private const float Lift = 0.002f;

    private readonly NavigationCapabilities capabilities;
    private readonly float margin;
    private readonly ShapeCast3D sweep;
    private readonly RayCast3D ray;
    private readonly Vector3 center;
    private readonly int flightChords;

    public long Queries { get; private set; }
    public int FlightQueries => flightChords;

    /// <summary>
    /// The probes join <paramref name="root"/>, which must be in the world's tree; they leave with it or on
    /// <see cref="Dispose"/>. <paramref name="jumpMargin"/> lowers the tested arc, a conservative allowance.
    /// </summary>
    public NavigationWorld(Node root, NavigationCapabilities capabilities, float jumpMargin)
    {
        this.capabilities = capabilities;
        margin = jumpMargin;
        center = Vector3.Up * (capabilities.Height / 2 + Lift);
        // Chords under the arc: each lies at most g·dt²/8 below it (under 2 cm at the baked jump), the safe side.
        flightChords = Math.Max(4, (int)Math.Ceiling(capabilities.FlightTime * 12));
        // The controller already leaves a contact gap. Expanding the capsule would trap an actor touching a fence or a
        // moving prop, because every route would start inside that expanded overlap; so no margin.
        sweep = new ShapeCast3D
        {
            Name = "NavigationSweep",
            TopLevel = true,
            Enabled = false,
            Shape = new CapsuleShape3D { Radius = capabilities.Radius, Height = capabilities.Height },
            CollisionMask = Obstacles,
            Margin = 0,
            MaxResults = 1,
        };
        ray = new RayCast3D { Name = "NavigationRay", TopLevel = true, Enabled = false, CollisionMask = Ground };
        root.AddChild(sweep);
        root.AddChild(ray);
    }

    public bool Fits(Vector3 feet) => Cast(feet, Vector3.Zero);

    public bool Sweep(Vector3 from, Vector3 to) => Cast(from, to - from);

    private bool Cast(Vector3 feet, Vector3 motion)
    {
        Queries++;
        sweep.Position = feet + center;
        sweep.TargetPosition = motion;
        sweep.ForceShapecastUpdate();
        return !sweep.IsColliding();
    }

    public bool Supported(Vector3 feet)
    {
        Queries++;
        ray.Position = feet + Vector3.Up * 0.2f;
        ray.TargetPosition = Vector3.Down * 0.45f;
        ray.ForceRaycastUpdate();
        return ray.IsColliding() && ray.GetCollisionNormal().Y > 0.9f && Math.Abs(ray.GetCollisionPoint().Y - feet.Y) < 0.2f;
    }

    public bool Flight(Vector3 from, Vector3 direction)
    {
        if (capabilities.JumpVelocity <= 0 || capabilities.Gravity <= 0 || capabilities.Speed <= 0) return false;
        var previous = from;
        for (var i = 1; i <= flightChords; i++)
        {
            var t = capabilities.FlightTime * i / flightChords;
            var point = from + direction * (capabilities.Speed * t) +
                Vector3.Up * Mathf.Max(0, capabilities.JumpVelocity * t - capabilities.Gravity * t * t / 2 - margin);
            if (!Sweep(previous, point)) return false;
            previous = point;
        }
        return true;
    }

    public void Dispose()
    {
        if (GodotObject.IsInstanceValid(sweep)) sweep.QueueFree();
        if (GodotObject.IsInstanceValid(ray)) ray.QueueFree();
    }
}
