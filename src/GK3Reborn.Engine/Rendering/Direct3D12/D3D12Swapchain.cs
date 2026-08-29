using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// The chain of images the window is presented from, and the colour space they carry.
/// </summary>
/// <remarks>
/// <para>
/// The flip model, which is the only model Direct3D 12 has. Its consequences are worth
/// stating because they differ from Vulkan's in ways that are easy to get wrong: a
/// back buffer may not be multisampled, the buffer index is chosen by DXGI rather than
/// acquired by the application, and — the one that bites — a buffer must be back in the
/// <c>Present</c> state before it is presented, every time, or the runtime removes the
/// device.
/// </para>
/// <para>
/// There is no <c>ErrorOutOfDateKhr</c> here. Vulkan tells the application its swapchain
/// has gone stale; DXGI resizes on request and says nothing, so a resize is something the
/// renderer notices from the window rather than from a present. That makes recreation
/// simpler and one thing harder: nothing will remind you.
/// </para>
/// <para>
/// <b>The format is not a preference.</b> It decides what the numbers in the last shader
/// mean, and the three cases are genuinely different pictures rather than three qualities
/// of one. See <see cref="Choose"/>.
/// </para>
/// </remarks>
public sealed unsafe class D3D12Swapchain : IDisposable
{
    /// <summary>How many back buffers the chain holds.</summary>
    /// <remarks>
    /// Three rather than two. The flip model does not block on present the way the old
    /// model did, so a third buffer costs one frame of memory and removes the stall that
    /// two buffers leave when the CPU finishes a frame while the display still holds one.
    /// It is also what frame generation needs: a generated frame is presented between two
    /// rendered ones, and there has to be somewhere to put it.
    /// </remarks>
    public const uint BufferCount = 3;

    private readonly D3D12Context _context;
    private readonly nint _window;

    private ComPtr<IDXGISwapChain4> _chain;
    private readonly ComPtr<ID3D12Resource>[] _buffers = new ComPtr<ID3D12Resource>[BufferCount];
    private readonly ResourceStates[] _states = new ResourceStates[BufferCount];
    private D3D12DescriptorHeap? _renderTargets;
    private bool _disposed;

    private D3D12Swapchain(D3D12Context context, nint window)
    {
        _context = context;
        _window = window;
    }

    /// <summary>The size of a back buffer, in pixels.</summary>
    public (int Width, int Height) Size { get; private set; }

    /// <summary>What a back buffer holds.</summary>
    public Format Format { get; private set; } = Format.FormatR8G8B8A8Unorm;

    /// <summary>How the numbers in a back buffer are to be read by the display.</summary>
    public ColorSpaceType ColorSpace { get; private set; } = ColorSpaceType.RgbFullG22NoneP709;

    /// <summary>Whether the chain is presenting high dynamic range.</summary>
    public bool HighDynamicRange =>
        ColorSpace is ColorSpaceType.RgbFullG2084NoneP2020 or ColorSpaceType.RgbFullG10NoneP709;

    /// <summary>Which buffer the next frame is drawn into.</summary>
    public uint CurrentBuffer =>
        _chain.Handle is null ? 0 : _chain.GetCurrentBackBufferIndex();

    /// <summary>Creates a swapchain for a window.</summary>
    /// <param name="context">The device.</param>
    /// <param name="window">The window's <c>HWND</c>.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="wantHdr">Whether to present high dynamic range if the display accepts it.</param>
    /// <returns>The swapchain.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    public static D3D12Swapchain Create(
        D3D12Context context,
        nint window,
        int width,
        int height,
        bool wantHdr = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (window == 0)
        {
            throw new D3D12Exception(
                "This window has no HWND, so there is nothing to make a swapchain against.");
        }

        var chain = new D3D12Swapchain(context, window);

        try
        {
            chain.Start(width, height, wantHdr);
            return chain;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    /// <summary>The resource behind one back buffer.</summary>
    /// <param name="index">Which buffer.</param>
    /// <returns>The resource.</returns>
    public ID3D12Resource* Buffer(uint index) => _buffers[index].Handle;

    /// <summary>Where the render target view of one back buffer is.</summary>
    /// <param name="index">Which buffer.</param>
    /// <returns>Its handle.</returns>
    public CpuDescriptorHandle RenderTarget(uint index) => _renderTargets!.Cpu(index);

    /// <summary>Moves a back buffer into a state, remembering which it is in.</summary>
    /// <param name="list">The list to record into.</param>
    /// <param name="index">Which buffer.</param>
    /// <param name="to">The state it should be in.</param>
    /// <remarks>
    /// The state is tracked per buffer rather than per frame, because with three buffers
    /// and two frames in flight the two do not line up. Tracking it per frame is how a
    /// buffer comes to be presented from the render target state, which removes the device
    /// with no message beyond the removal itself.
    /// </remarks>
    public void Transition(ID3D12GraphicsCommandList4* list, uint index, ResourceStates to)
    {
        D3D12Context.Transition(list, _buffers[index].Handle, _states[index], to);
        _states[index] = to;
    }

    /// <summary>Presents the current back buffer.</summary>
    /// <param name="verticalSync">Whether to wait for the display.</param>
    /// <returns>False when the chain needs rebuilding before the next frame.</returns>
    /// <exception cref="D3D12Exception">The present failed for a reason a rebuild will not fix.</exception>
    /// <remarks>
    /// Tearing is offered only with vertical sync off and only where DXGI says the machine
    /// allows it, which is not everywhere: it needs a tearing-capable adapter and a
    /// borderless window, and asking for it when either is missing fails the present with
    /// an invalid call rather than falling back.
    /// </remarks>
    public bool Present(bool verticalSync = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        uint interval = verticalSync ? 1u : 0u;
        uint flags = !verticalSync && AllowsTearing ? 0x200u : 0u;

        int hr = _chain.Present(interval, flags);

        // DXGI_STATUS_OCCLUDED. Not a failure: the window is behind something or the
        // machine is locked. The frame was not shown and the next one need not hurry.
        if (unchecked((uint)hr) == 0x087A0001)
        {
            return true;
        }

        // DXGI_ERROR_DEVICE_REMOVED / _RESET. A driver reset, an update, or a hang
        // somewhere earlier. Nothing here can recover it; saying which of the two it was
        // is the most useful thing available.
        if (unchecked((uint)hr) is 0x887A0005 or 0x887A0007)
        {
            int reason = _context.Device->GetDeviceRemovedReason();
            throw new D3D12Exception(
                $"The device was lost while presenting: 0x{reason:X8}. It reset after some earlier call.");
        }

        D3D12Exception.ThrowIfFailed(hr, "present");
        return true;
    }

    /// <summary>Whether this machine will let a present tear.</summary>
    public bool AllowsTearing { get; private set; }

    /// <summary>Rebuilds the chain at a new size.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="wantHdr">Whether to present high dynamic range if the display accepts it.</param>
    /// <exception cref="D3D12Exception">The chain could not be resized.</exception>
    /// <remarks>
    /// Every reference to a back buffer must be gone before the resize and the device must
    /// have finished with them, or DXGI refuses and says only that a call was invalid. The
    /// wait is the caller's to have done; releasing the buffers is this method's.
    /// </remarks>
    public void Resize(int width, int height, bool wantHdr = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i].Dispose();
            _buffers[i] = default;
            _states[i] = ResourceStates.Common;
        }

        Format format = Choose(wantHdr, out ColorSpaceType space);

        D3D12Exception.ThrowIfFailed(
            _chain.ResizeBuffers(
                BufferCount, (uint)width, (uint)height, format, AllowsTearing ? 0x800u : 0u),
            $"resize the swapchain to {width} by {height}");

        Size = (width, height);
        Format = format;
        ApplyColorSpace(space);
        BindBuffers();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i].Dispose();
        }

        _renderTargets?.Dispose();

        // A full-screen swapchain must be put back into a window before it is released, or
        // the display mode is left changed and the desktop with it.
        if (_chain.Handle is not null)
        {
            _chain.SetFullscreenState(false, (IDXGIOutput*)null);
        }

        _chain.Dispose();
    }

    /// <summary>Which format and colour space to present in.</summary>
    /// <param name="wantHdr">Whether high dynamic range was asked for.</param>
    /// <param name="space">How the display should read the numbers.</param>
    /// <returns>The format.</returns>
    /// <remarks>
    /// <para>
    /// Three genuinely different pictures rather than three qualities of one:
    /// </para>
    /// <para>
    /// <b>R8G8B8A8_UNORM with sRGB.</b> Eight bits a channel, encoded by the output pass.
    /// The format is deliberately not the <c>_SRGB</c> one: an sRGB back buffer converts on
    /// write, which is a second encode on top of the one the output pass already did, and
    /// the picture comes out washed out. The Vulkan path made exactly this mistake once.
    /// </para>
    /// <para>
    /// <b>R10G10B10A2_UNORM with ST.2084.</b> HDR10. Ten bits a channel is enough for a
    /// PQ curve and eight is not — eight bits of PQ bands visibly in the dark parts, which
    /// is where an adventure game set at night spends its time.
    /// </para>
    /// <para>
    /// <b>R16G16B16A16_FLOAT with linear scRGB.</b> What frame generation wants, because a
    /// PQ buffer is not linear and an interpolator that averages two PQ frames averages the
    /// wrong quantity. Not chosen here — the renderer asks for it when it needs it — but
    /// named, because the reason it exists is not obvious from the format.
    /// </para>
    /// </remarks>
    private Format Choose(bool wantHdr, out ColorSpaceType space)
    {
        if (wantHdr && SupportsColorSpace(ColorSpaceType.RgbFullG2084NoneP2020))
        {
            space = ColorSpaceType.RgbFullG2084NoneP2020;
            return Format.FormatR10G10B10A2Unorm;
        }

        space = ColorSpaceType.RgbFullG22NoneP709;
        return Format.FormatR8G8B8A8Unorm;
    }

    private bool SupportsColorSpace(ColorSpaceType space)
    {
        if (_chain.Handle is null)
        {
            return false;
        }

        uint support = 0;
        if (_chain.CheckColorSpaceSupport(space, &support) < 0)
        {
            return false;
        }

        // DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT.
        return (support & 0x1) != 0;
    }

    private void ApplyColorSpace(ColorSpaceType space)
    {
        if (space != ColorSpaceType.RgbFullG22NoneP709 && !SupportsColorSpace(space))
        {
            space = ColorSpaceType.RgbFullG22NoneP709;
        }

        if (_chain.SetColorSpace1(space) >= 0)
        {
            ColorSpace = space;
        }
    }

    private void Start(int width, int height, bool wantHdr)
    {
        AllowsTearing = TearingAllowed();

        var description = new SwapChainDesc1
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),

            // Started in the ordinary format and moved to a wide one afterwards, because
            // whether a colour space is presentable can only be asked of a chain that
            // exists. Nothing has been drawn yet, so the change costs a resize of an empty
            // chain.
            Format = Format.FormatR8G8B8A8Unorm,
            Stereo = false,
            SampleDesc = new SampleDesc(1, 0),
            BufferUsage = DXGI.UsageRenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = Scaling.Stretch,

            // The only model Direct3D 12 has. Discard rather than sequential: nothing reads
            // a back buffer after it has been presented.
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Unspecified,
            Flags = AllowsTearing ? 0x800u : 0u,
        };

        ComPtr<IDXGISwapChain1> chain1 = default;

        D3D12Exception.ThrowIfFailed(
            _context.Factory->CreateSwapChainForHwnd(
                (IUnknown*)_context.Queue,
                _window,
                &description,
                null,
                (IDXGIOutput*)null,
                chain1.GetAddressOf()),
            "create the swapchain");

        try
        {
            Guid chainId = IDXGISwapChain4.Guid;
            D3D12Exception.ThrowIfFailed(
                chain1.QueryInterface(&chainId, (void**)_chain.GetAddressOf()),
                "get a modern swapchain interface");
        }
        finally
        {
            chain1.Dispose();
        }

        // Alt+Enter belongs to the game, not to DXGI. Left alone, DXGI changes the display
        // mode behind the renderer's back and the swapchain it then presents from is one
        // nothing here made.
        _context.Factory->MakeWindowAssociation(_window, 1 /* DXGI_MWA_NO_ALT_ENTER */);

        _renderTargets = D3D12DescriptorHeap.Create(
            _context.Device, DescriptorHeapType.Rtv, BufferCount);

        Size = ((int)description.Width, (int)description.Height);
        Format = description.Format;

        Format wanted = Choose(wantHdr, out ColorSpaceType space);
        if (wanted != Format)
        {
            Resize((int)description.Width, (int)description.Height, wantHdr);
            return;
        }

        ApplyColorSpace(space);
        BindBuffers();
    }

    private bool TearingAllowed()
    {
        uint allowed = 0;

        // DXGI_FEATURE_PRESENT_ALLOW_TEARING.
        if (_context.Factory->CheckFeatureSupport(
                Silk.NET.DXGI.Feature.PresentAllowTearing, &allowed, sizeof(uint)) < 0)
        {
            return false;
        }

        return allowed != 0;
    }

    private void BindBuffers()
    {
        _renderTargets!.Reset();

        for (uint i = 0; i < BufferCount; i++)
        {
            Guid resourceId = ID3D12Resource.Guid;

            D3D12Exception.ThrowIfFailed(
                _chain.GetBuffer(i, &resourceId, (void**)_buffers[i].GetAddressOf()),
                $"take back buffer {i}");

            uint slot = _renderTargets.Allocate();
            _context.Device->CreateRenderTargetView(
                _buffers[i].Handle, (RenderTargetViewDesc*)null, _renderTargets.Cpu(slot));

            // A back buffer comes out of DXGI in the Present state, which is where the
            // renderer must put it back before each present.
            _states[i] = ResourceStates.Present;
        }
    }
}
