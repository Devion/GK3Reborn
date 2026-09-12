using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Builds a texture's mip chain on the device, a level at a time.
/// </summary>
public static unsafe class D3D12MipChain
{
    /// <summary>The downsample, in GLSL.</summary>
    private const string Source = """
        #version 460

        layout(local_size_x = 8, local_size_y = 8) in;

        layout(set = 0, binding = 0) uniform sampler2D coarser;
        layout(set = 0, binding = 1, rgba8) uniform writeonly image2D finer;

        layout(push_constant) uniform Push
        {
            // Width and height of the level being written, then whether the source is a
            // colour texture whose bytes carry an sRGB encode. The last word is padding,
            // because a root constant block is counted in whole words either way.
            ivec4 size;
        } push;

        // The sRGB transfer function, which is what the destination view cannot apply.
        // Written out rather than approximated by a power, because the hardware applies
        // exactly this on the way in and anything else would not round-trip.
        vec3 encode(vec3 light)
        {
            return mix(
                light * 12.92,
                (pow(light, vec3(1.0 / 2.4)) * 1.055) - 0.055,
                greaterThan(light, vec3(0.0031308)));
        }

        void main()
        {
            ivec2 at = ivec2(gl_GlobalInvocationID.xy);
            ivec2 target = push.size.xy;

            if (at.x >= target.x || at.y >= target.y)
            {
                return;
            }

            // The centre of this texel in the source's own normalised space. The sampler
            // is clamped, so an edge texel weights itself rather than wrapping, and the
            // filter covers whatever the footprint actually is — which for an odd step is
            // not two texels by two.
            vec2 uv = (vec2(at) + 0.5) / vec2(target);

            // Light, for a colour texture: the source view carries the encode and the
            // hardware took it off. Alpha never carries one, on either side.
            vec4 filtered = textureLod(coarser, uv, 0.0);

            if (push.size.z != 0)
            {
                filtered.rgb = encode(filtered.rgb);
            }

            imageStore(finer, at, filtered);
        }
        """;

    /// <summary>What the shader binds.</summary>
    private static readonly ShaderLayout Layout = new(
    [
        new ShaderBinding(0, 0, ShaderBindingKind.CombinedImageSampler, ShaderStages.Compute),
        new ShaderBinding(0, 1, ShaderBindingKind.StorageImage, ShaderStages.Compute),
    ],
    PushConstantBytes: 16);

    /// <summary>Builds the compute pipeline the filter runs as.</summary>
    /// <param name="context">The device.</param>
    /// <returns>The pipeline, which the context keeps for the life of the device.</returns>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    internal static D3D12Pipeline CreatePipeline(D3D12Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory) { DxilShaderModel = context.DxilShaderModel };

        return D3D12Pipeline.CreateCompute(context.Device, compiler, Source, "mip-chain", Layout);
    }

    /// <summary>Fills in every level below the top one.</summary>
    /// <param name="context">The device.</param>
    /// <param name="texture">The texture, whose top level is already filled.</param>
    /// <param name="into">An open batch to record into, or null to submit on its own.</param>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    public static void Build(D3D12Context context, D3D12Texture texture, D3D12Uploads? into = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(texture);

        if (texture.Mips <= 1)
        {
            return;
        }

        // Made once for the device rather than once for every texture: compiling the filter
        // and building a root signature and a pipeline state cost more than the dispatches do.
        D3D12Pipeline pipeline = context.MipChain;

        D3D12DescriptorHeap views = D3D12DescriptorHeap.Create(
            context.Device, DescriptorHeapType.CbvSrvUav, texture.Mips * 2, shaderVisible: true);

        D3D12DescriptorHeap samplers = D3D12DescriptorHeap.Create(
            context.Device, DescriptorHeapType.Sampler, texture.Mips, shaderVisible: true);

        bool own = into is null;

        // A batch holds the heaps until it has run, because what was recorded into its list
        // reads through them; on its own the two using blocks below are past the wait.
        if (into is { } batch)
        {
            batch.Keep(views);
            batch.Keep(samplers);
        }

        // Whether the stored bytes carry an sRGB encode, which decides whether the filtered
        // result has to be given one back. Every wall and floor texture in the game does;
        // a normal map, an occlusion map and a height map do not.
        bool encoded = D3D12Texture.Linearise(texture.Format) != texture.Format;

        ID3D12GraphicsCommandList4* list = into is { } recording ? recording.List : context.BeginOneShot();

        // Whole-resource first, so every subresource is in one known state before the
        // per-subresource moves below start disagreeing with each other.
        texture.Transition(list, ResourceStates.UnorderedAccess);

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = views.Handle;
        heaps[1] = samplers.Handle;

        list->SetDescriptorHeaps(2, heaps);
        list->SetComputeRootSignature(pipeline.Signature.Handle);
        list->SetPipelineState(pipeline.Handle);

        // Outside the loop: a stackalloc inside one grows the frame on every iteration.
        int* size = stackalloc int[4];

        for (uint level = 1; level < texture.Mips; level++)
        {
            int width = Math.Max(1, texture.Width >> (int)level);
            int height = Math.Max(1, texture.Height >> (int)level);

            // The level about to be read stops being written and starts being read. This
            // is also what orders the write that produced it against this read.
            D3D12Context.TransitionSubresource(
                list,
                texture.Handle,
                ResourceStates.UnorderedAccess,
                ResourceStates.NonPixelShaderResource,
                level - 1);

            uint first = views.Allocate(2);
            texture.DescribeLevel(context, views.Cpu(first), level - 1);
            texture.DescribeWrite(context, views.Cpu(first + 1), level);

            uint sampler = samplers.Allocate();
            WriteClampedSampler(context, samplers.Cpu(sampler));

            list->SetComputeRootDescriptorTable(
                (uint)pipeline.Signature.ParameterFor(0), views.Gpu(first));

            list->SetComputeRootDescriptorTable(
                (uint)pipeline.Signature.SamplerParameterFor(0), samplers.Gpu(sampler));

            size[0] = width;
            size[1] = height;
            size[2] = encoded ? 1 : 0;
            size[3] = 0;

            list->SetComputeRoot32BitConstants(
                (uint)pipeline.Signature.PushConstantParameter, 4, size, 0);

            list->Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);
        }

        // Every level but the last is a shader resource; the last is still unordered
        // access. Both go to being readable by every stage, which is what a texture is for.
        for (uint level = 0; level < texture.Mips; level++)
        {
            D3D12Context.TransitionSubresource(
                list,
                texture.Handle,
                level == texture.Mips - 1
                    ? ResourceStates.UnorderedAccess
                    : ResourceStates.NonPixelShaderResource,
                ResourceStates.AllShaderResource,
                level);
        }

        texture.Claim(ResourceStates.AllShaderResource);

        if (!own)
        {
            return;
        }

        context.EndOneShot();

        views.Dispose();
        samplers.Dispose();
    }

    /// <summary>Writes the sampler the filter reads through.</summary>
    private static void WriteClampedSampler(D3D12Context context, CpuDescriptorHandle where)
    {
        var description = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0f,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunc.Never,
            MinLOD = 0f,
            MaxLOD = 0f,
        };

        context.Device->CreateSampler(&description, where);
    }
}
