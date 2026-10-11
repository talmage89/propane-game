using System;
using System.Collections.Generic;
using Godot;

namespace Propane.Core;

/// <summary>Per-instance reveal values. Shader/material caches remain immutable and safe across worlds.</summary>
public sealed class WorldReveal : IDisposable
{
    private readonly Dictionary<GeometryInstance3D, Action> geometry = new();
    private Vector3 center;
    private float radius = 100000;
    private Color glow = new(0.35f, 0.75f, 1, 1);
    private bool disposed;
    public int Count => geometry.Count;

    public Vector3 Center { get => center; set { if (center == value) { return; } center = value; Update(); } }
    public float Radius { get => radius; set { if (radius == value) { return; } radius = value; Update(); } }
    public Color Glow { get => glow; set { if (glow == value) { return; } glow = value; Update(); } }

    public void Track(Node root)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (root is GeometryInstance3D self && root is not GpuParticles3D) { TrackGeometry(self); }
        foreach (var node in root.GetChildren()) { Track(node); }
    }

    private void TrackGeometry(GeometryInstance3D mesh)
    {
        if (geometry.ContainsKey(mesh)) { return; }
        void Remove()
        {
            geometry.Remove(mesh);
            mesh.TreeExiting -= Remove;
        }
        geometry.Add(mesh, Remove);
        mesh.TreeExiting += Remove;
        Apply(mesh);
    }

    private void Apply(GeometryInstance3D mesh)
    {
        mesh.SetInstanceShaderParameter("reveal_center", center);
        mesh.SetInstanceShaderParameter("reveal_radius", radius);
        mesh.SetInstanceShaderParameter("reveal_glow", glow);
    }

    private void Update()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var mesh in geometry.Keys) { Apply(mesh); }
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        foreach (var (mesh, exit) in geometry)
        {
            if (GodotObject.IsInstanceValid(mesh)) { mesh.TreeExiting -= exit; }
        }
        geometry.Clear();
    }
}
