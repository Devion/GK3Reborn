// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>
/// What the sun ray pass binds, declared once for both backends.
/// </summary>
public static class SunRayLayout
{
    /// <summary>The one set: the depth and the shafts.</summary>
    public const uint RaySet = 0;

    /// <summary>What the pass binds.</summary>
    public static ShaderLayout Bindings { get; } = new(
        [
            // How far the room got in front of each pixel, which is what says whether a
            // point on the walk toward the sun is sky or something in the way, and where
            // a ray through a shaft of daylight stops.
            new ShaderBinding(RaySet, 0, ShaderBindingKind.CombinedImageSampler, ShaderStages.Fragment),

            // The shafts of daylight at the room's windows: a count and up to sixteen
            // boxes. A storage buffer rather than more push constants, which are full.
            new ShaderBinding(RaySet, 1, ShaderBindingKind.ReadOnlyStorageBuffer, ShaderStages.Fragment),
        ],
        PushConstantBytes: 208);

    /// <summary>How many bytes the shaft buffer holds: a count, padded, then the boxes.</summary>
    public const int ShaftBufferBytes = 16 + (LightShaft.Capacity * 64);

    /// <summary>Writes the shafts as the shader reads them.</summary>
    /// <param name="shafts">The shafts, at most <see cref="LightShaft.Capacity"/>.</param>
    /// <returns>The bytes to put in the buffer.</returns>
    public static byte[] PackShafts(IReadOnlyList<LightShaft> shafts)
    {
        ArgumentNullException.ThrowIfNull(shafts);

        byte[] bytes = new byte[ShaftBufferBytes];
        int count = Math.Min(shafts.Count, LightShaft.Capacity);

        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), count);

        for (int i = 0; i < count; i++)
        {
            LightShaft shaft = shafts[i];

            var packed = new GpuShaft(
                new Vector4(shaft.Origin, shaft.HalfWidth),
                new Vector4(shaft.Direction, shaft.HalfHeight),
                new Vector4(shaft.Colour, shaft.Length),
                new Vector4(shaft.Strength, 0f, 0f, 0f));

            System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(16 + (i * 64), 64), in packed);
        }

        return bytes;
    }
}

/// <summary>One shaft of daylight, as the shader reads it.</summary>
/// <param name="OriginAndHalfWidth">xyz where the pane is, w half the shaft's width.</param>
/// <param name="DirectionAndHalfHeight">xyz which way the light goes, w half the shaft's height.</param>
/// <param name="ColourAndLength">rgb the light, w how far the shaft reaches.</param>
/// <param name="Strength">x how bright it is drawn; the rest nothing.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GpuShaft(
    Vector4 OriginAndHalfWidth,
    Vector4 DirectionAndHalfHeight,
    Vector4 ColourAndLength,
    Vector4 Strength);

/// <summary>What the sun ray pass is told, in two hundred and eight bytes.</summary>
/// <param name="ViewProjectionInverse">
/// Clip space back to the world, inverted from the jittered projection the room was drawn
/// with, for the fog's reason: the depth being unprojected was written by that one.
/// </param>
/// <param name="Forward">Where the camera looks, and nothing in w.</param>
/// <param name="Right">Its right, with the tangent of half the horizontal field of view in w.</param>
/// <param name="Up">Its up, with the tangent of half the vertical field of view in w.</param>
/// <param name="Sun">Toward the sun in the world, and how strongly the rays are drawn in w.</param>
/// <param name="Colour">
/// What the sun's light is in rgb, and in w one when the sky is the generated one — whose
/// clouds hold the light back — and nought when it is a painting.
/// </param>
/// <param name="Clouds">
/// The sky's cloud coverage, cloud scale and stable offset, exactly as the sky pass has them,
/// so the rays break through the same gaps. Nothing when <paramref name="Colour"/>'s w is nought.
/// </param>
/// <param name="Screen">The viewport in pixels in xy, and nothing in zw.</param>
/// <param name="Tuning">
/// How many samples the walk takes, how much each step is worth against the one before, how
/// far toward the sun the walk goes as a fraction of the distance, and how bright the whole is.
/// </param>
/// <param name="Time">
/// x the clock in seconds, which the dust in a shaft drifts on; y how many shafts there
/// are; the rest nothing.
/// </param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SunRayConstants(
    Matrix4x4 ViewProjectionInverse,
    Vector4 Forward,
    Vector4 Right,
    Vector4 Up,
    Vector4 Sun,
    Vector4 Colour,
    Vector4 Clouds,
    Vector4 Screen,
    Vector4 Tuning,
    Vector4 Time)
{
    /// <summary>How many depth reads a pixel spends on the walk.</summary>
    public const float Samples = 40f;

    /// <summary>How much each step is worth against the one before it.</summary>
    public const float Decay = 0.965f;

    /// <summary>How far toward the sun the walk goes, as a fraction of the way.</summary>
    public const float Density = 0.92f;

    /// <summary>
    /// How bright the rays are at the pass's own tuning, in linear light. Set by rendering
    /// WOD at ten in the morning, where the sun stands just off the top left of the opening
    /// shot: at 0.55 the sky beside it went to white and the whole left half of the frame
    /// read as glare rather than as air with light in it.
    /// </summary>
    public const float Exposure = 0.40f;

    /// <summary>Fills the block from the sun, a camera and a viewport.</summary>
    /// <param name="rays">The sun, and how strongly to draw it.</param>
    /// <param name="camera">Where the frame was drawn from.</param>
    /// <param name="clouds">
    /// The generated sky's cloud constants, or null where the sky is a painted cubemap.
    /// </param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="seconds">The clock the dust in the shafts drifts on.</param>
    /// <returns>The block.</returns>
    public static SunRayConstants For(
        SunRays rays, Camera camera, Vector4? clouds, int width, int height, float seconds = 0f)
    {
        ArgumentNullException.ThrowIfNull(camera);

        float aspect = (float)width / Math.Max(1, height);

        if (!Matrix4x4.Invert(camera.View * camera.Projection(aspect), out Matrix4x4 inverse))
        {
            inverse = Matrix4x4.Identity;
        }

        // The same orthonormal basis the sky is drawn with, and it has to be: the rays are
        // aimed at where the sky put the sun.
        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(camera.Up, forward));
        Vector3 up = Vector3.Cross(forward, right);
        float tanHalf = MathF.Tan(camera.FieldOfView / 2f);

        return new SunRayConstants(
            inverse,
            new Vector4(forward, 0f),
            new Vector4(right, tanHalf * width / Math.Max(1, height)),
            new Vector4(up, tanHalf),

            // A room with no sun in the sky, only at its windows, hands the sky's walk
            // nothing to aim at.
            new Vector4(rays.Sky ? rays.Toward : Vector3.Zero, rays.Strength),
            new Vector4(rays.Colour, clouds is null ? 0f : 1f),
            clouds ?? Vector4.Zero,
            new Vector4(width, height, 0f, 0f),
            new Vector4(Samples, Decay, Density, Exposure),
            new Vector4(seconds, Math.Min(rays.Windows.Count, LightShaft.Capacity), 0f, 0f));
    }
}
