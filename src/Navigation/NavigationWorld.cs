using System;
using System.Collections.Generic;
using Godot;
using Propane.Core;

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

/// <summary>Capsule sweeps test the whole route, including the ballistic arc and a clear, supported landing.</summary>
public sealed class NavigationWorld : INavigationWorld
{
    private readonly PhysicsDirectSpaceState3D space;
    private readonly NavigationCapabilities capabilities;
    private readonly IReadOnlyList<NavigationHazard> hazards;
    private readonly float margin;
    private readonly float minimumJumpRange;
    private readonly Vector3? escapeFrom;

    public NavigationWorld(PhysicsDirectSpaceState3D space, NavigationCapabilities capabilities,
        IReadOnlyList<NavigationHazard> hazards, float margin, Vector3? escapeFrom = null, float? minimumJumpRange = null)
    {
        this.space = space;
        this.capabilities = capabilities;
        this.hazards = hazards;
        this.margin = margin;
        this.minimumJumpRange = minimumJumpRange ?? Tuning.Current.BotJumpMinimumRange;
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
        using var query = new PhysicsShapeQueryParameters3D
        {
            Shape = capabilities.Shape,
            Transform = new Transform3D(Basis.Identity, from + capabilities.ShapeOffset + Vector3.Up * 0.002f),
            Motion = to - from,
            CollisionMask = Layers.World | Layers.Props | Layers.Tanks,
            // The controller already leaves a contact gap. Expanding the walking capsule traps an actor
            // touching a fence or moving tank, because every possible route starts in that expanded overlap.
            Margin = 0,
        };
        if (space.IntersectShape(query, 1).Count > 0) { return false; }
        var motion = space.CastMotion(query);
        return motion.Length == 2 && motion[0] >= 0.999f;
    }

    public bool Standable(Vector3 feet)
    {
        if (!Safe(feet) || !Clear(feet, feet)) { return false; }
        return Supported(feet);
    }

    private bool Supported(Vector3 feet)
    {
        using var ray = PhysicsRayQueryParameters3D.Create(feet + Vector3.Up * 0.2f, feet - Vector3.Up * 0.25f,
            Layers.Floor | Layers.World | Layers.Props | Layers.Tanks);
        var hit = space.IntersectRay(ray);
        return hit.Count > 0 && hit["normal"].AsVector3().Y > 0.9f &&
            Math.Abs(hit["position"].AsVector3().Y - feet.Y) < 0.2f;
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
        if (Walk(from, to)) { return false; }
        var direction = to - from;
        direction.Y = 0;
        var distance = direction.Length();
        // Jump links use the run gait, never creep or sprint. A grid endpoint may be a little before touchdown;
        // test the actual full arc as well, so an unchecked tank or wall just beyond that endpoint is rejected.
        if (distance < capabilities.JumpRange * minimumJumpRange || distance > capabilities.JumpRange) { return false; }
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
