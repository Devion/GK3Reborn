using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>Triangles a scene contributes to ray tracing.</summary>
/// <param name="Positions">World-space vertex positions.</param>
/// <param name="Indices">Triangle indices into <paramref name="Positions"/>.</param>
public readonly record struct RayTracingMesh(Vector3[] Positions, uint[] Indices);

/// <summary>
/// The scene as rays see it: one bottom-level acceleration structure over every opaque
/// triangle, and a top-level structure holding it.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes into a single structure in world space rather than one per object with
/// instance transforms. GK3's scenes are small — the largest is under thirty thousand
/// triangles — and its props do not move once a scene is loaded, so the flexibility of
/// per-object instances would buy nothing and cost a few hundred more device allocations,
/// of which drivers guarantee only a few thousand in total. Moving props will need that
/// flexibility; they will also need a rebuild policy, and neither exists yet.
/// </para>
/// <para>
/// Only opaque geometry is included. Alpha-tested surfaces — GK3's windows, railings and
/// foliage, keyed on magenta — would otherwise cast solid shadows from their transparent
/// parts, because deciding per-hit whether a texel is a hole needs an any-hit shader and
/// therefore a full ray-tracing pipeline. Leaving them out makes them cast no shadow at
/// all, which is wrong in the other direction but far less visible: a missing shadow
/// under a window reads as bright, a solid one reads as a black rectangle on the floor.
/// </para>
/// </remarks>
public sealed unsafe class RayTracingScene : IDisposable
{
    private readonly VulkanContext _context;
    private readonly KhrAccelerationStructure _api;
    private readonly List<VulkanBuffer> _buffers = [];

    private AccelerationStructureKHR _bottomLevel;
    private AccelerationStructureKHR _topLevel;

    private RayTracingScene(VulkanContext context, KhrAccelerationStructure api)
    {
        _context = context;
        _api = api;
    }

    /// <summary>The structure a shader binds.</summary>
    public AccelerationStructureKHR Handle => _topLevel;

    /// <summary>How many triangles it holds.</summary>
    public int TriangleCount { get; private set; }

    /// <summary>Builds a structure over some meshes.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="meshes">The geometry, already in world space.</param>
    /// <returns>The structure, or null if there is nothing to trace against.</returns>
    public static RayTracingScene? Build(VulkanContext context, IReadOnlyList<RayTracingMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(meshes);

        if (!context.SupportsRayTracing)
        {
            return null;
        }

        // One vertex and one index buffer for everything, so the build reads two ranges
        // rather than several hundred.
        List<Vector3> positions = [];
        List<uint> indices = [];

        foreach (RayTracingMesh mesh in meshes)
        {
            uint offset = (uint)positions.Count;
            positions.AddRange(mesh.Positions);

            foreach (uint index in mesh.Indices)
            {
                indices.Add(index + offset);
            }
        }

        if (indices.Count < 3)
        {
            return null;
        }

        if (!context.Api.TryGetDeviceExtension(
                context.Instance, context.Device, out KhrAccelerationStructure api))
        {
            return null;
        }

        var scene = new RayTracingScene(context, api) { TriangleCount = indices.Count / 3 };

        try
        {
            scene.BuildBottomLevel(
                CollectionsMarshal.AsSpan(positions), CollectionsMarshal.AsSpan(indices));

            scene.BuildTopLevel();
            return scene;
        }
        catch
        {
            scene.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_topLevel.Handle != 0)
        {
            _api.DestroyAccelerationStructure(_context.Device, _topLevel, null);
            _topLevel = default;
        }

        if (_bottomLevel.Handle != 0)
        {
            _api.DestroyAccelerationStructure(_context.Device, _bottomLevel, null);
            _bottomLevel = default;
        }

        foreach (VulkanBuffer buffer in _buffers)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
        _api.Dispose();
    }

    private void BuildBottomLevel(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)
    {
        const BufferUsageFlags InputUsage =
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr |
            BufferUsageFlags.ShaderDeviceAddressBit;

        VulkanBuffer vertices = VulkanBuffer.CreateDeviceLocal(_context, positions, InputUsage);
        VulkanBuffer triangles = VulkanBuffer.CreateDeviceLocal(_context, indices, InputUsage);

        _buffers.Add(vertices);
        _buffers.Add(triangles);

        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.TrianglesKhr,

            // Opaque, because nothing here is alpha tested: keyed geometry never reaches
            // this point. Saying so lets the traversal skip any-hit entirely.
            Flags = GeometryFlagsKHR.OpaqueBitKhr,
            Geometry = new AccelerationStructureGeometryDataKHR
            {
                Triangles = new AccelerationStructureGeometryTrianglesDataKHR
                {
                    SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                    VertexFormat = Format.R32G32B32Sfloat,
                    VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vertices.DeviceAddress },
                    VertexStride = (ulong)sizeof(Vector3),
                    MaxVertex = (uint)(positions.Length - 1),
                    IndexType = IndexType.Uint32,
                    IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = triangles.DeviceAddress },
                },
            },
        };

        uint primitives = (uint)(indices.Length / 3);

        var build = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            GeometryCount = 1,
            PGeometries = &geometry,
        };

        _bottomLevel = Create(build, primitives, AccelerationStructureTypeKHR.BottomLevelKhr);
    }

    private void BuildTopLevel()
    {
        // One instance, untransformed: the geometry is already in world space.
        var instance = new AccelerationStructureInstanceKHR
        {
            Transform = Identity(),
            InstanceCustomIndex = 0,
            Mask = 0xFF,
            InstanceShaderBindingTableRecordOffset = 0,
            Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr,
            AccelerationStructureReference = DeviceAddressOf(_bottomLevel),
        };

        VulkanBuffer instances = VulkanBuffer.CreateHostVisible(
            _context,
            (ulong)sizeof(AccelerationStructureInstanceKHR),
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
            addressable: true);

        instances.Write<AccelerationStructureInstanceKHR>([instance]);
        _buffers.Add(instances);

        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.InstancesKhr,
            Flags = GeometryFlagsKHR.OpaqueBitKhr,
            Geometry = new AccelerationStructureGeometryDataKHR
            {
                Instances = new AccelerationStructureGeometryInstancesDataKHR
                {
                    SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                    ArrayOfPointers = false,
                    Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instances.DeviceAddress },
                },
            },
        };

        var build = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            GeometryCount = 1,
            PGeometries = &geometry,
        };

        _topLevel = Create(build, 1, AccelerationStructureTypeKHR.TopLevelKhr);
    }

    /// <summary>Sizes, allocates and builds one structure.</summary>
    private AccelerationStructureKHR Create(
        AccelerationStructureBuildGeometryInfoKHR build,
        uint primitives,
        AccelerationStructureTypeKHR type)
    {
        var sizes = new AccelerationStructureBuildSizesInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildSizesInfoKhr,
        };

        AccelerationStructureBuildGeometryInfoKHR sizing = build;
        uint counts = primitives;

        _api.GetAccelerationStructureBuildSizes(
            _context.Device,
            AccelerationStructureBuildTypeKHR.DeviceKhr,
            &sizing,
            &counts,
            &sizes);

        VulkanBuffer storage = VulkanBuffer.CreateEmpty(
            _context,
            sizes.AccelerationStructureSize,
            BufferUsageFlags.AccelerationStructureStorageBitKhr,
            addressable: true);

        _buffers.Add(storage);

        var createInfo = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = storage.Handle,
            Size = sizes.AccelerationStructureSize,
            Type = type,
        };

        if (_api.CreateAccelerationStructure(_context.Device, in createInfo, null, out AccelerationStructureKHR handle)
            != Result.Success)
        {
            throw new VulkanException($"Could not create a {type} acceleration structure.");
        }

        // Scratch is only needed while the build runs, but freeing it means tracking when
        // the build finished; the builds here are one-shot and waited on, so it is simply
        // kept until the whole structure is disposed.
        VulkanBuffer scratch = VulkanBuffer.CreateEmpty(
            _context,
            Math.Max(sizes.BuildScratchSize, 1),
            BufferUsageFlags.StorageBufferBit,
            addressable: true);

        _buffers.Add(scratch);

        build.DstAccelerationStructure = handle;
        build.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratch.DeviceAddress };

        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = primitives };
        AccelerationStructureBuildRangeInfoKHR* ranges = &range;

        CommandBuffer command = _context.BeginOneShot();
        _api.CmdBuildAccelerationStructures(command, 1, in build, &ranges);
        _context.EndOneShot(command);

        return handle;
    }

    private ulong DeviceAddressOf(AccelerationStructureKHR structure)
    {
        var info = new AccelerationStructureDeviceAddressInfoKHR
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = structure,
        };

        return _api.GetAccelerationStructureDeviceAddress(_context.Device, in info);
    }

    /// <summary>The identity, in the three-by-four row-major form instances use.</summary>
    private static TransformMatrixKHR Identity()
    {
        var transform = default(TransformMatrixKHR);

        transform.Matrix[0] = 1f;
        transform.Matrix[5] = 1f;
        transform.Matrix[10] = 1f;

        return transform;
    }
}
