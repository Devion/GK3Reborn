using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>How a sampler behaves outside the zero-to-one range.</summary>
public enum SamplerAddressing
{
    /// <summary>Tiles. What GK3's wall and floor textures want.</summary>
    Repeat,

    /// <summary>Holds the edge texel. What an atlas and a full-screen picture want.</summary>
    Clamp,
}

/// <summary>
/// The handful of samplers the renderer actually uses, made once and shared.
/// </summary>
/// <remarks>
/// <para>
/// Vulkan makes a sampler an object and the texture path creates one per texture, which is
/// wasteful and harmless. Direct3D makes it a descriptor in a heap of its own, and the
/// heaps are the scarce thing: one sampler heap may be bound at a time and it holds at most
/// two thousand and forty-eight descriptors. A sampler per texture would exhaust that in a
/// room.
/// </para>
/// <para>
/// So they are shared. There are only ever a few distinct ones — the axis of variation is
/// how a texture tiles and nothing else — and every texture in a scene points at whichever
/// of them it wants. The heap is small, permanent, and bound for the life of the renderer.
/// </para>
/// <para>
/// Anisotropy is asked for unconditionally, which Vulkan cannot do. There, asking for
/// filtering the device did not enable is invalid rather than ignored; here sixteen-times
/// anisotropic filtering is required of every Direct3D 12 device, so there is nothing to
/// check and no fallback to carry.
/// </para>
/// </remarks>
public sealed unsafe class D3D12Samplers : IDisposable
{
    private readonly D3D12DescriptorHeap _heap;
    private readonly uint _repeat;
    private readonly uint _clamp;
    private bool _disposed;

    private D3D12Samplers(D3D12DescriptorHeap heap, uint repeat, uint clamp)
    {
        _heap = heap;
        _repeat = repeat;
        _clamp = clamp;
    }

    /// <summary>The heap holding them, which a command list binds.</summary>
    public ID3D12DescriptorHeap* Handle => _heap.Handle;

    /// <summary>Creates the shared samplers.</summary>
    /// <param name="context">The device.</param>
    /// <returns>The samplers.</returns>
    /// <exception cref="D3D12Exception">The heap could not be created.</exception>
    public static D3D12Samplers Create(D3D12Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        D3D12DescriptorHeap heap = D3D12DescriptorHeap.Create(
            context.Device, DescriptorHeapType.Sampler, 8, shaderVisible: true);

        try
        {
            uint repeat = Write(context, heap, TextureAddressMode.Wrap);
            uint clamp = Write(context, heap, TextureAddressMode.Clamp);

            return new D3D12Samplers(heap, repeat, clamp);
        }
        catch
        {
            heap.Dispose();
            throw;
        }
    }

    /// <summary>Where one of the samplers is, for a shader to read.</summary>
    /// <param name="addressing">Which one.</param>
    /// <returns>Its handle.</returns>
    public GpuDescriptorHandle Gpu(SamplerAddressing addressing) =>
        _heap.Gpu(addressing == SamplerAddressing.Clamp ? _clamp : _repeat);

    /// <summary>Copies one of the samplers into a descriptor slot in another heap.</summary>
    /// <param name="context">The device.</param>
    /// <param name="addressing">Which one.</param>
    /// <param name="where">Where to put it.</param>
    /// <remarks>
    /// For the tables that mix a texture and its sampler. A combined image sampler in GLSL
    /// becomes a texture and a sampler in HLSL at the same register index, so the sampler
    /// has to be at a known place in the pass's own sampler table rather than at a known
    /// place in this heap.
    /// </remarks>
    public void CopyInto(
        D3D12Context context, SamplerAddressing addressing, CpuDescriptorHandle where)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Device->CopyDescriptorsSimple(
            1,
            where,
            _heap.Cpu(addressing == SamplerAddressing.Clamp ? _clamp : _repeat),
            DescriptorHeapType.Sampler);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _heap.Dispose();
    }

    private static uint Write(
        D3D12Context context, D3D12DescriptorHeap heap, TextureAddressMode addressing)
    {
        var description = new SamplerDesc
        {
            Filter = Filter.Anisotropic,
            AddressU = addressing,
            AddressV = addressing,
            AddressW = addressing,
            MipLODBias = 0f,

            // Sixteen is required of every Direct3D 12 device, so unlike the Vulkan path
            // there is nothing to query and no lower number to fall back to.
            MaxAnisotropy = 16,
            ComparisonFunc = ComparisonFunc.Never,
            MinLOD = 0f,

            // Every level there is. Clamping this to a texture's own count would mean a
            // sampler per texture, which is the thing this class exists to avoid.
            MaxLOD = float.MaxValue,
        };

        uint slot = heap.Allocate();
        context.Device->CreateSampler(&description, heap.Cpu(slot));
        return slot;
    }
}
