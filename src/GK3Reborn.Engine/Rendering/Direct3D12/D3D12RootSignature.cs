using System.Runtime.InteropServices;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// A root signature, built from the same layout the Vulkan descriptor sets are built from.
/// </summary>
public sealed unsafe class D3D12RootSignature : IDisposable
{
    private readonly Dictionary<uint, int> _viewParameters = [];
    private readonly Dictionary<uint, int> _samplerParameters = [];
    private ComPtr<ID3D12RootSignature> _signature;
    private bool _disposed;

    private D3D12RootSignature(ComPtr<ID3D12RootSignature> signature, ShaderLayout layout)
    {
        _signature = signature;
        Layout = layout;
    }

    /// <summary>What this signature was built from.</summary>
    public ShaderLayout Layout { get; }

    /// <summary>The signature, for binding.</summary>
    public ID3D12RootSignature* Handle => _signature.Handle;

    /// <summary>Which root parameter carries the push constants, or -1 when there are none.</summary>
    public int PushConstantParameter { get; private set; } = -1;

    /// <summary>How many descriptors in the view heap a whole frame of this pipeline needs.</summary>
    public uint ViewDescriptorCount { get; private set; }

    /// <summary>How many sampler descriptors it needs.</summary>
    public uint SamplerDescriptorCount { get; private set; }

    /// <summary>Builds a root signature.</summary>
    /// <param name="device">The device.</param>
    /// <param name="layout">What the pipeline binds.</param>
    /// <param name="allowInputLayout">
    /// Whether a vertex input layout is used, which the signature must say in advance.
    /// </param>
    /// <returns>The signature.</returns>
    /// <exception cref="D3D12Exception">It could not be built.</exception>
    /// <exception cref="ShaderCompilationException">The layout is not one both backends can build.</exception>
    public static D3D12RootSignature Create(
        ID3D12Device5* device, ShaderLayout layout, bool allowInputLayout = true)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.Validate();

        List<RootParameter> parameters = [];
        List<nint> ranges = [];

        var viewParameters = new Dictionary<uint, int>();
        var samplerParameters = new Dictionary<uint, int>();
        uint views = 0;
        uint samplers = 0;

        try
        {
            foreach (uint set in layout.Sets)
            {
                ShaderBinding[] inSet = [.. layout.Bindings.Where(b => b.Set == set)
                    .OrderBy(b => b.Binding)];

                (nint block, int count, uint descriptors) = RangesFor(inSet, samplerHeap: false);
                if (count > 0)
                {
                    ranges.Add(block);
                    viewParameters[set] = parameters.Count;
                    views += descriptors;
                    parameters.Add(Table(block, count, VisibilityOf(inSet)));
                }

                (nint samplerBlock, int samplerCount, uint samplerDescriptors) =
                    RangesFor(inSet, samplerHeap: true);

                if (samplerCount > 0)
                {
                    ranges.Add(samplerBlock);
                    samplerParameters[set] = parameters.Count;
                    samplers += samplerDescriptors;
                    parameters.Add(Table(samplerBlock, samplerCount, VisibilityOf(inSet)));
                }
            }

            int pushConstantParameter = -1;

            if (layout.PushConstantBytes > 0)
            {
                pushConstantParameter = parameters.Count;

                var constants = new RootParameter
                {
                    ParameterType = RootParameterType.Type32BitConstants,

                    // Every stage. Push constants are small and which stages read them is a
                    // property of the shader rather than of the layout; narrowing it here
                    // would be a second place to keep in step for no saving worth having.
                    ShaderVisibility = ShaderVisibility.All,
                };

                constants.Anonymous.Constants = new RootConstants
                {
                    ShaderRegister = ShaderBindings.PushConstantRegister,
                    RegisterSpace = ShaderBindings.PushConstantSpace,
                    Num32BitValues = layout.PushConstantBytes / 4,
                };

                parameters.Add(constants);
            }

            ComPtr<ID3D12RootSignature> signature = Serialize(device, parameters, allowInputLayout);

            var built = new D3D12RootSignature(signature, layout)
            {
                PushConstantParameter = pushConstantParameter,
                ViewDescriptorCount = views,
                SamplerDescriptorCount = samplers,
            };

            foreach ((uint set, int parameter) in viewParameters)
            {
                built._viewParameters[set] = parameter;
            }

            foreach ((uint set, int parameter) in samplerParameters)
            {
                built._samplerParameters[set] = parameter;
            }

            return built;
        }
        finally
        {
            foreach (nint block in ranges)
            {
                Marshal.FreeHGlobal(block);
            }
        }
    }

    /// <summary>Which root parameter a set's views are bound to.</summary>
    /// <param name="set">The descriptor set.</param>
    /// <returns>The parameter index, or -1 when the set has no views.</returns>
    public int ParameterFor(uint set) => _viewParameters.GetValueOrDefault(set, -1);

    /// <summary>Where in a set's view table one binding's descriptor sits.</summary>
    /// <param name="set">The descriptor set.</param>
    /// <param name="binding">The binding within it.</param>
    /// <returns>How many descriptors into the table it is.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The set has no such binding.</exception>
    public uint ViewOffset(uint set, uint binding) => Offset(set, binding, samplers: false);

    /// <summary>Where in a set's sampler table one binding's descriptor sits.</summary>
    /// <param name="set">The descriptor set.</param>
    /// <param name="binding">The binding within it.</param>
    /// <returns>How many descriptors into the table it is.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The set has no such binding.</exception>
    public uint SamplerOffset(uint set, uint binding) => Offset(set, binding, samplers: true);

    /// <summary>Which root parameter a set's samplers are bound to.</summary>
    /// <param name="set">The descriptor set.</param>
    /// <returns>The parameter index, or -1 when the set has no samplers.</returns>
    public int SamplerParameterFor(uint set) => _samplerParameters.GetValueOrDefault(set, -1);

    /// <inheritdoc/>
    private uint Offset(uint set, uint binding, bool samplers)
    {
        uint offset = 0;

        foreach (ShaderBinding candidate in Layout.Bindings
            .Where(b => b.Set == set)
            .OrderBy(b => b.Binding))
        {
            foreach (DescriptorRangeType type in TypesOf(candidate.Kind))
            {
                if ((type == DescriptorRangeType.Sampler) != samplers)
                {
                    continue;
                }

                if (candidate.Binding == binding)
                {
                    return offset;
                }

                offset += candidate.Count;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(binding),
            binding,
            $"Set {set} has no {(samplers ? "sampler" : "view")} at this binding.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _signature.Dispose();
    }

    /// <summary>The descriptor ranges one set needs in one heap.</summary>
    /// <returns>
    /// Unmanaged storage for the ranges, how many there are, and how many descriptors they
    /// cover in total. The storage is the caller's to free.
    /// </returns>
    private static (nint Block, int Count, uint Descriptors) RangesFor(
        ShaderBinding[] inSet, bool samplerHeap)
    {
        List<DescriptorRange> found = [];
        uint descriptors = 0;
        uint offset = 0;

        foreach (ShaderBinding binding in inSet)
        {
            foreach (DescriptorRangeType type in TypesOf(binding.Kind))
            {
                bool isSampler = type == DescriptorRangeType.Sampler;
                if (isSampler != samplerHeap)
                {
                    continue;
                }

                found.Add(new DescriptorRange
                {
                    RangeType = type,
                    NumDescriptors = binding.Count,
                    BaseShaderRegister = binding.Binding,
                    RegisterSpace = binding.Set,

                    // Where in the table this range starts. Stated rather than appended,
                    // because a combined image sampler contributes to two tables and the
                    // offsets in each advance independently.
                    OffsetInDescriptorsFromTableStart = offset,
                });

                offset += binding.Count;
                descriptors += binding.Count;
            }
        }

        if (found.Count == 0)
        {
            return (0, 0, 0);
        }

        nint block = Marshal.AllocHGlobal(sizeof(DescriptorRange) * found.Count);
        var span = new Span<DescriptorRange>((void*)block, found.Count);
        CollectionsMarshal.AsSpan(found).CopyTo(span);

        return (block, found.Count, descriptors);
    }

    /// <summary>Which register classes one binding occupies.</summary>
    private static DescriptorRangeType[] TypesOf(ShaderBindingKind kind) => kind switch
    {
        ShaderBindingKind.UniformBuffer => [DescriptorRangeType.Cbv],
        ShaderBindingKind.ReadOnlyStorageBuffer => [DescriptorRangeType.Srv],
        ShaderBindingKind.StorageBuffer => [DescriptorRangeType.Uav],
        ShaderBindingKind.SampledImage => [DescriptorRangeType.Srv],
        ShaderBindingKind.StorageImage => [DescriptorRangeType.Uav],
        ShaderBindingKind.Sampler => [DescriptorRangeType.Sampler],
        ShaderBindingKind.AccelerationStructure => [DescriptorRangeType.Srv],
        _ => [DescriptorRangeType.Srv, DescriptorRangeType.Sampler],
    };

    private static ShaderVisibility VisibilityOf(ShaderBinding[] inSet)
    {
        ShaderStages stages = ShaderStages.None;
        foreach (ShaderBinding binding in inSet)
        {
            stages |= binding.Stages;
        }

        // Direct3D can narrow a table to one stage and no further: there is no "vertex and
        // pixel but not the rest". Anything used by more than one stage is visible to all,
        // which costs nothing but a slightly larger root signature.
        return stages switch
        {
            ShaderStages.Vertex => ShaderVisibility.Vertex,
            ShaderStages.Fragment => ShaderVisibility.Pixel,
            _ => ShaderVisibility.All,
        };
    }

    private static RootParameter Table(nint ranges, int count, ShaderVisibility visibility)
    {
        var parameter = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            ShaderVisibility = visibility,
        };

        parameter.Anonymous.DescriptorTable = new RootDescriptorTable
        {
            NumDescriptorRanges = (uint)count,
            PDescriptorRanges = (DescriptorRange*)ranges,
        };

        return parameter;
    }

    private static ComPtr<ID3D12RootSignature> Serialize(
        ID3D12Device5* device, List<RootParameter> parameters, bool allowInputLayout)
    {
        // Held for the life of the process rather than opened and closed per signature; see
        // D3D12Runtime.
        D3D12 d3d12 = D3D12Runtime.D3D12;

        fixed (RootParameter* first = CollectionsMarshal.AsSpan(parameters))
        {
            var description = new RootSignatureDesc
            {
                NumParameters = (uint)parameters.Count,
                PParameters = parameters.Count > 0 ? first : null,
                NumStaticSamplers = 0,
                PStaticSamplers = null,

                // A signature that does not say it allows an input layout gets one
                // rejected at pipeline creation, and a compute signature that says it
                // does wastes a root slot. Neither failure names the flag.
                Flags = allowInputLayout
                    ? RootSignatureFlags.AllowInputAssemblerInputLayout
                    : RootSignatureFlags.None,
            };

            ComPtr<ID3D10Blob> blob = default;
            ComPtr<ID3D10Blob> errors = default;

            int hr = d3d12.SerializeRootSignature(
                &description,
                D3DRootSignatureVersion.Version10,
                blob.GetAddressOf(),
                errors.GetAddressOf());

            try
            {
                if (hr < 0)
                {
                    string message = errors.Handle is not null
                        ? Marshal.PtrToStringAnsi((nint)errors.GetBufferPointer()) ?? "no detail"
                        : "no detail";

                    throw new D3D12Exception($"Could not serialise the root signature: {message}");
                }

                ComPtr<ID3D12RootSignature> signature = default;
                Guid signatureId = ID3D12RootSignature.Guid;

                D3D12Exception.ThrowIfFailed(
                    device->CreateRootSignature(
                        0,
                        blob.GetBufferPointer(),
                        blob.GetBufferSize(),
                        &signatureId,
                        (void**)signature.GetAddressOf()),
                    "create the root signature");

                return signature;
            }
            finally
            {
                blob.Dispose();
                errors.Dispose();
            }
        }
    }
}
