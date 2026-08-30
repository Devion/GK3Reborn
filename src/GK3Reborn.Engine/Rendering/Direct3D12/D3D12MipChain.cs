using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Builds a texture's mip chain on the device, a level at a time.
/// </summary>
/// <remarks>
/// <para>
/// Vulkan makes mips by blitting each level from the one above with <c>vkCmdBlitImage</c>
/// and a linear filter. Direct3D has no blit at all, so this is a compute shader — written
/// in the same GLSL as everything else and translated the same way.
/// </para>
/// <para>
/// <b>It samples rather than averaging four texels, and that is the whole design.</b> A
/// plain two-by-two box filter is correct only when a level halves exactly. It does not
/// when a size is odd: twenty-five columns become twelve, and a filter that reads columns
/// <c>2x</c> and <c>2x + 1</c> never reads column twenty-four at all. The lost column is
/// always the same edge, so the picture creeps towards the other one with every odd step,
/// and by the last level of a hundred-wide texture the average has moved a fifth of the way
/// across. That was measured, not feared.
/// </para>
/// <para>
/// Sampling with a linear filter at the centre of each destination texel is what the Vulkan
/// blit does, so it is what this does: the hardware weights whichever texels the footprint
/// actually covers, and an odd step comes out where it should. It costs a shader resource
/// view per level and a per-subresource barrier, because level <c>n</c> is read while level
/// <c>n + 1</c> is written and one texture cannot be in two states at once unless the
/// states are per subresource.
/// </para>
/// <para>
/// <b>A colour texture is averaged in light, not in its encoding.</b> The source is read
/// through the texture's own sRGB format so the hardware decodes it, and the result is
/// encoded again here before it is stored, because an unordered access view cannot carry an
/// sRGB encode and so cannot do it. Getting this wrong is not obvious and does not look like
/// a mistake: half black and half white averages to 128 in the encoding and to 188 in light,
/// so every level comes out three quarters of a stop dark and darker again at each level
/// after it, on every texture with detail in it and on nothing else. Vulkan's
/// <c>vkCmdBlitImage</c> decodes and re-encodes an sRGB image for exactly this reason, which
/// is what made the two backends disagree over the whole of a room.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// The source is a combined image sampler, which HLSL has no such thing as: SPIRV-Cross
    /// splits it into a texture and a sampler at the same register index in different
    /// classes, so this one binding costs a descriptor in each of two heaps. See
    /// <see cref="ShaderBindingKind.CombinedImageSampler"/>.
    /// </remarks>
    private static readonly ShaderLayout Layout = new(
    [
        new ShaderBinding(0, 0, ShaderBindingKind.CombinedImageSampler, ShaderStages.Compute),
        new ShaderBinding(0, 1, ShaderBindingKind.StorageImage, ShaderStages.Compute),
    ],
    PushConstantBytes: 16);

    /// <summary>Fills in every level below the top one.</summary>
    /// <param name="context">The device.</param>
    /// <param name="texture">The texture, whose top level is already filled.</param>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    /// <remarks>
    /// Submits once. Each step transitions the level it is about to read out of unordered
    /// access and into being a shader resource, which both makes it readable and orders the
    /// write that filled it — so no separate barrier is needed for the ordering.
    /// </remarks>
    public static void Build(D3D12Context context, D3D12Texture texture)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(texture);

        if (texture.Mips <= 1)
        {
            return;
        }

        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        using D3D12Pipeline pipeline = D3D12Pipeline.CreateCompute(
            context.Device, compiler, Source, "mip-chain", Layout);

        using D3D12DescriptorHeap views = D3D12DescriptorHeap.Create(
            context.Device, DescriptorHeapType.CbvSrvUav, texture.Mips * 2, shaderVisible: true);

        using D3D12DescriptorHeap samplers = D3D12DescriptorHeap.Create(
            context.Device, DescriptorHeapType.Sampler, texture.Mips, shaderVisible: true);

        // Whether the stored bytes carry an sRGB encode, which decides whether the filtered
        // result has to be given one back. Every wall and floor texture in the game does;
        // a normal map, an occlusion map and a height map do not.
        bool encoded = D3D12Texture.Linearise(texture.Format) != texture.Format;

        ID3D12GraphicsCommandList4* list = context.BeginOneShot();

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
        context.EndOneShot();
    }

    /// <summary>Writes the sampler the filter reads through.</summary>
    /// <remarks>
    /// Clamped, so an edge texel weights itself instead of wrapping round to the far side;
    /// linear, because the whole point is to let the hardware weight the footprint; and no
    /// anisotropy, which would be meaningless for an axis-aligned halving and is not free.
    /// </remarks>
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
