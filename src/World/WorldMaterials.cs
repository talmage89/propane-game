using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.World;

/// <summary>
/// Converts imported materials to the shared world shader (so everything supports the transition reveal and
/// per-instance tint), with caching so identical materials are shared.
/// </summary>
public static class WorldMaterials
{
    private static readonly Dictionary<(ulong, ulong, bool), ShaderMaterial> Converted = new();
    private static Shader? worldShader;

    public static Shader WorldShader => worldShader ??= GD.Load<Shader>("res://shaders/world_lit.gdshader");

    /// <summary>A flat-colour world material.</summary>
    public static ShaderMaterial Flat(Color color, float roughness = 0.85f, float metallic = 0f)
    {
        var material = new ShaderMaterial { Shader = WorldShader };
        material.SetShaderParameter("albedo_color", color);
        material.SetShaderParameter("roughness", roughness);
        material.SetShaderParameter("metallic", metallic);
        return material;
    }

    /// <summary>The world-shader equivalent of an imported material, optionally with a different albedo texture.</summary>
    public static ShaderMaterial Convert(Material? source, Texture2D? textureOverride = null, bool forceTintable = false)
    {
        var key = (source?.GetInstanceId() ?? 0, textureOverride?.GetInstanceId() ?? 0, forceTintable);
        if (Converted.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var material = new ShaderMaterial { Shader = WorldShader };
        if (source is BaseMaterial3D standard)
        {
            var texture = textureOverride ?? standard.AlbedoTexture;
            material.SetShaderParameter("albedo_color", standard.AlbedoColor);
            material.SetShaderParameter("use_texture", texture != null);
            material.SetShaderParameter("albedo_texture", texture != null ? texture : new Variant());
            material.SetShaderParameter("roughness", Mathf.Clamp(standard.Roughness, 0.35f, 1f));
            material.SetShaderParameter("metallic", standard.Metallic);
            var name = standard.ResourceName ?? "";
            material.SetShaderParameter("tintable", forceTintable || name.StartsWith("Tint_"));
            if (name == "Lamp")
            {
                material.SetShaderParameter("emission_color", new Color(1f, 0.85f, 0.6f) * 0.6f);
            }
        }
        Converted[key] = material;
        return material;
    }

    /// <summary>Converts every surface under a node to world materials and sets its render layer.</summary>
    public static void Apply(Node root, Texture2D? textureOverride = null, bool tintable = false)
    {
        foreach (var mesh in GameAssets.MeshInstances(root))
        {
            mesh.Layers = RenderLayers.Static;
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                mesh.SetSurfaceOverrideMaterial(surface, Convert(mesh.Mesh.SurfaceGetMaterial(surface), textureOverride, tintable));
            }
        }
    }
}
