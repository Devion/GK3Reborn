using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Scenes;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// The camera, as the shader sees it: one uniform buffer and its descriptor set per frame
/// in flight.
/// </summary>
/// <remarks>
/// A buffer per frame rather than one shared buffer, because the GPU may still be reading
/// the previous frame's camera when the next frame is recorded. Overwriting it there is
/// the classic cause of a view that jitters by exactly one frame under load — visible,
/// intermittent, and easy to blame on input handling instead.
/// </remarks>
public sealed unsafe class FrameUniformSet : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanBuffer[] _buffers;
    private readonly VulkanBuffer _rig;
    private readonly DescriptorSet[] _sets;
    private DescriptorPool _pool;

    private FrameUniformSet(
        VulkanContext context,
        VulkanBuffer[] buffers,
        VulkanBuffer rig,
        DescriptorSet[] sets,
        DescriptorPool pool)
    {
        _context = context;
        _buffers = buffers;
        _rig = rig;
        _sets = sets;
        _pool = pool;
    }

    /// <summary>How many frames it covers.</summary>
    public int Count => _sets.Length;

    /// <summary>Creates the set.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="pipeline">Pipeline whose frame layout to match.</param>
    /// <param name="frames">How many frames may be in flight.</param>
    /// <returns>The set.</returns>
    public static FrameUniformSet Create(VulkanContext context, MeshPipeline pipeline, int frames)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = (uint)(frames * 2),
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &size,
            MaxSets = (uint)frames,
        };

        if (context.Api.CreateDescriptorPool(context.Device, in poolInfo, null, out DescriptorPool pool)
            != Result.Success)
        {
            throw new VulkanException("Could not create the frame descriptor pool.");
        }

        var buffers = new VulkanBuffer[frames];
        var sets = new DescriptorSet[frames];

        // Allocated once outside the loop: a stackalloc inside one grows the frame with
        // every iteration.
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
        ulong bufferSize = (ulong)Marshal.SizeOf<FrameUniforms>();

        // One rig for every frame: the lights are fixed for as long as a scene is loaded,
        // so there is nothing for a later frame to race against.
        ulong rigSize = (ulong)(16 + (GpuLight.Capacity * Marshal.SizeOf<GpuLight>()));
        VulkanBuffer rig = VulkanBuffer.CreateHostVisible(
            context, rigSize, BufferUsageFlags.UniformBufferBit);

        for (int i = 0; i < frames; i++)
        {
            buffers[i] = VulkanBuffer.CreateHostVisible(
                context, bufferSize, BufferUsageFlags.UniformBufferBit);

            DescriptorSetLayout layout = pipeline.FrameLayout;
            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };

            if (context.Api.AllocateDescriptorSets(context.Device, in allocateInfo, out sets[i])
                != Result.Success)
            {
                throw new VulkanException("Could not allocate a frame descriptor set.");
            }

            var bufferInfo = new DescriptorBufferInfo
            {
                Buffer = buffers[i].Handle,
                Range = bufferSize,
            };

            var rigInfo = new DescriptorBufferInfo
            {
                Buffer = rig.Handle,
                Range = rigSize,
            };

            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = sets[i],
                DstBinding = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo = &bufferInfo,
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = sets[i],
                DstBinding = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo = &rigInfo,
            };

            context.Api.UpdateDescriptorSets(context.Device, 2, writes, 0, null);
        }

        var created = new FrameUniformSet(context, buffers, rig, sets, pool);
        created.SetLights([]);

        return created;
    }

    /// <summary>Uploads the lights a scene was authored with.</summary>
    /// <param name="lights">The rig; more than the shader holds are narrowed down.</param>
    public void SetLights(IReadOnlyList<AuthoredLight> lights)
    {
        ArgumentNullException.ThrowIfNull(lights);

        IReadOnlyList<AuthoredLight> chosen = GpuLight.Choose(lights);

        int stride = Marshal.SizeOf<GpuLight>();
        byte[] bytes = new byte[16 + (GpuLight.Capacity * stride)];

        // The count leads, padded to a float4 so the array that follows starts on the
        // 16-byte boundary the standard uniform layout requires.
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (float)chosen.Count);

        for (int i = 0; i < chosen.Count; i++)
        {
            GpuLight light = GpuLight.From(chosen[i]);
            MemoryMarshal.Write(bytes.AsSpan(16 + (i * stride), stride), in light);
        }

        _rig.Write<byte>(bytes);
    }

    /// <summary>Writes a frame's camera and binds its descriptor set.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="pipeline">Pipeline whose layout to bind against.</param>
    /// <param name="frame">Which frame in flight this is.</param>
    /// <param name="camera">The camera.</param>
    /// <param name="aspect">Viewport width divided by height.</param>
    public void Bind(
        CommandBuffer command, MeshPipeline pipeline, int frame, Camera camera, float aspect)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(camera);

        int index = frame % _sets.Length;

        var uniforms = new FrameUniforms(
            camera.View * camera.Projection(aspect),
            new Vector4(Vector3.Normalize(camera.LightDirection), 0),
            new Vector4(camera.Position, 1));

        _buffers[index].Write<FrameUniforms>([uniforms]);

        DescriptorSet set = _sets[index];
        _context.Api.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, pipeline.Layout, 0, 1, in set, 0, null);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (VulkanBuffer buffer in _buffers)
        {
            buffer.Dispose();
        }

        _rig.Dispose();

        if (_pool.Handle != 0)
        {
            _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
            _pool = default;
        }
    }
}
