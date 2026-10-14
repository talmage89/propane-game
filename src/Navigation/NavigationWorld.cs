using System;
using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Fx;

namespace Propane.Navigation;

/// <summary>Feet positions, a collision shape and movement physics supplied by the actor, independent of Match.</summary>
public readonly record struct NavigationCapabilities(Shape3D Shape, Vector3 ShapeOffset, float Radius, float Speed,
    float SprintSpeed, float JumpVelocity, float Gravity, float Acceleration, float AirControl)
{
    public float FlightTime => 2 * JumpVelocity / Gravity;
    public float JumpRange => Speed * FlightTime;
    public float JumpHeight => JumpVelocity * JumpVelocity / (2 * Gravity);
}

public readonly record struct NavigationHazard(Vector3 Center, float Radius);

public interface INavigationWorld
{
    bool Standable(Vector3 feet);
    bool Walk(Vector3 from, Vector3 to);
    bool Jump(Vector3 from, Vector3 to);
}

/// <summary>
/// Query objects one caller reuses across its navigation worlds, instead of creating and freeing a native object per
/// sweep or ray. Every query sets all the fields it uses, so results equal fresh queries.
/// </summary>
public sealed class NavigationQueries : IDisposable
{
    internal readonly PhysicsShapeQueryParameters3D Shape = new();
    internal readonly PhysicsRayQueryParameters3D Ray = new();
    public void Dispose()
    {
        Shape.Dispose();
        Ray.Dispose();
    }
}

/// <summary>
/// Capsule sweeps test the whole route, including the ballistic arc and a clear, supported landing. A world answers for
/// one physics tick: callers build a new one each tick, so its remembered answers never outlive the physics they saw.
/// </summary>
public sealed class NavigationWorld : INavigationWorld
{
    private readonly PhysicsDirectSpaceState3D space;
    private readonly NavigationCapabilities capabilities;
    private readonly IReadOnlyList<NavigationHazard> hazards;
    private readonly float margin;
    private readonly float minimumJumpRange;
    private readonly Vector3? escapeFrom;
    private readonly NavigationQueries? queries;

    public NavigationWorld(PhysicsDirectSpaceState3D space, NavigationCapabilities capabilities,
        IReadOnlyList<NavigationHazard> hazards, float margin, float minimumJumpRange, Vector3? escapeFrom = null,
        NavigationQueries? queries = null)
    {
        this.queries = queries;
        this.space = space;
        this.capabilities = capabilities;
        this.hazards = hazards;
        this.margin = margin;
        this.minimumJumpRange = minimumJumpRange;
        this.escapeFrom = escapeFrom;
    }

    private bool Safe(Vector3 point, bool allowEscape = true)
    {
        foreach (var hazard in hazards)
        {
            var d = point - hazard.Center;
            d.Y = 0;
            if (d.LengthSquared() < hazard.Radius * hazard.Radius)
            {
                // A bot already inside danger must be able to run out, but can never jump through it.
                if (!allowEscape || escapeFrom == null || d.LengthSquared() + 0.001f <
                    new Vector2(escapeFrom.Value.X - hazard.Center.X, escapeFrom.Value.Z - hazard.Center.Z).LengthSquared()) { return false; }
            }
        }
        return true;
    }

    private bool Clear(Vector3 from, Vector3 to)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.NavigationSweep);
        var query = queries?.Shape ?? new PhysicsShapeQueryParameters3D();
        try
        {
            query.Shape = capabilities.Shape;
            query.Transform = new Transform3D(Basis.Identity, from + capabilities.ShapeOffset + Vector3.Up * 0.002f);
            query.Motion = to - from;
            query.CollisionMask = Layers.World | Layers.Props | Layers.Tanks;
            // The controller already leaves a contact gap. Expanding the walking capsule traps an actor
            // touching a fence or moving tank, because every possible route starts in that expanded overlap.
            query.Margin = 0;
            if (space.IntersectShape(query, 1).Count > 0) { return false; }
            var motion = space.CastMotion(query);
            return motion.Length == 2 && motion[0] >= 0.999f;
        }
        finally
        {
            if (queries == null) query.Dispose();
        }
    }

    // A world lives for one tick, while physics stands still, so a point's answer cannot change. Searches ask again for
    // the same cells (a cell check, then its walk and jump links, then a walk's end samples), so remember each answer.
    private Dictionary<Vector3, bool>? standable;

    public bool Standable(Vector3 feet)
    {
        standable ??= new();
        if (standable.TryGetValue(feet, out var known)) { return known; }
        var result = Safe(feet) && Clear(feet, feet) && Supported(feet);
        standable[feet] = result;
        return result;
    }

    private Dictionary<Vector3, bool>? supported;

    private bool Supported(Vector3 feet)
    {
        supported ??= new();
        if (supported.TryGetValue(feet, out var known)) { return known; }
        return supported[feet] = Support(feet);
    }

    private bool Support(Vector3 feet)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.NavigationSupport);
        var ray = queries?.Ray ?? new PhysicsRayQueryParameters3D();
        try
        {
            ray.From = feet + Vector3.Up * 0.2f;
            ray.To = feet - Vector3.Up * 0.25f;
            ray.CollisionMask = Layers.Floor | Layers.World | Layers.Props | Layers.Tanks;
            var hit = space.IntersectRay(ray);
            return hit.Count > 0 && hit["normal"].AsVector3().Y > 0.9f &&
                Math.Abs(hit["position"].AsVector3().Y - feet.Y) < 0.2f;
        }
        finally
        {
            if (queries == null) ray.Dispose();
        }
    }

    public bool Walk(Vector3 from, Vector3 to)
    {
        if (!Standable(to) || !Clear(from, to)) { return false; }
        var samples = Math.Max(1, (int)Math.Ceiling(from.DistanceTo(to) / capabilities.Radius));
        for (var i = 0; i <= samples; i++)
        {
            if (!Safe(from.Lerp(to, (float)i / samples)) || !Supported(from.Lerp(to, (float)i / samples))) { return false; }
        }
        return true;
    }

    public bool Jump(Vector3 from, Vector3 to)
    {
        if (capabilities.JumpVelocity <= 0 || capabilities.Gravity <= 0 || capabilities.Speed <= 0 ||
            !Safe(from, false) || !Safe(to, false) || !Standable(from) || !Standable(to)) { return false; }
        var direction = to - from;
        direction.Y = 0;
        var distance = direction.Length();
        // Jump links use the run gait, never creep or sprint. A grid endpoint may be a little before touchdown;
        // test the actual full arc as well, so an unchecked tank or wall just beyond that endpoint is rejected.
        // The range costs nothing, so it goes before the walk test's sweeps; either refusal gives the same answer.
        if (distance < capabilities.JumpRange * minimumJumpRange || distance > capabilities.JumpRange) { return false; }
        if (Walk(from, to)) { return false; }
        direction = direction.Normalized();
        var landing = from + direction * capabilities.JumpRange;
        if (!Safe(landing, false) || !Standable(landing)) { return false; }
        var previous = from;
        var steps = (int)Math.Ceiling(capabilities.FlightTime * 60);
        for (var i = 1; i <= steps; i++)
        {
            var t = capabilities.FlightTime * i / steps;
            var point = from + direction * (capabilities.Speed * t) +
                Vector3.Up * Mathf.Max(0, capabilities.JumpVelocity * t - capabilities.Gravity * t * t / 2 - margin);
            if (!Safe(point, false) || !Clear(previous, point)) { return false; }
            previous = point;
        }
        return true;
    }
}
