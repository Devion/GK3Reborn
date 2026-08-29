using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>
/// What the mesh pipeline binds, declared once for both backends.
/// </summary>
/// <remarks>
/// <para>
/// One statement, two spellings. Vulkan builds descriptor set layouts from it and Direct3D
/// builds a root signature; neither is derived from the other, and the shaders that read
/// them are the same shaders. The correspondence is <see cref="ShaderBindings"/> — a set
/// becomes a register space and a binding becomes a register index.
/// </para>
/// <para>
/// Two sets, split by how often they change. Set 0 holds the camera, the light rig and —
/// where the device can trace — the acceleration structure, and is bound once a frame. Set 1
/// holds a batch's five textures and never changes at all once a room is built. What is left,
/// the model transform and the shading mode, travels as push constants, which need no buffer,
/// no descriptor and no synchronisation between frames in flight.
/// </para>
/// </remarks>
public static class MeshLayout
{
    /// <summary>Set 0: what a frame is, bound once for all of it.</summary>
    public const uint FrameSet = 0;

    /// <summary>Set 1: what a batch is drawn with.</summary>
    public const uint MaterialSet = 1;

    /// <summary>How many textures a material binds.</summary>
    /// <remarks>
    /// Colour, lightmap, normal, occlusion-roughness-metalness, height. The stand-ins are
    /// bound where a surface has none of a thing, because both APIs require every declared
    /// binding to point at something valid even when the shader ignores what it reads.
    /// </remarks>
    public const int TexturesPerMaterial = 5;

    /// <summary>What the pipeline binds without ray tracing.</summary>
    public static ShaderLayout Raster { get; } = Build(rayTracing: false);

    /// <summary>What it binds with ray tracing compiled in.</summary>
    public static ShaderLayout Traced { get; } = Build(rayTracing: true);

    /// <summary>What the pipeline binds.</summary>
    /// <param name="rayTracing">Whether the ray-tracing paths are compiled in.</param>
    /// <returns>The layout.</returns>
    public static ShaderLayout For(bool rayTracing) => rayTracing ? Traced : Raster;

    private static ShaderLayout Build(bool rayTracing)
    {
        List<ShaderBinding> bindings =
        [
            // The camera and the frame's own numbers, read by both stages: the vertex stage
            // for the projection, the fragment stage for everything else.
            new(FrameSet, 0, ShaderBindingKind.UniformBuffer, ShaderStages.Raster),

            // The rig, and the grid that says which of it reaches where. Storage buffers
            // rather than uniform ones: a uniform block has to be sized at compile time and
            // the standard only guarantees sixteen kilobytes of it, which is what put a
            // limit of sixty-four lights on a scene. A storage buffer is unsized on both
            // sides and the loop is bounded by the cell rather than by the array. See
            // SceneLightGrid.
            new(FrameSet, 1, ShaderBindingKind.ReadOnlyStorageBuffer, ShaderStages.Fragment),
            new(FrameSet, 2, ShaderBindingKind.ReadOnlyStorageBuffer, ShaderStages.Fragment),
            new(FrameSet, 3, ShaderBindingKind.ReadOnlyStorageBuffer, ShaderStages.Fragment),
        ];

        // Last, and left out entirely on a device that cannot trace. Declaring a binding
        // nothing can fill is not harmless: Vulkan requires every statically used binding to
        // point at something valid whether its branch runs or not, which is why there are two
        // shader variants rather than one that branches.
        if (rayTracing)
        {
            bindings.Add(
                new ShaderBinding(FrameSet, 4, ShaderBindingKind.AccelerationStructure, ShaderStages.Fragment));
        }

        for (uint i = 0; i < TexturesPerMaterial; i++)
        {
            bindings.Add(
                new ShaderBinding(MaterialSet, i, ShaderBindingKind.CombinedImageSampler, ShaderStages.Fragment));
        }

        // A hundred and ninety-two bytes; see DrawConstants, which explains why that is past
        // what Vulkan guarantees and why it is nevertheless what this uses.
        return new ShaderLayout(bindings, PushConstantBytes: 192);
    }
}
