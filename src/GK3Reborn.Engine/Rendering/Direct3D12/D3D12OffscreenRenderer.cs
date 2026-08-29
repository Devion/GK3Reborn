using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Draws into a texture with no window anywhere, and reads the result back.
/// </summary>
/// <remarks>
/// <para>
/// The Direct3D twin of <c>OffscreenRenderer</c>, and it exists for the same reason: a
/// renderer that has never had its output looked at has only been proved not to crash. A
/// picture that comes back with the right number of lit pixels in the right places is the
/// difference between "the pipeline was created" and "the pipeline draws".
/// </para>
/// <para>
/// The triangle is the same triangle as the Vulkan side's, in the same source, and it is
/// deliberately taken through the whole chain — HLSL to SPIR-V to HLSL to DXIL — rather
/// than handed straight to DXC. Compiling HLSL by way of SPIR-V is silly for this one
/// shader and is exactly the point: it is the path every real shader takes, so a break
/// anywhere in it breaks here first, in the smallest thing there is to debug.
/// </para>
/// </remarks>
public sealed unsafe class D3D12OffscreenRenderer : IDisposable
{
    /// <summary>
    /// The bring-up triangle. Three vertices from the vertex index and nothing else.
    /// </summary>
    /// <remarks>
    /// No vertex buffer, no descriptor, no push constant. If this does not appear the fault
    /// is in the device, the pipeline or the target, and it cannot be in a buffer or a
    /// binding — which is what makes a first bring-up debuggable at all. The same source as
    /// the Vulkan backend's, character for character.
    /// </remarks>
    private const string Source = """
        struct VertexOutput
        {
            float4 position : SV_Position;
            float3 color    : COLOR0;
        };

        // A full triangle from the vertex index alone, so this stage needs no buffers.
        VertexOutput VertexMain(uint vertexId : SV_VertexID)
        {
            float2 positions[3] =
            {
                float2( 0.0, -0.6),
                float2( 0.6,  0.6),
                float2(-0.6,  0.6)
            };

            float3 colors[3] =
            {
                float3(0.90, 0.32, 0.28),
                float3(0.36, 0.72, 0.45),
                float3(0.35, 0.55, 0.92)
            };

            VertexOutput output;
            output.position = float4(positions[vertexId], 0.0, 1.0);
            output.color = colors[vertexId];
            return output;
        }

        float4 FragmentMain(VertexOutput input) : SV_Target
        {
            return float4(input.color, 1.0);
        }
        """;

    private readonly D3D12Context _context;
    private readonly ShaderCompiler _compiler;
    private bool _disposed;

    private D3D12OffscreenRenderer(D3D12Context context, ShaderCompiler compiler)
    {
        _context = context;
        _compiler = compiler;
    }

    /// <summary>Name of the device being used.</summary>
    public string DeviceName => _context.DeviceName;

    /// <summary>Everything the debug layer has said since it was last asked.</summary>
    public IReadOnlyList<string> Messages => _context.DrainMessages();

    /// <summary>Creates a headless renderer.</summary>
    /// <returns>The renderer.</returns>
    /// <exception cref="D3D12Exception">There is no usable device.</exception>
    public static D3D12OffscreenRenderer Create()
    {
        D3D12Context context = D3D12Context.Create(enableValidation: true);

        try
        {
            return new D3D12OffscreenRenderer(
                context, new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory));
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>Renders the bring-up triangle and returns the pixels.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="clear">What to clear to.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    public DecodedImage RenderTriangle(int width, int height, (float R, float G, float B) clear)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        const Format format = Format.FormatR8G8B8A8Unorm;

        using D3D12Texture target = D3D12Texture.CreateRenderTarget(
            _context, format, width, height, (clear.R, clear.G, clear.B, 1f));

        using D3D12DescriptorHeap targets = D3D12DescriptorHeap.Create(
            _context.Device, DescriptorHeapType.Rtv, 1);

        uint slot = targets.Allocate();
        _context.Device->CreateRenderTargetView(
            target.Handle, (RenderTargetViewDesc*)null, targets.Cpu(slot));

        using D3D12Pipeline pipeline = D3D12Pipeline.CreateGraphics(
            _context.Device,
            _compiler,
            Source,
            Source,
            "triangle",
            colorFormats: [format],
            language: ShaderLanguage.Hlsl,
            depthWrite: false,
            depthTest: false,

            // The triangle is wound for neither face in particular, and culling it is not
            // what is being tested. The real pipelines cull; this one draws whichever way
            // round it came out, so that a winding mistake shows up as a wrong picture
            // somewhere it matters rather than as an empty one here.
            cull: CullMode.None,
            vertexEntryPoint: "VertexMain",
            fragmentEntryPoint: "FragmentMain");

        ID3D12GraphicsCommandList4* list = _context.BeginOneShot();

        target.Transition(list, ResourceStates.RenderTarget);

        CpuDescriptorHandle view = targets.Cpu(slot);
        float* colour = stackalloc float[4] { clear.R, clear.G, clear.B, 1f };

        list->ClearRenderTargetView(view, colour, 0, (Box2D<int>*)null);
        list->OMSetRenderTargets(1, &view, false, (CpuDescriptorHandle*)null);

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
        list->SetGraphicsRootSignature(pipeline.Signature.Handle);
        list->SetPipelineState(pipeline.Handle);
        list->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        list->DrawInstanced(3, 1, 0, 0);

        _context.EndOneShot();

        return D3D12Readback.Read(
            _context, target.Handle, target.State, width, height);
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
}
