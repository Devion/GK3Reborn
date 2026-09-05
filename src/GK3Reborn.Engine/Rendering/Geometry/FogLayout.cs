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
/// What the fog pass binds, declared once for both backends.
/// </summary>
/// <remarks>
/// <para>
/// The same four things the room's own shading reads, minus everything about a surface: the
/// rig, the grid that says which of it reaches where, the list inside a cell, and the depth
/// the room left behind. Fog is lit by the lamps in the room and by nothing else, so a pass
/// that could not see the rig would have to be told a colour instead — see
/// <see cref="FogVolume.Colour"/> for why that is the wrong answer.
/// </para>
/// <para>
/// <b>The rig is bound directly rather than through the frame's own set.</b> The three
/// buffers are one apiece for a whole scene — they are written when a room loads and not
/// again — so there is nothing per-frame to keep apart here, and a set of this pass's own
/// costs one allocation and saves matching a layout it does not otherwise share. What the
/// frame's uniform block would have carried instead travels as push constants; the block is
/// a hundred and ninety-two bytes, which fits both backends with room over.
/// </para>
/// </remarks>
public static class FogLayout
{
    /// <summary>The one set: the rig, the grid and the depth.</summary>
    public const uint FogSet = 0;

    /// <summary>What the pass binds.</summary>
    public static ShaderLayout Bindings { get; } = new(
        [
            // The rig, and which of it reaches where. Storage buffers for the reason
            // MeshLayout gives: a uniform block is sized when the shader is compiled and a
            // room's rig is not.
            new ShaderBinding(FogSet, 0, ShaderBindingKind.ReadOnlyStorageBuffer, ShaderStages.Fragment),
            new ShaderBinding(FogSet, 1, ShaderBindingKind.ReadOnlyStorageBuffer, ShaderStages.Fragment),
            new ShaderBinding(FogSet, 2, ShaderBindingKind.ReadOnlyStorageBuffer, ShaderStages.Fragment),

            // How far the room got in front of each pixel, which is where the march stops.
            new ShaderBinding(FogSet, 3, ShaderBindingKind.CombinedImageSampler, ShaderStages.Fragment),
        ],
        PushConstantBytes: 192);
}

/// <summary>What the fog pass is told, in a hundred and ninety-two bytes.</summary>
/// <remarks>
/// Shared between the backends because it is the shader's own block, and the shader is one
/// source compiled two ways. A field reordered here and not there is a picture that is wrong
/// in a way neither compiler can see.
/// </remarks>
/// <param name="ViewProjectionInverse">
/// Clip space back to the world. It is inverted from the <em>jittered</em> projection the
/// room was drawn with, because the depth it is unprojecting was written by that one; the
/// unjittered matrix would move every reconstructed point by up to a pixel's worth of world,
/// which at the far plane is a good deal of world.
/// </param>
/// <param name="EyeAndTime">Where the camera is in xyz, and the clock in seconds in w.</param>
/// <param name="GridOrigin">
/// The corner the light grid starts at, and how wide one of its cells is. The same numbers
/// the mesh shader reads from the frame's block; see <see cref="SceneLightGrid"/>.
/// </param>
/// <param name="GridCounts">How many cells the grid has along each axis, and nothing in w.</param>
/// <param name="Tint">What a scattering event returns in rgb, and the density per unit in w.</param>
/// <param name="Layer">
/// The top of the layer, how fast it thins above that, the phase's g, and how much of the
/// ambient floor the fog scatters.
/// </param>
/// <param name="Grain">
/// The noise's cell size, how fast it drifts, how far it takes the density either side of
/// its mean, and how many steps the march takes.
/// </param>
/// <param name="Ambient">The room's ambient floor in rgb, and nothing in w.</param>
/// <param name="Screen">The viewport in pixels in xy, and nothing in zw.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct FogConstants(
    Matrix4x4 ViewProjectionInverse,
    Vector4 EyeAndTime,
    Vector4 GridOrigin,
    Vector4 GridCounts,
    Vector4 Tint,
    Vector4 Layer,
    Vector4 Grain,
    Vector4 Ambient,
    Vector4 Screen)
{
    /// <summary>Fills the block from a scene, a camera and a viewport.</summary>
    /// <param name="fog">The layer to draw.</param>
    /// <param name="grid">How the room's lights are divided up, or null where none were laid.</param>
    /// <param name="ambient">The room's ambient floor, which the tier decides.</param>
    /// <param name="camera">Where the frame was drawn from.</param>
    /// <param name="seconds">The clock the flicker runs on.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The block.</returns>
    /// <remarks>
    /// Here rather than in either backend's pass, because both fill it from the same things
    /// and the only way for two copies of this arithmetic to stay equal is for there to be
    /// one of them.
    /// </remarks>
    public static FogConstants For(
        FogVolume fog,
        SceneLightGrid? grid,
        Vector3 ambient,
        Camera camera,
        float seconds,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(camera);

        float aspect = (float)width / Math.Max(1, height);
        Matrix4x4 viewProjection = camera.View * camera.Projection(aspect);

        // A projection this renderer builds is always invertible; the fallback is identity
        // rather than a throw because a pass that draws the fog in the wrong place for one
        // frame is a better failure than a frame that does not draw.
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse))
        {
            inverse = Matrix4x4.Identity;
        }

        // A room with no grid — nothing has laid a rig yet — gets a single cell covering
        // everywhere, which is what the mesh shader's own default is. The cell is then
        // empty and the fog is lit by the ambient floor alone, which is the right picture
        // for a room whose lights have not arrived.
        Vector3 origin = grid?.Origin ?? Vector3.Zero;
        float cell = grid?.Cell ?? 1f;
        (int X, int Y, int Z) counts = grid?.Counts ?? (1, 1, 1);

        return new FogConstants(
            inverse,
            new Vector4(camera.Position, seconds),
            new Vector4(origin, cell),
            new Vector4(counts.X, counts.Y, counts.Z, 0f),
            new Vector4(fog.Colour, fog.Density),
            new Vector4(fog.Top, MathF.Max(fog.Falloff, 0.001f), fog.Anisotropy, fog.Ambient),
            new Vector4(fog.NoiseScale, fog.NoiseDrift, fog.NoiseStrength, fog.Steps),
            new Vector4(ambient, 0f),
            new Vector4(width, height, 0f, 0f));
    }
}
