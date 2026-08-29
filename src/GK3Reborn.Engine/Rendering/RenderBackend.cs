namespace GK3Reborn.Rendering;

/// <summary>Which graphics API a renderer is built on.</summary>
public enum RenderBackend
{
    /// <summary>Whichever suits the machine. See <see cref="RenderBackends.Choose"/>.</summary>
    Automatic,

    /// <summary>Vulkan, which runs everywhere the game does.</summary>
    Vulkan,

    /// <summary>Direct3D 12, on Windows.</summary>
    Direct3D12,
}

/// <summary>Which backend to use, and why.</summary>
/// <remarks>
/// <para>
/// Windows gets Direct3D 12 and everything else gets Vulkan. The reason is not performance
/// — the two draw the same picture, from the same shaders, at the same rate — but
/// Streamline. On Direct3D, Streamline interposes by handing the application a proxy device
/// and a proxy swapchain, which are objects the renderer already holds and passes around.
/// On Vulkan it interposes by *being the loader*: <c>sl.interposer.dll</c> has to be loaded
/// in place of <c>vulkan-1.dll</c>, the surface has to be created through it, and
/// <c>slSetVulkanInfo</c> must then not be called at all. Getting one of those three wrong
/// costs frame generation silently, and getting them wrong together costs the swapchain
/// outright. See <c>docs/upscaling.md</c>.
/// </para>
/// <para>
/// So the default follows where the runtime is least likely to be subtly wrong, and the
/// choice stays a choice: <c>--backend vulkan</c> on Windows is supported, tested and the
/// first thing to try when a Direct3D machine misbehaves.
/// </para>
/// </remarks>
public static class RenderBackends
{
    /// <summary>The backend to use when nobody has asked for one.</summary>
    /// <returns>The backend.</returns>
    public static RenderBackend Choose() =>
        OperatingSystem.IsWindows() ? RenderBackend.Direct3D12 : RenderBackend.Vulkan;

    /// <summary>Resolves a request, which may be for whatever suits the machine.</summary>
    /// <param name="requested">What was asked for.</param>
    /// <returns>A backend that is not <see cref="RenderBackend.Automatic"/>.</returns>
    public static RenderBackend Resolve(RenderBackend requested) =>
        requested == RenderBackend.Automatic ? Choose() : requested;

    /// <summary>Reads a backend from what someone typed.</summary>
    /// <param name="text">The word, in any case.</param>
    /// <param name="backend">The backend it names.</param>
    /// <returns>False if it names none.</returns>
    /// <remarks>
    /// The spellings people actually use, not only the enumeration's own. Someone who types
    /// <c>--backend dx12</c> means Direct3D 12 and should not be told there is no such
    /// thing.
    /// </remarks>
    public static bool TryParse(string? text, out RenderBackend backend)
    {
        backend = RenderBackend.Automatic;

        switch (text?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto" or "automatic" or "default":
                backend = RenderBackend.Automatic;
                return true;

            case "vulkan" or "vk":
                backend = RenderBackend.Vulkan;
                return true;

            case "direct3d12" or "direct3d" or "d3d12" or "d3d" or "dx12" or "directx12" or "directx":
                backend = RenderBackend.Direct3D12;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Whether a backend could possibly run on this machine.</summary>
    /// <param name="backend">The backend.</param>
    /// <returns>False when the operating system rules it out.</returns>
    /// <remarks>
    /// Only the answer the operating system alone can give. Whether a device is actually
    /// there, and what it can do, is a survey — see <see cref="DeviceReport"/>.
    /// </remarks>
    public static bool IsPossible(RenderBackend backend) =>
        Resolve(backend) != RenderBackend.Direct3D12 || OperatingSystem.IsWindows();
}
