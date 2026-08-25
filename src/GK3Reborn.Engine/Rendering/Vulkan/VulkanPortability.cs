using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// What a device actually offers, out of the things the renderer would like to use.
/// </summary>
/// <param name="BlockCompression">
/// Whether BC images may be created. False on Apple silicon, where Metal has no BC
/// formats at all and the content pipeline's blocks have to be expanded on the host.
/// </param>
/// <param name="AnisotropicFiltering">Whether the sampler may ask for anisotropy.</param>
/// <param name="AstcCompression">Whether ASTC images may be created.</param>
/// <param name="Etc2Compression">Whether ETC2 images may be created.</param>
/// <remarks>
/// A feature is asked for at device creation and is a hard error if the device does not
/// have it — <c>vkCreateDevice</c> fails outright rather than granting what it can. So
/// what is asked for has to be the intersection of what is wanted and what is offered,
/// and the rest of the renderer has to be able to read which way that went.
/// </remarks>
public readonly record struct DeviceCapabilities(
    bool BlockCompression,
    bool AnisotropicFiltering,
    bool AstcCompression,
    bool Etc2Compression)
{
    /// <summary>The features to ask for, given what this device offers.</summary>
    /// <returns>A structure safe to pass to <c>vkCreateDevice</c>.</returns>
    public PhysicalDeviceFeatures Requested() => new()
    {
        SamplerAnisotropy = AnisotropicFiltering,
        TextureCompressionBC = BlockCompression,
    };

    /// <summary>One line for the startup report.</summary>
    /// <returns>What was found, in the order it matters.</returns>
    public override string ToString()
    {
        string compression = BlockCompression
            ? "BC"
            : AstcCompression
                ? "no BC (ASTC present); blocks expanded on the host"
                : "no BC; blocks expanded on the host";

        return AnisotropicFiltering ? compression : compression + ", no anisotropy";
    }
}

/// <summary>
/// The parts of instance and device creation that differ between platforms.
/// </summary>
/// <remarks>
/// <para>
/// Vulkan on macOS is MoltenVK, which translates to Metal and is therefore a
/// <em>portability</em> driver rather than a conformant one. Two things follow, and both
/// are refusals rather than degradations if they are not honoured: an instance has to opt
/// in to enumerating such a device at all, and a device that advertises
/// <c>VK_KHR_portability_subset</c> must have it in its enabled extension list.
/// </para>
/// <para>
/// Both are no-ops everywhere else. The extension is absent on Windows and Linux
/// drivers, so nothing is added and nothing changes; keeping the decision here rather
/// than behind an operating-system check means the same code path is taken on every
/// platform and a Linux run exercises it.
/// </para>
/// </remarks>
public static unsafe class VulkanPortability
{
    /// <summary>Instance extension that allows a portability driver to be enumerated.</summary>
    public const string EnumerationExtension = "VK_KHR_portability_enumeration";

    /// <summary>Device extension a portability driver requires to be enabled.</summary>
    public const string SubsetExtension = "VK_KHR_portability_subset";

    /// <summary>Dynamic rendering, for a device that has it as an extension rather than as core.</summary>
    public const string DynamicRenderingExtension = "VK_KHR_dynamic_rendering";

    /// <summary>Answers every query as though the device had no block compression.</summary>
    /// <remarks>
    /// <c>--expand-blocks</c>. The path that expands the content pipeline's blocks on the
    /// host is only reached on hardware that cannot read them, which is a Mac — and a
    /// path that can only be exercised on hardware nobody has to hand is a path that
    /// breaks silently. This makes a Windows or Linux machine take it, so a screenshot
    /// from either can be compared against one from the same scene on the device path.
    /// </remarks>
    public static bool ForceHostExpansion { get; set; }

    /// <summary>The extension list to create an instance with, and the flags to go with it.</summary>
    /// <param name="api">The Vulkan API.</param>
    /// <param name="wanted">Extensions the caller needs, such as the surface ones.</param>
    /// <param name="flags">Flags to pass with them.</param>
    /// <returns>The list to enable.</returns>
    /// <remarks>
    /// Asking for an extension the loader does not have fails instance creation, so the
    /// portability one is added only where it is present. Adding the flag without the
    /// extension fails in the same way, which is why the two are decided together.
    /// </remarks>
    public static string[] InstanceExtensions(
        Vk api, IEnumerable<string> wanted, out InstanceCreateFlags flags)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(wanted);

        List<string> names = [.. wanted];

        if (InstanceSupports(api, EnumerationExtension))
        {
            names.Add(EnumerationExtension);
            flags = InstanceCreateFlags.EnumeratePortabilityBitKhr;
        }
        else
        {
            flags = InstanceCreateFlags.None;
        }

        return [.. names];
    }

    /// <summary>Whether the loader offers an instance extension.</summary>
    /// <param name="api">The Vulkan API.</param>
    /// <param name="extension">Its name.</param>
    /// <returns>True if it is present.</returns>
    public static bool InstanceSupports(Vk api, string extension)
    {
        ArgumentNullException.ThrowIfNull(api);

        uint count = 0;
        if (api.EnumerateInstanceExtensionProperties((byte*)null, ref count, null) != Result.Success)
        {
            return false;
        }

        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = properties)
        {
            api.EnumerateInstanceExtensionProperties((byte*)null, ref count, pointer);
        }

        foreach (ExtensionProperties property in properties)
        {
            if (Name(property) == extension)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a device offers an extension.</summary>
    /// <param name="api">The Vulkan API.</param>
    /// <param name="device">The physical device.</param>
    /// <param name="extension">Its name.</param>
    /// <returns>True if it is present.</returns>
    public static bool DeviceSupports(Vk api, PhysicalDevice device, string extension)
    {
        ArgumentNullException.ThrowIfNull(api);

        foreach (string name in DeviceExtensionNames(api, device))
        {
            if (name == extension)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The extension list to create a device with.</summary>
    /// <param name="api">The Vulkan API.</param>
    /// <param name="device">The physical device.</param>
    /// <param name="wanted">Extensions the caller needs.</param>
    /// <returns>The list to enable, with the portability subset added where required.</returns>
    public static string[] DeviceExtensions(
        Vk api, PhysicalDevice device, IEnumerable<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(wanted);

        List<string> names = [.. wanted];
        List<string> available = DeviceExtensionNames(api, device);

        if (available.Contains(SubsetExtension))
        {
            names.Add(SubsetExtension);
        }

        // Dynamic rendering is core in Vulkan 1.3 and an extension before it. The renderer
        // asks for its feature either way, and asking for a feature whose extension is not
        // enabled is invalid on a device that reports less than 1.3 — which a MoltenVK
        // older than 1.2.7 does, and which is otherwise perfectly able to run the game.
        api.GetPhysicalDeviceProperties(device, out PhysicalDeviceProperties properties);

        if (properties.ApiVersion < Vk.Version13 && available.Contains(DynamicRenderingExtension))
        {
            names.Add(DynamicRenderingExtension);
        }

        return [.. names];
    }

    /// <summary>Reads what a device offers of what the renderer would like.</summary>
    /// <param name="api">The Vulkan API.</param>
    /// <param name="device">The physical device.</param>
    /// <returns>The capabilities.</returns>
    public static DeviceCapabilities Query(Vk api, PhysicalDevice device)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.GetPhysicalDeviceFeatures(device, out PhysicalDeviceFeatures features);

        return new DeviceCapabilities(
            BlockCompression: features.TextureCompressionBC && !ForceHostExpansion,
            AnisotropicFiltering: features.SamplerAnisotropy,
            AstcCompression: features.TextureCompressionAstcLdr,
            Etc2Compression: features.TextureCompressionEtc2);
    }

    /// <summary>Every extension a device advertises.</summary>
    /// <param name="api">The Vulkan API.</param>
    /// <param name="device">The physical device.</param>
    /// <returns>Their names.</returns>
    private static List<string> DeviceExtensionNames(Vk api, PhysicalDevice device)
    {
        uint count = 0;
        api.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, null);

        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = properties)
        {
            api.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, pointer);
        }

        var names = new List<string>((int)count);
        foreach (ExtensionProperties property in properties)
        {
            names.Add(Name(property));
        }

        return names;
    }

    /// <summary>An extension's name, out of the fixed buffer it is reported in.</summary>
    /// <param name="property">The reported extension.</param>
    /// <returns>Its name.</returns>
    private static string Name(ExtensionProperties property) =>
        Marshal.PtrToStringAnsi((nint)property.ExtensionName) ?? string.Empty;
}
