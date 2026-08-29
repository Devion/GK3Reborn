using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Draws a room into the G-buffer.
/// </summary>
/// <remarks>
/// <para>
/// The Direct3D twin of <c>MeshPipeline</c>, and the shaders are not twins at all: they are
/// the same shaders. <c>MeshShaders.Compose</c> produces the GLSL, the compiler takes it
/// through SPIR-V and HLSL to DXIL, and what differs between the backends is which object
/// the result is put into.
/// </para>
/// <para>
/// Four colour targets and a depth: the lit picture, the normal and roughness, the motion
/// vector, and — with ray tracing compiled in — the direct light the tracing pass will
/// filter. All of them, always. A rendering scope that binds fewer attachments than its
/// pipeline writes is not a smaller frame, it is undefined behaviour, and Direct3D says so
/// only if the debug layer is on.
/// </para>
/// <para>
/// The vertex layout is two streams of the same shape: this pose and the one before it.
/// SPIRV-Cross has no name to give a GLSL vertex input but <c>TEXCOORD</c> plus its location,
/// so every element is a TEXCOORD and the semantic index is the location — see
/// <see cref="ShaderBindings.VertexInputSemantic"/>. Eight attributes, four per stream, and a
/// stride that disagreed with the shader would not fail: it would read the previous pose from
/// halfway through a vertex and report movement nothing made.
/// </para>
/// </remarks>
public sealed unsafe class D3D12MeshPass : IDisposable
{
    /// <summary>How many bytes one vertex takes in either stream.</summary>
    private const uint VertexStride = 32;

    private readonly D3D12Pipeline _pipeline;
    private bool _disposed;

    private D3D12MeshPass(D3D12Pipeline pipeline, bool rayTracing)
    {
        _pipeline = pipeline;
        RayTracing = rayTracing;
    }

    /// <summary>Whether the ray-tracing paths are compiled into these shaders.</summary>
    public bool RayTracing { get; }

    /// <summary>The root signature the frame and the materials bind through.</summary>
    public D3D12RootSignature Signature => _pipeline.Signature;

    /// <summary>Builds the pass.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Where the shaders come from.</param>
    /// <param name="colorFormats">What the colour targets hold, the picture first.</param>
    /// <param name="depthFormat">What the depth target holds.</param>
    /// <param name="rayTracing">Whether to compile the ray-tracing paths in.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="D3D12Exception">The pipeline could not be created.</exception>
    public static D3D12MeshPass Create(
        D3D12Context context,
        ShaderCompiler compiler,
        IReadOnlyList<Format> colorFormats,
        Format depthFormat,
        bool rayTracing)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(colorFormats);

        // Four attributes a stream, twice: position, normal, texture coordinate, lightmap
        // coordinate for this pose, then the same four for the previous one. The locations
        // are what the shader declares and what SPIRV-Cross turned into semantic indices.
        VertexInput[] attributes =
        [
            new(0, Format.FormatR32G32B32Float, 0, 0),
            new(1, Format.FormatR32G32B32Float, 12, 0),
            new(2, Format.FormatR32G32Float, 24, 0),
            new(3, Format.FormatR32G32Float, 32 - 8, 0),

            new(4, Format.FormatR32G32B32Float, 0, 1),
            new(5, Format.FormatR32G32B32Float, 12, 1),
            new(6, Format.FormatR32G32Float, 24, 1),
            new(7, Format.FormatR32G32Float, 32 - 8, 1),
        ];

        D3D12Pipeline pipeline = D3D12Pipeline.CreateGraphics(
            context.Device,
            compiler,
            MeshShaders.Compose(fragment: false, rayTracing),
            MeshShaders.Compose(fragment: true, rayTracing),
            rayTracing ? "mesh.rt" : "mesh",
            MeshLayout.For(rayTracing),
            colorFormats,
            depthFormat,
            attributes,
            [new VertexBufferLayout(VertexStride), new VertexBufferLayout(VertexStride)],
            ShaderLanguage.Glsl,
            depthWrite: true,
            depthTest: true,

            // GK3's world is left-handed and its scenes were authored for Direct3D, so a
            // front face is clockwise and the back of one is what is thrown away.
            cull: CullMode.Back);

        return new D3D12MeshPass(pipeline, rayTracing);
    }

    /// <summary>Binds the pass, ready for the draws.</summary>
    /// <param name="list">The list to record into.</param>
    /// <param name="geometry">The device the materials were made on.</param>
    /// <param name="frame">Where the frame's own descriptors start.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <remarks>
    /// The heaps are bound here rather than per draw. Direct3D allows one shader-visible
    /// heap of each kind at a time and changing either is a pipeline flush on some hardware,
    /// so the whole room draws out of the one heap its materials were allocated from.
    /// </remarks>
    public void Begin(
        ID3D12GraphicsCommandList4* list,
        D3D12GeometryDevice geometry,
        GpuDescriptorHandle frame,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(geometry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = geometry.Views.Handle;
        heaps[1] = geometry.Samplers.Handle;
        list->SetDescriptorHeaps(2, heaps);

        list->SetGraphicsRootSignature(_pipeline.Signature.Handle);
        list->SetPipelineState(_pipeline.Handle);
        list->IASetPrimitiveTopology(Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        var viewport = new Viewport
        {
            TopLeftX = 0f,
            TopLeftY = 0f,
            Width = width,
            Height = height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };

        var scissor = new Box2D<int>(0, 0, width, height);
        list->RSSetViewports(1, &viewport);
        list->RSSetScissorRects(1, &scissor);

        list->SetGraphicsRootDescriptorTable(
            (uint)_pipeline.Signature.ParameterFor(MeshLayout.FrameSet), frame);

        // One run of samplers for every material in the game; see D3D12GeometryDevice.
        int samplers = _pipeline.Signature.SamplerParameterFor(MeshLayout.MaterialSet);
        if (samplers >= 0)
        {
            list->SetGraphicsRootDescriptorTable((uint)samplers, geometry.SamplerTable);
        }
    }

    /// <summary>Issues the draws a scene worked out.</summary>
    /// <param name="list">The list to record into, already bound by <see cref="Begin"/>.</param>
    /// <param name="geometry">The device the materials were made on.</param>
    /// <param name="draws">What to draw, from <c>SceneGeometry.Draws</c>.</param>
    /// <remarks>
    /// Nothing here decides anything. Which pose is current, whether the lightmap applies and
    /// how many shells of fur stand over a skin were all settled before the draws arrived,
    /// which is what lets the same reasoning serve the Vulkan backend.
    /// </remarks>
    public void Record(
        ID3D12GraphicsCommandList4* list,
        D3D12GeometryDevice geometry,
        IEnumerable<SceneDraw> draws)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(draws);
        ObjectDisposedException.ThrowIf(_disposed, this);

        uint materialParameter = (uint)_pipeline.Signature.ParameterFor(MeshLayout.MaterialSet);
        uint constants = (uint)_pipeline.Signature.PushConstantParameter;

        VertexBufferView* streams = stackalloc VertexBufferView[2];

        foreach (SceneDraw draw in draws)
        {
            var material = (D3D12GeometryMaterial)draw.Material;

            list->SetGraphicsRootDescriptorTable(
                materialParameter, geometry.Views.Gpu(material.First));

            DrawConstants block = draw.Constants;
            list->SetGraphicsRoot32BitConstants(constants, 48, &block, 0);

            streams[0] = Buffer(draw.Vertices).AsVertices(VertexStride);
            streams[1] = Buffer(draw.Previous).AsVertices(VertexStride);

            IndexBufferView indices = Buffer(draw.Indices).AsIndices(draw.ShortIndices);

            list->IASetVertexBuffers(0, 2, streams);
            list->IASetIndexBuffer(&indices);
            list->DrawIndexedInstanced(draw.IndexCount, 1, 0, 0, 0);

            // Everything else about the draw is already bound, so a shell is one push and one
            // draw. That is what makes twelve of them affordable on a model.
            foreach (DrawConstants shell in draw.Shells)
            {
                DrawConstants over = shell;
                list->SetGraphicsRoot32BitConstants(constants, 48, &over, 0);
                list->DrawIndexedInstanced(draw.IndexCount, 1, 0, 0, 0);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pipeline.Dispose();
    }

    private static D3D12Buffer Buffer(IGeometryBuffer buffer) =>
        buffer is D3D12GeometryBuffer direct
            ? direct.Buffer
            : throw new ArgumentException("That buffer is not on this device.", nameof(buffer));
}
