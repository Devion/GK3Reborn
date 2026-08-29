using System.Globalization;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>Direct3D refused something.</summary>
/// <remarks>
/// The Vulkan backend has <c>VulkanException</c> and everything that decides "this machine
/// cannot render" keys off it. This is the same idea for the other backend, and the reason
/// it is a separate type rather than a shared one is that the two carry different evidence:
/// a Vulkan failure is a <c>VkResult</c> with a name, and this is an <c>HRESULT</c>, which
/// is a number that has to be printed in hexadecimal to mean anything to anybody.
/// </remarks>
public sealed class D3D12Exception : Exception
{
    /// <summary>Creates an exception.</summary>
    public D3D12Exception()
    {
    }

    /// <summary>Creates an exception.</summary>
    /// <param name="message">What went wrong.</param>
    public D3D12Exception(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">What caused it.</param>
    public D3D12Exception(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The <c>HRESULT</c> behind the failure, when there was one.</summary>
    public int Result { get; private init; }

    /// <summary>Throws when a call failed.</summary>
    /// <param name="hr">What it returned.</param>
    /// <param name="what">What was being attempted, as a phrase that follows "could not".</param>
    /// <exception cref="D3D12Exception">The call failed.</exception>
    public static void ThrowIfFailed(int hr, string what)
    {
        if (hr >= 0)
        {
            return;
        }

        throw new D3D12Exception(
            string.Create(CultureInfo.InvariantCulture, $"Could not {what}: 0x{hr:X8} ({Explain(hr)})."))
        {
            Result = hr,
        };
    }

    /// <summary>What the common failures mean, in words.</summary>
    /// <param name="hr">The result.</param>
    /// <returns>A short phrase.</returns>
    /// <remarks>
    /// Only the ones worth naming. A device removal in particular is not a bug in the call
    /// that reported it — it is the driver having reset since some earlier call — and
    /// saying so is the difference between looking in the right place and the wrong one.
    /// </remarks>
    private static string Explain(int hr) => unchecked((uint)hr) switch
    {
        0x80070057 => "invalid argument",
        0x8007000E => "out of memory",
        0x80004001 => "not implemented by this runtime",
        0x80004005 => "unspecified failure",
        0x887A0001 => "invalid call",
        0x887A0002 => "not found",
        0x887A0005 => "the device was removed, which means it reset after some earlier call",
        0x887A0006 => "the device hung on work submitted earlier",
        0x887A0020 => "the driver reported an internal error",
        0x887A002D => "the SDK layers are not installed; turn validation off or install them",
        _ => "unrecognised",
    };
}
