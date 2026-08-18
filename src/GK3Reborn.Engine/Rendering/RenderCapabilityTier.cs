namespace GK3Reborn.Rendering;

/// <summary>
/// Feature tiers selected from queried device capabilities.
/// </summary>
/// <remarks>
/// Plan/01-architecture.md section 5.1. Tiers are additive and cumulative: a device
/// that supports ray tracing still renders every scene correctly with ray tracing
/// off, and HDR never prevents raster play.
/// </remarks>
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
