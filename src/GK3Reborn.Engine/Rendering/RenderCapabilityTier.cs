namespace GK3Reborn.Rendering;

/// <summary>
/// Feature tiers selected from queried device capabilities.
/// </summary>
[Flags]
public enum RenderCapabilityTier
{
    /// <summary>Nothing supported. Not a valid runtime state.</summary>
    None = 0,

    /// <summary>Raster, shadow maps, PBR, TAA/FXAA, scalable post. Always required.</summary>
    Compatibility = 1 << 0,

    /// <summary>Clustered lighting, SSR, volumetrics, GPU culling.</summary>
    Enhanced = 1 << 1,

    /// <summary>Acceleration structures and ray tracing pipelines or ray queries.</summary>
    RayTracing = 1 << 2,

    /// <summary>HDR output with a compatible surface color space.</summary>
    HighDynamicRange = 1 << 3,
}
