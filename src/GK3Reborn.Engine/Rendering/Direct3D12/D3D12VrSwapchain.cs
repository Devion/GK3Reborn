using GK3Reborn.Rendering.OpenXR;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.OpenXR;
using XrStructure = Silk.NET.OpenXR.StructureType;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>Runtime-owned images; only the RTV descriptors belong to the game.</summary>
internal sealed unsafe class D3D12VrSwapchain : IDisposable
{
    private readonly OpenXrSession _session;
    private D3D12DescriptorHeap? _targets;
    private readonly SwapchainImageD3D12KHR[] _images = [];
    private bool _acquired;
    private bool _waited;
    private uint _index;
    public Swapchain Handle;
    public int Width { get; }
    public int Height { get; }
    public CpuDescriptorHandle Target => _targets!.Cpu(_index);
    public SwapchainSubImage SubImage => new()
    {
        Swapchain = Handle,
        ImageRect = new Rect2Di { Extent = new Extent2Di(Width, Height) }
    };

    public D3D12VrSwapchain(OpenXrSession session, D3D12Context context, int width, int height, Format format)
    {
        _session = session; Width = width; Height = height;
        try
        {
            var info = new SwapchainCreateInfo
            {
                Type = XrStructure.SwapchainCreateInfo,
                UsageFlags = SwapchainUsageFlags.ColorAttachmentBit,
                Format = (long)format,
                SampleCount = 1,
                Width = (uint)width,
                Height = (uint)height,
                FaceCount = 1,
                ArraySize = 1,
                MipCount = 1
            };
            OpenXrSession.Check(session.Api.CreateSwapchain(session.Session, ref info, ref Handle), "create eye swapchain");
            uint count = 0;
            OpenXrSession.Check(session.Api.EnumerateSwapchainImages(Handle, 0, &count, null), "count headset images");
            _images = new SwapchainImageD3D12KHR[count];
            for (int i = 0; i < _images.Length; i++) { _images[i].Type = XrStructure.SwapchainImageD3D12Khr; }
            fixed (SwapchainImageD3D12KHR* images = _images)
            {
                OpenXrSession.Check(session.Api.EnumerateSwapchainImages(Handle, count, &count, (SwapchainImageBaseHeader*)images), "get headset images");
            }
            _targets = D3D12DescriptorHeap.Create(context.Device, DescriptorHeapType.Rtv, count);
            for (uint i = 0; i < count; i++)
            {
                var target = new RenderTargetViewDesc { Format = format, ViewDimension = RtvDimension.Texture2D };
                context.Device->CreateRenderTargetView((ID3D12Resource*)_images[i].Texture, &target, _targets.Cpu(i));
            }
        }
        catch { Dispose(); throw; }
    }
    public void Acquire()
    {
        var acquire = new SwapchainImageAcquireInfo { Type = XrStructure.SwapchainImageAcquireInfo };
        OpenXrSession.Check(_session.Api.AcquireSwapchainImage(Handle, ref acquire, ref _index), "acquire headset image");
        _acquired = true;
        var wait = new SwapchainImageWaitInfo { Type = XrStructure.SwapchainImageWaitInfo, Timeout = long.MaxValue };
        OpenXrSession.Check(_session.Api.WaitSwapchainImage(Handle, ref wait), "wait for headset image");
        _waited = true;
    }
    public void Release()
    {
        if (!_acquired || !_waited) { return; }
        var release = new SwapchainImageReleaseInfo { Type = XrStructure.SwapchainImageReleaseInfo };
        OpenXrSession.Check(_session.Api.ReleaseSwapchainImage(Handle, ref release), "release headset image");
        _acquired = _waited = false;
    }
    public void Dispose()
    {
        _targets?.Dispose(); _targets = null;
        if (Handle.Handle != 0) { _session.Api.DestroySwapchain(Handle); Handle = default; }
    }
}
