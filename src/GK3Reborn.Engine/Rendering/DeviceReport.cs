using System.Globalization;

namespace GK3Reborn.Rendering;

/// <summary>What one graphics adapter is and what it can do.</summary>
/// <remarks>
/// The fields both backends can honestly fill in. A Vulkan survey knows things a Direct3D
/// one does not and the other way about — extension names against feature levels, a driver
/// version encoded by the vendor against one encoded by the runtime — so anything that only
/// one of them can answer goes in <see cref="Notes"/> as a line of prose rather than
/// becoming a field the other has to invent a value for.
/// </remarks>
public sealed record AdapterInfo
{
    /// <summary>Adapter name as the driver reports it.</summary>
    public required string Name { get; init; }

    /// <summary>Discrete, integrated, virtual, CPU or other.</summary>
    public required string Kind { get; init; }

    /// <summary>Which API this adapter was surveyed through.</summary>
    public required RenderBackend Backend { get; init; }

    /// <summary>
    /// How capable the API says it is: a Vulkan version, or a Direct3D feature level.
    /// </summary>
    public required string ApiVersion { get; init; }

    /// <summary>Driver version, as the driver encodes it.</summary>
    public required string DriverVersion { get; init; }

    /// <summary>PCI vendor identifier.</summary>
    public required uint VendorId { get; init; }

    /// <summary>Total size of device-local memory, in bytes.</summary>
    public required ulong DeviceLocalMemory { get; init; }

    /// <summary>Tiers this adapter satisfies.</summary>
    public required RenderCapabilityTier Tiers { get; init; }

    /// <summary>
    /// Whether the content pipeline's block-compressed textures can be uploaded as they are.
    /// </summary>
    /// <remarks>
    /// False on Apple silicon, where they are expanded on the host instead. It is worth
    /// reporting because it is invisible on screen and costs four times the video memory.
    /// </remarks>
    public required bool BlockCompression { get; init; }

    /// <summary>Why the adapter did or did not reach each tier, and anything else notable.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>A one-line summary for logs.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Name} ({Kind}, {Backend} {ApiVersion}, {DeviceLocalMemory / 1_073_741_824.0:F1} GiB) -> {Tiers}");
}

/// <summary>The result of surveying what one backend can see.</summary>
public sealed record DeviceReport
{
    /// <summary>Which API was surveyed.</summary>
    public required RenderBackend Backend { get; init; }

    /// <summary>Whether that API is present on the machine at all.</summary>
    public required bool Available { get; init; }

    /// <summary>Why it could not be used, when it could not.</summary>
    public string? Unavailable { get; init; }

    /// <summary>Whether the API's own validation is installed and could be turned on.</summary>
    public required bool ValidationAvailable { get; init; }

    /// <summary>Every adapter found.</summary>
    public required IReadOnlyList<AdapterInfo> Adapters { get; init; }

    /// <summary>The adapter the selector would use, or null when none qualifies.</summary>
    public AdapterInfo? Selected { get; init; }

    /// <summary>A report for a backend that is not present.</summary>
    /// <param name="backend">Which one.</param>
    /// <param name="why">What is missing.</param>
    /// <returns>The report.</returns>
    public static DeviceReport Missing(RenderBackend backend, string why) => new()
    {
        Backend = backend,
        Available = false,
        Unavailable = why,
        ValidationAvailable = false,
        Adapters = [],
    };
}
