using System.Numerics;
using System.Runtime.CompilerServices;
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

    /// <summary>What names this geometry when its shape changes, or minus one.</summary>
    /// <remarks>
    /// A character has no skeleton: an <c>.ACT</c> clip rewrites its vertices outright,
    /// every frame of every animation. The structure has to be given those vertices or it
    /// goes on holding the pose the model was authored in, and rays leaving an animated
    /// shoulder start inside a rest-pose body.
    /// </remarks>
    public int Key { get; init; } = -1;
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

    private readonly List<Part> _parts = [];
    private readonly List<AccelerationStructureInstanceKHR> _instances = [];
    private readonly Dictionary<int, int> _instanceOf = [];

    /// <summary>Where each reshapeable mesh's vertices sit, by the key that names it.</summary>
    private readonly Dictionary<int, (int Part, int Offset, int Count)> _shapes = [];

    /// <summary>Parts whose vertices have been rewritten since they were last built.</summary>
    private readonly HashSet<int> _reshaped = [];

    private AccelerationStructureGeometryKHR _topLevelGeometry;
    private VulkanBuffer? _instanceBuffer;
    private Structure _topLevelStructure;
    private uint _topLevelPrimitives;
    private bool _moved;

    private RayTracingScene(VulkanContext context, KhrAccelerationStructure api)
    {
        _context = context;
        _api = api;
    }

    /// <summary>The structure a shader binds.</summary>
    public AccelerationStructureKHR Handle => _topLevelStructure.Handle;

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
        var shapes = new Dictionary<int, (int Part, int Offset, int Count)>();

        foreach (RayTracingMesh mesh in meshes)
        {
            if (!parts.TryGetValue(mesh.Part, out (List<Vector3>, List<uint>) group))
            {
                group = ([], []);
                parts[mesh.Part] = group;
            }

            uint offset = (uint)group.Item1.Count;

            if (mesh.Key >= 0)
            {
                shapes[mesh.Key] = (mesh.Part, (int)offset, mesh.Positions.Length);
            }

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

        foreach ((int key, (int part, int offset, int count)) in shapes)
        {
            scene._shapes[key] = (part, offset, count);
        }

        try
        {
            foreach ((int part, (List<Vector3> positions, List<uint> indices)) in parts)
            {
                if (indices.Count < 3)
                {
                    continue;
                }

                scene._instanceOf[part] = scene._parts.Count;

                // Everything but the room may be posed, and posing rewrites vertices, so
                // those parts keep their vertices where the host can write them.
                scene._parts.Add(scene.BuildBottomLevel(
                    [.. positions], CollectionsMarshal.AsSpan(indices), part != 0));

                scene._instances.Add(new AccelerationStructureInstanceKHR
                {
                    Transform = Identity(),
                    InstanceCustomIndex = 0,
                    Mask = MaskFor(part),
                    InstanceShaderBindingTableRecordOffset = 0,
                    Flags = FacingOf(part),
                    AccelerationStructureReference =
                        scene.DeviceAddressOf(scene._parts[^1].Structure.Handle),
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

    /// <summary>Takes one of the things the structure holds in or out of the world.</summary>
    /// <param name="part">Which thing, as <see cref="RayTracingMesh.Part"/> named it.</param>
    /// <param name="traced">Whether rays may hit it.</param>
    /// <remarks>
    /// The instance stays and its mask goes to nothing, which is what an instance mask is
    /// for: rebuilding the structure without it would renumber everything else. A model a
    /// script has hidden must not be traced or the room grows the shadow of something
    /// nobody can see — RC1's moped waits out of sight for a scripted drive-past, and its
    /// shadow would be lying on the square the whole time.
    /// </remarks>
    public void SetTraced(int part, bool traced)
    {
        if (!_instanceOf.TryGetValue(part, out int at))
        {
            return;
        }

        AccelerationStructureInstanceKHR instance = _instances[at];
        uint mask = traced ? MaskFor(part) : 0u;

        if (instance.Mask == mask)
        {
            return;
        }

        _instances[at] = instance with { Mask = mask };
        _moved = true;
    }

    /// <summary>The room's own geometry, for a ray that wants to skip what stands in it.</summary>
    /// <remarks>
    /// Part zero is the room. Everything else is a model placed in it — a character, a
    /// prop — and a model is the thing a shadow ray must be able to ignore.
    /// </remarks>
    public const uint WorldMask = 0x01;

    /// <summary>The models standing in the room.</summary>
    public const uint ModelMask = 0x02;

    /// <summary>
    /// Which mask an instance carries.
    /// </summary>
    /// <param name="part">The part key; zero is the room.</param>
    /// <returns>The mask.</returns>
    /// <remarks>
    /// <para>
    /// Split so that a shadow ray leaving a character can trace the room and nothing else.
    /// <b>GK3's people are not solid bodies.</b> A character is a dozen separate meshes —
    /// a shirt shell with a torso inside it, arms passing through sleeves — so a ray
    /// leaving the shirt towards a lamp hits the arm underneath it before it has gone
    /// anywhere. Every character in every room came out with a hard dark patch across the
    /// chest and the small of the back, fully shadowed and fully occluded, whatever the
    /// lighting was doing.
    /// </para>
    /// <para>
    /// No bias fixes it, because the geometry the ray hits is genuinely inside the surface
    /// it started from. Skipping models entirely, from a model, is what does — and it costs
    /// only the shadow one character would cast on another. A ray leaving the <em>room</em>
    /// still traces everything, so a character standing in the lobby still lays a shadow on
    /// the floor.
    /// </para>
    /// </remarks>
    public static uint MaskFor(int part) => part == 0 ? WorldMask : ModelMask;

    /// <summary>Whether a part's triangles may be told apart by which side they are met from.</summary>
    /// <param name="part">The part key; zero is the room.</param>
    /// <returns>The instance's facing flags.</returns>
    /// <remarks>
    /// <para>
    /// A model keeps its winding, so a ray may cull the faces it meets from within. That is
    /// what lets a character shadow itself: a person is a stack of overlapping shells and
    /// the only thing separating "this shell is around me" from "this arm is in my light"
    /// is which side of the triangle the ray arrives at. See the trace stage's kSkipShells.
    /// </para>
    /// <para>
    /// The room does not, and nothing asks it to. A BSP's polygons carry no consistent
    /// winding — each triangle is given its own plane's normal at load, which is exactly
    /// the admission that the file does not say — so a room triangle's two sides are not
    /// distinguishable and disabling the test is the honest reading. Every ray that traces
    /// the room today asks for no culling anyway, so this changes nothing for it.
    /// </para>
    /// </remarks>
    private static GeometryInstanceFlagsKHR FacingOf(int part) => part == 0
        ? GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr
        : 0;

    /// <summary>Gives a mesh the vertices it is currently drawn with.</summary>
    /// <param name="key">Which mesh, as <see cref="RayTracingMesh.Key"/> named it.</param>
    /// <param name="positions">Its vertices now, in the model's own space.</param>
    /// <remarks>
    /// Recorded rather than applied, like <see cref="Move"/>: several meshes of one
    /// character may be posed in a frame and the structure only has to be right by the
    /// time something traces against it.
    /// </remarks>
    public void Reshape(int key, ReadOnlySpan<Vector3> positions)
    {
        if (!_shapes.TryGetValue(key, out (int Part, int Offset, int Count) at) ||
            positions.Length != at.Count ||
            !_instanceOf.TryGetValue(at.Part, out int index))
        {
            return;
        }

        Part part = _parts[index];

        if (!part.Rewritable)
        {
            return;
        }

        positions.CopyTo(part.Positions.AsSpan(at.Offset, at.Count));
        _reshaped.Add(index);
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

        // Every rebuild this frame in one submission: a posed character is two or three
        // structures and the top level, and waiting on each in turn would cost more than
        // all of them together.
        CommandBuffer command = _context.BeginOneShot();

        foreach (int index in _reshaped)
        {
            Part part = _parts[index];

            part.Vertices.Write<Vector3>(part.Positions);
            Rebuild(command, part.Build(), part.Primitives, part.Structure);
        }

        if (_reshaped.Count > 0)
        {
            // The top level reads what those builds wrote.
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.AccelerationStructureWriteBitKhr,
                DstAccessMask = AccessFlags.AccelerationStructureReadBitKhr,
            };

            _context.Api.CmdPipelineBarrier(
                command,
                PipelineStageFlags.AccelerationStructureBuildBitKhr,
                PipelineStageFlags.AccelerationStructureBuildBitKhr,
                0, 1, in barrier, 0, null, 0, null);

            _reshaped.Clear();
        }

        // Rebuilt rather than refitted. Refitting is quicker and is only sound for small
        // movements; a character crossing a room is not one. The structure itself is
        // reused: the instance count never changes, so neither does the size of it, and
        // creating a new one every frame anything moved used to leak both its buffers.
        Rebuild(command, TopLevelBuild(), _topLevelPrimitives, _topLevelStructure);
        _context.EndOneShot(command);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Release(_topLevelStructure);
        _topLevelStructure = default;

        foreach (Part part in _parts)
        {
            Release(part.Structure);
            part.Vertices.Dispose();
            part.Indices.Dispose();
        }

        _parts.Clear();

        foreach (VulkanBuffer buffer in _buffers)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
        _api.Dispose();
    }

    private Part BuildBottomLevel(
        Vector3[] positions, ReadOnlySpan<uint> indices, bool rewritable)
    {
        const BufferUsageFlags InputUsage =
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr |
            BufferUsageFlags.ShaderDeviceAddressBit;

        // Host visible only where something may rewrite it. The room is most of the
        // triangles in a scene and never changes shape, and device-local memory is where
        // a build wants to read from.
        VulkanBuffer vertices = rewritable
            ? VulkanBuffer.CreateHostVisible(
                _context, (ulong)(positions.Length * sizeof(Vector3)), InputUsage, addressable: true)
            : VulkanBuffer.CreateDeviceLocal<Vector3>(_context, positions, InputUsage);

        if (rewritable)
        {
            vertices.Write<Vector3>(positions);
        }

        VulkanBuffer triangles = VulkanBuffer.CreateDeviceLocal(_context, indices, InputUsage);

        var part = new Part
        {
            Vertices = vertices,
            Indices = triangles,
            Positions = positions,
            Primitives = (uint)(indices.Length / 3),
            Rewritable = rewritable,
        };

        part.Structure = Create(
            part.Build(), part.Primitives, AccelerationStructureTypeKHR.BottomLevelKhr);

        return part;
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

        _topLevelPrimitives = (uint)_instances.Count;
        _topLevelStructure = Create(
            TopLevelBuild(), _topLevelPrimitives, AccelerationStructureTypeKHR.TopLevelKhr);
    }

    private AccelerationStructureBuildGeometryInfoKHR TopLevelBuild()
    {
        // The geometry description has to outlive this call, because the build reads it.
        // One per scene, kept alongside the instance buffer it points at.
        _topLevelGeometry = new AccelerationStructureGeometryKHR
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
                    Data = new DeviceOrHostAddressConstKHR
                    {
                        DeviceAddress = _instanceBuffer!.DeviceAddress,
                    },
                },
            },
        };

        return new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            GeometryCount = 1,
            PGeometries = (AccelerationStructureGeometryKHR*)Unsafe.AsPointer(ref _topLevelGeometry),
        };
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
    /// <remarks>
    /// The scratch is kept rather than freed. It is only needed while a build runs, but a
    /// structure whose geometry can be rewritten is rebuilt every frame that geometry
    /// moves, and allocating scratch for each of those would cost more than holding it.
    /// </remarks>
    private Structure Create(
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

        var createInfo = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = storage.Handle,
            Size = sizes.AccelerationStructureSize,
            Type = type,
        };

        if (_api.CreateAccelerationStructure(
                _context.Device, in createInfo, null, out AccelerationStructureKHR handle)
            != Result.Success)
        {
            storage.Dispose();
            throw new VulkanException($"Could not create a {type} acceleration structure.");
        }

        VulkanBuffer scratch = VulkanBuffer.CreateEmpty(
            _context,
            Math.Max(sizes.BuildScratchSize, 1),
            BufferUsageFlags.StorageBufferBit,
            addressable: true);

        var structure = new Structure(handle, storage, scratch);

        CommandBuffer command = _context.BeginOneShot();
        Rebuild(command, build, primitives, structure);
        _context.EndOneShot(command);

        return structure;
    }

    /// <summary>Builds into a structure that already exists.</summary>
    /// <remarks>
    /// A full build rather than a refit, and into the same memory: the geometry's shape
    /// and count do not change when a character is posed, only where its vertices are, so
    /// nothing about the structure needs to be a different size.
    /// </remarks>
    private void Rebuild(
        CommandBuffer command,
        AccelerationStructureBuildGeometryInfoKHR build,
        uint primitives,
        Structure structure)
    {
        build.DstAccelerationStructure = structure.Handle;
        build.ScratchData = new DeviceOrHostAddressKHR
        {
            DeviceAddress = structure.Scratch.DeviceAddress,
        };

        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = primitives };
        AccelerationStructureBuildRangeInfoKHR* ranges = &range;

        _api.CmdBuildAccelerationStructures(command, 1, in build, &ranges);
    }

    private void Release(Structure structure)
    {
        if (structure.Handle.Handle != 0)
        {
            _api.DestroyAccelerationStructure(_context.Device, structure.Handle, null);
        }

        structure.Storage?.Dispose();
        structure.Scratch?.Dispose();
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
    /// <summary>One structure and the two buffers it cannot live without.</summary>
    private readonly record struct Structure(
        AccelerationStructureKHR Handle, VulkanBuffer Storage, VulkanBuffer Scratch);

    /// <summary>One movable thing's geometry.</summary>
    private sealed class Part
    {
        public required VulkanBuffer Vertices { get; init; }

        public required VulkanBuffer Indices { get; init; }

        /// <summary>The vertices as the host last saw them, for rewriting a slice.</summary>
        public required Vector3[] Positions { get; init; }

        public required uint Primitives { get; init; }

        /// <summary>Whether anything may pose this, and so whether it is host visible.</summary>
        public required bool Rewritable { get; init; }

        public Structure Structure { get; set; }

        private AccelerationStructureGeometryKHR _geometry;

        /// <summary>Describes this geometry to a build.</summary>
        /// <returns>The description, pointing at storage this object owns.</returns>
        public AccelerationStructureBuildGeometryInfoKHR Build()
        {
            _geometry = new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.TrianglesKhr,

                // Opaque, because nothing here is alpha tested: keyed geometry never
                // reaches this point. Saying so lets the traversal skip any-hit entirely.
                Flags = GeometryFlagsKHR.OpaqueBitKhr,
                Geometry = new AccelerationStructureGeometryDataKHR
                {
                    Triangles = new AccelerationStructureGeometryTrianglesDataKHR
                    {
                        SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                        VertexFormat = Format.R32G32B32Sfloat,
                        VertexData = new DeviceOrHostAddressConstKHR
                        {
                            DeviceAddress = Vertices.DeviceAddress,
                        },
                        VertexStride = (ulong)sizeof(Vector3),
                        MaxVertex = (uint)(Positions.Length - 1),
                        IndexType = IndexType.Uint32,
                        IndexData = new DeviceOrHostAddressConstKHR
                        {
                            DeviceAddress = Indices.DeviceAddress,
                        },
                    },
                },
            };

            return new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.BottomLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                GeometryCount = 1,
                PGeometries =
                    (AccelerationStructureGeometryKHR*)Unsafe.AsPointer(ref _geometry),
            };
        }
    }

    private static TransformMatrixKHR Identity()
    {
        var transform = default(TransformMatrixKHR);

        transform.Matrix[0] = 1f;
        transform.Matrix[5] = 1f;
        transform.Matrix[10] = 1f;

        return transform;
    }
}
