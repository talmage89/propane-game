using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.Dev;

/// <summary>SAT checks against the authored convex collision mesh, including edge/edge separating axes.</summary>
internal sealed class TankGeometry
{
    private readonly Vector3[] points;
    private readonly List<Vector3> normals = new();
    private readonly List<Vector3> edges = new();
    private readonly List<(Vector3 Axis, float Width)> translationAxes = new();

    public TankGeometry()
    {
        var root = GameAssets.Instantiate("res://assets/tank/tank.glb");
        var mesh = GameAssets.MeshInstances(root).Single(m => m.Name == "TankCollision").Mesh;
        var faces = mesh.GetFaces();
        points = faces.Distinct().ToArray();
        for (var i = 0; i < faces.Length; i += 3)
        {
            var a = faces[i + 1] - faces[i];
            var b = faces[i + 2] - faces[i];
            AddAxis(normals, a.Cross(b));
            AddAxis(edges, a);
            AddAxis(edges, b);
            AddAxis(edges, faces[i + 2] - faces[i + 1]);
        }
        root.Free();
        var axes = new List<Vector3>(normals);
        foreach (var a in edges)
            foreach (var b in edges) AddAxis(axes, a.Cross(b));
        foreach (var axis in axes)
            translationAxes.Add((axis, points.Max(p => p.Dot(axis)) - points.Min(p => p.Dot(axis))));
    }

    private static void AddAxis(List<Vector3> axes, Vector3 axis)
    {
        if (axis.LengthSquared() < 1e-10f) return;
        axis = axis.Normalized();
        if (axes.All(a => Mathf.Abs(a.Dot(axis)) < 0.99999f)) axes.Add(axis);
    }

    public bool Overlaps(Transform3D a, Transform3D b, float tolerance = 0.001f)
    {
        var relative = a.AffineInverse() * b;
        if ((relative.Basis.X - Vector3.Right).LengthSquared() < 1e-8f &&
            (relative.Basis.Y - Vector3.Up).LengthSquared() < 1e-8f &&
            (relative.Basis.Z - Vector3.Back).LengthSquared() < 1e-8f)
            return translationAxes.All(axis => Mathf.Abs(relative.Origin.Dot(axis.Axis)) < axis.Width - tolerance);
        var pa = points.Select(p => a * p).ToArray();
        var pb = points.Select(p => b * p).ToArray();
        bool Separated(Vector3 axis)
        {
            if (axis.LengthSquared() < 1e-10f) return false;
            axis = axis.Normalized();
            var minA = float.MaxValue; var maxA = float.MinValue;
            var minB = float.MaxValue; var maxB = float.MinValue;
            foreach (var p in pa) { var d = p.Dot(axis); minA = Mathf.Min(minA, d); maxA = Mathf.Max(maxA, d); }
            foreach (var p in pb) { var d = p.Dot(axis); minB = Mathf.Min(minB, d); maxB = Mathf.Max(maxB, d); }
            return maxA <= minB + tolerance || maxB <= minA + tolerance;
        }
        foreach (var axis in normals) if (Separated(a.Basis * axis) || Separated(b.Basis * axis)) return false;
        foreach (var ea in edges)
            foreach (var eb in edges)
                if (Separated((a.Basis * ea).Cross(b.Basis * eb))) return false;
        return true;
    }

    public float Bottom(Transform3D transform) => points.Min(p => (transform * p).Y);
}
