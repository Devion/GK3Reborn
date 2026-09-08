using System.Globalization;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Feature = Silk.NET.Direct3D12.Feature;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Finds out what Direct3D adapters the machine has and what they can do.
/// </summary>
public static unsafe class D3D12DeviceSelector
{
    /// <summary>
    /// The shader model the renderer's shaders are compiled against.
    /// </summary>
    internal const uint RequiredShaderModel = 0x65;

    /// <summary>Surveys every Direct3D 12 adapter on the machine.</summary>
    /// <returns>What was found, and which one would be used.</returns>
    public static DeviceReport Survey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return DeviceReport.Missing(
                RenderBackend.Direct3D12, "Direct3D is a Windows API and this is not Windows.");
        }

        DXGI dxgi;
        D3D12 d3d12;

        try
        {
            dxgi = D3D12Runtime.Dxgi;
            d3d12 = D3D12Runtime.D3D12;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return DeviceReport.Missing(
                RenderBackend.Direct3D12, "the Direct3D 12 runtime is not present.");
        }

        try
        {
            ComPtr<IDXGIFactory6> factory = default;
            Guid factoryId = IDXGIFactory6.Guid;

            int hr = dxgi.CreateDXGIFactory2(0u, &factoryId, (void**)factory.GetAddressOf());
            if (hr < 0)
            {
                return DeviceReport.Missing(
                    RenderBackend.Direct3D12,
                    string.Create(CultureInfo.InvariantCulture, $"DXGI would not start: 0x{hr:X8}."));
            }

            try
            {
                List<AdapterInfo> adapters = [];

                // DXGI hands the same physical adapter back more than once on a machine
                // with more than one way of reaching it — a desktop with an RTX 5090 lists
                // it twice — and a report that names one card twice reads as two cards.
                // The LUID is the thing that is actually unique; the name is not.
                HashSet<long> seen = [];

                for (uint index = 0; ; index++)
                {
                    ComPtr<IDXGIAdapter1> adapter = default;
                    Guid adapterId = IDXGIAdapter1.Guid;

                    hr = factory.EnumAdapterByGpuPreference(
                        index,
                        GpuPreference.HighPerformance,
                        &adapterId,
                        (void**)adapter.GetAddressOf());

                    // DXGI_ERROR_NOT_FOUND. The end of the list, not a failure.
                    if (unchecked((uint)hr) == 0x887A0002)
                    {
                        break;
                    }

                    if (hr < 0)
                    {
                        break;
                    }

                    try
                    {
                        AdapterInfo? info = Describe(d3d12, ref adapter, out long luid);
                        if (info is not null && seen.Add(luid))
                        {
                            adapters.Add(info);
                        }
                    }
                    finally
                    {
                        adapter.Dispose();
                    }
                }

                return new DeviceReport
                {
                    Backend = RenderBackend.Direct3D12,
                    Available = true,
                    Unavailable = adapters.Count > 0 ? null : "no adapter supports Direct3D 12.",
                    ValidationAvailable = HasDebugLayer(d3d12),
                    Adapters = adapters,
                    Selected = Select(adapters),
                };
            }
            finally
            {
                factory.Dispose();
            }
        }
        finally
        {
            // The two library handles are D3D12Runtime's and stay loaded; see the note
            // there. The factory and the adapters above are this survey's and are released.
        }
    }

    /// <summary>Whether the debug layer is installed and could be turned on.</summary>
    /// <param name="d3d12">The API.</param>
    /// <returns>True if it is there.</returns>
    internal static bool HasDebugLayer(D3D12 d3d12)
    {
        ComPtr<ID3D12Debug> debug = default;
        Guid debugId = ID3D12Debug.Guid;

        int hr = d3d12.GetDebugInterface(&debugId, (void**)debug.GetAddressOf());
        if (hr < 0)
        {
            return false;
        }

        debug.Dispose();
        return true;
    }

    /// <summary>Which adapter the renderer would run on.</summary>
    /// <param name="adapters">Every adapter found, in the order DXGI preferred them.</param>
    /// <returns>The one to use, or null when there is nothing to use.</returns>
    private static AdapterInfo? Select(List<AdapterInfo> adapters) =>
        adapters.FirstOrDefault(a => !a.Kind.Equals("software", StringComparison.Ordinal))
        ?? adapters.FirstOrDefault();

    private static AdapterInfo? Describe(D3D12 d3d12, ref ComPtr<IDXGIAdapter1> adapter, out long luid)
    {
        luid = 0;

        AdapterDesc1 description = default;
        if (adapter.GetDesc1(&description) < 0)
        {
            return null;
        }

        luid = ((long)description.AdapterLuid.High << 32) | (uint)description.AdapterLuid.Low;

        // DXGI_ADAPTER_FLAG_SOFTWARE. WARP renders correctly and at about one frame a
        // minute; offering it as a choice would be offering a machine that appears to have
        // hung. It is still reported, so that a log from a machine with nothing else says
        // what it had.
        bool software = (description.Flags & (uint)AdapterFlag.Software) != 0;

        ComPtr<ID3D12Device5> device = default;
        Guid deviceId = ID3D12Device5.Guid;

        int hr = d3d12.CreateDevice(
            (IUnknown*)adapter.Handle, D3DFeatureLevel.Level110, &deviceId, (void**)device.GetAddressOf());

        if (hr < 0)
        {
            return null;
        }

        try
        {
            List<string> notes = [];

            D3DFeatureLevel level = HighestFeatureLevel(ref device);
            uint shaderModel = HighestShaderModel(ref device);

            FeatureDataD3D12Options options = default;
            device.CheckFeatureSupport(Feature.D3D12Options, &options, (uint)sizeof(FeatureDataD3D12Options));

            FeatureDataD3D12Options5 options5 = default;
            device.CheckFeatureSupport(Feature.D3D12Options5, &options5, (uint)sizeof(FeatureDataD3D12Options5));

            RenderCapabilityTier tiers = RenderCapabilityTier.Compatibility;

            if (level >= D3DFeatureLevel.Level120 && options.ResourceBindingTier >= ResourceBindingTier.Tier2)
            {
                tiers |= RenderCapabilityTier.Enhanced;
            }
            else
            {
                notes.Add(
                    $"no enhanced tier: feature level {Name(level)}, resource binding {options.ResourceBindingTier}");
            }

            // Tier 1.0 is DXR with a shader table, which none of these shaders is written
            // for. Inline ray tracing is 1.1, and is the only form the renderer uses.
            if (options5.RaytracingTier >= RaytracingTier.Tier11 && shaderModel >= RequiredShaderModel)
            {
                tiers |= RenderCapabilityTier.RayTracing;
                notes.Add($"inline ray tracing at {options5.RaytracingTier}");
            }
            else if (options5.RaytracingTier == RaytracingTier.Tier10)
            {
                notes.Add(
                    "no ray tracing: the adapter is tier 1.0, which has no inline ray query; "
                    + "these shaders use nothing else");
            }
            else if (shaderModel < RequiredShaderModel)
            {
                notes.Add(
                    $"no ray tracing: shader model {Name(shaderModel)}, and ray query needs 6.5");
            }
            else
            {
                notes.Add("no ray tracing: the adapter does not support it");
            }

            if (HasHdrOutput(ref adapter))
            {
                tiers |= RenderCapabilityTier.HighDynamicRange;
                notes.Add("an attached display accepts ST.2084");
            }

            if (software)
            {
                notes.Add("software adapter: correct, and far too slow to play");
            }

            return new AdapterInfo
            {
                Name = Marshal.PtrToStringUni((nint)description.Description) ?? "unknown",

                // Not from the memory size, which lies. An integrated Radeon reports a
                // two-gigabyte carve-out of system memory as dedicated video memory and
                // reads as a discrete card. UMA is the question actually being asked —
                // whether the adapter has memory of its own or shares the host's — and
                // Direct3D answers it outright.
                Kind = software ? "software" : !Architecture(ref device).UMA ? "discrete" : "integrated",
                Backend = RenderBackend.Direct3D12,
                ApiVersion = $"feature level {Name(level)}, shader model {Name(shaderModel)}",

                // Direct3D does not report a driver version through DXGI at all; it is in
                // the registry, under the adapter's own key. Saying so is more honest than
                // printing a zero that reads as a version.
                DriverVersion = "not reported by DXGI",
                VendorId = description.VendorId,
                DeviceLocalMemory = description.DedicatedVideoMemory,
                Tiers = tiers,

                // BC1 through BC7 are required of every Direct3D 12 device, so the
                // content pipeline's blocks always upload as they are. This is the field
                // that is false on Apple silicon under Vulkan; here it cannot be.
                BlockCompression = true,
                Notes = notes,
            };
        }
        finally
        {
            device.Dispose();
        }
    }

    private static FeatureDataArchitecture Architecture(ref ComPtr<ID3D12Device5> device)
    {
        FeatureDataArchitecture architecture = default;
        device.CheckFeatureSupport(
            Feature.Architecture, &architecture, (uint)sizeof(FeatureDataArchitecture));

        return architecture;
    }

    /// <summary>The highest feature level a device supports.</summary>
    /// <param name="device">The device.</param>
    /// <returns>The level, or 11_0 if the runtime will not say.</returns>
    internal static D3DFeatureLevel HighestFeatureLevel(ref ComPtr<ID3D12Device5> device)
    {
        D3DFeatureLevel[] wanted =
        [
            D3DFeatureLevel.Level122,
            D3DFeatureLevel.Level121,
            D3DFeatureLevel.Level120,
            D3DFeatureLevel.Level111,
            D3DFeatureLevel.Level110,
        ];

        fixed (D3DFeatureLevel* levels = wanted)
        {
            var query = new FeatureDataFeatureLevels
            {
                NumFeatureLevels = (uint)wanted.Length,
                PFeatureLevelsRequested = levels,
            };

            if (device.CheckFeatureSupport(
                    Feature.FeatureLevels, &query, (uint)sizeof(FeatureDataFeatureLevels)) >= 0)
            {
                return query.MaxSupportedFeatureLevel;
            }
        }

        return D3DFeatureLevel.Level110;
    }

    /// <summary>The highest shader model a device supports, as D3D writes it (0x65 is 6.5).</summary>
    /// <param name="device">The device.</param>
    /// <returns>The model, or 6.0 if the runtime will not say.</returns>
    internal static uint HighestShaderModel(ref ComPtr<ID3D12Device5> device)
    {
        // The call is a negotiation rather than a question: it is given the highest model
        // the caller understands and writes back the highest the device has, and it fails
        // outright if the runtime has never heard of the one asked for. So it walks down.
        uint[] wanted = [0x69, 0x68, 0x67, 0x66, 0x65, 0x61, 0x60];

        foreach (uint model in wanted)
        {
            var query = new FeatureDataShaderModel { HighestShaderModel = (D3DShaderModel)model };

            if (device.CheckFeatureSupport(
                    Feature.ShaderModel, &query, (uint)sizeof(FeatureDataShaderModel)) >= 0)
            {
                return (uint)query.HighestShaderModel;
            }
        }

        return 0x60;
    }

    private static bool HasHdrOutput(ref ComPtr<IDXGIAdapter1> adapter)
    {
        for (uint index = 0; ; index++)
        {
            ComPtr<IDXGIOutput> output = default;
            if (adapter.EnumOutputs(index, output.GetAddressOf()) < 0)
            {
                return false;
            }

            try
            {
                ComPtr<IDXGIOutput6> output6 = default;
                Guid outputId = IDXGIOutput6.Guid;

                if (output.QueryInterface(&outputId, (void**)output6.GetAddressOf()) < 0)
                {
                    continue;
                }

                try
                {
                    OutputDesc1 description = default;
                    if (output6.GetDesc1(&description) < 0)
                    {
                        continue;
                    }

                    // The one colour space that means the display is in HDR now. A monitor
                    // that merely *could* be reports the ordinary sRGB space until Windows
                    // has switched it, and presenting ST.2084 to it would be presenting a
                    // washed-out picture.
                    if (description.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020)
                    {
                        return true;
                    }
                }
                finally
                {
                    output6.Dispose();
                }
            }
            finally
            {
                output.Dispose();
            }
        }
    }

    /// <summary>A feature level as Direct3D spells it.</summary>
    internal static string Name(D3DFeatureLevel level) => level switch
    {
        D3DFeatureLevel.Level122 => "12_2",
        D3DFeatureLevel.Level121 => "12_1",
        D3DFeatureLevel.Level120 => "12_0",
        D3DFeatureLevel.Level111 => "11_1",
        _ => "11_0",
    };

    /// <summary>A shader model as Direct3D spells it.</summary>
    internal static string Name(uint shaderModel) =>
        string.Create(CultureInfo.InvariantCulture, $"{shaderModel >> 4}.{shaderModel & 0xF}");
}
