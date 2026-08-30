using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

public sealed class VulkanDeviceSelectorTests
{
    private static AdapterInfo Device(
        string name,
        RenderCapabilityTier tiers,
        string kind = "Discrete",
        ulong memory = 8UL * 1024 * 1024 * 1024,
        bool blockCompression = true) => new()
        {
            Name = name,
            BlockCompression = blockCompression,
            Kind = kind,
            ApiVersion = "1.3.0",
            DriverVersion = "1.0.0",
            VendorId = 0,
            DeviceLocalMemory = memory,
            Tiers = tiers,
            Backend = RenderBackend.Vulkan,
            Notes = [],
        };

    private const RenderCapabilityTier Basic = RenderCapabilityTier.Compatibility;

    private const RenderCapabilityTier Everything =
        RenderCapabilityTier.Compatibility | RenderCapabilityTier.Enhanced |
        RenderCapabilityTier.RayTracing | RenderCapabilityTier.HighDynamicRange;

    [Fact]
    public void A_device_that_cannot_present_is_never_chosen()
    {
        // However capable it is otherwise, a device with no swapchain support cannot be
        // the render device.
        AdapterInfo[] devices =
        [
            Device("compute only", RenderCapabilityTier.Enhanced | RenderCapabilityTier.RayTracing),
            Device("modest but usable", Basic, kind: "Integrated", memory: 1024),
        ];

        Assert.Equal("modest but usable", VulkanDeviceSelector.Choose(devices)?.Name);
    }

    [Fact]
    public void More_capability_wins_over_more_memory()
    {
        AdapterInfo[] devices =
        [
            Device("big but basic", Basic, memory: 32UL * 1024 * 1024 * 1024),
            Device("smaller but complete", Everything, memory: 8UL * 1024 * 1024 * 1024),
        ];

        Assert.Equal("smaller but complete", VulkanDeviceSelector.Choose(devices)?.Name);
    }

    [Fact]
    public void Discrete_hardware_wins_a_tie_on_capability()
    {
        AdapterInfo[] devices =
        [
            Device("integrated", Everything, kind: "Integrated"),
            Device("discrete", Everything, kind: "Discrete"),
        ];

        Assert.Equal("discrete", VulkanDeviceSelector.Choose(devices)?.Name);
    }

    [Fact]
    public void Memory_breaks_a_tie_only_after_capability_and_kind()
    {
        AdapterInfo[] devices =
        [
            Device("small", Everything, memory: 4UL * 1024 * 1024 * 1024),
            Device("large", Everything, memory: 24UL * 1024 * 1024 * 1024),
        ];

        Assert.Equal("large", VulkanDeviceSelector.Choose(devices)?.Name);
    }

    [Fact]
    public void No_usable_device_yields_nothing_rather_than_a_bad_choice()
    {
        Assert.Null(VulkanDeviceSelector.Choose([]));
        Assert.Null(VulkanDeviceSelector.Choose([Device("compute", RenderCapabilityTier.Enhanced)]));
    }

    [Fact]
    public void Ray_tracing_absence_does_not_disqualify_a_device()
    {
        // The plan requires that ray tracing and HDR never prevent raster play.
        AdapterInfo[] devices =
        [
            Device("raster only", RenderCapabilityTier.Compatibility | RenderCapabilityTier.Enhanced),
        ];

        Assert.NotNull(VulkanDeviceSelector.Choose(devices));
    }

    [Fact]
    public void Surveying_a_machine_never_throws()
    {
        // On a build agent with no GPU or no loader this must report the fact rather than
        // fail, which is the difference between a diagnostic and a crash.
        DeviceReport report = VulkanDeviceSelector.Survey();

        if (report.Available)
        {
            Assert.NotNull(report.Adapters);
        }
        else
        {
            Assert.NotNull(report.Unavailable);
            Assert.Empty(report.Adapters);
        }
    }
}
