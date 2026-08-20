using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>Triangles a scene contributes to ray tracing.</summary>
/// <param name="Positions">World-space vertex positions.</param>
/// <param name="Indices">Triangle indices into <paramref name="Positions"/>.</param>
public readonly record struct RayTracingMesh(Vector3[] Positions, uint[] Indices)
{
    /// <summary>
    /// Which movable thing this geometry belongs to, or zero for the room itself.
    /// </summary>
    /// <remarks>
    /// Everything sharing a part is built into one structure and placed by one transform,
    /// so moving that thing is a matter of rewriting the transform rather than rebuilding
    /// the geometry. The room never moves and is always part zero.
    /// </remarks>
    public int Part { get; init; }
}

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

    private readonly List<AccelerationStructureKHR> _parts = [];
    private readonly List<AccelerationStructureInstanceKHR> _instances = [];
    private readonly Dictionary<int, int> _instanceOf = [];

    private VulkanBuffer? _instanceBuffer;
    private AccelerationStructureKHR _topLevel;
    private bool _moved;

    private RayTracingScene(VulkanContext context, KhrAccelerationStructure api)
    {
        _context = context;
        _api = api;
    }

    /// <summary>The structure a shader binds.</summary>
    public AccelerationStructureKHR Handle => _topLevel;

    /// <summary>How many triangles it holds.</summary>
    public int TriangleCount { get; private set; }

    /// <summary>How many separately movable things it holds, the room among them.</summary>
    public int PartCount => _parts.Count;

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

        if (!context.Api.TryGetDeviceExtension(
                context.Instance, context.Device, out KhrAccelerationStructure api))
        {
            return null;
        }

        // Grouped by the thing that moves. Everything in a group goes into one structure
        // and is placed by one transform, so a character walking is a transform rewrite
        // rather than a rebuild of ten thousand triangles.
        var parts = new SortedDictionary<int, (List<Vector3> Positions, List<uint> Indices)>();

        foreach (RayTracingMesh mesh in meshes)
        {
            if (!parts.TryGetValue(mesh.Part, out (List<Vector3>, List<uint>) group))
            {
                group = ([], []);
                parts[mesh.Part] = group;
            }

            uint offset = (uint)group.Item1.Count;
            group.Item1.AddRange(mesh.Positions);

            foreach (uint index in mesh.Indices)
            {
                group.Item2.Add(index + offset);
            }
        }

        int triangles = parts.Values.Sum(g => g.Indices.Count) / 3;

        if (triangles == 0)
        {
            return null;
        }

        var scene = new RayTracingScene(context, api) { TriangleCount = triangles };

        try
        {
            foreach ((int part, (List<Vector3> positions, List<uint> indices)) in parts)
            {
                if (indices.Count < 3)
                {
                    continue;
                }

                scene._instanceOf[part] = scene._parts.Count;

                scene._parts.Add(scene.BuildBottomLevel(
                    CollectionsMarshal.AsSpan(positions), CollectionsMarshal.AsSpan(indices)));

                scene._instances.Add(new AccelerationStructureInstanceKHR
                {
                    Transform = Identity(),
                    InstanceCustomIndex = 0,
                    Mask = 0xFF,
                    InstanceShaderBindingTableRecordOffset = 0,
                    Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr,
                    AccelerationStructureReference = scene.DeviceAddressOf(scene._parts[^1]),
                });
            }

            scene.BuildTopLevel();
            return scene;
        }
        catch
        {
            scene.Dispose();
            throw;
        }
    }

    /// <summary>Moves one of the things the structure holds.</summary>
    /// <param name="part">Which thing, as <see cref="RayTracingMesh.Part"/> named it.</param>
    /// <param name="transform">Where it is now, in world space.</param>
    /// <remarks>
    /// Recorded rather than applied: several models may move in a frame and the structure
    /// only has to be right by the time something traces against it. <see cref="Settle"/>
    /// is what makes it so.
    /// </remarks>
    public void Move(int part, Matrix4x4 transform)
    {
        if (!_instanceOf.TryGetValue(part, out int at))
        {
            return;
        }

        TransformMatrixKHR rows = RowsOf(transform);

        // A struct in a list, so it has to go back rather than be edited in place.
        AccelerationStructureInstanceKHR instance = _instances[at];

        _instances[at] = instance with { Transform = rows };
        _moved = true;
    }

    /// <summary>Rebuilds the top level if anything has moved since it last was.</summary>
    /// <remarks>
    /// Only the top level: the geometry inside each thing has not changed, only where the
    /// thing is, and a rebuild over a few dozen instances is nothing beside one over the
    /// room's ten thousand triangles. Called once a frame before anything traces.
    /// </remarks>
    public void Settle()
    {
        if (!_moved || _instanceBuffer is null)
        {
            return;
        }

        _moved = false;

        _instanceBuffer.Write<AccelerationStructureInstanceKHR>(
            CollectionsMarshal.AsSpan(_instances));

        // The structure is rebuilt rather than refitted. Refitting is quicker and is only
        // sound for small movements; a character crossing a room is not one.
        if (_topLevel.Handle != 0)
        {
            _api.DestroyAccelerationStructure(_context.Device, _topLevel, null);
            _topLevel = default;
        }

        BuildTopLevel(reuseInstances: true);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_topLevel.Handle != 0)
        {
            _api.DestroyAccelerationStructure(_context.Device, _topLevel, null);
            _topLevel = default;
        }

        foreach (AccelerationStructureKHR part in _parts)
        {
            if (part.Handle != 0)
            {
                _api.DestroyAccelerationStructure(_context.Device, part, null);
            }
        }

        _parts.Clear();

        foreach (VulkanBuffer buffer in _buffers)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
        _api.Dispose();
    }

    private AccelerationStructureKHR BuildBottomLevel(
        ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)
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

        return Create(build, primitives, AccelerationStructureTypeKHR.BottomLevelKhr);
    }

    private void BuildTopLevel(bool reuseInstances = false)
    {
        if (!reuseInstances)
        {
            // Host visible and kept, because moving something rewrites it every time that
            // thing moves and a device-local copy would need a staging pass a frame.
            _instanceBuffer = VulkanBuffer.CreateHostVisible(
                _context,
                (ulong)(sizeof(AccelerationStructureInstanceKHR) * Math.Max(1, _instances.Count)),
                BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
                addressable: true);

            _instanceBuffer.Write<AccelerationStructureInstanceKHR>(
                CollectionsMarshal.AsSpan(_instances));

            _buffers.Add(_instanceBuffer);
        }

        VulkanBuffer instances = _instanceBuffer!;

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

        _topLevel = Create(build, (uint)_instances.Count, AccelerationStructureTypeKHR.TopLevelKhr);
    }

    /// <summary>The first three rows of a transform, which is what an instance carries.</summary>
    /// <remarks>
    /// Row-major and three rows deep, where <see cref="Matrix4x4"/> is row-vector and four:
    /// the translation that sits in the fourth row there belongs in the fourth column here.
    /// Transposing is the whole of the conversion, and getting it wrong puts a shadow
    /// somewhere plausible rather than nowhere, which is worse.
    /// </remarks>
    private static TransformMatrixKHR RowsOf(Matrix4x4 transform)
    {
        var rows = new TransformMatrixKHR();

        rows.Matrix[0] = transform.M11;
        rows.Matrix[1] = transform.M21;
        rows.Matrix[2] = transform.M31;
        rows.Matrix[3] = transform.M41;

        rows.Matrix[4] = transform.M12;
        rows.Matrix[5] = transform.M22;
        rows.Matrix[6] = transform.M32;
        rows.Matrix[7] = transform.M42;

        rows.Matrix[8] = transform.M13;
        rows.Matrix[9] = transform.M23;
        rows.Matrix[10] = transform.M33;
        rows.Matrix[11] = transform.M43;

        return rows;
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
