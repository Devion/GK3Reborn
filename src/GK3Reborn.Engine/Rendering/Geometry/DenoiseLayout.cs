using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>
/// What the tracing and denoising passes bind, declared once for both backends.
/// </summary>
/// <remarks>
/// <para>
/// Three compute shaders and two layouts. The tracing pass casts the rays and writes a mask
/// and a coverage fraction for each of three channels; the classify and filter passes share
/// one layout because they are the same shader family walking the same set of surfaces, and
/// what differs between them is a push constant rather than a binding.
/// </para>
/// <para>
/// The channels are the shadow, the ambient occlusion, and the shadow cast by things
/// standing in the room as distinct from the room itself. Three of everything, and the
/// tracing pass reaches all three at once — which is why its mask and fraction bindings are
/// out of order: the rig took bindings three to five, so the third channel's pair had to go
/// on the end.
/// </para>
/// </remarks>
public static class DenoiseLayout
{
    /// <summary>How wide a tracing tile is, in pixels.</summary>
    public const int TileWidth = 8;

    /// <summary>How tall a tracing tile is.</summary>
    public const int TileHeight = 4;

    /// <summary>How many channels are traced and filtered.</summary>
    /// <remarks>
    /// The shadow, the ambient occlusion, and the shadow of what is standing in the room.
    /// The third is kept apart because a shadow ray leaving a character has to skip
    /// characters — GK3's people are a stack of overlapping shells, and a ray leaving the
    /// shirt hits the arm inside it.
    /// </remarks>
    public const int Channels = 3;

    /// <summary>Where each channel's coverage mask is bound in the tracing pass.</summary>
    /// <remarks>
    /// Not consecutive, and not an oversight: the light rig took bindings three to five, so
    /// the third channel's pair went on the end rather than renumbering a shader that works.
    /// </remarks>
    public static ReadOnlySpan<uint> MaskBinding => [3, 4, 8];

    /// <summary>Where each channel's traced fraction is bound in the tracing pass.</summary>
    public static ReadOnlySpan<uint> FractionBinding => [6, 7, 9];

    /// <summary>How many bytes of push constants the tracing pass takes.</summary>
    public const uint TraceConstantBytes = 88;

    /// <summary>How many the classify and filter passes take.</summary>
    /// <remarks>Two integers: which step of the blur, and how far apart its taps are.</remarks>
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
    /// <remarks>
    /// One layout for two shaders. They read the same seven textures, the same two buffers
    /// and the same uniform block, and write the same four; which of the blur's steps is
    /// running is a push constant. Two layouts would be two things to keep in step for no
    /// difference either shader can see.
    /// </remarks>
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
