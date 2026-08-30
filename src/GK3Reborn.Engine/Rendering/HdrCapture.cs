// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering;

/// <summary>How a high dynamic range frame is turned back into an ordinary picture.</summary>
/// <remarks>
/// <para>
/// A screenshot is an eight-bit sRGB file whatever the swapchain was, so a frame presented
/// in HDR10 or scRGB has to be brought back down before it can be written out. That is not a
/// nicety: a ten-bit frame read as though it were four bytes of sRGB is not a slightly wrong
/// picture, it is the right picture with every value scrambled — the geometry and the
/// texture detail come through perfectly and the colours are noise, which reads as a
/// rendering bug rather than as a screenshot bug.
/// </para>
/// <para>
/// Shared between the backends because it is arithmetic about a format rather than about an
/// API, and because the two of them present the same three formats for the same reasons.
/// </para>
/// </remarks>
public static class HdrCapture
{
    /// <summary>Turns a wide frame into eight-bit sRGB.</summary>
    /// <param name="raw">The frame, tightly packed, four bytes a pixel or eight.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="halfFloat">Whether it is scRGB halves rather than ten-bit ST.2084.</param>
    /// <param name="paperWhite">Where diffuse white sat, in candelas per square metre.</param>
    /// <returns>Four bytes a pixel, sRGB, opaque.</returns>
    /// <remarks>
    /// Paper white becomes one, so the picture looks like what a standard-range display would
    /// have shown of the same frame. Anything above it clips, which is the whole point of the
    /// format it is being converted into.
    /// </remarks>
    public static byte[] ToOrdinary(
        ReadOnlySpan<byte> raw, int width, int height, bool halfFloat, float paperWhite)
    {
        byte[] pixels = new byte[width * height * 4];
        float white = MathF.Max(paperWhite, 1f);

        for (int i = 0; i < width * height; i++)
        {
            float r;
            float g;
            float b;

            if (halfFloat)
            {
                // scRGB: linear light in sRGB primaries, where one unit is 80 candelas.
                int at = i * 8;
                float scale = 80f / white;

                r = (float)BitConverter.ToHalf(raw[at..]) * scale;
                g = (float)BitConverter.ToHalf(raw[(at + 2)..]) * scale;
                b = (float)BitConverter.ToHalf(raw[(at + 4)..]) * scale;
            }
            else
            {
                // HDR10: ten bits a channel through ST.2084, in Rec.2020 primaries. The pack
                // order is A2B10G10R10, so red is the low ten bits.
                uint packed = BitConverter.ToUInt32(raw[(i * 4)..]);

                float wideRed = Luminance(packed & 0x3FF) / white;
                float wideGreen = Luminance((packed >> 10) & 0x3FF) / white;
                float wideBlue = Luminance((packed >> 20) & 0x3FF) / white;

                // Rec.2020 back to Rec.709, which is the inverse of the matrix the output
                // pass applied. Out-of-gamut colours come back negative and are clamped.
                r = (1.6605f * wideRed) - (0.5876f * wideGreen) - (0.0728f * wideBlue);
                g = (-0.1246f * wideRed) + (1.1329f * wideGreen) - (0.0083f * wideBlue);
                b = (-0.0182f * wideRed) - (0.1006f * wideGreen) + (1.1187f * wideBlue);
            }

            pixels[(i * 4) + 0] = Encode(r);
            pixels[(i * 4) + 1] = Encode(g);
            pixels[(i * 4) + 2] = Encode(b);
            pixels[(i * 4) + 3] = 255;
        }

        return pixels;
    }

    /// <summary>Undoes ST.2084, giving absolute luminance in candelas.</summary>
    /// <param name="tenBits">One channel, as the ten bits the format stores.</param>
    /// <returns>The luminance it stands for.</returns>
    public static float Luminance(uint tenBits)
    {
        const float M1 = 0.1593017578125f;
        const float M2 = 78.84375f;
        const float C1 = 0.8359375f;
        const float C2 = 18.8515625f;
        const float C3 = 18.6875f;

        float encoded = MathF.Pow(tenBits / 1023f, 1f / M2);
        float numerator = MathF.Max(encoded - C1, 0f);

        return 10_000f * MathF.Pow(numerator / (C2 - (C3 * encoded)), 1f / M1);
    }

    /// <summary>A linear value as an sRGB byte.</summary>
    /// <param name="linear">The value, where one is diffuse white.</param>
    /// <returns>The byte to write.</returns>
    public static byte Encode(float linear)
    {
        float value = Math.Clamp(linear, 0f, 1f);

        float encoded = value <= 0.0031308f
            ? value * 12.92f
            : (1.055f * MathF.Pow(value, 1f / 2.4f)) - 0.055f;

        return (byte)Math.Clamp(MathF.Round(encoded * 255f), 0f, 255f);
    }
}
