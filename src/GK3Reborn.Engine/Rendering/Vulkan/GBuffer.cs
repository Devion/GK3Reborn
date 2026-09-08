using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// The targets a frame writes besides its picture.
/// </summary>
public static class GBuffer
{
    /// <summary>How many colour attachments a frame has, the picture included.</summary>
    public const uint Targets = 4;

    /// <summary>The picture's attachment index.</summary>
    public const int Colour = 0;

    /// <summary>The surface normal's attachment index.</summary>
    public const int Normal = 1;

    /// <summary>The motion vector's attachment index.</summary>
    public const int Motion = 2;

    /// <summary>The unshadowed direct light's attachment index.</summary>
    public const int Direct = 3;

    /// <summary>World-space normals, signed and with room to spare.</summary>
    public const Format NormalFormat = Format.R16G16B16A16Sfloat;

    /// <summary>Where each pixel was a frame ago, in screen space.</summary>
    public const Format MotionFormat = Format.R16G16Sfloat;

    /// <summary>Light, in a format that has somewhere to put values above one.</summary>
    public const Format LightFormat = Format.R16G16B16A16Sfloat;
}
