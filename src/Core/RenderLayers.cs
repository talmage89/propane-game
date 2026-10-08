namespace Propane.Core;

/// <summary>Visual layers, used to keep decals off actors.</summary>
public static class RenderLayers
{
    public const uint Static = 1 << 0;
    public const uint Actors = 1 << 1;
}
