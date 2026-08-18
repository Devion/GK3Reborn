using System.Globalization;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>What a physical device is and what it can do.</summary>
public sealed record VulkanDeviceInfo
{
    /// <summary>Device name as the driver reports it.</summary>
    public required string Name { get; init; }

    /// <summary>Discrete, integrated, virtual, CPU or other.</summary>
    public required string Kind { get; init; }

    /// <summary>Highest Vulkan version the device supports, as <c>major.minor.patch</c>.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Driver version, as the driver encodes it.</summary>
    public required string DriverVersion { get; init; }

    /// <summary>PCI vendor identifier.</summary>
    public required uint VendorId { get; init; }

    /// <summary>Total size of device-local memory heaps, in bytes.</summary>
    public required ulong DeviceLocalMemory { get; init; }

    /// <summary>Tiers this device satisfies.</summary>
    public required RenderCapabilityTier Tiers { get; init; }

    /// <summary>Extensions relevant to the tier decision that the device advertises.</summary>
    public required IReadOnlyList<string> NotableExtensions { get; init; }

    /// <summary>Why the device did or did not reach each tier.</summary>
    public required IReadOnlyList<string> TierNotes { get; init; }

    /// <summary>A one-line summary for logs.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Name} ({Kind}, Vulkan {ApiVersion}, {DeviceLocalMemory / 1_073_741_824.0:F1} GiB) -> {Tiers}");
}

/// <summary>The result of surveying every Vulkan device on the machine.</summary>
public sealed record VulkanDeviceReport
{
    /// <summary>Whether a Vulkan loader was found at all.</summary>
    public required bool VulkanAvailable { get; init; }

    /// <summary>Why Vulkan could not be used, when it could not.</summary>
    public string? Unavailable { get; init; }

    /// <summary>Whether validation layers are installed.</summary>
    public required bool ValidationAvailable { get; init; }

    /// <summary>Every device found.</summary>
    public required IReadOnlyList<VulkanDeviceInfo> Devices { get; init; }

    /// <summary>The device the selector would use, or null when none qualifies.</summary>
    public VulkanDeviceInfo? Selected { get; init; }
}
