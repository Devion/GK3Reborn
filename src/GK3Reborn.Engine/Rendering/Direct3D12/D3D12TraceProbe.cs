using System.Numerics;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Traces a grid of rays past a known obstacle and says which of them were blocked.
/// </summary>
/// <remarks>
/// <para>
/// The ray-tracing counterpart of <see cref="D3D12OffscreenRenderer"/>, and it exists for
/// the same reason with more force. Everything that makes inline ray tracing work on this
/// backend is somebody's guess until it is looked at: whether SPIRV-Cross turns
/// <c>rayQueryEXT</c> into a <c>RayQuery</c> that behaves the same way, whether an
/// acceleration structure built from this engine's matrices ends up where the geometry
/// actually is, whether an acceleration structure binds correctly as a shader resource view
/// made from an address rather than a resource. A shader that compiles proves none of them,
/// and every one of them fails as a plausible wrong picture rather than as an error.
/// </para>
/// <para>
/// So the probe is arranged to give an answer with a shape. A square blocker floats above a
/// square grid of upward rays, and what comes back is the blocker's shadow: not a number
/// that could be anything, but a pattern that is right in the middle or it is wrong.
/// </para>
/// </remarks>
public sealed unsafe class D3D12TraceProbe : IDisposable
{
    /// <summary>How many rays on a side.</summary>
    public const int Side = 16;

    /// <summary>The shader, in GLSL, as every ray-traced shader in the engine is.</summary>
    private const string Source = """
        #version 460
        #extension GL_EXT_ray_query : require

        layout(local_size_x = 8, local_size_y = 8) in;

        layout(set = 0, binding = 0) uniform accelerationStructureEXT scene;
        layout(set = 0, binding = 1, r32f) uniform writeonly image2D occlusion;

        void main()
        {
            ivec2 at = ivec2(gl_GlobalInvocationID.xy);
            if (at.x >= 16 || at.y >= 16) { return; }

            // One ray a cell, straight up, over a patch of world from -7.5 to +7.5.
            vec3 origin = vec3(float(at.x) - 7.5, 0.0, float(at.y) - 7.5);
            vec3 direction = vec3(0.0, 1.0, 0.0);

            rayQueryEXT query;
            rayQueryInitializeEXT(
                query,
                scene,
                gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsOpaqueEXT,
                0xFF,
                origin,
                0.01,
                direction,
                100.0);

            while (rayQueryProceedEXT(query)) { }

            float blocked = rayQueryGetIntersectionTypeEXT(query, true)
                == gl_RayQueryCommittedIntersectionNoneEXT ? 0.0 : 1.0;

            imageStore(occlusion, at, vec4(blocked, 0.0, 0.0, 0.0));
        }
        """;

    private readonly D3D12Context _context;
    private readonly ShaderCompiler _compiler;
    private bool _disposed;

    private D3D12TraceProbe(D3D12Context context, ShaderCompiler compiler)
    {
        _context = context;
        _compiler = compiler;
    }

    /// <summary>Name of the device being used.</summary>
    public string DeviceName => _context.DeviceName;

    /// <summary>Whether the device can trace at all.</summary>
    public bool CanTrace => _context.SupportsRayTracing;

    /// <summary>Everything the debug layer has said since it was last asked.</summary>
    public IReadOnlyList<string> Messages => _context.DrainMessages();

    /// <summary>Creates a probe.</summary>
    /// <returns>The probe.</returns>
    /// <exception cref="D3D12Exception">There is no usable device.</exception>
    public static D3D12TraceProbe Create()
    {
        D3D12Context context = D3D12Context.Create(enableValidation: true);

        try
        {
            return new D3D12TraceProbe(
                context, new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory) { DxilShaderModel = context.DxilShaderModel });
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Traces the grid past a square blocker and returns which rays it stopped.
    /// </summary>
    /// <param name="half">Half the blocker's width, in world units.</param>
    /// <param name="height">How far above the rays it floats.</param>
    /// <param name="offset">How far the blocker is moved, to test the instance transform.</param>
    /// <returns>
    /// One value a ray, row-major, one where it was blocked and zero where it was not.
    /// </returns>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    /// <exception cref="InvalidOperationException">The device cannot trace.</exception>
    public float[] Trace(float half = 4f, float height = 4f, Vector3 offset = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Vector3[] vertices =
        [
            new(-half, height, -half),
            new(half, height, -half),
            new(half, height, half),
            new(-half, height, half),
        ];

        uint[] indices = [0, 1, 2, 0, 2, 3];

        using D3D12AccelerationStructure structure = D3D12AccelerationStructure.Build(
            _context,
            [new TraceablePart(vertices, indices, Matrix4x4.CreateTranslation(offset))]);

        var layout = new ShaderLayout(
        [
            new ShaderBinding(0, 0, ShaderBindingKind.AccelerationStructure, ShaderStages.Compute),
            new ShaderBinding(0, 1, ShaderBindingKind.StorageImage, ShaderStages.Compute),
        ]);

        using D3D12Pipeline pipeline = D3D12Pipeline.CreateCompute(
            _context.Device, _compiler, Source, "trace-probe", layout);

        using D3D12Texture occlusion = D3D12Texture.CreateStorage(
            _context, Format.FormatR32Float, Side, Side);

        using D3D12DescriptorHeap heap = D3D12DescriptorHeap.Create(
            _context.Device, DescriptorHeapType.CbvSrvUav, 8, shaderVisible: true);

        uint first = heap.Allocate(2);
        structure.Describe(_context, heap.Cpu(first));

        var view = new UnorderedAccessViewDesc
        {
            Format = Format.FormatR32Float,
            ViewDimension = UavDimension.Texture2D,
        };
        view.Anonymous.Texture2D = new Tex2DUav { MipSlice = 0, PlaneSlice = 0 };

        _context.Device->CreateUnorderedAccessView(
            occlusion.Handle, (ID3D12Resource*)null, &view, heap.Cpu(first + 1));

        ID3D12GraphicsCommandList4* list = _context.BeginOneShot();

        ID3D12DescriptorHeap* heaps = heap.Handle;
        list->SetDescriptorHeaps(1, &heaps);
        list->SetComputeRootSignature(pipeline.Signature.Handle);
        list->SetPipelineState(pipeline.Handle);
        list->SetComputeRootDescriptorTable(
            (uint)pipeline.Signature.ParameterFor(0), heap.Gpu(first));

        list->Dispatch(Side / 8, Side / 8, 1);

        _context.EndOneShot();

        return ReadFloats(occlusion);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _compiler.Dispose();
        _context.Dispose();
    }

    private float[] ReadFloats(D3D12Texture texture)
    {
        // The picture readback would work and would round every value to a byte, which for
        // a mask of ones and zeroes is fine and for anything else would not be. Reading the
        // floats keeps this probe able to answer questions about partial occlusion later.
        ResourceDesc description = texture.Handle->GetDesc();

        PlacedSubresourceFootprint footprint = default;
        ulong bytes = 0;
        uint rows = 0;
        ulong rowBytes = 0;

        _context.Device->GetCopyableFootprints(
            &description, 0, 1, 0, &footprint, &rows, &rowBytes, &bytes);

        Silk.NET.Core.Native.ComPtr<ID3D12Resource> staging =
            _context.CreateBuffer(bytes, HeapType.Readback);

        try
        {
            ID3D12GraphicsCommandList4* list = _context.BeginOneShot();

            ResourceStates was = texture.State;
            texture.Transition(list, ResourceStates.CopySource);

            var destination = new TextureCopyLocation
            {
                PResource = staging.Handle,
                Type = TextureCopyType.PlacedFootprint,
            };
            destination.Anonymous.PlacedFootprint = footprint;

            var origin = new TextureCopyLocation
            {
                PResource = texture.Handle,
                Type = TextureCopyType.SubresourceIndex,
            };
            origin.Anonymous.SubresourceIndex = 0;

            list->CopyTextureRegion(&destination, 0, 0, 0, &origin, (Box*)null);
            texture.Transition(list, was);

            _context.EndOneShot();

            void* mapped;
            var range = new Silk.NET.Direct3D12.Range { Begin = 0, End = (nuint)bytes };
            D3D12Exception.ThrowIfFailed(staging.Map(0, &range, &mapped), "map the readback buffer");

            try
            {
                float[] values = new float[Side * Side];
                var source = new ReadOnlySpan<byte>(mapped, (int)bytes);

                for (int y = 0; y < Side; y++)
                {
                    ReadOnlySpan<byte> row =
                        source.Slice((int)(y * footprint.Footprint.RowPitch), Side * sizeof(float));

                    System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(row)
                        .CopyTo(values.AsSpan(y * Side, Side));
                }

                return values;
            }
            finally
            {
                var written = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
                staging.Unmap(0, &written);
            }
        }
        finally
        {
            staging.Dispose();
        }
    }
}
