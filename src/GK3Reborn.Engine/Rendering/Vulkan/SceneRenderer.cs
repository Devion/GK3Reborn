using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Renders loaded scene geometry into an offscreen image.
/// </summary>
/// <remarks>
/// Headless by design. A render that needs no window runs on a build agent, produces a
/// file that can be compared between runs, and can be inspected without anyone watching
/// the screen at the right moment — none of which is true of a screenshot.
/// </remarks>
public sealed unsafe class SceneRenderer : IDisposable
{
    /// <summary>
    /// sRGB, not UNORM.
    /// </summary>
    /// <remarks>
    /// Textures decode to linear on sample and shading happens in linear space, so the
    /// target has to encode back on write. Writing linear values into a UNORM target and
    /// calling the result sRGB is what makes an otherwise correct render come out about a
    /// gamma too dark.
    /// </remarks>
    public const Format ColorFormat = Format.R8G8B8A8Srgb;

    /// <summary>Depth format used by the offscreen path and the swapchain alike.</summary>
    public const Format DepthFormat = Format.D32Sfloat;

    private readonly VulkanContext _context;
    private readonly ShaderCompiler _compiler;
    private readonly MeshPipeline _pipeline;
    private readonly FrameUniformSet _frames;

    private SceneRenderer(
        VulkanContext context, ShaderCompiler compiler, MeshPipeline pipeline, FrameUniformSet frames)
    {
        _context = context;
        _compiler = compiler;
        _pipeline = pipeline;
        _frames = frames;
    }

    /// <summary>The pipeline, for building geometry that matches it.</summary>
    public MeshPipeline Pipeline => _pipeline;

    /// <summary>Creates a renderer.</summary>
    /// <param name="context">Device context.</param>
    /// <returns>The renderer.</returns>
    public static SceneRenderer Create(VulkanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var compiler = new ShaderCompiler(Path.Combine(AppContext.BaseDirectory, "shader-cache"));

        try
        {
            MeshPipeline pipeline = MeshPipeline.Create(context, ColorFormat, DepthFormat, compiler);
            FrameUniformSet frames = FrameUniformSet.Create(context, pipeline, 1);
            return new SceneRenderer(context, compiler, pipeline, frames);
        }
        catch
        {
            compiler.Dispose();
            throw;
        }
    }

    /// <summary>Creates geometry this renderer can draw.</summary>
    /// <returns>Empty scene geometry.</returns>
    public SceneGeometry CreateGeometry() => SceneGeometry.Create(_context, _pipeline);

    /// <summary>Renders geometry and returns the image.</summary>
    /// <param name="geometry">What to draw.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="camera">Where to look from.</param>
    /// <returns>The rendered image.</returns>
    public DecodedImage Render(SceneGeometry geometry, int width, int height, Camera camera)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(camera);

        geometry.Finish();

        (Image color, DeviceMemory colorMemory, ImageView colorView) = CreateTarget(
            width, height, ColorFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            ImageAspectFlags.ColorBit);

        (Image depth, DeviceMemory depthMemory, ImageView depthView) = CreateTarget(
            width, height, DepthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit,
            ImageAspectFlags.DepthBit);

        try
        {
            CommandBuffer command = _context.BeginOneShot();

            _context.Transition(command, color, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);

            _context.Transition(
                command, depth, ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal,
                ImageAspectFlags.DepthBit);

            SceneDraw.Begin(
                _context.Api, command, colorView, depthView, width, height, camera.Background);

            SceneDraw.Record(
                _context.Api, command, _pipeline, _frames, geometry, 0, width, height, camera);

            _context.Api.CmdEndRendering(command);

            _context.Transition(
                command, color, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal);

            return ReadBack(command, color, width, height);
        }
        finally
        {
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
        _frames.Dispose();
        _pipeline.Dispose();
        _compiler.Dispose();
    }

    private DecodedImage ReadBack(CommandBuffer command, Image color, int width, int height)
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
                command, color, ImageLayout.TransferSrcOptimal, buffer, 1, in region);

            _context.EndOneShot(command);

            byte[] pixels = new byte[width * height * 4];
            void* mapped;
            _context.Api.MapMemory(_context.Device, memory, 0, (ulong)pixels.Length, 0, &mapped);
            new ReadOnlySpan<byte>(mapped, pixels.Length).CopyTo(pixels);
            _context.Api.UnmapMemory(_context.Device, memory);

            return new DecodedImage(width, height, pixels, HasAlpha: false, "vulkan");
        }
        finally
        {
            _context.Api.DestroyBuffer(_context.Device, buffer, null);
            _context.Api.FreeMemory(_context.Device, memory, null);
        }
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
}

/// <summary>
/// The recording steps a scene draw needs, independent of where it is drawn.
/// </summary>
/// <remarks>
/// Shared between the offscreen renderer and the windowed one so that what a regression
/// image shows and what a player sees cannot drift apart.
/// </remarks>
public static unsafe class SceneDraw
{
    /// <summary>Begins rendering into a colour and depth view.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="color">Colour view.</param>
    /// <param name="depth">Depth view.</param>
    /// <param name="width">Target width.</param>
    /// <param name="height">Target height.</param>
    /// <param name="background">Colour to clear to.</param>
    public static void Begin(
        Vk vk,
        CommandBuffer command,
        ImageView color,
        ImageView depth,
        int width,
        int height,
        Vector3 background)
    {
        ArgumentNullException.ThrowIfNull(vk);

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

        vk.CmdBeginRendering(command, in rendering);
    }

    /// <summary>Binds the pipeline and camera, then records the geometry's draws.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="pipeline">Pipeline to draw with.</param>
    /// <param name="frames">Per-frame camera resources.</param>
    /// <param name="geometry">What to draw.</param>
    /// <param name="frame">Which frame in flight this is.</param>
    /// <param name="width">Target width.</param>
    /// <param name="height">Target height.</param>
    /// <param name="camera">Where to look from.</param>
    public static void Record(
        Vk vk,
        CommandBuffer command,
        MeshPipeline pipeline,
        FrameUniformSet frames,
        SceneGeometry geometry,
        int frame,
        int width,
        int height,
        Camera camera)
    {
        ArgumentNullException.ThrowIfNull(vk);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(geometry);

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        vk.CmdSetViewport(command, 0, 1, in viewport);
        vk.CmdSetScissor(command, 0, 1, in scissor);
        vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, pipeline.Handle);

        frames.Bind(command, pipeline, frame, camera, (float)width / height);
        geometry.Record(command);
    }
}
