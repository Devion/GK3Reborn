namespace GK3Reborn.Rendering.Shaders;

/// <summary>Which stages a binding is visible to.</summary>
[Flags]
public enum ShaderStages
{
    /// <summary>None, which is not a useful binding.</summary>
    None = 0,

    /// <summary>The vertex stage.</summary>
    Vertex = 1 << 0,

    /// <summary>The fragment stage. Called the pixel stage by Direct3D.</summary>
    Fragment = 1 << 1,

    /// <summary>The compute stage.</summary>
    Compute = 1 << 2,

    /// <summary>Both raster stages.</summary>
    Raster = Vertex | Fragment,

    /// <summary>Every stage there is.</summary>
    All = Vertex | Fragment | Compute,
}

/// <summary>What kind of thing a binding names.</summary>
/// <remarks>
/// The Vulkan descriptor types the renderer actually uses, no more. Each maps to exactly
/// one Direct3D register class, which is what makes a root signature derivable from a
/// layout rather than from reflection — see <see cref="ShaderBindings"/>.
/// </remarks>
public enum ShaderBindingKind
{
    /// <summary>A uniform buffer. A constant buffer to Direct3D, in <c>b</c>.</summary>
    UniformBuffer,

    /// <summary>A storage buffer nothing writes. A <c>ByteAddressBuffer</c>, in <c>t</c>.</summary>
    ReadOnlyStorageBuffer,

    /// <summary>A storage buffer a shader writes. An <c>RWByteAddressBuffer</c>, in <c>u</c>.</summary>
    StorageBuffer,

    /// <summary>
    /// A texture with a sampler attached, which Direct3D has no such thing as.
    /// </summary>
    /// <remarks>
    /// GLSL's <c>sampler2D</c> is one object; HLSL has a <c>Texture2D</c> and a
    /// <c>SamplerState</c> and no way to join them. SPIRV-Cross splits one into the other
    /// two and gives them the same register index in different classes, so a combined
    /// binding at <c>set 0, binding 2</c> becomes <c>t2, space0</c> and <c>s2, space0</c>.
    /// One binding here, two descriptors there; the root signature builder knows.
    /// </remarks>
    CombinedImageSampler,

    /// <summary>A texture read without a sampler of its own, in <c>t</c>.</summary>
    SampledImage,

    /// <summary>A texture a shader writes, in <c>u</c>.</summary>
    StorageImage,

    /// <summary>A sampler on its own, in <c>s</c>.</summary>
    Sampler,

    /// <summary>An acceleration structure, which Direct3D puts in <c>t</c>.</summary>
    /// <remarks>
    /// Not a resource type of its own in a root signature: it is a shader resource view
    /// whose dimension says it is an acceleration structure, and it can also be a raw
    /// address in a root descriptor. The table form is used, so that it binds like
    /// everything else.
    /// </remarks>
    AccelerationStructure,
}

/// <summary>One thing a shader reads or writes, and where it is.</summary>
/// <param name="Set">The descriptor set. A register space in Direct3D.</param>
/// <param name="Binding">The binding within that set. A register index in Direct3D.</param>
/// <param name="Kind">What it is, which decides the register class.</param>
/// <param name="Stages">Which stages can see it.</param>
/// <param name="Count">
/// How many, for an array. One for anything that is not an array; zero is not valid.
/// </param>
public readonly record struct ShaderBinding(
    uint Set,
    uint Binding,
    ShaderBindingKind Kind,
    ShaderStages Stages,
    uint Count = 1);

/// <summary>
/// Everything a pipeline binds, described once for both backends.
/// </summary>
/// <remarks>
/// <para>
/// A Vulkan pipeline layout and a Direct3D root signature are the same statement in two
/// languages, and this is the statement. Vulkan builds descriptor set layouts from it;
/// Direct3D builds a root signature with one descriptor table per set. Neither is derived
/// from the other, and neither is derived by reflecting the compiled shader — reflection
/// would work and would mean the layout is discovered rather than declared, which is a
/// difference that matters the first time a shader stops using a binding and a table
/// silently changes shape underneath a renderer that still writes to it.
/// </para>
/// <para>
/// The correspondence is <see cref="ShaderBindings"/>: set becomes register space, binding
/// becomes register index, and the kind decides the register class. It is collision-free by
/// construction, so no remapping is needed in either direction.
/// </para>
/// </remarks>
/// <param name="Bindings">What the pipeline binds.</param>
/// <param name="PushConstantBytes">
/// How many bytes of push constants, or zero for none.
/// </param>
/// <remarks>
/// Push constants become root constants, at the register <see cref="ShaderBindings"/>
/// reserves for them. What the two backends will actually take differs, and by enough to
/// matter: see <see cref="MaximumPushConstantBytes"/> and
/// <see cref="GuaranteedPushConstantBytes"/>.
/// </remarks>
public sealed record ShaderLayout(
    IReadOnlyList<ShaderBinding> Bindings,
    uint PushConstantBytes = 0)
{
    /// <summary>The most push constant bytes a pipeline may declare.</summary>
    /// <remarks>
    /// <para>
    /// Two hundred and fifty-six, which is what every desktop driver this renderer has run
    /// on offers and what a Direct3D root signature holds in total. It is <em>not</em>
    /// Vulkan's guarantee: <c>maxPushConstantsSize</c> is only promised to be a hundred and
    /// twenty-eight, and the mesh pipeline's draw constants are a hundred and ninety-two —
    /// two matrices alone are past the floor. See <see cref="Geometry.DrawConstants"/>.
    /// </para>
    /// <para>
    /// So this is the practical ceiling rather than the portable one, and the check exists
    /// to catch a block that could not work anywhere rather than to promise one that works
    /// everywhere. Direct3D counts its root signature in thirty-two-bit words and allows
    /// sixty-four; two hundred and fifty-six bytes is all of them, leaving no room for a
    /// descriptor table, so a pipeline that actually asked for this much would fail there
    /// first and say so.
    /// </para>
    /// </remarks>
    public const uint MaximumPushConstantBytes = 256;

    /// <summary>The most push constant bytes Vulkan promises every device will take.</summary>
    /// <remarks>
    /// Worth having as a number even though nothing enforces it, because the day a device
    /// refuses a pipeline layout this is the first thing to compare against — and the fix
    /// is a uniform buffer rather than a smaller struct.
    /// </remarks>
    public const uint GuaranteedPushConstantBytes = 128;

    /// <summary>A layout that binds nothing.</summary>
    public static ShaderLayout Empty { get; } = new([]);

    /// <summary>The distinct descriptor sets this layout uses, in order.</summary>
    public IReadOnlyList<uint> Sets { get; } =
        [.. Bindings.Select(b => b.Set).Distinct().Order()];

    /// <summary>Checks that the layout is one both backends can build.</summary>
    /// <exception cref="ShaderCompilationException">It is not.</exception>
    /// <remarks>
    /// Called when a pipeline is built rather than when a layout is written, because a
    /// layout is data and the thing that fails is the pipeline. The failures caught here
    /// are the ones that would otherwise be caught by one backend and not the other, which
    /// is the worst way to find them.
    /// </remarks>
    public void Validate()
    {
        if (PushConstantBytes > MaximumPushConstantBytes)
        {
            throw new ShaderCompilationException(
                $"{PushConstantBytes} bytes of push constants is more than the {MaximumPushConstantBytes} "
                + "both backends allow.");
        }

        if (PushConstantBytes % 4 != 0)
        {
            throw new ShaderCompilationException(
                $"{PushConstantBytes} bytes of push constants is not a whole number of words.");
        }

        HashSet<(uint Set, uint Binding)> seen = [];

        foreach (ShaderBinding binding in Bindings)
        {
            if (binding.Count == 0)
            {
                throw new ShaderCompilationException(
                    $"The binding at set {binding.Set}, binding {binding.Binding} has no elements.");
            }

            if (binding.Stages == ShaderStages.None)
            {
                throw new ShaderCompilationException(
                    $"The binding at set {binding.Set}, binding {binding.Binding} is visible to no stage.");
            }

            if (binding.Set == ShaderBindings.PushConstantSpace)
            {
                throw new ShaderCompilationException(
                    $"Set {binding.Set} is reserved for push constants; see ShaderBindings.");
            }

            if (!seen.Add((binding.Set, binding.Binding)))
            {
                throw new ShaderCompilationException(
                    $"Set {binding.Set}, binding {binding.Binding} is declared twice.");
            }
        }
    }
}
