// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>What a pass that writes the swapchain has to know about the display.</summary>
/// <param name="Transfer">
/// Which encoding: nought for an sRGB target the hardware encodes, one for ST.2084, two for
/// scRGB. See <c>OutputPipeline</c> for the same three constants.
/// </param>
/// <param name="PaperWhite">Where diffuse white sits, in candelas per square metre.</param>
/// <param name="Headroom">How far above it the display can go.</param>
/// <param name="Unused">Padding, so the whole is a float4.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DisplayEncode(
    float Transfer,
    float PaperWhite,
    float Headroom,
    float Unused = 0f)
{
    /// <summary>The hardware encodes: write linear and let the sRGB target do the curve.</summary>
    public const float TransferHardware = 0f;

    /// <summary>ST.2084, in Rec.2020 primaries, with luminance in absolute nits.</summary>
    public const float TransferPerceptualQuantiser = 1f;

    /// <summary>scRGB: linear light in sRGB primaries, where 1.0 is 80 nits.</summary>
    public const float TransferExtendedLinear = 2f;

    /// <summary>The ordinary case: an sRGB target, encoded by the hardware.</summary>
    public static DisplayEncode Standard { get; } = new(TransferHardware, 200f, 1f);
}

/// <summary>
/// The one copy of the display encode, shared by every pass that writes the swapchain.
/// </summary>
/// <remarks>
/// <para>
/// The interface, a movie and the fade all draw straight onto the swapchain image, after
/// the room has been encoded onto it. On an ordinary sRGB surface that needs no thought:
/// they write linear light, the hardware encodes it on write, and that is the whole of it.
/// On an HDR surface there is no hardware encode — the format is a plain ten-bit or
/// half-float one — so anything written without doing the encode itself comes out as a
/// number the display reads through the wrong curve. Which is exactly what it looked like:
/// a correct room with a grey, washed-out interface over it.
/// </para>
/// <para>
/// <b>They blend in encoded space.</b> The alternative is to draw the interface into a
/// target of its own and composite it, which is more correct and changes how every existing
/// standard-range frame blends. Between a theoretical improvement to HDR blending and
/// leaving the SDR picture alone, this project's regression images decide it. What it costs
/// is that a half-transparent panel over a bright room sits slightly differently in HDR
/// than it would in SDR; what it saves is every reference image in the corpus.
/// </para>
/// </remarks>
internal static class DisplayEncoding
{
    /// <summary>
    /// The encode, as GLSL, for pasting into a fragment shader.
    /// </summary>
    /// <remarks>
    /// A string constant rather than an include, because this renderer compiles its shaders
    /// from strings in C# and has no include resolver. One copy is what stops the four
    /// implementations of ST.2084 from drifting apart.
    /// </remarks>
    public const string Glsl = """
        // Rec.709 to Rec.2020, which is what ST.2084 signalling is carried in.
        const mat3 kEncodeRec709ToRec2020 = mat3(
            0.6274040, 0.0690970, 0.0163916,
            0.3292820, 0.9195400, 0.0880132,
            0.0433136, 0.0113612, 0.8955950);

        float EncodeQuantiser(float nits)
        {
            const float m1 = 0.1593017578125;
            const float m2 = 78.84375;
            const float c1 = 0.8359375;
            const float c2 = 18.8515625;
            const float c3 = 18.6875;

            float y = clamp(nits / 10000.0, 0.0, 1.0);
            float p = pow(y, m1);

            return pow((c1 + (c2 * p)) / (1.0 + (c3 * p)), m2);
        }

        // Linear light in, whatever the display wants out.
        //
        // x is the transfer function, y is where paper white sits and z is how far above it
        // the display can go. With x at nought this returns what it was given, which is the
        // standard-range path and is why nothing about an ordinary frame changes.
        vec3 EncodeForDisplay(vec3 colour, vec3 display)
        {
            if (display.x < 0.5)
            {
                return colour;
            }

            colour = max(colour, vec3(0.0));

            float paperWhite = max(display.y, 1.0);
            float headroom = max(display.z, 1.0);
            float luminance = dot(colour, vec3(0.2126, 0.7152, 0.0722));

            if (luminance > headroom)
            {
                colour *= headroom / luminance;
            }

            if (display.x > 1.5)
            {
                // scRGB: linear light, sRGB primaries, one unit is 80 candelas.
                return colour * (paperWhite / 80.0);
            }

            vec3 wide = kEncodeRec709ToRec2020 * (colour * paperWhite);

            return vec3(
                EncodeQuantiser(wide.r),
                EncodeQuantiser(wide.g),
                EncodeQuantiser(wide.b));
        }
        """;
}
