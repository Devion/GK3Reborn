// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Numerics;
using GK3Reborn.Rendering.Upscaling;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>One image the upscaler is given or asked to fill.</summary>
/// <param name="Image">The image itself.</param>
/// <param name="View">A view of the whole of it.</param>
/// <param name="Format">What it holds.</param>
/// <param name="Extent">How big it is.</param>
/// <param name="Usage">
/// The usage flags it was created with. AMD's runtime wants them, and the reason is worth
/// knowing: it decides from them whether it may write into a resource it was handed.
/// </param>
public readonly record struct UpscaleImage(
    Image Image,
    ImageView View,
    Format Format,
    Extent2D Extent,
    ImageUsageFlags Usage)
{
    /// <summary>Whether there is an image here at all.</summary>
    public bool Exists => Image.Handle != 0;
}

/// <summary>Everything an upscaler needs to know about one frame.</summary>
/// <param name="Colour">The room as drawn, in linear light at render resolution.</param>
/// <param name="Depth">Its depth buffer.</param>
/// <param name="Motion">
/// Where each pixel was a frame ago, in pixels, at render resolution and with this frame's
/// jitter already taken out. See <see cref="GBuffer.MotionFormat"/>.
/// </param>
/// <param name="Output">Where to put the result, at display resolution.</param>
/// <param name="JitterPixels">Where inside its pixel this frame sampled.</param>
/// <param name="DeltaSeconds">How long since the last frame.</param>
/// <param name="Reset">
/// Whether the history is worthless: a cut, a new room, a resize. Every temporal upscaler
/// needs telling, and one that is not told smears the last room across the first frame of
/// the next one.
/// </param>
/// <param name="Camera">Where the frame was seen from, for the backends that ask.</param>
/// <param name="Aspect">Render width over render height.</param>
/// <param name="Sharpen">Whether the backend was asked to sharpen its own output.</param>
/// <param name="Sharpness">How hard, nought to one.</param>
/// <param name="HighDynamicRange">Whether the colour carries values above one.</param>
public readonly record struct UpscaleFrame(
    UpscaleImage Colour,
    UpscaleImage Depth,
    UpscaleImage Motion,
    UpscaleImage Output,
    Vector2 JitterPixels,
    float DeltaSeconds,
    bool Reset,
    Camera? Camera,
    float Aspect,
    bool Sharpen,
    float Sharpness,
    bool HighDynamicRange);

/// <summary>
/// Something that turns a small picture into a big one.
/// </summary>
/// <remarks>
/// <para>
/// Three implement it and they have almost nothing in common underneath: one is a compute
/// shader in this repository, one is a call into AMD's runtime, and one is a call into
/// NVIDIA's through Streamline. What they share is exactly this — they are given the frame
/// at render resolution and fill an image at display resolution — and keeping that the
/// whole of the contract is what lets the renderer switch between them while the game is
/// running.
/// </para>
/// <para>
/// <b>The renderer owns the images and the barriers.</b> Every implementation is handed its
/// inputs already in <c>ShaderReadOnlyOptimal</c> and its output in <c>General</c>, and
/// must leave them that way. Letting each backend transition for itself was the first
/// design and it does not survive contact with a vendor runtime that transitions the same
/// image again on the way in.
/// </para>
/// </remarks>
internal interface IUpscaler : IDisposable
{
    /// <summary>Which one this is.</summary>
    UpscalerKind Kind { get; }

    /// <summary>What to say about it in the startup report.</summary>
    string Describe();

    /// <summary>Whether the history has to be thrown away and rebuilt.</summary>
    /// <param name="plan">What the player is asking for now.</param>
    /// <param name="render">The size the room is being drawn at.</param>
    /// <param name="display">The size it is being shown at.</param>
    /// <returns>False when this instance cannot serve that and must be replaced.</returns>
    bool Serves(UpscalePlan plan, Extent2D render, Extent2D display);

    /// <summary>Records the upscale.</summary>
    /// <param name="command">The frame's command buffer.</param>
    /// <param name="frame">What to upscale.</param>
    /// <returns>
    /// False when the backend failed and should be torn down. A vendor runtime can decline
    /// a frame — a driver that lost its NGX feature, a context that outlived its device —
    /// and the answer to that is to fall back rather than to stop drawing.
    /// </returns>
    bool Record(CommandBuffer command, in UpscaleFrame frame);
}
