using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// What a frame's targets hold, in Direct3D's spelling.
/// </summary>
public static class GBufferFormats
{
    /// <summary>How many colour targets a frame has, the picture included.</summary>
    public const uint Targets = 4;

    /// <summary>Light, in a format with somewhere to put values above one.</summary>
    public const Format Light = Format.FormatR16G16B16A16Float;

    /// <summary>World-space normals, signed and with room to spare.</summary>
    public const Format Normal = Format.FormatR16G16B16A16Float;

    /// <summary>Where each pixel was a frame ago, in screen space.</summary>
    public const Format Motion = Format.FormatR16G16Float;

    /// <summary>The depth the room is drawn against.</summary>
    public const Format Depth = Format.FormatD32Float;

    /// <summary>The encoded picture, as a display would take it.</summary>
    public const Format Picture = Format.FormatR8G8B8A8UnormSrgb;
}
