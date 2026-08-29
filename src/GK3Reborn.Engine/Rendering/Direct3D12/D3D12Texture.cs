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
    /// <returns>The texture.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    /// <remarks>
    /// Cleared to one, which is the far plane. The projection puts near at zero and the
    /// depth test is <c>Less</c>, on both backends alike.
    /// </remarks>
    public static D3D12Texture CreateDepthTarget(
        D3D12Context context, Format format, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(context);

        var value = new ClearValue { Format = format };
        value.Anonymous.DepthStencil = new DepthStencilValue { Depth = 1f, Stencil = 0 };

        return Create(
            context,
            format,
            width,
            height,
            ResourceFlags.AllowDepthStencil,
            ResourceStates.DepthWrite,
            &value);
    }

    /// <summary>Makes a texture a compute shader can write into.</summary>
    /// <param name="context">The device.</param>
    /// <param name="format">What it holds.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    public static D3D12Texture CreateStorage(
        D3D12Context context, Format format, int width, int height)
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
            null);
    }

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
        ClearValue* clear)
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
            DepthOrArraySize = 1,
            MipLevels = 1,
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

        return new D3D12Texture(resource, format, width, height, state);
    }
}
