// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Rendering.Shaders;
using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>What the reflection passes bind, declared once for both backends.</summary>
/// <remarks>
/// Two compute shaders and one layout. The downsample builds a min-depth pyramid a level at
/// a time and the march casts a ray a pixel over it; they read the same six textures and
/// write the same two, and which of the two each one means is a push constant rather than a
/// binding. One layout is one thing to keep in step with the shaders instead of two.
/// </remarks>
public static class ReflectLayout
{
    /// <summary>How many levels the depth pyramid has, the full-size one included.</summary>
    /// <remarks>
    /// Six halvings takes a 1280 by 720 frame down to 40 by 23, which is coarse enough that
    /// a ray crossing empty space clears it in a step or two.
    /// </remarks>
    public const int Levels = 7;

    /// <summary>How many bytes of push constants each stage takes.</summary>
    /// <remarks>The size of the level being written, and which level it is.</remarks>
    public const uint LevelConstantBytes = 12;

    /// <summary>How far behind a surface a hit may land and still be that surface.</summary>
    /// <remarks>In scene units, where a hotel room is about a thousand across.</remarks>
    public const float Thickness = 250f;

    /// <summary>What both stages bind.</summary>
    public static ShaderLayout Bindings { get; } = new(
    [
        new ShaderBinding(0, 0, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 1, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 2, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 3, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 4, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 5, ShaderBindingKind.SampledImage, ShaderStages.Compute),
        new ShaderBinding(0, 6, ShaderBindingKind.Sampler, ShaderStages.Compute),
        new ShaderBinding(0, 7, ShaderBindingKind.StorageImage, ShaderStages.Compute),
        new ShaderBinding(0, 8, ShaderBindingKind.StorageImage, ShaderStages.Compute),
        new ShaderBinding(0, 9, ShaderBindingKind.UniformBuffer, ShaderStages.Compute),
    ],
    LevelConstantBytes);
}

/// <summary>Which level of the pyramid is being written, and how big it is.</summary>
/// <param name="Width">Width of that level in pixels.</param>
/// <param name="Height">Its height.</param>
/// <param name="Level">Which level it is, counting the full-size one as zero.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct LevelConstants(int Width, int Height, int Level);

/// <summary>What the marching stage reads, once a frame.</summary>
/// <param name="Projection">The camera's projection.</param>
/// <param name="InverseProjection">And back again.</param>
/// <param name="View">Where the camera is looking from.</param>
/// <param name="InverseViewProjection">Clip space back to the world.</param>
/// <param name="EyeAndSeed">The camera position, and this frame's place in the grain sequence.</param>
/// <param name="Width">Viewport width in pixels.</param>
/// <param name="Height">Viewport height in pixels.</param>
/// <param name="InverseWidth">One over the width.</param>
/// <param name="InverseHeight">One over the height.</param>
/// <param name="Tuning">Thickness, the roughest surface worth a ray, and the level count.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ReflectUniforms(
    Matrix4x4 Projection,
    Matrix4x4 InverseProjection,
    Matrix4x4 View,
    Matrix4x4 InverseViewProjection,
    Vector4 EyeAndSeed,
    int Width,
    int Height,
    float InverseWidth,
    float InverseHeight,
    Vector4 Tuning);
