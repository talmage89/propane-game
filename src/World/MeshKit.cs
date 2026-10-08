using Godot;

namespace Propane.World;

/// <summary>Small helpers for building flat ground geometry with SurfaceTool. Map (x, y) is world (X, Z).</summary>
public sealed class MeshKit
{
    private readonly SurfaceTool tool = new();
    private bool empty = true;

    public MeshKit()
    {
        tool.Begin(Mesh.PrimitiveType.Triangles);
    }

    /// <summary>An upward-facing quad from four map-space corners (any winding) at height y.</summary>
    public void Quad(Vector2[] corners, float y)
    {
        var a = new Vector3(corners[0].X, y, corners[0].Y);
        var b = new Vector3(corners[1].X, y, corners[1].Y);
        var c = new Vector3(corners[2].X, y, corners[2].Y);
        var d = new Vector3(corners[3].X, y, corners[3].Y);
        // Make sure the face points up.
        if ((b - a).Cross(c - a).Y > 0)
        {
            (b, d) = (d, b);
        }
        Triangle(a, b, c, Vector3.Up);
        Triangle(a, c, d, Vector3.Up);
    }

    /// <summary>A strip along a line in map space, of the given width, at height y.</summary>
    public void Strip(Vector2 from, Vector2 to, float width, float y)
    {
        var dir = (to - from).Normalized();
        var side = new Vector2(-dir.Y, dir.X) * width * 0.5f;
        Quad(new[] { from - side, to - side, to + side, from + side }, y);
    }

    /// <summary>A box along a line (top and both long sides and ends), for curbs.</summary>
    public void Bar(Vector2 from, Vector2 to, float width, float bottom, float top)
    {
        var dir = (to - from).Normalized();
        var side = new Vector2(-dir.Y, dir.X) * width * 0.5f;
        Vector3 V(Vector2 p, float y) => new(p.X, y, p.Y);
        Quad(new[] { from - side, to - side, to + side, from + side }, top);
        Wall(V(from + side, bottom), V(to + side, bottom), top - bottom);
        Wall(V(to - side, bottom), V(from - side, bottom), top - bottom);
        Wall(V(from - side, bottom), V(from + side, bottom), top - bottom);
        Wall(V(to + side, bottom), V(to - side, bottom), top - bottom);
    }

    /// <summary>A vertical wall from a to b (both at the bottom), facing to the right of a-&gt;b... either way.</summary>
    public void Wall(Vector3 a, Vector3 b, float height, Vector3? outward = null)
    {
        var up = Vector3.Up * height;
        var normal = outward ?? (b - a).Cross(Vector3.Up).Normalized();
        Triangle(a, b, b + up, normal);
        Triangle(a, b + up, a + up, normal);
    }

    /// <summary>A flat ring sector (or disc when inner is 0) in map space.</summary>
    public void Ring(Vector2 center, float inner, float outer, float y, float startAngle, float endAngle, int segments)
    {
        for (var i = 0; i < segments; i++)
        {
            var a0 = Mathf.Lerp(startAngle, endAngle, i / (float)segments);
            var a1 = Mathf.Lerp(startAngle, endAngle, (i + 1) / (float)segments);
            var d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
            var d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
            if (inner <= 0.001f)
            {
                var c = new Vector3(center.X, y, center.Y);
                var p0 = new Vector3(center.X + d0.X * outer, y, center.Y + d0.Y * outer);
                var p1 = new Vector3(center.X + d1.X * outer, y, center.Y + d1.Y * outer);
                if ((p0 - c).Cross(p1 - c).Y > 0)
                {
                    (p0, p1) = (p1, p0);
                }
                Triangle(c, p0, p1, Vector3.Up);
            }
            else
            {
                Quad(new[] { center + d0 * inner, center + d0 * outer, center + d1 * outer, center + d1 * inner }, y);
            }
        }
    }

    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        // Godot's front faces are clockwise as seen from the front.
        if ((b - a).Cross(c - a).Dot(normal) > 0)
        {
            (b, c) = (c, b);
        }
        foreach (var v in new[] { a, b, c })
        {
            tool.SetNormal(normal);
            tool.SetUV(new Vector2(v.X, v.Z));
            tool.AddVertex(v);
        }
        empty = false;
    }

    public ArrayMesh? Commit(Material material)
    {
        if (empty)
        {
            return null;
        }
        tool.SetMaterial(material);
        var mesh = tool.Commit();
        return mesh;
    }
}
