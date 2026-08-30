using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// A texture on the device, and the state it is currently in.
/// </summary>
/// <remarks>
/// <para>
/// The state travels with the texture rather than being tracked by whoever draws with it,
/// because a texture outlives a pass and is read by several of them. Direct3D does not
/// track it at all: a resource read in the wrong state is undefined data, silently, unless
/// the debug layer is on. Keeping the state beside the resource is what makes
/// <see cref="Transition"/> able to be a no-op when nothing needs to change, which in turn
/// is what lets callers ask for the state they want without first working out what it is.
/// </para>
/// <para>
/// A render target or depth target is created with a clear value. That is not an
/// optimisation to skip: a target created without one and then cleared is a slow path on
/// every driver, and one created with a clear value different from the one it is cleared
/// with is a validation error.
/// </para>
/// </remarks>
public sealed unsafe class D3D12Texture : IDisposable
{
    private ComPtr<ID3D12Resource> _resource;
    private bool _disposed;

    private D3D12Texture(
        ComPtr<ID3D12Resource> resource, Format format, int width, int height, ResourceStates state)
    {
        _resource = resource;
        Format = format;
        Width = width;
        Height = height;
        State = state;
    }

    /// <summary>What the texture holds.</summary>
    public Format Format { get; }

    /// <summary>The format a shader resource view of it declares.</summary>
    /// <remarks>
    /// The same as <see cref="Format"/> for everything except a depth target the denoiser
    /// reads. A resource that is both a depth target and a texture has to be created
    /// typeless, because the two views want different formats of the same bits — D32_FLOAT
    /// for the one the hardware tests against, R32_FLOAT for the one a shader samples — and
    /// a typed resource may only be viewed as the type it was created with.
    /// </remarks>
    public Format Sampling { get; private init; }

    /// <summary>The format a depth stencil view of it declares.</summary>
    public Format Depth { get; private init; }

    /// <summary>Its width in pixels.</summary>
    public int Width { get; }

    /// <summary>Its height in pixels.</summary>
    public int Height { get; }

    /// <summary>Which state it is in.</summary>
    public ResourceStates State { get; private set; }

    /// <summary>The resource itself.</summary>
    public ID3D12Resource* Handle => _resource.Handle;

    /// <summary>Makes a texture that can be drawn into.</summary>
    /// <param name="context">The device.</param>
    /// <param name="format">What it holds.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="clear">What it is cleared to, which must match what it is cleared with.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    public static D3D12Texture CreateRenderTarget(
        D3D12Context context,
        Format format,
        int width,
        int height,
        (float R, float G, float B, float A) clear = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var value = new ClearValue { Format = format };
        value.Anonymous.Color[0] = clear.R;
        value.Anonymous.Color[1] = clear.G;
        value.Anonymous.Color[2] = clear.B;
        value.Anonymous.Color[3] = clear.A;

        return Create(
            context,
            format,
            width,
            height,
            ResourceFlags.AllowRenderTarget,
            ResourceStates.RenderTarget,
            &value);
    }

    /// <summary>Makes a depth target.</summary>
    /// <param name="context">The device.</param>
    /// <param name="format">What it holds.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="sampled">Whether a shader reads it as well as the depth test.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    /// <remarks>
    /// Cleared to one, which is the far plane. The projection puts near at zero and the
    /// depth test is <c>Less</c>, on both backends alike.
    /// </remarks>
    public static D3D12Texture CreateDepthTarget(
        D3D12Context context, Format format, int width, int height, bool sampled = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        var value = new ClearValue { Format = format };
        value.Anonymous.DepthStencil = new DepthStencilValue { Depth = 1f, Stencil = 0 };

        // Typeless when something is going to read it. The clear value keeps the real depth
        // format either way: a clear value is checked against what the view clears through,
        // not against what the resource was declared as, and a typeless clear value is
        // refused outright.
        return Create(
            context,
            sampled ? Typeless(format) : format,
            width,
            height,
            ResourceFlags.AllowDepthStencil,
            ResourceStates.DepthWrite,
            &value,
            views: (SampledForm(format), format));
    }

    /// <summary>Makes a texture a compute shader can write into.</summary>
    /// <param name="context">The device.</param>
    /// <param name="format">What it holds.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="mips">How many levels, for a chain a compute shader builds itself.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    public static D3D12Texture CreateStorage(
        D3D12Context context, Format format, int width, int height, uint mips = 1)
    {
        ArgumentNullException.ThrowIfNull(context);

        // No clear value: a resource with unordered access may not have one, and asking
        // for one is refused rather than ignored.
        return Create(
            context,
            format,
            width,
            height,
            ResourceFlags.AllowUnorderedAccess,
            ResourceStates.UnorderedAccess,
            null,
            mips);
    }

    /// <summary>How many mip levels it has.</summary>
    public uint Mips { get; private init; } = 1;

    /// <summary>How many array slices it has, which is six for a cube and one otherwise.</summary>
    public ushort Slices { get; private init; } = 1;

    /// <summary>Makes a cube map a shader can sample in six directions.</summary>
    /// <param name="context">The device.</param>
    /// <param name="format">What it holds.</param>
    /// <param name="size">How wide and tall each face is.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    /// <remarks>
    /// No mip chain. A sky is always at the far plane and never minified, and generating one
    /// would blend across the face boundaries — which is exactly where a skybox shows its
    /// seams.
    /// </remarks>
    public static D3D12Texture CreateCube(D3D12Context context, Format format, int size)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Create(
            context,
            format,
            size,
            size,
            ResourceFlags.None,
            ResourceStates.Common,
            null,
            mips: 1,
            views: null,
            slices: 6);
    }

    /// <summary>Writes a cube map view of this texture into a descriptor slot.</summary>
    /// <param name="context">The device.</param>
    /// <param name="where">Where to write it.</param>
    /// <exception cref="InvalidOperationException">It is not a cube.</exception>
    /// <remarks>
    /// A separate call rather than a flag on <see cref="Describe"/>, because the dimension is
    /// a property of how the shader declares the binding rather than of the resource: the
    /// same six slices can be read as an array or as a cube, and only one of them matches a
    /// <c>samplerCube</c>.
    /// </remarks>
    public void DescribeCube(D3D12Context context, CpuDescriptorHandle where)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Slices != 6)
        {
            throw new InvalidOperationException(
                $"A cube map has six slices and this texture has {Slices}.");
        }

        var description = new ShaderResourceViewDesc
        {
            Format = Sampling,
            ViewDimension = SrvDimension.Texturecube,
            Shader4ComponentMapping = D3D12AccelerationStructure.DefaultComponentMapping,
        };

        description.Anonymous.TextureCube = new TexcubeSrv
        {
            MostDetailedMip = 0,
            MipLevels = Mips,
            ResourceMinLODClamp = 0f,
        };

        context.Device->CreateShaderResourceView(_resource.Handle, &description, where);
    }

    /// <summary>Makes a texture a shader can sample.</summary>
    /// <param name="context">The device.</param>
    /// <param name="format">What it holds.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="mips">How many levels.</param>
    /// <param name="writable">Whether the mip builder will write into it.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    /// <remarks>
    /// It starts in <c>Common</c> rather than in a shader-read state, because the next
    /// thing that happens to it is a copy and a copy destination must be reached from a
    /// state a copy can begin from. Whoever fills it puts it where it belongs afterwards.
    /// </remarks>
    public static D3D12Texture CreateSampled(
        D3D12Context context,
        Format format,
        int width,
        int height,
        uint mips = 1,
        bool writable = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Create(
            context,
            format,
            width,
            height,
            writable ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None,
            ResourceStates.Common,
            null,
            mips);
    }

    /// <summary>Writes a shader resource view of this texture into a descriptor slot.</summary>
    /// <param name="context">The device.</param>
    /// <param name="where">Where to write it.</param>
    public void Describe(D3D12Context context, CpuDescriptorHandle where)
    {
        ArgumentNullException.ThrowIfNull(context);

        var description = new ShaderResourceViewDesc
        {
            Format = Sampling,
            ViewDimension = SrvDimension.Texture2D,

            // Without this every channel reads as red. It is a macro in the header rather
            // than a constant, so nothing generated carries it and nothing warns.
            Shader4ComponentMapping = D3D12AccelerationStructure.DefaultComponentMapping,
        };

        description.Anonymous.Texture2D = new Tex2DSrv
        {
            MostDetailedMip = 0,
            MipLevels = Mips,
            PlaneSlice = 0,
            ResourceMinLODClamp = 0f,
        };

        context.Device->CreateShaderResourceView(_resource.Handle, &description, where);
    }

    /// <summary>Writes a shader resource view of one level into a descriptor slot.</summary>
    /// <param name="context">The device.</param>
    /// <param name="where">Where to write it.</param>
    /// <param name="level">Which mip level.</param>
    /// <remarks>
    /// <para>
    /// One level and no others, which is what the mip builder samples through: a view of
    /// the whole chain would let the filter pick a level of its own and the result would
    /// depend on what the sampler decided rather than on what was asked for.
    /// </para>
    /// <para>
    /// The texture's own format, sRGB encode and all — unlike <see cref="DescribeWrite"/>,
    /// which has no choice. Reading a colour texture through the plain format hands the
    /// filter the stored bytes and it averages a transfer curve, which is not what averaging
    /// light means and not what Vulkan's blit does. See <see cref="D3D12MipChain"/>.
    /// </para>
    /// </remarks>
    public void DescribeLevel(D3D12Context context, CpuDescriptorHandle where, uint level)
    {
        ArgumentNullException.ThrowIfNull(context);

        var description = new ShaderResourceViewDesc
        {
            Format = Format,
            ViewDimension = SrvDimension.Texture2D,
            Shader4ComponentMapping = D3D12AccelerationStructure.DefaultComponentMapping,
        };

        description.Anonymous.Texture2D = new Tex2DSrv
        {
            MostDetailedMip = level,
            MipLevels = 1,
            PlaneSlice = 0,
            ResourceMinLODClamp = 0f,
        };

        context.Device->CreateShaderResourceView(_resource.Handle, &description, where);
    }

    /// <summary>Writes an unordered access view of one level into a descriptor slot.</summary>
    /// <param name="context">The device.</param>
    /// <param name="where">Where to write it.</param>
    /// <param name="level">Which mip level.</param>
    public void DescribeWrite(D3D12Context context, CpuDescriptorHandle where, uint level = 0)
    {
        ArgumentNullException.ThrowIfNull(context);

        var description = new UnorderedAccessViewDesc
        {
            // An unordered access view of an sRGB texture is refused: a shader writes
            // linear values and the hardware would have to encode them, which unordered
            // access has no path for. The view is declared as the plain format instead, so
            // the mip builder writes the bytes it means to.
            Format = Linearise(Format),
            ViewDimension = UavDimension.Texture2D,
        };

        description.Anonymous.Texture2D = new Tex2DUav { MipSlice = level, PlaneSlice = 0 };

        context.Device->CreateUnorderedAccessView(
            _resource.Handle, (ID3D12Resource*)null, &description, where);
    }

    /// <summary>Writes a depth stencil view of this texture into a descriptor slot.</summary>
    /// <param name="context">The device.</param>
    /// <param name="where">Where to write it.</param>
    /// <remarks>
    /// Stated rather than left null. A null description means "whatever the resource says",
    /// which is exactly what a typeless resource cannot answer, so a depth target the
    /// denoiser reads must be given its format here explicitly.
    /// </remarks>
    public void DescribeDepth(D3D12Context context, CpuDescriptorHandle where)
    {
        ArgumentNullException.ThrowIfNull(context);

        var description = new DepthStencilViewDesc
        {
            Format = Depth,
            ViewDimension = DsvDimension.Texture2D,
            Flags = DsvFlags.None,
        };

        description.Anonymous.Texture2D = new Tex2DDsv { MipSlice = 0 };

        context.Device->CreateDepthStencilView(_resource.Handle, &description, where);
    }

    /// <summary>The typeless form of a depth format.</summary>
    /// <param name="format">The depth format.</param>
    /// <returns>The format the resource is created as when a shader reads it too.</returns>
    public static Format Typeless(Format format) => format switch
    {
        Format.FormatD32Float => Format.FormatR32Typeless,
        Format.FormatD16Unorm => Format.FormatR16Typeless,
        Format.FormatD24UnormS8Uint => Format.FormatR24G8Typeless,
        _ => format,
    };

    /// <summary>The format a shader samples a depth target through.</summary>
    /// <param name="format">The depth format.</param>
    /// <returns>The colour format of the same bits.</returns>
    public static Format SampledForm(Format format) => format switch
    {
        Format.FormatD32Float => Format.FormatR32Float,
        Format.FormatD16Unorm => Format.FormatR16Unorm,
        Format.FormatD24UnormS8Uint => Format.FormatR24UnormX8Typeless,
        _ => format,
    };

    /// <summary>The plain form of a format that carries an sRGB encode.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The same format without the encode.</returns>
    public static Format Linearise(Format format) => format switch
    {
        Format.FormatR8G8B8A8UnormSrgb => Format.FormatR8G8B8A8Unorm,
        Format.FormatB8G8R8A8UnormSrgb => Format.FormatB8G8R8A8Unorm,
        Format.FormatBC7UnormSrgb => Format.FormatBC7Unorm,
        _ => format,
    };

    /// <summary>Says what state the texture is in, without recording anything.</summary>
    /// <param name="state">The state it is in.</param>
    /// <remarks>
    /// For the one caller that moves the subresources individually. Building a mip chain
    /// reads one level while it writes the next, which a whole-resource transition cannot
    /// express, so it does its own and then says where it left things. Anything else that
    /// reaches for this is almost certainly about to lie to the tracker.
    /// </remarks>
    public void Claim(ResourceStates state) => State = state;

    /// <summary>Moves the texture into a state, if it is not in it already.</summary>
    /// <param name="list">The list to record into.</param>
    /// <param name="to">The state it should be in.</param>
    public void Transition(ID3D12GraphicsCommandList4* list, ResourceStates to)
    {
        D3D12Context.Transition(list, _resource.Handle, State, to);
        State = to;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resource.Dispose();
    }

    private static D3D12Texture Create(
        D3D12Context context,
        Format format,
        int width,
        int height,
        ResourceFlags flags,
        ResourceStates state,
        ClearValue* clear,
        uint mips = 1,
        (Format Sampling, Format Depth)? views = null,
        ushort slices = 1)
    {
        var properties = new HeapProperties
        {
            Type = HeapType.Default,
            CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown,
            CreationNodeMask = 1,
            VisibleNodeMask = 1,
        };

        var description = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            DepthOrArraySize = slices < 1 ? (ushort)1 : slices,
            MipLevels = (ushort)Math.Max(1, mips),
            Format = format,
            SampleDesc = new SampleDesc(1, 0),

            // Unknown, not row-major: the driver picks whatever swizzle the hardware wants.
            // Row-major on a texture is legal and slow, and is only needed for one that is
            // mapped, which none of these is.
            Layout = TextureLayout.LayoutUnknown,
            Flags = flags,
        };

        ComPtr<ID3D12Resource> resource = default;
        Guid resourceId = ID3D12Resource.Guid;

        D3D12Exception.ThrowIfFailed(
            context.Device->CreateCommittedResource(
                &properties,
                HeapFlags.None,
                &description,
                state,
                clear,
                &resourceId,
                (void**)resource.GetAddressOf()),
            $"create a {width} by {height} {format} texture");

        return new D3D12Texture(resource, format, width, height, state)
        {
            Mips = Math.Max(1, mips),
            Slices = slices < 1 ? (ushort)1 : slices,
            Sampling = views?.Sampling ?? format,
            Depth = views?.Depth ?? format,
        };
    }
}
