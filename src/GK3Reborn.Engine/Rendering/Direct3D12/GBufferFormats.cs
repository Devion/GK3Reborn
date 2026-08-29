using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// What a frame's targets hold, in Direct3D's spelling.
/// </summary>
/// <remarks>
/// The same formats <c>GBuffer</c> chose for Vulkan and for the same reasons, which are
/// worth repeating because none of them is arbitrary. This is a separate list rather than a
/// translation of that one because the two enumerations have no relationship a lookup could
/// express, and a wrong entry here would be a picture that is subtly wrong rather than a
/// build that fails.
/// </remarks>
public static class GBufferFormats
{
    /// <summary>How many colour targets a frame has, the picture included.</summary>
    public const uint Targets = 4;

    /// <summary>Light, in a format with somewhere to put values above one.</summary>
    /// <remarks>
    /// Both colour targets are this while ray tracing, the picture included: the picture at
    /// that point holds only half the lighting, and clamping half of a sum to one and then
    /// adding the other half loses the highlights the two would have made together.
    /// </remarks>
    public const Format Light = Format.FormatR16G16B16A16Float;

    /// <summary>World-space normals, signed and with room to spare.</summary>
    /// <remarks>
    /// Sixteen bits a channel rather than eight. An eight-bit normal is enough to shade with
    /// and not enough to reproject with: the error is a fraction of a degree, which is
    /// several pixels once it is followed across a room.
    /// </remarks>
    public const Format Normal = Format.FormatR16G16B16A16Float;

    /// <summary>Where each pixel was a frame ago, in screen space.</summary>
    /// <remarks>
    /// Two signed channels, in pixels rather than in normalised coordinates, which is what
    /// FidelityFX's passes expect and what makes the numbers readable when they are wrong.
    /// </remarks>
    public const Format Motion = Format.FormatR16G16Float;

    /// <summary>The depth the room is drawn against.</summary>
    public const Format Depth = Format.FormatD32Float;

    /// <summary>The encoded picture, as a display would take it.</summary>
    /// <remarks>
    /// sRGB, and that is load-bearing rather than a preference: the output shader writes
    /// linear light and says so in a comment - "the target is an sRGB format and the hardware
    /// does the encode on write" - so a plain UNORM target skips the encode entirely and the
    /// whole room comes out dark. It rendered that way once and looked like a lighting bug.
    /// </remarks>
    public const Format Picture = Format.FormatR8G8B8A8UnormSrgb;
}
