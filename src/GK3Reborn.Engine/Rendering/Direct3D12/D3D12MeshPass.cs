using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Draws a room into the G-buffer.
/// </summary>
public sealed unsafe class D3D12MeshPass : IDisposable
{
    /// <summary>How many bytes one vertex takes in either stream.</summary>
    private static readonly uint VertexStride =
        (uint)System.Runtime.InteropServices.Marshal.SizeOf<MeshVertex>();

    private readonly D3D12Pipeline _pipeline;
    private readonly D3D12Pipeline _culled;
    private readonly D3D12Pipeline _culledMirror;
    private bool _reflection;
    private bool? _bound;
    private bool _disposed;

    private D3D12MeshPass(
        D3D12Pipeline pipeline, D3D12Pipeline culled, D3D12Pipeline culledMirror, bool rayTracing)
    {
        _pipeline = pipeline;
        _culled = culled;
        _culledMirror = culledMirror;
        RayTracing = rayTracing;
    }

    /// <summary>How many draws the last Record issued.</summary>
    public int Drawn { get; private set; }

    /// <summary>How many indices those draws covered.</summary>
    public uint Indices { get; private set; }

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
            new(3, Format.FormatR32G32Float, 32, 0),

            // The previous pose, and only its position: that is all the vertex shader
            // declares of it, and an element the shader has no input for is ignored.
            new(4, Format.FormatR32G32B32Float, 0, 1),
        ];

        // Three pipelines over one pair of shaders, differing in nothing but which faces
        // survive the rasteriser. The shader compiler caches by source, so the second and
        // third cost a pipeline object and no compilation at all.
        //
        // A draw says which it wants. Placed models take the first, because a grown tree's
        // leaf is a single sheet with no back; the room takes the second, because a GK3 room
        // is a shell of inward-facing surfaces and drawing their backs paints over what is
        // meant to be seen through them — R25's dumbwaiter, where the shaft's room-side
        // face is a solid sheet of lath with no hole cut for the door. The third is that one
        // again for the mirror pass, where the reflected view reverses every winding.
        D3D12Pipeline Build(CullMode cull, bool mirrored, D3D12RootSignature? reuse) =>
            D3D12Pipeline.CreateGraphics(
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
                cull: cull,
                frontCounterClockwise: mirrored,
                reuse: reuse);

        D3D12Pipeline pipeline = Build(CullMode.None, mirrored: false, reuse: null);
        D3D12Pipeline culled;
        D3D12Pipeline culledMirror;

        try
        {
            culled = Build(CullMode.Back, mirrored: false, reuse: pipeline.Signature);
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }

        try
        {
            culledMirror = Build(CullMode.Back, mirrored: true, reuse: pipeline.Signature);
        }
        catch
        {
            culled.Dispose();
            pipeline.Dispose();
            throw;
        }

        return new D3D12MeshPass(pipeline, culled, culledMirror, rayTracing);
    }

    /// <summary>Binds the pass, ready for the draws.</summary>
    /// <param name="list">The list to record into.</param>
    /// <param name="geometry">The device the materials were made on.</param>
    /// <param name="frame">Where the frame's own descriptors start.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <param name="reflection">
    /// Whether this is the mirror's pass. Its view is reflected, so a culled draw within it
    /// wants the opposite front face; nothing else about the pass changes.
    /// </param>
    public void Begin(
        ID3D12GraphicsCommandList4* list,
        D3D12GeometryDevice geometry,
        GpuDescriptorHandle frame,
        int width,
        int height,
        bool reflection = false)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(geometry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = geometry.Views.Handle;
        heaps[1] = geometry.Samplers.Handle;
        list->SetDescriptorHeaps(2, heaps);

        _reflection = reflection;
        _bound = null;

        list->SetGraphicsRootSignature(_pipeline.Signature.Handle);
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

        // The frame set's own one sampler: how a mirror reads the reflection drawn for it.
        // A combined image sampler is a texture and a sampler in Direct3D and they land in
        // different heaps, so declaring one in set 0 gives set 0 a sampler table — bound
        // here or the shader samples with whatever happens to be at that slot.
        int frameSamplers = _pipeline.Signature.SamplerParameterFor(MeshLayout.FrameSet);
        if (frameSamplers >= 0)
        {
            list->SetGraphicsRootDescriptorTable(
                (uint)frameSamplers, geometry.ReflectionSamplerTable);
        }

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
        Drawn = 0;
        Indices = 0;

        foreach (SceneDraw draw in draws)
        {
            // One state change per run of draws that agree rather than one per draw: the
            // room's own batches are built before any model is placed, so a frame normally
            // switches once. Nothing here sorts them, because a sort would be a decision and
            // this file makes none.
            if (_bound != draw.DoubleSided)
            {
                _bound = draw.DoubleSided;
                list->SetPipelineState(
                    draw.DoubleSided
                        ? _pipeline.Handle
                        : _reflection ? _culledMirror.Handle : _culled.Handle);
            }

            var material = (D3D12GeometryMaterial)draw.Material;

            list->SetGraphicsRootDescriptorTable(
                materialParameter, geometry.Views.Gpu(material.First));

            DrawConstants block = draw.Constants;
            list->SetGraphicsRoot32BitConstants(constants, DrawConstants.Words, &block, 0);

            streams[0] = Buffer(draw.Vertices).AsVertices(VertexStride);
            streams[1] = Buffer(draw.Previous).AsVertices(VertexStride);

            IndexBufferView indices = Buffer(draw.Indices).AsIndices(draw.ShortIndices);

            list->IASetVertexBuffers(0, 2, streams);
            list->IASetIndexBuffer(&indices);
            list->DrawIndexedInstanced(draw.IndexCount, 1, 0, 0, 0);
            Drawn++;
            Indices += draw.IndexCount;

            // Everything else about the draw is already bound, so a shell is one push and one
            // draw. That is what makes twelve of them affordable on a model.
            foreach (DrawConstants shell in draw.Shells)
            {
                DrawConstants over = shell;
                list->SetGraphicsRoot32BitConstants(constants, DrawConstants.Words, &over, 0);
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
        _culledMirror.Dispose();
        _culled.Dispose();
        _pipeline.Dispose();
    }

    private static D3D12Buffer Buffer(IGeometryBuffer buffer) =>
        buffer is D3D12GeometryBuffer direct
            ? direct.Buffer
            : throw new ArgumentException("That buffer is not on this device.", nameof(buffer));
}
