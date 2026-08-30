using System.Globalization;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Finds the Vulkan devices on the machine and works out what each can do.
/// </summary>
/// <remarks>
/// <para>
/// This is the first thing the renderer needs and the last thing that should guess.
/// <c>Plan/01-architecture.md</c> section 5.1 requires feature tiers to be selected from
/// *queried* capabilities rather than from vendor or version assumptions: ray tracing and
/// HDR must never prevent raster play, so the tier a device reaches has to be derived from
/// what it actually advertises.
/// </para>
/// <para>
/// It runs without a window or a surface, which makes it usable as a diagnostic on a
/// machine that cannot run the game and testable on a build agent that has no GPU at all.
/// A missing loader is a reported condition, not a crash.
/// </para>
/// </remarks>
public sealed class VulkanDeviceSelector
{
    private const string RayTracingPipeline = "VK_KHR_ray_tracing_pipeline";
    private const string AccelerationStructure = "VK_KHR_acceleration_structure";
    private const string RayQuery = "VK_KHR_ray_query";
    private const string DeferredHostOperations = "VK_KHR_deferred_host_operations";
    private const string Swapchain = "VK_KHR_swapchain";
    private const string HdrMetadata = "VK_EXT_hdr_metadata";
    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    /// <summary>Surveys the machine.</summary>
    /// <returns>What was found.</returns>
    /// <summary>Surveys the devices an instance that already exists can see.</summary>
    /// <param name="vk">The Vulkan API.</param>
    /// <param name="instance">An instance the caller owns and keeps.</param>
    /// <returns>The report.</returns>
    /// <remarks>
    /// The renderer has an instance by the time anybody wants to read this, and building a
    /// second one to look through costs 145 ms of the time to a first frame. Building it on
    /// another thread to hide that cost is worse than paying it: two instances being created
    /// at once enumerated only one of this machine's two devices about one run in six.
    /// </remarks>
    public static DeviceReport Survey(Vk vk, Instance instance)
    {
        ArgumentNullException.ThrowIfNull(vk);

        unsafe
        {
            List<AdapterInfo> devices = EnumerateDevices(vk, instance);

            return new DeviceReport
            {
                Backend = RenderBackend.Vulkan,
                Available = true,
                ValidationAvailable = HasValidationLayer(vk),
                Adapters = devices,
                Selected = Choose(devices),
            };
        }
    }

    public static DeviceReport Survey()
    {
        Vk vk;
        try
        {
            vk = Vk.GetApi();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or FileNotFoundException)
        {
            // No loader means no Vulkan. That is a fact about the machine, not a failure
            // of the engine, and the caller needs to be able to say so.
            return DeviceReport.Missing(RenderBackend.Vulkan, ex.Message);
        }

        unsafe
        {
            Instance instance = default;

            try
            {
                bool validation = HasValidationLayer(vk);
                instance = CreateInstance(vk, validation);

                List<AdapterInfo> devices = EnumerateDevices(vk, instance);

                return new DeviceReport
                {
                    Backend = RenderBackend.Vulkan,
                    Available = true,
                    ValidationAvailable = validation,
                    Adapters = devices,
                    Selected = Choose(devices),
                };
            }
            catch (VulkanException ex)
            {
                return DeviceReport.Missing(RenderBackend.Vulkan, ex.Message);
            }
            finally
            {
                if (instance.Handle != 0)
                {
                    vk.DestroyInstance(instance, null);
                }

                vk.Dispose();
            }
        }
    }

    /// <summary>
    /// Picks the device to render with.
    /// </summary>
    /// <param name="devices">Candidates.</param>
    /// <returns>The chosen device, or null when none can render.</returns>
    /// <remarks>
    /// A device that cannot present is not a candidate at all, however capable it is
    /// otherwise. Beyond that the ordering prefers more capability, then discrete
    /// hardware, then more memory — deliberately not vendor or device name, which is how
    /// renderers acquire quiet hardware-specific behaviour.
    /// </remarks>
    public static AdapterInfo? Choose(IReadOnlyList<AdapterInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        return devices
            .Where(d => d.Tiers.HasFlag(RenderCapabilityTier.Compatibility))
            .OrderByDescending(d => System.Numerics.BitOperations.PopCount((uint)d.Tiers))
            .ThenByDescending(d => d.Kind == "Discrete")
            .ThenByDescending(d => d.DeviceLocalMemory)
            .FirstOrDefault();
    }

    private static unsafe Instance CreateInstance(Vk vk, bool validation)
    {
        // The instance asks for Vulkan 1.3. Devices reporting less are still enumerated;
        // the tier decision, not the instance, is what gates them.
        var applicationInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("GK3Reborn"),
            ApplicationVersion = Vk.MakeVersion(0, 1, 0),
            PEngineName = (byte*)SilkMarshal.StringToPtr("GK3Reborn"),
            EngineVersion = Vk.MakeVersion(0, 1, 0),
            ApiVersion = Vk.Version13,
        };

        // Portability drivers are not enumerated unless the instance says it accepts one,
        // so a survey without this reports no devices at all on macOS.
        string[] extensions = VulkanPortability.InstanceExtensions(vk, [], out InstanceCreateFlags flags);
        nint extensionNames = extensions.Length > 0 ? SilkMarshal.StringArrayToPtr(extensions) : 0;

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &applicationInfo,
            Flags = flags,
            EnabledExtensionCount = (uint)extensions.Length,
            PpEnabledExtensionNames = (byte**)extensionNames,
        };

        nint layers = 0;
        if (validation)
        {
            layers = SilkMarshal.StringArrayToPtr([ValidationLayer]);
            createInfo.EnabledLayerCount = 1;
            createInfo.PpEnabledLayerNames = (byte**)layers;
        }

        try
        {
            Result result = vk.CreateInstance(in createInfo, null, out Instance instance);
            if (result != Result.Success)
            {
                throw new VulkanException($"Could not create a Vulkan instance: {result}.");
            }

            return instance;
        }
        finally
        {
            SilkMarshal.Free((nint)applicationInfo.PApplicationName);
            SilkMarshal.Free((nint)applicationInfo.PEngineName);
            if (layers != 0)
            {
                SilkMarshal.Free(layers);
            }

            if (extensionNames != 0)
            {
                SilkMarshal.Free(extensionNames);
            }
        }
    }

    private static unsafe bool HasValidationLayer(Vk vk)
    {
        uint count = 0;
        if (vk.EnumerateInstanceLayerProperties(ref count, null) != Result.Success || count == 0)
        {
            return false;
        }

        LayerProperties[] layers = new LayerProperties[count];
        fixed (LayerProperties* pointer = layers)
        {
            if (vk.EnumerateInstanceLayerProperties(ref count, pointer) != Result.Success)
            {
                return false;
            }
        }

        foreach (LayerProperties layer in layers)
        {
            if (SilkMarshal.PtrToString((nint)layer.LayerName) == ValidationLayer)
            {
                return true;
            }
        }

        return false;
    }

    // Not an iterator: a method cannot be both unsafe and use yield.
    private static unsafe List<AdapterInfo> EnumerateDevices(Vk vk, Instance instance)
    {
        List<AdapterInfo> results = [];

        uint count = 0;
        if (vk.EnumeratePhysicalDevices(instance, ref count, null) != Result.Success || count == 0)
        {
            return results;
        }

        PhysicalDevice[] handles = new PhysicalDevice[count];
        fixed (PhysicalDevice* pointer = handles)
        {
            if (vk.EnumeratePhysicalDevices(instance, ref count, pointer) != Result.Success)
            {
                return results;
            }
        }

        foreach (PhysicalDevice handle in handles)
        {
            results.Add(Describe(vk, handle));
        }

        return results;
    }

    private static unsafe AdapterInfo Describe(Vk vk, PhysicalDevice device)
    {
        vk.GetPhysicalDeviceProperties(device, out PhysicalDeviceProperties properties);
        vk.GetPhysicalDeviceMemoryProperties(device, out PhysicalDeviceMemoryProperties memory);

        HashSet<string> extensions = ExtensionsOf(vk, device);

        ulong deviceLocal = 0;
        for (int i = 0; i < memory.MemoryHeapCount; i++)
        {
            if (memory.MemoryHeaps[i].Flags.HasFlag(MemoryHeapFlags.DeviceLocalBit))
            {
                deviceLocal += memory.MemoryHeaps[i].Size;
            }
        }

        List<string> notes = [];
        RenderCapabilityTier tiers = RenderCapabilityTier.None;

        // Compatibility means it can present and run the raster path at all.
        if (extensions.Contains(Swapchain))
        {
            tiers |= RenderCapabilityTier.Compatibility;
        }
        else
        {
            notes.Add($"no {Swapchain}: cannot present, so cannot be the render device");
        }

        // Enhanced needs the compute and descriptor headroom clustered lighting and GPU
        // culling depend on, which Vulkan 1.2 is a reasonable proxy for.
        if (properties.ApiVersion >= Vk.Version12)
        {
            tiers |= RenderCapabilityTier.Enhanced;
        }
        else
        {
            notes.Add("Vulkan below 1.2: no enhanced tier");
        }

        bool rayTracing = extensions.Contains(RayTracingPipeline)
            && extensions.Contains(AccelerationStructure)
            && extensions.Contains(DeferredHostOperations);

        if (rayTracing)
        {
            tiers |= RenderCapabilityTier.RayTracing;
        }
        else
        {
            notes.Add("ray tracing needs pipeline, acceleration structure and deferred host operations");
        }

        if (extensions.Contains(HdrMetadata))
        {
            tiers |= RenderCapabilityTier.HighDynamicRange;
        }
        else
        {
            notes.Add($"no {HdrMetadata}: HDR output unavailable");
        }

        string[] interesting =
        [
            Swapchain, RayTracingPipeline, AccelerationStructure,
            RayQuery, DeferredHostOperations, HdrMetadata,
        ];

        string[] notable = [.. interesting.Where(extensions.Contains)];

        DeviceCapabilities capabilities = VulkanPortability.Query(vk, device);

        if (!capabilities.BlockCompression)
        {
            notes.Add("no BC formats: the pipeline's textures are expanded on the host, at four times the memory");
        }

        return new AdapterInfo
        {
            BlockCompression = capabilities.BlockCompression,
            Name = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "unknown",
            Kind = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => "Discrete",
                PhysicalDeviceType.IntegratedGpu => "Integrated",
                PhysicalDeviceType.VirtualGpu => "Virtual",
                PhysicalDeviceType.Cpu => "CPU",
                _ => "Other",
            },
            ApiVersion = FormatVersion(properties.ApiVersion),
            DriverVersion = FormatVersion(properties.DriverVersion),
            VendorId = properties.VendorID,
            DeviceLocalMemory = deviceLocal,
            Tiers = tiers,
            Backend = RenderBackend.Vulkan,
            Notes = [.. notes, .. notable.Select(e => "extension: " + e)],
        };
    }

    private static unsafe HashSet<string> ExtensionsOf(Vk vk, PhysicalDevice device)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        uint count = 0;
        if (vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, null) != Result.Success)
        {
            return names;
        }

        ExtensionProperties[] extensions = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = extensions)
        {
            if (vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, pointer) != Result.Success)
            {
                return names;
            }
        }

        foreach (ExtensionProperties extension in extensions)
        {
            string? name = SilkMarshal.PtrToString((nint)extension.ExtensionName);
            if (name is not null)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string FormatVersion(uint version) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}");
}

/// <summary>Raised when Vulkan reports a failure the engine cannot proceed past.</summary>
public sealed class VulkanException : Exception
{
    /// <summary>Creates an exception.</summary>
    public VulkanException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public VulkanException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public VulkanException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
