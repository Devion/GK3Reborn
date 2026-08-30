// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>How a frame of film is fitted into the window, and what the display wants.</summary>
/// <param name="Fit">How much of the window the picture covers, and where it starts.</param>
/// <param name="Display">Which encoding, paper white, and the headroom above it.</param>
/// <remarks>
/// One block across both stages. The vertex stage reads the first four floats and the
/// fragment stage the last four; two stages describing one push constant block differently
/// is a validation error at best and a driver disagreement at worst.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MovieConstants(Vector4 Fit, DisplayEncode Display);

/// <summary>A frame of film, letterboxed into the window.</summary>
/// <remarks>
/// <para>
/// <b>Letterboxed rather than stretched.</b> GK3's movies are 4:3 — 320x240 originally, and
/// larger where they have been re-upscaled — and a modern window is not. Filling it would
/// make everybody in the cutscene short and wide, so the picture is fitted to whichever
/// dimension runs out first and the rest is left black. The scans and the parchment
/// close-ups are not 4:3 at all, which is the other reason to fit rather than assume.
/// </para>
/// <para>
/// Sampled linearly and clamped. A movie is a photograph rather than a bitmap font, so
/// filtering it is what a player expects; clamping keeps the edge pixels from wrapping round
/// into the letterbox.
/// </para>
/// </remarks>
public static class MovieShaders
{
    /// <summary>The vertex stage.</summary>
    public const string Vertex = """
        #version 450

        // Declared identically in both stages, members this one never reads included. A
        // push constant block is one block across the pipeline, and two stages describing
        // it differently is a validation error at best and a driver disagreement at worst.
        layout(push_constant) uniform Fit
        {
            // How much of the window the picture covers, and where it starts. In clip
            // space, so the whole of the letterboxing is two numbers and an offset.
            vec2 scale;
            vec2 offset;

            // The fragment stage's, and unread here.
            vec4 display;
        } fit;

        layout(location = 0) out vec2 fragTexCoord;

        void main()
        {
            // One triangle covering the whole window, from nothing but the vertex index.
            // Two of its corners are outside the window and are clipped, which is cheaper
            // than the two triangles of a quad and has no seam down the middle.
            vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);

            gl_Position = vec4((uv * 2.0) - 1.0, 0.0, 1.0);

            // The picture is fitted inside the window rather than the triangle being
            // shrunk to fit it: covering every pixel is what lets the bars be painted
            // black here instead of leaving whatever was on screen showing through them.
            fragTexCoord = ((uv - 0.5) / fit.scale) + 0.5;
        }
        """;

    /// <summary>The fragment stage, with the shared display encode spliced in.</summary>
    /// <remarks>See <see cref="DisplayEncoding"/>: one copy of ST.2084 rather than four.</remarks>
    public static string Fragment => Prelude + "\n" + DisplayEncoding.Glsl + "\n" + Body;

    private const string Prelude = """
        #version 450

        layout(binding = 0) uniform sampler2D picture;

        // The same block the vertex stage declares, member for member.
        layout(push_constant) uniform Fit
        {
            // The vertex stage's, and unread here.
            vec2 scale;
            vec2 offset;

            // Which encoding the swapchain wants, where paper white sits, and how far
            // above it the display goes. All nought on an ordinary sRGB surface.
            vec4 display;
        } fit;

        layout(location = 0) in vec2 fragTexCoord;
        layout(location = 0) out vec4 outColor;
        """;

    private const string Body = """
        void main()
        {
            // Outside the picture is the letterbox, and the letterbox is black. Leaving it
            // alone would show the room behind the cutscene down both sides.
            vec2 uv = fragTexCoord;

            if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
            {
                // Black is black in every encoding, so there is nothing to convert.
                outColor = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }

            // Opaque. A movie has nothing behind it worth showing through, and a frame
            // whose alpha channel the decoder filled with something unhelpful should not
            // be able to make the room appear underneath it.
            //
            // The film is standard-range material and stays at paper white on an HDR
            // display: a 1999 cutscene has no highlights above white to recover, and
            // stretching it into the headroom would only make the whites glare.
            outColor = vec4(
                EncodeForDisplay(texture(picture, uv).rgb, fit.display.xyz), 1.0);
        }
        """;
}
