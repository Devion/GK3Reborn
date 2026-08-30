// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>One corner of an overlay rectangle.</summary>
/// <param name="Position">Where it is, in clip space.</param>
/// <param name="TexCoord">Where it reads from the atlas.</param>
/// <param name="Color">Its tint, straight alpha.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct OverlayVertex(Vector2 Position, Vector2 TexCoord, Vector4 Color);

/// <summary>What the interface's fragment stage is told, per run of quads.</summary>
/// <param name="Picture">Nought for a glyph, one for one of the screens' own pictures.</param>
/// <param name="Pad0">Padding to the vector's alignment.</param>
/// <param name="Pad1">Padding.</param>
/// <param name="Pad2">Padding.</param>
/// <param name="Transfer">Which encoding the swapchain wants.</param>
/// <param name="PaperWhite">Where diffuse white sits.</param>
/// <param name="Headroom">How far above it the display goes.</param>
/// <param name="Unused">Padding, so the vector is a whole float4.</param>
/// <remarks>
/// Twelve bytes of padding between the flag and the vector, because a vector in a push
/// constant block is aligned to sixteen bytes whatever precedes it. Writing this as an
/// <c>int</c> and three floats put the shader's idea of paper white twelve bytes past the
/// end of what was pushed, and the interface came out almost black on an HDR display.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct OverlayConstants(
    int Picture,
    int Pad0,
    int Pad1,
    int Pad2,
    float Transfer,
    float PaperWhite,
    float Headroom,
    float Unused);

/// <summary>The interface, drawn on top of the room.</summary>
/// <remarks>
/// One pipeline, one texture, one vertex buffer, one draw. The interface is a few hundred
/// rectangles at most and they all come from the same atlas, so batching them by anything
/// would cost more bookkeeping than it saved.
/// </remarks>
public static class OverlayShaders
{
    /// <summary>How many bytes of push constants the fragment stage takes.</summary>
    public const uint ConstantBytes = 32;

    /// <summary>The vertex stage.</summary>
    public const string Vertex = """
        #version 450

        layout(location = 0) in vec2 inPosition;
        layout(location = 1) in vec2 inTexCoord;
        layout(location = 2) in vec4 inColor;

        layout(location = 0) out vec2 fragTexCoord;
        layout(location = 1) out vec4 fragColor;

        void main()
        {
            // Already in clip space. The display list knows the size of the surface it was
            // laid out for, so converting there costs one multiply per corner on the CPU
            // and removes a push constant from the pipeline.
            gl_Position = vec4(inPosition, 0.0, 1.0);
            fragTexCoord = inTexCoord;
            fragColor = inColor;
        }
        """;

    /// <summary>
    /// The fragment stage, with the shared display encode spliced into the middle of it.
    /// </summary>
    /// <remarks>
    /// Two halves and a shared function between them, rather than one string, because the
    /// encode is the same arithmetic in four passes and four copies of ST.2084 is four
    /// places for it to be wrong differently. See <see cref="DisplayEncoding"/>.
    /// </remarks>
    public static string Fragment => Prelude + "\n" + DisplayEncoding.Glsl + "\n" + Body;

    private const string Prelude = """
        #version 450

        layout(binding = 0) uniform sampler2D atlas;

        // Zero for the sheet of letters, one for one of the screens' own pictures. A
        // picture is content rather than a stencil, so it is drawn as it is; a glyph is a
        // shape cut out of a colour.
        // The offsets are stated rather than left to the compiler. A vector is aligned to
        // sixteen bytes in this layout whatever precedes it, so an int followed by a vec3
        // does *not* put the vector at offset four — it puts it at sixteen, and a push of
        // sixteen bytes then leaves the shader reading past the end of the range. Which it
        // did: the interface came out almost black, because what it read as "paper white"
        // was whatever the driver had left there.
        layout(push_constant) uniform Draw
        {
            layout(offset = 0) int picture;

            // Which encoding the swapchain wants, where paper white sits, and how far
            // above it the display goes. All nought on an ordinary sRGB surface, where
            // the hardware does the encode and this shader writes linear light.
            layout(offset = 16) vec4 display;
        } draw;

        layout(location = 0) in vec2 fragTexCoord;
        layout(location = 1) in vec4 fragColor;

        layout(location = 0) out vec4 outColor;
        """;

    private const string Body = """
        void main()
        {
            vec4 texel = texture(atlas, fragTexCoord);

            if (draw.picture != 0)
            {
                // The game's own art: its colour, tinted, and nothing inferred from its
                // brightness. Running a photograph of the Rennes-le-Château countryside
                // through the glyph rule below turns it into a silhouette.
                outColor = vec4(
                    EncodeForDisplay(texel.rgb * fragColor.rgb, draw.display.xyz),
                    fragColor.a * texel.a);

                return;
            }

            // Two font conventions, one rule. White-on-magenta sheets arrive with the
            // magenta already transparent, so brightness leaves them alone but erases the
            // black glyph markers along the top of the sheet. Grey-on-black sheets have no
            // transparency at all, and brightness is exactly their antialiasing.
            float brightness = max(texel.r, max(texel.g, texel.b));

            outColor = vec4(
                EncodeForDisplay(fragColor.rgb, draw.display.xyz),
                fragColor.a * texel.a * brightness);
        }
        """;
}
