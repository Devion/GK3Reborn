using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Feature = Silk.NET.Direct3D12.Feature;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Owns the Direct3D device and the operations everything else needs from it.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <c>VulkanContext</c>: allocation, one-shot submission and resource
/// transitions in one place, so that subtly different versions of the same barrier do not
/// end up scattered through a renderer.
/// </para>
/// <para>
/// Two things are markedly simpler here than on the Vulkan side and one is markedly harder.
/// Simpler: a committed resource carries its own heap, so there is no allocator to write and
/// no thousand-allocation limit to fear; and a queue is a queue, with no family to select or
/// present capability to check. Harder: every resource has a *state* rather than a layout
/// plus access mask, and a state transition is not optional the way a well-chosen Vulkan
/// layout sometimes is — a resource read in the wrong state is undefined data with no
/// validation message unless the debug layer is on.
/// </para>
/// <para>
/// One direct queue does everything. The renderer has no async compute and no copy queue:
/// its compute passes read what the raster passes wrote, in order, within a frame, so a
/// second queue would buy nothing but two more fences to get wrong.
/// </para>
/// </remarks>
public sealed unsafe class D3D12Context : IDisposable
{
    private readonly D3D12 _d3d12;
    private readonly DXGI _dxgi;

    private ComPtr<IDXGIFactory6> _factory;
    private ComPtr<IDXGIAdapter1> _adapter;
    private ComPtr<ID3D12Device5> _device;
    private ComPtr<ID3D12CommandQueue> _queue;

    private ComPtr<ID3D12CommandAllocator> _oneShotAllocator;
    private ComPtr<ID3D12GraphicsCommandList4> _oneShotList;
    private ComPtr<ID3D12Fence1> _oneShotFence;
    private ulong _oneShotValue;
    private AutoResetEvent? _oneShotEvent;
    private bool _oneShotOpen;

    private bool _disposed;

    private D3D12Context(D3D12 d3d12, DXGI dxgi)
    {
        _d3d12 = d3d12;
        _dxgi = dxgi;
    }

    /// <summary>The Direct3D 12 API.</summary>
    public D3D12 Api => _d3d12;

    /// <summary>The DXGI API, which owns adapters and swapchains.</summary>
    public DXGI Dxgi => _dxgi;

    /// <summary>The factory the adapter came from, which a swapchain is also made by.</summary>
    public IDXGIFactory6* Factory => _factory.Handle;

    /// <summary>The adapter in use.</summary>
    public IDXGIAdapter1* Adapter => _adapter.Handle;

    /// <summary>The device.</summary>
    public ID3D12Device5* Device => _device.Handle;

    /// <summary>The one queue, which does graphics, compute and copies alike.</summary>
    public ID3D12CommandQueue* Queue => _queue.Handle;

    /// <summary>Name of the adapter in use.</summary>
    public string DeviceName { get; private set; } = "unknown";

    /// <summary>What this adapter offers of what the renderer would like to use.</summary>
    public AdapterInfo Adapter1 { get; private set; } = null!;

    /// <summary>Whether acceleration structures and inline ray queries are available.</summary>
    /// <remarks>
    /// Inline, specifically. Ray-tracing tier 1.0 has acceleration structures and a shader
    /// table and cannot run a <c>RayQuery</c>, which is the only form these shaders use, so
    /// a tier 1.0 device reports false here however capable it looks.
    /// </remarks>
    public bool SupportsRayTracing => Adapter1.Tiers.HasFlag(RenderCapabilityTier.RayTracing);

    /// <summary>The oldest shader model a Direct3D 12 device can load: 6.0, which is DXIL.</summary>
    public const uint LowestShaderModel = 0x60;

    /// <summary>The feature level the device actually has.</summary>
    /// <remarks>
    /// Not what was asked for. The device is created against the 11_0 floor and this is
    /// what came back, so a first-generation Maxwell reads 11_0 here and an RTX reads 12_2.
    /// The renderer's raster path needs nothing above the floor; ray tracing is a tier of
    /// its own and is read from <see cref="SupportsRayTracing"/>.
    /// </remarks>
    public D3DFeatureLevel FeatureLevel { get; private set; } = D3DFeatureLevel.Level110;

    /// <summary>The highest shader model the device accepts, as D3D writes it (0x65 is 6.5).</summary>
    public uint ShaderModel { get; private set; } = LowestShaderModel;

    /// <summary>The shader model the renderer's DXIL is compiled for on this device.</summary>
    /// <remarks>
    /// The device's own, capped at 6.5, which is the highest anything here is written for
    /// and the floor for <c>RayQuery</c>. A device that stops short of 6.5 cannot have been
    /// given the ray-tracing tier — the survey ties the two together — so the shaders it is
    /// handed never contain a ray query, and nothing else in them needs more than 6.0.
    /// </remarks>
    public uint DxilShaderModel => Math.Min(ShaderModel, D3D12DeviceSelector.RequiredShaderModel);

    /// <summary>The adapter's locally unique identifier, eight bytes.</summary>
    /// <remarks>
    /// How Streamline names an adapter on this backend — Vulkan hands it a physical device
    /// handle instead, which is the one place the two disagree about identity. DXGI has it in
    /// the adapter description, so there is nothing to derive.
    /// </remarks>
    public byte[] AdapterLuid { get; private set; } = new byte[8];

    /// <summary>Whether the debug layer is on for this device.</summary>
    public bool Validating { get; private set; }

    /// <summary>Creates a device on the adapter the selector would choose.</summary>
    /// <param name="enableValidation">Whether to turn the debug layer on when it is installed.</param>
    /// <returns>The context.</returns>
    /// <exception cref="D3D12Exception">No usable adapter, or the device would not start.</exception>
    /// <remarks>
    /// The debug layer must be asked for before the device is made, not after, and asking
    /// for one that is not installed fails device creation outright rather than degrading.
    /// So it is checked first and turned on only if it is there.
    /// </remarks>
    public static D3D12Context Create(bool enableValidation = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new D3D12Exception("Direct3D is a Windows API and this is not Windows.");
        }

        D3D12 d3d12;
        DXGI dxgi;

        try
        {
            d3d12 = D3D12Runtime.D3D12;
            dxgi = D3D12Runtime.Dxgi;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new D3D12Exception("The Direct3D 12 runtime is not present.", exception);
        }

        var context = new D3D12Context(d3d12, dxgi);

        try
        {
            context.Start(enableValidation);
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>Makes a buffer in device memory.</summary>
    /// <param name="bytes">How large.</param>
    /// <param name="heap">Which memory it lives in.</param>
    /// <param name="state">Which state it starts in.</param>
    /// <param name="allowUnorderedAccess">Whether a shader may write to it.</param>
    /// <returns>The resource.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    /// <remarks>
    /// An upload heap resource must start in <c>GenericRead</c> and a readback one in
    /// <c>CopyDest</c>; the runtime refuses anything else. Rather than make every caller
    /// remember that, the state asked for is corrected to the only legal one.
    /// </remarks>
    public ComPtr<ID3D12Resource> CreateBuffer(
        ulong bytes,
        HeapType heap = HeapType.Default,
        ResourceStates state = ResourceStates.Common,
        bool allowUnorderedAccess = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ResourceStates initial = heap switch
        {
            HeapType.Upload => ResourceStates.GenericRead,
            HeapType.Readback => ResourceStates.CopyDest,

            // A buffer in device memory is created in Common whatever is asked for. The
            // runtime does not refuse another state, it ignores it and says so in the debug
            // layer, which means the caller's idea of the state and the runtime's have
            // silently diverged before the first barrier. An acceleration structure is the
            // one exception and must be created in its own state.
            _ when state == ResourceStates.RaytracingAccelerationStructure => state,
            _ => ResourceStates.Common,
        };

        var properties = new HeapProperties
        {
            Type = heap,
            CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown,
            CreationNodeMask = 1,
            VisibleNodeMask = 1,
        };

        var description = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer,
            Alignment = 0,

            // A zero-byte buffer is legal to ask for and illegal to create, and it happens
            // in the ordinary course of things: a scene with no lights has no light buffer.
            // One byte costs nothing and keeps every caller from having to special-case it.
            Width = Math.Max(1, bytes),
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatUnknown,
            SampleDesc = new SampleDesc(1, 0),

            // Buffers are always row-major. Anything else is refused.
            Layout = TextureLayout.LayoutRowMajor,
            Flags = allowUnorderedAccess
                ? ResourceFlags.AllowUnorderedAccess
                : ResourceFlags.None,
        };

        ComPtr<ID3D12Resource> resource = default;
        Guid resourceId = ID3D12Resource.Guid;

        D3D12Exception.ThrowIfFailed(
            _device.CreateCommittedResource(
                &properties,
                HeapFlags.None,
                &description,
                initial,
                (ClearValue*)null,
                &resourceId,
                (void**)resource.GetAddressOf()),
            $"create a {bytes}-byte buffer in the {heap} heap");

        return resource;
    }

    /// <summary>Starts recording work that runs once and is waited for.</summary>
    /// <returns>A command list, already open.</returns>
    /// <remarks>
    /// Uploads and acceleration structure builds. Not re-entrant, and deliberately not: two
    /// overlapping one-shots would need two allocators and a fence each, and nothing here
    /// wants that. See <see cref="EndOneShot"/>.
    /// </remarks>
    public ID3D12GraphicsCommandList4* BeginOneShot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_oneShotOpen)
        {
            throw new InvalidOperationException(
                "A one-shot command list is already open; finish it before starting another.");
        }

        D3D12Exception.ThrowIfFailed(_oneShotAllocator.Reset(), "reset the one-shot allocator");
        D3D12Exception.ThrowIfFailed(
            _oneShotList.Reset(_oneShotAllocator, (ID3D12PipelineState*)null), "reset the one-shot list");

        _oneShotOpen = true;
        return _oneShotList.Handle;
    }

    /// <summary>Submits one-shot work and waits for it.</summary>
    /// <exception cref="D3D12Exception">The work could not be submitted.</exception>
    public void EndOneShot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_oneShotOpen)
        {
            return;
        }

        _oneShotOpen = false;

        D3D12Exception.ThrowIfFailed(_oneShotList.Close(), "close the one-shot list");

        ID3D12CommandList* list = (ID3D12CommandList*)_oneShotList.Handle;
        _queue.ExecuteCommandLists(1, &list);

        Wait();
    }

    /// <summary>Waits until the queue has finished everything given to it.</summary>
    /// <exception cref="D3D12Exception">The wait could not be set up.</exception>
    /// <remarks>
    /// The Direct3D spelling of <c>vkDeviceWaitIdle</c>, which does not exist here: a fence
    /// is signalled at the end of the queue and the thread waits on it. Called before
    /// anything the device might still be reading is freed.
    /// </remarks>
    public void Wait()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WaitCore();
    }

    /// <summary>Waits without minding whether this context is being disposed.</summary>
    /// <remarks>
    /// Disposal has to wait — freeing a resource the device is still reading is the whole
    /// hazard — but it has already said the context is disposed by the time it gets there,
    /// so it cannot use the public form. Splitting the check off is the entire difference.
    /// </remarks>
    private void WaitCore()
    {
        ulong target = ++_oneShotValue;
        D3D12Exception.ThrowIfFailed(_queue.Signal(_oneShotFence, target), "signal the queue");

        if (_oneShotFence.GetCompletedValue() >= target)
        {
            return;
        }

        D3D12Exception.ThrowIfFailed(
            _oneShotFence.SetEventOnCompletion(
                target, (void*)_oneShotEvent!.SafeWaitHandle.DangerousGetHandle()),
            "wait for the queue");

        _oneShotEvent.WaitOne();
    }

    /// <summary>Everything the debug layer has said since it was last asked.</summary>
    /// <returns>The messages, oldest first, or nothing when validation is off.</returns>
    /// <remarks>
    /// <para>
    /// Direct3D's diagnostics do not arrive anywhere by themselves. The debug layer writes
    /// them into a queue on the device and something has to come and read it; a program
    /// that never does gets an <c>HRESULT</c> and no more, which for a whole class of
    /// mistake — a resource in the wrong state, a root signature that does not match its
    /// shader — is a number with no way back to the line that caused it.
    /// </para>
    /// <para>
    /// Reading the queue clears it, so this is called after anything that failed and at the
    /// end of a frame that did not, and the messages are attached to whatever is reported.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> DrainMessages()
    {
        if (_disposed || !Validating || _device.Handle is null)
        {
            return [];
        }

        ComPtr<ID3D12InfoQueue> queue = default;
        Guid queueId = ID3D12InfoQueue.Guid;

        if (_device.QueryInterface(&queueId, (void**)queue.GetAddressOf()) < 0)
        {
            return [];
        }

        try
        {
            ulong count = queue.GetNumStoredMessages();
            if (count == 0)
            {
                return [];
            }

            List<string> messages = [];

            for (ulong i = 0; i < count; i++)
            {
                nuint length = 0;
                if (queue.GetMessageA(i, (Message*)null, &length) < 0 || length == 0)
                {
                    continue;
                }

                byte[] storage = new byte[length];
                fixed (byte* bytes = storage)
                {
                    var message = (Message*)bytes;
                    if (queue.GetMessageA(i, message, &length) < 0)
                    {
                        continue;
                    }

                    string text = System.Runtime.InteropServices.Marshal
                        .PtrToStringAnsi((nint)message->PDescription, (int)Math.Max(0, (long)message->DescriptionByteLength - 1));

                    messages.Add($"[{message->Severity}] {text}");
                }
            }

            queue.ClearStoredMessages();
            return messages;
        }
        finally
        {
            queue.Dispose();
        }
    }

    /// <summary>Moves a resource from one state to another.</summary>
    /// <param name="list">The list to record into.</param>
    /// <param name="resource">What to move.</param>
    /// <param name="from">The state it is in.</param>
    /// <param name="to">The state it should be in.</param>
    /// <remarks>
    /// A transition to the state a resource is already in is not a no-op to the runtime; it
    /// is an error. Since the states are tracked by the callers rather than by the runtime,
    /// the redundant case is filtered here instead of at every call.
    /// </remarks>
    public static void Transition(
        ID3D12GraphicsCommandList4* list,
        ID3D12Resource* resource,
        ResourceStates from,
        ResourceStates to)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (from == to)
        {
            return;
        }

        var barrier = new ResourceBarrier
        {
            Type = ResourceBarrierType.Transition,
            Flags = ResourceBarrierFlags.None,
        };

        barrier.Anonymous.Transition = new ResourceTransitionBarrier
        {
            PResource = resource,
            Subresource = 0xFFFFFFFF,
            StateBefore = from,
            StateAfter = to,
        };

        list->ResourceBarrier(1, &barrier);
    }

    /// <summary>Moves one subresource of a resource from one state to another.</summary>
    /// <param name="list">The list to record into.</param>
    /// <param name="resource">What to move.</param>
    /// <param name="from">The state that subresource is in.</param>
    /// <param name="to">The state it should be in.</param>
    /// <param name="subresource">Which one.</param>
    /// <remarks>
    /// Direct3D tracks state per subresource, which Vulkan does too and which almost
    /// nothing needs — except building a mip chain, where level <c>n</c> is read while
    /// level <c>n + 1</c> is written and the two are subresources of one texture. Moving
    /// the whole resource would mean it was being read and written in the same state,
    /// which is not a thing that can be said.
    /// </remarks>
    public static void TransitionSubresource(
        ID3D12GraphicsCommandList4* list,
        ID3D12Resource* resource,
        ResourceStates from,
        ResourceStates to,
        uint subresource)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (from == to)
        {
            return;
        }

        var barrier = new ResourceBarrier
        {
            Type = ResourceBarrierType.Transition,
            Flags = ResourceBarrierFlags.None,
        };

        barrier.Anonymous.Transition = new ResourceTransitionBarrier
        {
            PResource = resource,
            Subresource = subresource,
            StateBefore = from,
            StateAfter = to,
        };

        list->ResourceBarrier(1, &barrier);
    }

    /// <summary>Waits for every shader write to a resource before the next read.</summary>
    /// <param name="list">The list to record into.</param>
    /// <param name="resource">What was written, or null for all of them.</param>
    /// <remarks>
    /// The compute passes read what the pass before them wrote, in the same state, so there
    /// is no transition to carry the dependency. Without this the reads are not ordered
    /// against the writes at all and the result is noise that changes between runs.
    /// </remarks>
    public static void Barrier(ID3D12GraphicsCommandList4* list, ID3D12Resource* resource)
    {
        ArgumentNullException.ThrowIfNull(list);

        var barrier = new ResourceBarrier
        {
            Type = ResourceBarrierType.Uav,
            Flags = ResourceBarrierFlags.None,
        };

        barrier.Anonymous.UAV = new ResourceUavBarrier { PResource = resource };
        list->ResourceBarrier(1, &barrier);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_queue.Handle is not null && _oneShotFence.Handle is not null)
        {
            try
            {
                WaitCore();
            }
            catch (D3D12Exception)
            {
                // A device that has already been removed cannot be waited for, and there
                // is nothing to do about it here: everything below is released either way.
            }
        }

        _oneShotList.Dispose();
        _oneShotAllocator.Dispose();
        _oneShotFence.Dispose();

        _oneShotEvent?.Dispose();
        _oneShotEvent = null;

        _queue.Dispose();
        _device.Dispose();
        _adapter.Dispose();
        _factory.Dispose();

        // The two library handles belong to D3D12Runtime and outlive this context; see the
        // note there. Everything above is this context's own and is released.
    }

    private void Start(bool enableValidation)
    {
        uint factoryFlags = 0;

        if (enableValidation && D3D12DeviceSelector.HasDebugLayer(_d3d12))
        {
            ComPtr<ID3D12Debug> debug = default;
            Guid debugId = ID3D12Debug.Guid;

            if (_d3d12.GetDebugInterface(&debugId, (void**)debug.GetAddressOf()) >= 0)
            {
                debug.EnableDebugLayer();
                debug.Dispose();

                // DXGI_CREATE_FACTORY_DEBUG. Without it the DXGI half of a failure — every
                // swapchain and present error — says nothing at all.
                factoryFlags = 0x1;
                Validating = true;
            }
        }

        Guid factoryId = IDXGIFactory6.Guid;
        D3D12Exception.ThrowIfFailed(
            _dxgi.CreateDXGIFactory2(factoryFlags, &factoryId, (void**)_factory.GetAddressOf()),
            "start DXGI");

        DeviceReport report = D3D12DeviceSelector.Survey();
        AdapterInfo chosen = report.Selected
            ?? throw new D3D12Exception(
                report.Unavailable ?? "no adapter on this machine supports Direct3D 12.");

        SelectAdapter(chosen);

        // 11_0 is a floor, not a request: the device that comes back has every capability
        // the hardware has, whatever minimum was named. It used to say 12_0 here, and that
        // was the whole of why a GeForce GTX 960M — first-generation Maxwell, which reports
        // 11_0 and runs everything the raster path asks of it — failed to start with
        // DXGI_ERROR_UNSUPPORTED while the survey above had just made a device on it. The
        // survey asks for 11_0; asking for anything else here is asking a different question.
        Guid deviceId = ID3D12Device5.Guid;
        D3D12Exception.ThrowIfFailed(
            _d3d12.CreateDevice(
                (IUnknown*)_adapter.Handle,
                D3DFeatureLevel.Level110,
                &deviceId,
                (void**)_device.GetAddressOf()),
            $"create a device on {chosen.Name}");

        DeviceName = chosen.Name;
        Adapter1 = chosen;

        // What was actually made, asked of the device itself rather than read back out of
        // the survey's text. The shaders are compiled for this: DXC is told a profile, and
        // a module compiled for a newer model than the driver has is refused at pipeline
        // creation with an error that names neither.
        FeatureLevel = D3D12DeviceSelector.HighestFeatureLevel(ref _device);
        ShaderModel = D3D12DeviceSelector.HighestShaderModel(ref _device);

        if (ShaderModel < LowestShaderModel)
        {
            throw new D3D12Exception(
                $"{chosen.Name} reports shader model " +
                $"{D3D12DeviceSelector.Name(ShaderModel)}, and Direct3D 12 loads nothing " +
                "older than 6.0. A newer driver may add it; otherwise run with --vulkan.");
        }

        Foundation.Diagnostics.Log.Info(
            $"Direct3D 12: {chosen.Name}, feature level " +
            $"{D3D12DeviceSelector.Name(FeatureLevel)}, shader model " +
            $"{D3D12DeviceSelector.Name(ShaderModel)}; shaders compiled for " +
            $"{D3D12DeviceSelector.Name(DxilShaderModel)}");

        var queueDescription = new CommandQueueDesc
        {
            Type = CommandListType.Direct,
            Priority = 0,
            Flags = CommandQueueFlags.None,
            NodeMask = 0,
        };

        Guid queueId = ID3D12CommandQueue.Guid;
        D3D12Exception.ThrowIfFailed(
            _device.CreateCommandQueue(&queueDescription, &queueId, (void**)_queue.GetAddressOf()),
            "create the command queue");

        Guid allocatorId = ID3D12CommandAllocator.Guid;
        D3D12Exception.ThrowIfFailed(
            _device.CreateCommandAllocator(
                CommandListType.Direct, &allocatorId, (void**)_oneShotAllocator.GetAddressOf()),
            "create the one-shot command allocator");

        Guid listId = ID3D12GraphicsCommandList4.Guid;
        D3D12Exception.ThrowIfFailed(
            _device.CreateCommandList(
                0,
                CommandListType.Direct,
                _oneShotAllocator,
                (ID3D12PipelineState*)null,
                &listId,
                (void**)_oneShotList.GetAddressOf()),
            "create the one-shot command list");

        // A command list is created open and BeginOneShot resets it, which a list must be
        // closed to allow. Closing it here is what makes the first BeginOneShot legal.
        D3D12Exception.ThrowIfFailed(_oneShotList.Close(), "close the one-shot command list");

        Guid fenceId = ID3D12Fence1.Guid;
        D3D12Exception.ThrowIfFailed(
            _device.CreateFence(0, FenceFlags.None, &fenceId, (void**)_oneShotFence.GetAddressOf()),
            "create the one-shot fence");

        // A fence signals an operating system event, so the wait needs a real handle
        // rather than anything the runtime can synthesise. Taking it from a managed event
        // keeps the handle owned and closed by something that knows how, which three
        // hand-written imports of kernel32 would not.
        //
        // Auto-reset, and that is not a detail. A manual-reset event stays signalled once
        // the first wait has set it, so every wait after the first returns at once - which
        // does not look like a synchronisation bug, it looks like the command allocator
        // being reset while the device is still reading it, three messages deep in the
        // debug layer and a removed device after that. It is how this was found.
        _oneShotEvent = new AutoResetEvent(false);
    }

    private void SelectAdapter(AdapterInfo chosen)
    {
        for (uint index = 0; ; index++)
        {
            ComPtr<IDXGIAdapter1> candidate = default;
            Guid adapterId = IDXGIAdapter1.Guid;

            int hr = _factory.EnumAdapterByGpuPreference(
                index, GpuPreference.HighPerformance, &adapterId, (void**)candidate.GetAddressOf());

            if (hr < 0)
            {
                break;
            }

            AdapterDesc1 description = default;
            if (candidate.GetDesc1(&description) >= 0)
            {
                string name = Marshal.PtrToStringUni((nint)description.Description) ?? string.Empty;
                bool software = (description.Flags & (uint)AdapterFlag.Software) != 0;

                if (string.Equals(name, chosen.Name, StringComparison.Ordinal)
                    && software == chosen.Kind.Equals("software", StringComparison.Ordinal))
                {
                    _adapter = candidate;

                    BitConverter.TryWriteBytes(AdapterLuid.AsSpan(0), description.AdapterLuid.Low);
                    BitConverter.TryWriteBytes(AdapterLuid.AsSpan(4), description.AdapterLuid.High);

                    return;
                }
            }

            candidate.Dispose();
        }

        throw new D3D12Exception($"The adapter the survey chose, {chosen.Name}, is no longer there.");
    }
}
