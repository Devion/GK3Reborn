using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Scenes;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>One drawable piece of a model: a mesh with one texture.</summary>
internal sealed class DrawBatch : IDisposable
{
    public required VulkanBuffer Vertices { get; init; }

    public required VulkanBuffer Indices { get; init; }

    public required uint IndexCount { get; init; }

    public required IndexType IndexType { get; init; }

    public required Matrix4x4 Transform { get; init; }

    public required string TextureName { get; init; }

    public required bool UseLightmap { get; init; }

    public void Dispose()
    {
        Vertices.Dispose();
        Indices.Dispose();
    }
}

/// <summary>
/// Renders GK3 models offscreen.
/// </summary>
/// <remarks>
/// <para>
/// The point where the content pipeline and the renderer meet: a parsed
/// <see cref="ModFile"/> and its decoded textures become vertex buffers, GPU textures and
/// draw calls. Nothing here goes through an intermediate format — the same parsers that
/// produce the glTF exports feed the renderer directly.
/// </para>
/// <para>
/// Each submesh becomes its own batch because each carries its own texture, and its
/// mesh's transform is applied per batch rather than being baked into the vertices, which
/// keeps the original hierarchy intact for animation later.
/// </para>
/// </remarks>
public sealed unsafe class ModelRenderer : IDisposable
{
    // sRGB, not UNORM. Textures decode to linear on sample and shading happens in linear
    // space, so the target has to encode back on write. Writing linear values into a UNORM
    // target and calling them sRGB is what makes an otherwise correct render look about a
    // gamma too dark.
    private const Format ColorFormat = Format.R8G8B8A8Srgb;
    private const Format DepthFormat = Format.D32Sfloat;

    private readonly VulkanContext _context;
    private readonly ShaderCompiler _compiler;
    private readonly MeshPipeline _pipeline;
    private readonly List<DrawBatch> _batches = [];
    private readonly Dictionary<string, VulkanTexture> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly VulkanTexture _fallbackTexture;
    private readonly VulkanTexture _whiteTexture;
    private DescriptorPool _descriptorPool;
    private VulkanTexture? _lightmap;
    private IReadOnlyList<Vector4>? _lightmapRegions;
    private Vector3 _minimum = new(float.MaxValue);
    private Vector3 _maximum = new(float.MinValue);

    private ModelRenderer(VulkanContext context, ShaderCompiler compiler, MeshPipeline pipeline)
    {
        _context = context;
        _compiler = compiler;
        _pipeline = pipeline;

        // A model referencing a texture the corpus does not contain still has to draw.
        // C2 found 14 such references among the models alone.
        _fallbackTexture = VulkanTexture.Create(context, CheckerBoard());

        // Bound wherever a batch has no lightmap. Vulkan requires every declared binding
        // to point at something valid even when the shader ignores what it reads.
        _whiteTexture = VulkanTexture.Create(context, Solid(255));
    }

    /// <summary>Total triangles queued.</summary>
    public int TriangleCount => _batches.Sum(b => (int)b.IndexCount / 3);

    /// <summary>Distinct textures uploaded.</summary>
    public int TextureCount => _textures.Count;

    /// <summary>Lower corner of everything queued, in world space.</summary>
    public Vector3 Minimum => _batches.Count > 0 ? _minimum : Vector3.Zero;

    /// <summary>Upper corner of everything queued, in world space.</summary>
    public Vector3 Maximum => _batches.Count > 0 ? _maximum : Vector3.One;

    /// <summary>Creates a renderer.</summary>
    /// <param name="context">Device context.</param>
    /// <returns>The renderer.</returns>
    public static ModelRenderer Create(VulkanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var compiler = new ShaderCompiler(Path.Combine(AppContext.BaseDirectory, "shader-cache"));

        try
        {
            MeshPipeline pipeline = MeshPipeline.Create(context, ColorFormat, DepthFormat, compiler);
            return new ModelRenderer(context, compiler, pipeline);
        }
        catch
        {
            compiler.Dispose();
            throw;
        }
    }

    /// <summary>Uploads a texture under a name models can reference.</summary>
    /// <param name="name">Texture name, matched case-insensitively.</param>
    /// <param name="image">The decoded image.</param>
    public void AddTexture(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_textures.ContainsKey(name))
        {
            // Keying happens before upload so that mip generation never sees the key
            // colour; see TextureKeying.
            _textures[name] = VulkanTexture.Create(_context, TextureKeying.Apply(image));
        }
    }

    /// <summary>Queues a model for drawing.</summary>
    /// <param name="model">The parsed model.</param>
    public void Add(ModFile model)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach (ModMesh mesh in model.Meshes)
        {
            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                if (submesh.Positions.Length == 0 || submesh.Indices.Length == 0)
                {
                    continue;
                }

                MeshVertex[] vertices = new MeshVertex[submesh.Positions.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = new MeshVertex(
                        submesh.Positions[i],
                        i < submesh.Normals.Length ? submesh.Normals[i] : Vector3.UnitY,
                        i < submesh.TexCoords.Length ? submesh.TexCoords[i] : Vector2.Zero,
                        Vector2.Zero);

                    // Bounds are accumulated in world space, since the camera that frames
                    // them works there and each mesh carries its own transform.
                    Vector3 world = Vector3.Transform(submesh.Positions[i], mesh.MeshToLocal);
                    _minimum = Vector3.Min(_minimum, world);
                    _maximum = Vector3.Max(_maximum, world);
                }

                _batches.Add(new DrawBatch
                {
                    Vertices = VulkanBuffer.CreateDeviceLocal<MeshVertex>(
                        _context, vertices, BufferUsageFlags.VertexBufferBit),
                    Indices = VulkanBuffer.CreateDeviceLocal<ushort>(
                        _context, submesh.Indices, BufferUsageFlags.IndexBufferBit),
                    IndexCount = (uint)submesh.Indices.Length,
                    IndexType = IndexType.Uint16,
                    Transform = mesh.MeshToLocal,
                    TextureName = submesh.TextureName,
                    UseLightmap = false,
                });
            }
        }
    }

    /// <summary>Queues a scene's geometry for drawing.</summary>
    /// <param name="scene">The parsed scene.</param>
    /// <param name="lightmaps">The scene's baked lightmaps, in surface order, if any.</param>
    /// <remarks>
    /// BSP files carry no normals, so each triangle gets the normal of its own plane.
    /// Flat shading is wrong for the curved surfaces a few scenes contain, but it is
    /// right for the walls, floors and doorways that make up nearly all of them, and it
    /// makes the geometry legible without inventing smoothing groups the data never had.
    /// </remarks>
    public void AddScene(BspFile scene, MulFile? lightmaps = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (lightmaps is not null)
        {
            LightmapAtlas atlas = LightmapAtlas.Pack(lightmaps.Lightmaps);

            // No mips and clamped addressing: both would sample across tile edges.
            _lightmap?.Dispose();
            _lightmap = VulkanTexture.Create(
                _context, atlas.Image, mipmaps: false, SamplerAddressMode.ClampToEdge);

            _lightmapRegions = atlas.Regions;
        }

        Dictionary<string, (List<MeshVertex> Vertices, List<uint> Indices)> groups =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (BspPolygon polygon in scene.Polygons)
        {
            if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= scene.Surfaces.Count)
            {
                continue;
            }

            BspSurface surface = scene.Surfaces[polygon.SurfaceIndex];
            string texture = surface.TextureName;

            Vector4 region = _lightmapRegions is not null && polygon.SurfaceIndex < _lightmapRegions.Count
                ? _lightmapRegions[polygon.SurfaceIndex]
                : Vector4.Zero;

            if (!groups.TryGetValue(texture, out (List<MeshVertex> Vertices, List<uint> Indices) group))
            {
                group = ([], []);
                groups[texture] = group;
            }

            foreach ((ushort a, ushort b, ushort c) in scene.Triangulate(polygon))
            {
                Vector3 pa = scene.Vertices[a];
                Vector3 pb = scene.Vertices[b];
                Vector3 pc = scene.Vertices[c];

                Vector3 normal = Vector3.Cross(pb - pa, pc - pa);
                normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;

                Vector2 ua = scene.TexCoordFor(a);
                Vector2 ub = scene.TexCoordFor(b);
                Vector2 uc = scene.TexCoordFor(c);

                uint at = (uint)group.Vertices.Count;
                group.Vertices.Add(new MeshVertex(pa, normal, ua, Lightmap(ua, surface, region)));
                group.Vertices.Add(new MeshVertex(pb, normal, ub, Lightmap(ub, surface, region)));
                group.Vertices.Add(new MeshVertex(pc, normal, uc, Lightmap(uc, surface, region)));
                group.Indices.Add(at);
                group.Indices.Add(at + 1);
                group.Indices.Add(at + 2);

                _minimum = Vector3.Min(_minimum, Vector3.Min(pa, Vector3.Min(pb, pc)));
                _maximum = Vector3.Max(_maximum, Vector3.Max(pa, Vector3.Max(pb, pc)));
            }
        }

        foreach ((string texture, (List<MeshVertex> vertices, List<uint> indices)) in groups)
        {
            if (indices.Count == 0)
            {
                continue;
            }

            _batches.Add(new DrawBatch
            {
                Vertices = VulkanBuffer.CreateDeviceLocal<MeshVertex>(
                    _context, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices),
                    BufferUsageFlags.VertexBufferBit),
                Indices = VulkanBuffer.CreateDeviceLocal<uint>(
                    _context, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices),
                    BufferUsageFlags.IndexBufferBit),
                IndexCount = (uint)indices.Count,

                // Scene batches routinely pass 65,535 vertices; a single wall texture in
                // the larger scenes covers more geometry than a 16-bit index can address.
                IndexType = IndexType.Uint32,
                Transform = Matrix4x4.Identity,
                TextureName = texture,
                UseLightmap = true,
            });
        }
    }

    /// <summary>Renders everything queued and returns the image.</summary>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="camera">Where to look from and at.</param>
    /// <returns>The rendered image.</returns>
    public DecodedImage Render(int width, int height, Camera camera)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(camera);

        Matrix4x4 view = camera.View;
        Matrix4x4 projection = camera.Projection((float)width / height);

        (Image color, DeviceMemory colorMemory, ImageView colorView) = CreateTarget(
            width, height, ColorFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            ImageAspectFlags.ColorBit);

        (Image depth, DeviceMemory depthMemory, ImageView depthView) = CreateTarget(
            width, height, DepthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit,
            ImageAspectFlags.DepthBit);

        List<VulkanBuffer> uniformBuffers = [];
        CreateDescriptorPool();

        try
        {
            CommandBuffer command = _context.BeginOneShot();

            _context.Transition(command, color, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);
            TransitionDepth(command, depth);

            BeginRendering(command, colorView, depthView, width, height, camera.Background);

            var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
            var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };
            _context.Api.CmdSetViewport(command, 0, 1, in viewport);
            _context.Api.CmdSetScissor(command, 0, 1, in scissor);
            _context.Api.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline.Handle);

            foreach (DrawBatch batch in _batches)
            {
                Matrix4x4 mvp = batch.Transform * view * projection;

                // The original multiplies texture by lightmap by two, in gamma space.
                // Doing the same multiplication in linear space needs the constant raised
                // to the gamma, or a fully lit surface comes out at about 70% of the
                // brightness the game showed.
                const float LightmapMultiplier = 4.59f;

                var shading = new Vector4(
                    _lightmap is not null && batch.UseLightmap ? 1f : 0f, LightmapMultiplier, 0, 0);

                var uniforms = new MeshUniforms(
                    mvp,
                    batch.Transform,
                    new Vector4(Vector3.Normalize(camera.LightDirection), 0),
                    shading);

                VulkanBuffer uniformBuffer = VulkanBuffer.CreateHostVisible(
                    _context, (ulong)System.Runtime.InteropServices.Marshal.SizeOf<MeshUniforms>(),
                    BufferUsageFlags.UniformBufferBit);

                uniformBuffer.Write<MeshUniforms>([uniforms]);
                uniformBuffers.Add(uniformBuffer);

                DescriptorSet set = AllocateDescriptorSet(
                    uniformBuffer,
                    TextureFor(batch.TextureName),
                    batch.UseLightmap ? _lightmap ?? _whiteTexture : _whiteTexture);

                _context.Api.CmdBindDescriptorSets(
                    command, PipelineBindPoint.Graphics, _pipeline.Layout, 0, 1, in set, 0, null);

                ulong offset = 0;
                Silk.NET.Vulkan.Buffer vertexBuffer = batch.Vertices.Handle;
                _context.Api.CmdBindVertexBuffers(command, 0, 1, in vertexBuffer, in offset);
                _context.Api.CmdBindIndexBuffer(command, batch.Indices.Handle, 0, batch.IndexType);
                _context.Api.CmdDrawIndexed(command, batch.IndexCount, 1, 0, 0, 0);
            }

            _context.Api.CmdEndRendering(command);

            _context.Transition(command, color, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal);

            (Silk.NET.Vulkan.Buffer readback, DeviceMemory readbackMemory) = CreateReadback(width, height);

            try
            {
                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D((uint)width, (uint)height, 1),
                };

                _context.Api.CmdCopyImageToBuffer(
                    command, color, ImageLayout.TransferSrcOptimal, readback, 1, in region);

                _context.EndOneShot(command);

                byte[] pixels = new byte[width * height * 4];
                void* mapped;
                _context.Api.MapMemory(_context.Device, readbackMemory, 0, (ulong)pixels.Length, 0, &mapped);
                new ReadOnlySpan<byte>(mapped, pixels.Length).CopyTo(pixels);
                _context.Api.UnmapMemory(_context.Device, readbackMemory);

                return new DecodedImage(width, height, pixels, HasAlpha: false, "vulkan-model");
            }
            finally
            {
                _context.Api.DestroyBuffer(_context.Device, readback, null);
                _context.Api.FreeMemory(_context.Device, readbackMemory, null);
            }
        }
        finally
        {
            foreach (VulkanBuffer buffer in uniformBuffers)
            {
                buffer.Dispose();
            }

            if (_descriptorPool.Handle != 0)
            {
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
                _descriptorPool = default;
            }

            _context.Api.DestroyImageView(_context.Device, depthView, null);
            _context.Api.DestroyImage(_context.Device, depth, null);
            _context.Api.FreeMemory(_context.Device, depthMemory, null);
            _context.Api.DestroyImageView(_context.Device, colorView, null);
            _context.Api.DestroyImage(_context.Device, color, null);
            _context.Api.FreeMemory(_context.Device, colorMemory, null);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _context.Api.DeviceWaitIdle(_context.Device);

        foreach (DrawBatch batch in _batches)
        {
            batch.Dispose();
        }

        foreach (VulkanTexture texture in _textures.Values)
        {
            texture.Dispose();
        }

        _fallbackTexture.Dispose();
        _whiteTexture.Dispose();
        _lightmap?.Dispose();
        _pipeline.Dispose();
        _compiler.Dispose();
    }

    /// <summary>Maps a surface's diffuse UV into the lightmap atlas.</summary>
    private static Vector2 Lightmap(Vector2 uv, BspSurface surface, Vector4 region)
    {
        // The original derives the lightmap coordinate as (uv + offset) * scale; see
        // GEngine's Uber.glsl. Clamping keeps a surface whose UVs stray outside the unit
        // square from reading a neighbouring tile once everything shares one atlas.
        Vector2 tile = (uv + surface.LightmapUvOffset) * surface.LightmapUvScale;

        return new Vector2(
            region.X + (Math.Clamp(tile.X, 0f, 1f) * region.Z),
            region.Y + (Math.Clamp(tile.Y, 0f, 1f) * region.W));
    }

    /// <summary>A one-pixel image of a single grey level.</summary>
    private static DecodedImage Solid(byte level) =>
        new(1, 1, [level, level, level, 255], HasAlpha: false, "solid");

    /// <summary>A visibly wrong texture, so a missing one is obvious rather than silent.</summary>
    private static DecodedImage CheckerBoard()
    {
        const int Size = 64;
        byte[] pixels = new byte[Size * Size * 4];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                bool light = ((x / 8) + (y / 8)) % 2 == 0;
                int at = ((y * Size) + x) * 4;

                pixels[at] = light ? (byte)220 : (byte)40;
                pixels[at + 1] = light ? (byte)40 : (byte)40;
                pixels[at + 2] = light ? (byte)220 : (byte)40;
                pixels[at + 3] = 255;
            }
        }

        return new DecodedImage(Size, Size, pixels, HasAlpha: false, "fallback");
    }

    private VulkanTexture TextureFor(string name) =>
        name.Length > 0 && _textures.TryGetValue(name, out VulkanTexture? texture)
            ? texture
            : _fallbackTexture;

    private void CreateDescriptorPool()
    {
        int sets = Math.Max(1, _batches.Count);

        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = (uint)sets,
        };
        sizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = (uint)(sets * 2),
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
            MaxSets = (uint)sets,
        };

        if (_context.Api.CreateDescriptorPool(_context.Device, in poolInfo, null, out _descriptorPool)
            != Result.Success)
        {
            throw new VulkanException("Could not create a descriptor pool.");
        }
    }

    private DescriptorSet AllocateDescriptorSet(
        VulkanBuffer uniforms, VulkanTexture texture, VulkanTexture lightmap)
    {
        DescriptorSetLayout layout = _pipeline.DescriptorLayout;
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };

        if (_context.Api.AllocateDescriptorSets(_context.Device, in allocateInfo, out DescriptorSet set)
            != Result.Success)
        {
            throw new VulkanException("Could not allocate a descriptor set.");
        }

        var bufferInfo = new DescriptorBufferInfo
        {
            Buffer = uniforms.Handle,
            Range = uniforms.Size,
        };

        var imageInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = texture.View,
            Sampler = texture.Sampler,
        };

        var lightmapInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = lightmap.View,
            Sampler = lightmap.Sampler,
        };

        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[3];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            PBufferInfo = &bufferInfo,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo,
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 2,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &lightmapInfo,
        };

        _context.Api.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
        return set;
    }

    private void BeginRendering(
        CommandBuffer command, ImageView color, ImageView depth, int width, int height, Vector3 background)
    {
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = color,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(background.X, background.Y, background.Z, 1f)),
        };

        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = depth,
            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)),
        };

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment,
        };

        _context.Api.CmdBeginRendering(command, in rendering);
    }

    private (Image, DeviceMemory, ImageView) CreateTarget(
        int width, int height, Format format, ImageUsageFlags usage, ImageAspectFlags aspect)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            InitialLayout = ImageLayout.Undefined,
        };

        if (_context.Api.CreateImage(_context.Device, in imageInfo, null, out Image image) != Result.Success)
        {
            throw new VulkanException("Could not create a render target.");
        }

        _context.Api.GetImageMemoryRequirements(_context.Device, image, out MemoryRequirements requirements);
        DeviceMemory memory = _context.Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit);
        _context.Api.BindImageMemory(_context.Device, image, memory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                LevelCount = 1,
                LayerCount = 1,
            },
        };

        if (_context.Api.CreateImageView(_context.Device, in viewInfo, null, out ImageView view) != Result.Success)
        {
            throw new VulkanException("Could not create a render target view.");
        }

        return (image, memory, view);
    }

    private void TransitionDepth(CommandBuffer command, Image image)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                LevelCount = 1,
                LayerCount = 1,
            },
            DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
        };

        _context.Api.CmdPipelineBarrier(
            command,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, in barrier);
    }

    private (Silk.NET.Vulkan.Buffer, DeviceMemory) CreateReadback(int width, int height)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)(width * height * 4),
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };

        _context.Api.CreateBuffer(_context.Device, in bufferInfo, null, out Silk.NET.Vulkan.Buffer buffer);
        _context.Api.GetBufferMemoryRequirements(_context.Device, buffer, out MemoryRequirements requirements);

        DeviceMemory memory = _context.Allocate(
            requirements, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        _context.Api.BindBufferMemory(_context.Device, buffer, memory, 0);
        return (buffer, memory);
    }
}
