using System;
using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.Assets;

/// <summary>
/// Actor-owned native attachment and tint copies, retired before its skeleton. Borrowed catalog resources are
/// immutable. BoneAttachment follows final skeleton updates (including physical bones); no new clock/callback.
/// </summary>
public sealed class HeadAttachment : IDisposable
{
    private readonly BoneAttachment3D attachment;
    private readonly Transform3D socket;
    private readonly List<Material> tints = new();
    private Node3D? instance;
    private HatCatalog.Item? selected;
    private bool disposed;
    public Node3D? Instance => instance;

    public HeadAttachment(Skeleton3D skeleton)
    {
        socket = DefaultCharacter.HeadSocket(skeleton);
        attachment = new BoneAttachment3D { Name = "HeadAttachment", BoneName = DefaultCharacter.HeadBone, OverridePose = false };
        skeleton.AddChild(attachment);
    }

    public void Apply(HatCatalog.Item? item, Color color, float detailBias)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(item, selected))
        {
            Clear();
            if (item != null)
            {
                var next = item.Scene.Instantiate<Node3D>();
                try
                {
                    // Preserve an imported root transform; authored roots are validated in the source gate.
                    next.Transform = socket * next.Transform;
                    attachment.AddChild(next);
                    instance = next;
                    foreach (var mesh in GameAssets.MeshInstances(next))
                    {
                        mesh.Layers = RenderLayers.Actors;
                        mesh.LodBias *= detailBias;
                        for (var i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
                        {
                            if (mesh.Mesh.SurfaceGetMaterial(i) is BaseMaterial3D original && original.ResourceName == "Tint_Player")
                            {
                                var tint = (BaseMaterial3D)original.Duplicate();
                                tint.AlbedoColor = original.AlbedoColor * color;
                                mesh.SetSurfaceOverrideMaterial(i, tint);
                                tints.Add(tint);
                            }
                        }
                    }
                    selected = item;
                }
                catch
                {
                    if (instance == null) next.Free();
                    Clear();
                    throw;
                }
            }
        }
        else if (instance != null)
        {
            foreach (var mesh in GameAssets.MeshInstances(instance))
            {
                for (var i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
                {
                    if (mesh.GetSurfaceOverrideMaterial(i) is BaseMaterial3D tint && mesh.Mesh.SurfaceGetMaterial(i) is BaseMaterial3D original)
                        tint.AlbedoColor = original.AlbedoColor * color;
                }
            }
        }
    }

    private void Clear()
    {
        instance?.Free();
        instance = null;
        selected = null;
        foreach (var material in tints) material.Dispose();
        tints.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Clear();
        attachment.Free();
    }
}
