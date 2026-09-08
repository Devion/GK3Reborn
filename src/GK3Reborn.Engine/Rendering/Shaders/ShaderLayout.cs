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
    CombinedImageSampler,

    /// <summary>A texture read without a sampler of its own, in <c>t</c>.</summary>
    SampledImage,

    /// <summary>A texture a shader writes, in <c>u</c>.</summary>
    StorageImage,

    /// <summary>A sampler on its own, in <c>s</c>.</summary>
    Sampler,

    /// <summary>An acceleration structure, which Direct3D puts in <c>t</c>.</summary>
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
/// <param name="Bindings">What the pipeline binds.</param>
/// <param name="PushConstantBytes">
/// How many bytes of push constants, or zero for none.
/// </param>
public sealed record ShaderLayout(
    IReadOnlyList<ShaderBinding> Bindings,
    uint PushConstantBytes = 0)
{
    /// <summary>The most push constant bytes a pipeline may declare.</summary>
    public const uint MaximumPushConstantBytes = 256;

    /// <summary>The most push constant bytes Vulkan promises every device will take.</summary>
    public const uint GuaranteedPushConstantBytes = 128;

    /// <summary>A layout that binds nothing.</summary>
    public static ShaderLayout Empty { get; } = new([]);

    /// <summary>The distinct descriptor sets this layout uses, in order.</summary>
    public IReadOnlyList<uint> Sets { get; } =
        [.. Bindings.Select(b => b.Set).Distinct().Order()];

    /// <summary>Checks that the layout is one both backends can build.</summary>
    /// <exception cref="ShaderCompilationException">It is not.</exception>
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
