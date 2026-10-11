using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Propane.World.Plan;

public enum PileFormation : byte
{
    Rows,
    Square,
    Triangular,
    Cluster,
}

/// <summary>Local tank transforms, bottom first. No scene, random state, catalog or tuning dependency.</summary>
public static class PileBuilder
{
    // Dimensions of the authored vessel: foot origin, 31.2 cm diameter, 46.8 cm overall height.
    public const float Radius = 0.156f;
    public const float Height = 0.468f;
    public static readonly Vector3 LocalCenter = new(0, 0.235f, 0);

    /// <summary>
    /// Spacing is (across, layer rise, depth), in meters. Rows lie on their sides in a barrel pyramid;
    /// square and triangular tiers also lie on their sides, cradled between two tanks below. Incomplete base tiers
    /// with enough tanks reserve one above its two supports, so a pyramid does not become a flat mat. Cluster uses the across pitch
    /// for a compact upright hexagonal lattice.
    /// </summary>
    public static IReadOnlyList<Transform3D> Build(PileFormation formation, int count, Vector3 spacing)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (spacing.X < Radius * 2 || spacing.Y <= 0 || spacing.Z < Height || !spacing.IsFinite())
            throw new ArgumentOutOfRangeException(nameof(spacing), "Across spacing must fit the diameter, depth must fit the height, and layer rise must be positive.");
        if (!Enum.IsDefined(formation)) throw new ArgumentOutOfRangeException(nameof(formation));
        var result = new List<Transform3D>(count);
        if (count == 0) return result;
        void Add(Vector3 p, Basis basis) { if (result.Count < count) result.Add(new Transform3D(basis, p)); }
        if (formation == PileFormation.Cluster)
        {
            var side = (int)Math.Ceiling(Math.Sqrt(count)) + 1;
            var points = new List<Vector3>();
            for (var row = -side; row <= side; row++)
            {
                for (var col = -side; col <= side; col++)
                    points.Add(new Vector3((col + (row & 1) * 0.5f) * spacing.X, 0, row * spacing.X * Mathf.Sqrt(3f) / 2f));
            }

            foreach (var p in points.OrderBy(p => p.LengthSquared()).ThenBy(p => p.Z).ThenBy(p => p.X).Take(count)) Add(p, Basis.Identity);
        }
        else
        {
            long Capacity(long width) => formation switch
            {
                PileFormation.Rows => width * (width + 1) / 2,
                PileFormation.Square => width * (width + 1) * (2 * width + 1) / 6,
                _ => width * (width + 1) * (width + 2) / 6,
            };
            var width = 1;
            while (Capacity(width) < count) width++;
            // Five tanks can form square tiers (four plus one), four triangular tiers (three plus one).
            // When the next wider base would consume the whole count, leave one tank for an upper tier.
            var reserveUpper = formation == PileFormation.Square && count >= 5 && count <= width * width ||
                formation == PileFormation.Triangular && count >= 4 && count <= width * (width + 1) / 2;
            for (var layer = 0; layer < width && result.Count < count; layer++)
            {
                var n = width - layer;
                var points = new List<Vector3>();
                for (var row = 0; row < (formation == PileFormation.Rows ? 1 : n); row++)
                {
                    for (var col = 0; col < (formation == PileFormation.Triangular ? n - row : n); col++)
                    {
                        var x = (col - (n - 1) / 2f + (formation == PileFormation.Triangular ? row * 0.5f : 0)) * spacing.X;
                        var z = formation == PileFormation.Rows ? 0 : formation == PileFormation.Triangular
                            ? row * spacing.Z : (row + layer / 2) * spacing.Z;
                        points.Add(new Vector3(x, layer * spacing.Y, z));
                    }
                }

                foreach (var p in points.OrderBy(p => p.X * p.X + p.Z * p.Z).ThenBy(p => p.Z).ThenBy(p => p.X))
                {
                    if (layer == 0 && reserveUpper && result.Count == count - 1) break;
                    if (layer == 1 && reserveUpper)
                    {
                        var left = new Vector3(p.X - spacing.X / 2, Radius, p.Z - LocalCenter.Y);
                        var right = new Vector3(p.X + spacing.X / 2, Radius, p.Z - LocalCenter.Y);
                        if (!result.Any(t => t.Origin.IsEqualApprox(left)) || !result.Any(t => t.Origin.IsEqualApprox(right))) continue;
                    }
                    Add(p + new Vector3(0, Radius, -LocalCenter.Y), new Basis(Vector3.Right, Mathf.Pi / 2));
                }
            }
        }
        // Center the finite footprint without changing any relative support positions.
        var centers = result.Select(t => t * LocalCenter).ToArray();
        var center = new Vector3((centers.Min(p => p.X) + centers.Max(p => p.X)) / 2, 0,
            (centers.Min(p => p.Z) + centers.Max(p => p.Z)) / 2);
        for (var i = 0; i < result.Count; i++) result[i] = new Transform3D(result[i].Basis, result[i].Origin - center);
        return result;
    }
}
