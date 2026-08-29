namespace GK3Reborn.Rendering.Upscaling;

/// <summary>One texture, described the way Streamline asks for it.</summary>
/// <param name="Native">The image or resource itself.</param>
/// <param name="View">A view of the whole of it, or zero where the API has no such object.</param>
/// <param name="State">
/// What state or layout it is in when the runtime is handed it. A Vulkan image layout or a
/// Direct3D resource state — the runtime keeps the number and never interprets it, because
/// it knows which API it was given a device for.
/// </param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="NativeFormat">What it holds, in that API's own numbering.</param>
/// <param name="Usage">
/// What it was created for. Vulkan's usage flags; nothing on Direct3D, where a resource
/// carries its own flags and the runtime can ask.
/// </param>
/// <remarks>
/// Neutral because Streamline is. The runtime takes a handle, a size, a format and a state,
/// and the only thing that differs between the backends is which numbers those are — which
/// is why this is a record of numbers rather than an abstraction over two APIs.
/// </remarks>
public readonly record struct UpscaleSurface(
    nint Native,
    nint View,
    uint State,
    uint Width,
    uint Height,
    uint NativeFormat,
    uint Usage = 0)
{
    /// <summary>Whether there is a surface here at all.</summary>
    public bool Exists => Native != 0;
}

/// <summary>Everything Streamline needs to know about one frame.</summary>
/// <param name="Colour">The room as drawn, in linear light at render resolution.</param>
/// <param name="Depth">Its depth buffer.</param>
/// <param name="Motion">Where each pixel was a frame ago, in pixels at render resolution.</param>
/// <param name="Output">Where to put the result, at display resolution.</param>
/// <param name="JitterPixels">Where inside its pixel this frame sampled.</param>
/// <param name="DeltaSeconds">How long since the last frame.</param>
/// <param name="Reset">Whether the history is worthless: a cut, a new room, a resize.</param>
/// <param name="Camera">Where the frame was seen from.</param>
/// <param name="Aspect">Render width over render height.</param>
/// <param name="Sharpen">Whether the backend was asked to sharpen its own output.</param>
/// <param name="Sharpness">How hard, nought to one.</param>
/// <param name="HighDynamicRange">Whether the colour carries values above one.</param>
public readonly record struct StreamlineFrame(
    UpscaleSurface Colour,
    UpscaleSurface Depth,
    UpscaleSurface Motion,
    UpscaleSurface Output,
    System.Numerics.Vector2 JitterPixels,
    float DeltaSeconds,
    bool Reset,
    Camera? Camera,
    float Aspect,
    bool Sharpen,
    float Sharpness,
    bool HighDynamicRange);
