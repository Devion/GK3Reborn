using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// The targets a frame writes besides its picture.
/// </summary>
/// <remarks>
/// <para>
/// A forward renderer needs none of this to draw a room. Everything that filters over time
/// needs all of it: a denoiser has to know which pixel of the last frame corresponds to
/// this one, and that is what a motion vector says. Reflections need the same, plus the
/// depth and the normal to march against.
/// </para>
/// <para>
/// Written by the room's own pass rather than by a prepass. The geometry is drawn once
/// either way, and drawing it twice to fill a depth buffer would cost more than the two
/// extra attachments do.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// Only the ray-traced variant writes it. The occlusion terms are not known while the
    /// geometry is being drawn — they are traced from this pass's own depth and normals
    /// and then filtered across frames — so the light the rig gives each pixel is kept
    /// apart from the rest and multiplied by them afterwards.
    /// </remarks>
    public const int Direct = 3;

    /// <summary>World-space normals, signed and with room to spare.</summary>
    /// <remarks>
    /// Sixteen bits a channel rather than eight. An eight-bit normal is enough to shade
    /// with and not enough to reproject with: the error is a fraction of a degree, which is
    /// several pixels once it is followed across a room.
    /// </remarks>
    public const Format NormalFormat = Format.R16G16B16A16Sfloat;

    /// <summary>Where each pixel was a frame ago, in screen space.</summary>
    /// <remarks>
    /// Two signed channels, in pixels rather than in normalised coordinates, which is what
    /// FidelityFX's passes expect and what makes the numbers readable when they are wrong.
    /// </remarks>
    public const Format MotionFormat = Format.R16G16Sfloat;

    /// <summary>Light, in a format that has somewhere to put values above one.</summary>
    /// <remarks>
    /// Both colour targets are this while ray tracing, the picture included: the picture
    /// at that point holds only half the lighting, and clamping half of a sum to one and
    /// then adding the other half loses the highlights the two would have made together.
    /// </remarks>
    public const Format LightFormat = Format.R16G16B16A16Sfloat;
}
