// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>What the fade is told: the colour to draw, and what the display wants.</summary>
/// <param name="Color">The wash, straight alpha.</param>
/// <param name="Display">Which encoding, paper white, and the headroom above it.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct FadeConstants(Vector4 Color, DisplayEncode Display);

/// <summary>
/// A flat colour drawn over the finished picture, at whatever opacity it is given.
/// </summary>
/// <remarks>
/// <para>
/// What a scene change looks like. Everything else the renderer draws is a thing in the
/// world or a thing on the interface; this is neither, and it goes over both — a fade that
/// left the inventory bar showing would be a fade of the room rather than of the picture.
/// </para>
/// <para>
/// No vertex buffer, no descriptors and no texture: one triangle covering the screen,
/// generated from the vertex index, and a push constant carrying the colour. That makes it
/// the cheapest pass in the renderer, which matters because it is recorded on every frame of
/// the game and does nothing on almost all of them.
/// </para>
/// <para>
/// The colour is written as it is given and the blend is straight, so an alpha of one leaves
/// the target exactly that colour and an alpha of a half leaves it halfway there. The
/// interface's own colours have to be converted first; this one writes the number it is
/// handed, and the ramp it is driven along is the caller's business.
/// </para>
/// </remarks>
public static class FadeShaders
{
    /// <summary>The vertex stage.</summary>
    /// <remarks>
    /// GLSL rather than HLSL: a push constant is one unambiguous declaration here and a coin
    /// toss through shaderc's HLSL front end, which fails by compiling and drawing nothing.
    /// </remarks>
    public const string Vertex = """
        #version 450

        void main()
        {
            // One oversized triangle rather than two, so there is no seam down the diagonal
            // and no vertex buffer to bind. Vertices 0, 1 and 2 land at (-1,-1), (3,-1) and
            // (-1,3); the part of each that falls outside the viewport is clipped away and
            // what remains covers it exactly.
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4((corner * 2.0) - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>The fragment stage, with the shared display encode spliced in.</summary>
    /// <remarks>See <see cref="DisplayEncoding"/>: one copy of ST.2084 rather than four.</remarks>
    public static string Fragment => Prelude + "\n" + DisplayEncoding.Glsl + "\n" + Body;

    private const string Prelude = """
        #version 450

        layout(push_constant) uniform Fade
        {
            vec4 color;

            // Which encoding the swapchain wants, where paper white sits, and how far above
            // it the display goes. All nought on an ordinary sRGB surface.
            vec4 display;
        } fade;

        layout(location = 0) out vec4 outColor;
        """;

    private const string Body = """

        void main()
        {
            // The fade covers the interface as well as the room, so it is encoded the same
            // way both of them were. A fade written unencoded onto a PQ surface is a wash of
            // the wrong colour that gets *lighter* as it deepens.
            outColor = vec4(EncodeForDisplay(fade.color.rgb, fade.display.xyz), fade.color.a);
        }
        """;
}
