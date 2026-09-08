using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>
/// What the tracing and denoising passes bind, declared once for both backends.
/// </summary>
public static class DenoiseLayout
{
    /// <summary>How wide a tracing tile is, in pixels.</summary>
    public const int TileWidth = 8;

    /// <summary>How tall a tracing tile is.</summary>
    public const int TileHeight = 4;

    /// <summary>How many channels are traced and filtered.</summary>
    public const int Channels = 3;

    /// <summary>Where each channel's coverage mask is bound in the tracing pass.</summary>
    public static ReadOnlySpan<uint> MaskBinding => [3, 4, 8];

    /// <summary>Where each channel's traced fraction is bound in the tracing pass.</summary>
    public static ReadOnlySpan<uint> FractionBinding => [6, 7, 9];

    /// <summary>How many bytes of push constants the tracing pass takes.</summary>
    public const uint TraceConstantBytes = 88;

    /// <summary>How many the classify and filter passes take.</summary>
    public const uint StageConstantBytes = 8;

    /// <summary>What the tracing pass binds.</summary>
    public static ShaderLayout Trace { get; } = new(
    [
        new ShaderBinding(0, 0, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 1, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 2, ShaderBindingKind.AccelerationStructure, ShaderStages.Compute),

        // The first two channels' masks, and the light rig between them.
        //
        // The rig is read-only and the masks are not, which Vulkan does not distinguish and
        // Direct3D does: a storage buffer nothing writes becomes a ByteAddressBuffer in t,
        // and one a shader writes becomes an RWByteAddressBuffer in u. Declaring all three
        // writable gives a root signature with u3, u4 and u5 against a shader that wants u3,
        // u4 and t5, which Direct3D refuses by name - "SRV descriptor range (BaseShaderRegister=5)
        // is not fully bound in root signature".
        new ShaderBinding(0, 3, ShaderBindingKind.StorageBuffer, ShaderStages.Compute),
        new ShaderBinding(0, 4, ShaderBindingKind.StorageBuffer, ShaderStages.Compute),
        new ShaderBinding(0, 5, ShaderBindingKind.ReadOnlyStorageBuffer, ShaderStages.Compute),
        new ShaderBinding(0, 6, ShaderBindingKind.StorageImage, ShaderStages.Compute),
        new ShaderBinding(0, 7, ShaderBindingKind.StorageImage, ShaderStages.Compute),

        // The third channel, out of order because the rig took five.
        new ShaderBinding(0, 8, ShaderBindingKind.StorageBuffer, ShaderStages.Compute),
        new ShaderBinding(0, 9, ShaderBindingKind.StorageImage, ShaderStages.Compute),
    ],
    TraceConstantBytes);

    /// <summary>What the classify and filter passes bind.</summary>
    public static ShaderLayout Denoise { get; } = new(
    [
        new ShaderBinding(0, 0, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 1, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 2, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 3, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 4, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 5, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 6, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 7, ShaderBindingKind.Sampler, ShaderStages.Compute),
        new ShaderBinding(0, 8, ShaderBindingKind.StorageBuffer, ShaderStages.Compute),
        new ShaderBinding(0, 9, ShaderBindingKind.StorageBuffer, ShaderStages.Compute),
        new ShaderBinding(0, 10, ShaderBindingKind.StorageImage, ShaderStages.Compute),
        new ShaderBinding(0, 11, ShaderBindingKind.StorageImage, ShaderStages.Compute),
        new ShaderBinding(0, 12, ShaderBindingKind.StorageImage, ShaderStages.Compute),
        new ShaderBinding(0, 13, ShaderBindingKind.StorageImage, ShaderStages.Compute),
        new ShaderBinding(0, 14, ShaderBindingKind.UniformBuffer, ShaderStages.Compute),
        new ShaderBinding(0, 15, ShaderBindingKind.SampledImage, ShaderStages.Compute),
    ],
    StageConstantBytes);
}
