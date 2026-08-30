// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>What the tracing stage is told, in eighty-eight bytes.</summary>
/// <param name="ViewProjectionInverse">Clip space back to the world, to make a ray from a pixel.</param>
/// <param name="Width">Viewport width in pixels.</param>
/// <param name="Height">Viewport height in pixels.</param>
/// <param name="Radius">How far an occlusion ray looks.</param>
/// <param name="Seed">Where in the sequence this frame's grain starts.</param>
/// <param name="Samples">How many rays each pixel spends on each signal.</param>
/// <param name="Padding">Unused, and there so the block is a whole number of vectors.</param>
/// <remarks>
/// Shared between the backends because it is the shader's own struct, and the shader is one
/// source compiled two ways. A field reordered here and not there would be a picture that is
/// wrong in a way neither compiler can see.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct TraceConstants(
    Matrix4x4 ViewProjectionInverse,
    int Width,
    int Height,
    float Radius,
    float Seed,
    int Samples,
    int Padding);

/// <summary>Which of the blurs this is, and how far apart its taps are.</summary>
/// <param name="StepSize">The gap between neighbouring taps, which doubles each pass.</param>
/// <param name="Index">Which pass of the three it is.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct StageConstants(int StepSize, int Index);

/// <summary>What the filtering stages read, once a frame.</summary>
/// <param name="ProjectionInverse">Clip space back to view space.</param>
/// <param name="ReprojectionMatrix">Where a pixel of this frame sat in the last one's clip space.</param>
/// <param name="ViewProjectionInverse">Clip space back to the world.</param>
/// <param name="EyeAndFirst">The camera position, and one in w on the first frame of a scene.</param>
/// <param name="Width">Viewport width in pixels.</param>
/// <param name="Height">Viewport height in pixels.</param>
/// <param name="InverseWidth">One over the width, so the shader need not divide.</param>
/// <param name="InverseHeight">One over the height.</param>
/// <param name="Sigma">How far apart two depths may be before they stop being one surface.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DenoiseUniforms(
    Matrix4x4 ProjectionInverse,
    Matrix4x4 ReprojectionMatrix,
    Matrix4x4 ViewProjectionInverse,
    Vector4 EyeAndFirst,
    int Width,
    int Height,
    float InverseWidth,
    float InverseHeight,
    Vector4 Sigma);
