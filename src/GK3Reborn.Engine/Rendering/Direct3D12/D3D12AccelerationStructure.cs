using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>One piece of geometry the rays can hit.</summary>
/// <param name="Vertices">Positions, three floats each.</param>
/// <param name="Indices">Triangle indices into those positions.</param>
/// <param name="Transform">Where the piece stands in the world.</param>
/// <param name="Opaque">
/// Whether a ray may stop at the first hit without asking the shader about it.
/// </param>
public readonly record struct TraceablePart(
    ReadOnlyMemory<Vector3> Vertices,
    ReadOnlyMemory<uint> Indices,
    Matrix4x4 Transform,
    bool Opaque = true);

/// <summary>
/// The acceleration structure the ray queries trace against.
/// </summary>
/// <remarks>
/// <para>
/// Two levels, as both APIs have them: a bottom-level structure per piece of geometry, and
/// one top-level structure holding an instance of each with its transform. The division is
/// the same as Vulkan's and so is the reason for it — the bottom level is the expensive
/// part and does not change when something moves, so a moving object is a new transform in
/// the top level rather than a rebuilt tree.
/// </para>
/// <para>
/// Three buffers per structure and all three matter. The result is what the rays read and
/// lives in a state of its own that nothing else uses. The scratch is working space the
/// build needs and is <em>not</em> free afterwards on any driver that overlaps builds — it
/// is kept alive until the build has been waited for. The instance buffer is upload-heap
/// memory holding the top level's descriptions, read by the device during the build, so it
/// cannot be a stack array.
/// </para>
/// <para>
/// The transform rows are the trap. Direct3D wants a three-by-four row-major matrix, which
/// is the transpose of the four-by-four this engine carries everywhere else, and a
/// transform written the wrong way round does not fail: it puts the geometry somewhere
/// plausible and wrong, and the shadows land in the wrong place with nothing to say why.
/// </para>
/// </remarks>
public sealed unsafe class D3D12AccelerationStructure : IDisposable
{
    private readonly List<ComPtr<ID3D12Resource>> _owned = [];
    private ComPtr<ID3D12Resource> _topLevel;
    private bool _disposed;

    private D3D12AccelerationStructure(ComPtr<ID3D12Resource> topLevel, int parts, int triangles)
    {
        _topLevel = topLevel;
        PartCount = parts;
        TriangleCount = triangles;
    }

    /// <summary>How many pieces of geometry are in it.</summary>
    public int PartCount { get; }

    /// <summary>How many triangles those pieces hold.</summary>
    public int TriangleCount { get; }

    /// <summary>Where the top-level structure is, for a shader resource view.</summary>
    public ulong Address => _topLevel.Handle is null ? 0 : _topLevel.GetGPUVirtualAddress();

    /// <summary>Builds a structure over some geometry.</summary>
    /// <param name="context">The device.</param>
    /// <param name="parts">The geometry.</param>
    /// <returns>The structure.</returns>
    /// <exception cref="D3D12Exception">It could not be built.</exception>
    /// <exception cref="InvalidOperationException">The device cannot trace.</exception>
    public static D3D12AccelerationStructure Build(
        D3D12Context context, IReadOnlyList<TraceablePart> parts)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parts);

        if (!context.SupportsRayTracing)
        {
            throw new InvalidOperationException(
                "This device has no inline ray tracing, so there is nothing to trace against.");
        }

        List<ComPtr<ID3D12Resource>> owned = [];
        List<ComPtr<ID3D12Resource>> scratch = [];
        int triangles = 0;

        try
        {
            ID3D12GraphicsCommandList4* list = context.BeginOneShot();

            ulong[] bottomLevels = new ulong[parts.Count];

            for (int i = 0; i < parts.Count; i++)
            {
                TraceablePart part = parts[i];
                triangles += part.Indices.Length / 3;

                bottomLevels[i] = BuildBottomLevel(context, list, part, owned, scratch);
            }

            // Every bottom level must be finished before the top level reads it, and
            // nothing but a barrier says so: the builds are on one queue but the runtime
            // does not order them against each other.
            D3D12Context.Barrier(list, null);

            ComPtr<ID3D12Resource> topLevel =
                BuildTopLevel(context, list, parts, bottomLevels, owned, scratch);

            context.EndOneShot();

            var structure = new D3D12AccelerationStructure(topLevel, parts.Count, triangles);
            structure._owned.AddRange(owned);
            owned.Clear();

            return structure;
        }
        finally
        {
            // The scratch is only needed until the build has been waited for, which
            // EndOneShot did. Anything still in owned is there because the build threw.
            foreach (ComPtr<ID3D12Resource> buffer in scratch)
            {
                buffer.Dispose();
            }

            foreach (ComPtr<ID3D12Resource> buffer in owned)
            {
                buffer.Dispose();
            }
        }
    }

    /// <summary>Writes a shader resource view of this structure into a descriptor slot.</summary>
    /// <param name="context">The device.</param>
    /// <param name="where">Where to write it.</param>
    /// <remarks>
    /// The one view in Direct3D made from an address rather than from a resource: the
    /// resource pointer must be null and the address goes in the description. Passing the
    /// resource as well is a validation error, which is a helpful way to be told, since
    /// every other view in the API works the other way round.
    /// </remarks>
    public void Describe(D3D12Context context, CpuDescriptorHandle where)
    {
        ArgumentNullException.ThrowIfNull(context);

        var description = new ShaderResourceViewDesc
        {
            Format = Format.FormatUnknown,
            ViewDimension = SrvDimension.RaytracingAccelerationStructure,
            Shader4ComponentMapping = DefaultComponentMapping,
        };

        description.Anonymous.RaytracingAccelerationStructure =
            new RaytracingAccelerationStructureSrv { Location = Address };

        context.Device->CreateShaderResourceView((ID3D12Resource*)null, &description, where);
    }

    /// <summary>
    /// The identity component mapping, which every view that does not swizzle must state.
    /// </summary>
    /// <remarks>
    /// <c>D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING</c>, which is a macro rather than a
    /// constant and so does not survive into any binding. Leaving it zero maps every
    /// channel to red, and a texture that comes out grey is a long way from a mapping
    /// nobody set.
    /// </remarks>
    public const uint DefaultComponentMapping = (0 << 0) | (1 << 3) | (2 << 6) | (3 << 9) | (1 << 12);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (ComPtr<ID3D12Resource> buffer in _owned)
        {
            buffer.Dispose();
        }

        _owned.Clear();
        _topLevel.Dispose();
    }

    private static ulong BuildBottomLevel(
        D3D12Context context,
        ID3D12GraphicsCommandList4* list,
        TraceablePart part,
        List<ComPtr<ID3D12Resource>> owned,
        List<ComPtr<ID3D12Resource>> scratch)
    {
        ComPtr<ID3D12Resource> vertices = Upload<Vector3>(context, part.Vertices.Span, owned);
        ComPtr<ID3D12Resource> indices = Upload<uint>(context, part.Indices.Span, owned);

        var triangles = new RaytracingGeometryTrianglesDesc
        {
            VertexFormat = Format.FormatR32G32B32Float,
            VertexCount = (uint)part.Vertices.Length,
            VertexBuffer = new GpuVirtualAddressAndStride
            {
                StartAddress = vertices.GetGPUVirtualAddress(),
                StrideInBytes = (ulong)sizeof(Vector3),
            },
            IndexFormat = Format.FormatR32Uint,
            IndexCount = (uint)part.Indices.Length,
            IndexBuffer = indices.GetGPUVirtualAddress(),

            // Zero, and the geometry is placed by the instance instead. A per-geometry
            // transform is read by the build and baked in, which would mean rebuilding the
            // tree every time something moved.
            Transform3x4 = 0,
        };

        var geometry = new RaytracingGeometryDesc
        {
            Type = RaytracingGeometryType.Triangles,
            Flags = part.Opaque
                ? RaytracingGeometryFlags.Opaque
                : RaytracingGeometryFlags.None,
        };

        geometry.Anonymous.Triangles = triangles;

        var inputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.BottomLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
            NumDescs = 1,
            DescsLayout = ElementsLayout.Array,
        };

        inputs.Anonymous.PGeometryDescs = &geometry;

        return Build(context, list, inputs, owned, scratch);
    }

    private static ComPtr<ID3D12Resource> BuildTopLevel(
        D3D12Context context,
        ID3D12GraphicsCommandList4* list,
        IReadOnlyList<TraceablePart> parts,
        ulong[] bottomLevels,
        List<ComPtr<ID3D12Resource>> owned,
        List<ComPtr<ID3D12Resource>> scratch)
    {
        var instances = new RaytracingInstanceDesc[Math.Max(1, parts.Count)];

        for (int i = 0; i < parts.Count; i++)
        {
            var instance = new RaytracingInstanceDesc
            {
                InstanceID = (uint)i,
                InstanceMask = 0xFF,
                InstanceContributionToHitGroupIndex = 0,
                Flags = (uint)RaytracingInstanceFlags.None,
                AccelerationStructure = bottomLevels[i],
            };

            WriteTransform(parts[i].Transform, instance.Transform);
            instances[i] = instance;
        }

        ComPtr<ID3D12Resource> buffer =
            Upload<RaytracingInstanceDesc>(context, instances.AsSpan(0, parts.Count), owned);

        var inputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.TopLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
            NumDescs = (uint)parts.Count,
            DescsLayout = ElementsLayout.Array,
        };

        inputs.Anonymous.InstanceDescs = parts.Count > 0 ? buffer.GetGPUVirtualAddress() : 0;

        List<ComPtr<ID3D12Resource>> result = [];
        ulong address = Build(context, list, inputs, result, scratch);
        _ = address;

        // The top level is the one thing that outlives the build and is not scratch, so it
        // is taken out of the list rather than added to the owned ones.
        ComPtr<ID3D12Resource> topLevel = result[0];
        result.RemoveAt(0);
        owned.AddRange(result);

        return topLevel;
    }

    private static ulong Build(
        D3D12Context context,
        ID3D12GraphicsCommandList4* list,
        BuildRaytracingAccelerationStructureInputs inputs,
        List<ComPtr<ID3D12Resource>> results,
        List<ComPtr<ID3D12Resource>> scratch)
    {
        RaytracingAccelerationStructurePrebuildInfo sizes = default;
        context.Device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &sizes);

        if (sizes.ResultDataMaxSizeInBytes == 0)
        {
            throw new D3D12Exception(
                "The device says this acceleration structure needs no memory, which it cannot.");
        }

        ComPtr<ID3D12Resource> result = context.CreateBuffer(
            sizes.ResultDataMaxSizeInBytes,
            HeapType.Default,

            // Its own state, which nothing else uses and which it may never leave.
            ResourceStates.RaytracingAccelerationStructure,
            allowUnorderedAccess: true);

        // Common, not UnorderedAccess: a buffer in device memory is created in Common
        // whatever is asked for. The build writes it through an unordered access view, so
        // the flag is needed and the state is not.
        ComPtr<ID3D12Resource> working = context.CreateBuffer(
            sizes.ScratchDataSizeInBytes,
            HeapType.Default,
            ResourceStates.Common,
            allowUnorderedAccess: true);

        results.Add(result);
        scratch.Add(working);

        var description = new BuildRaytracingAccelerationStructureDesc
        {
            DestAccelerationStructureData = result.GetGPUVirtualAddress(),
            Inputs = inputs,
            SourceAccelerationStructureData = 0,
            ScratchAccelerationStructureData = working.GetGPUVirtualAddress(),
        };

        list->BuildRaytracingAccelerationStructure(&description, 0, (RaytracingAccelerationStructurePostbuildInfoDesc*)null);

        return result.GetGPUVirtualAddress();
    }

    /// <summary>Writes a transform into the three-by-four row-major form Direct3D wants.</summary>
    /// <remarks>
    /// The engine's matrices are row-vector, so a point is <c>p * M</c> and the translation
    /// is the fourth row. Direct3D's instance transform is column-vector — <c>M * p</c> —
    /// with the translation in the fourth column, so this is a transpose and not a copy.
    /// Getting it wrong puts the geometry somewhere plausible and wrong.
    /// </remarks>
    private static void WriteTransform(Matrix4x4 transform, float* rows)
    {
        rows[0] = transform.M11; rows[1] = transform.M21; rows[2] = transform.M31; rows[3] = transform.M41;
        rows[4] = transform.M12; rows[5] = transform.M22; rows[6] = transform.M32; rows[7] = transform.M42;
        rows[8] = transform.M13; rows[9] = transform.M23; rows[10] = transform.M33; rows[11] = transform.M43;
    }

    private static ComPtr<ID3D12Resource> Upload<T>(
        D3D12Context context, ReadOnlySpan<T> data, List<ComPtr<ID3D12Resource>> owned)
        where T : unmanaged
    {
        ulong bytes = (ulong)(data.Length * sizeof(T));

        // The upload heap rather than a staged copy into device memory. A build reads these
        // once and the geometry here is small; a copy would cost a second buffer and a
        // barrier to save a read the build does anyway.
        ComPtr<ID3D12Resource> buffer = context.CreateBuffer(bytes, HeapType.Upload);
        owned.Add(buffer);

        if (data.Length == 0)
        {
            return buffer;
        }

        void* mapped;
        var nothing = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };

        D3D12Exception.ThrowIfFailed(buffer.Map(0, &nothing, &mapped), "map an upload buffer");

        try
        {
            data.CopyTo(new Span<T>(mapped, data.Length));
        }
        finally
        {
            buffer.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
        }

        return buffer;
    }
}
