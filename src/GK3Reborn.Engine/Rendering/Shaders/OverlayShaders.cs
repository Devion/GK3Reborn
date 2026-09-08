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
/// <param name="Picture">
/// Nought for a glyph, and otherwise <see cref="OverlayShaders.PictureOver"/> or
/// <see cref="OverlayShaders.PictureBlended"/>. The blend state does the combining; this
/// decides what the shader has to hand the blender to make that state come out right at
/// less than full opacity.
/// </param>
/// <param name="Pad0">Padding to the vector's alignment.</param>
/// <param name="Pad1">Padding.</param>
/// <param name="Pad2">Padding.</param>
/// <param name="Transfer">Which encoding the swapchain wants.</param>
/// <param name="PaperWhite">Where diffuse white sits.</param>
/// <param name="Headroom">How far above it the display goes.</param>
/// <param name="Unused">Padding, so the vector is a whole float4.</param>
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
public static class OverlayShaders
{
    /// <summary>How many bytes of push constants the fragment stage takes.</summary>
    public const uint ConstantBytes = 32;

    /// <summary>A run of the sheet of letters: a shape cut out of a colour.</summary>
    public const int Glyphs = 0;

    /// <summary>One of the screens' own pictures, over what is behind it.</summary>
    public const int PictureOver = 1;

    /// <summary>
    /// One of the screens' own pictures, screened or multiplied into what is behind it.
    /// </summary>
    /// <remarks>
    /// <b>One value for both, on purpose.</b> Each wants the picture already faded by its
    /// own coverage — <c>aS</c> — and the pair of blend factors does the rest: screen is
    /// <c>(one, one minus source colour)</c>, which gives <c>D + aS(1 - D)</c>, and multiply
    /// is <c>(destination colour, one minus source alpha)</c>, which gives
    /// <c>D(1 - a(1 - S))</c>. Both leave the destination exactly alone where the picture is
    /// transparent, which is the property the whole thing turns on.
    /// <para>
    /// It was two values and two branches, and the failure that cost was ugly and hard to
    /// see: a run whose shader took one branch while its pipeline carried the other blend
    /// wrote a *factor* where a *colour* was wanted, and the sigils came out as flat dark
    /// squares the size of their own quads. With one branch there is nothing left to
    /// disagree about.
    /// </para>
    /// </remarks>
    public const int PictureBlended = 2;

    /// <summary>Which run kind a blend wants written.</summary>
    /// <param name="blend">How the run is combined with the screen.</param>
    /// <returns>The constant the fragment stage is pushed.</returns>
    public static int PictureMode(OverlayBlend blend) => blend switch
    {
        OverlayBlend.Screen or OverlayBlend.Multiply => PictureBlended,
        _ => PictureOver,
    };

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
                vec3 art = texel.rgb * fragColor.rgb;
                float cover = fragColor.a * texel.a;

                // Screened or multiplied, at whatever opacity the quad asked for. Both
                // want the same thing written: the colour already faded by its own
                // coverage, with the coverage in the alpha. The pair of blend factors is
                // what makes one of them a screen and the other a multiply, and neither
                // touches the destination where the picture is transparent. See
                // PictureBlended, which is why this is one branch and not two.
                if (draw.picture == 2)
                {
                    outColor = vec4(
                        EncodeForDisplay(art * cover, draw.display.xyz), cover);

                    return;
                }

                outColor = vec4(EncodeForDisplay(art, draw.display.xyz), cover);

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
