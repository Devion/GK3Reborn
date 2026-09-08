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
    public static bool IsPossible(RenderBackend backend) =>
        Resolve(backend) != RenderBackend.Direct3D12 || OperatingSystem.IsWindows();
}
