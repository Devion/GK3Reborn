// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>
/// A layer of fog lying in a room, as whichever backend is drawing takes it.
/// </summary>
/// <remarks>
/// <para>
/// One layer, described by a height rather than by a box. Every fog worth having in this
/// game is the same shape — something that pools at the bottom of a space and thins out
/// above it — and a height and a falloff say that in two numbers where a box needs six and
/// then has corners the player can walk round. Damp in a cellar and murk in a chasm are the
/// same statement with the plane at a different level; see <see cref="Game.SceneFog"/>.
/// </para>
/// <para>
/// <b>Nothing here is a colour the fog is drawn in.</b> <see cref="Colour"/> is what the
/// fog does to light, not what it looks like: the pass marches the ray, gathers what the
/// room's own lamps put into each step, and tints that. A fog with a colour of its own is
/// the flat grey wash <c>docs/rendering.md</c> already rejected for the horizon — it paints
/// the lit end of a corridor and the dark end the same, which is the one thing that stops
/// fog reading as depth.
/// </para>
/// </remarks>
/// <param name="Colour">
/// How much of each channel a scattering event returns, from nought to one. Water vapour is
/// very nearly white and slightly cool; a tint is how a cellar's damp is told from a
/// chasm's cold.
/// </param>
/// <param name="Density">
/// Extinction at the thickest part of the layer, per world unit. A GK3 unit is about two
/// and a half centimetres, so 0.002 halves the light over four hundred units — the length
/// of a corridor — and 0.01 leaves a twentieth of it at three hundred, which is a depth
/// nothing is visible down.
/// </param>
/// <param name="Top">
/// The world height the layer is at full density up to. Everything below this is fog;
/// everything above it thins by <see cref="Falloff"/>.
/// </param>
/// <param name="Falloff">
/// How many units above <see cref="Top"/> the density falls by a factor of e. Small makes a
/// sharp-topped bank lying on the floor; large lets the layer breathe up into the room.
/// </param>
/// <param name="Anisotropy">
/// How much the fog scatters forward, from -1 to 1, as the Henyey-Greenstein g. Nought
/// scatters in every direction equally and is what makes a fog look like smoke; between 0.4
/// and 0.7 is water, which is why a lamp seen through mist has a halo round it and the same
/// lamp seen from behind does not.
/// </param>
/// <param name="Ambient">
/// How much of the room's ambient floor the fog scatters, as a multiplier. This is the part
/// that is there with every lamp out, and it is what keeps a corner of the layer that no
/// light reaches from being a hole in the picture rather than fog.
/// </param>
/// <param name="NoiseScale">
/// How many world units one cell of the density noise spans. Zero switches the noise off
/// and with it the eight hashes a step costs.
/// </param>
/// <param name="NoiseDrift">How fast the noise moves through the layer, in units a second.</param>
/// <param name="NoiseStrength">
/// How far the noise takes the density either side of its mean, from nought to one. Above
/// about a half the layer stops reading as fog and starts reading as cloud.
/// </param>
/// <param name="Steps">
/// How many samples a ray takes through the layer. They are spread over the part of the ray
/// that is actually in the fog rather than over its whole length, so this is the resolution
/// of the layer and not of the room.
/// </param>
public readonly record struct FogVolume(
    Vector3 Colour,
    float Density,
    float Top,
    float Falloff,
    float Anisotropy,
    float Ambient,
    float NoiseScale,
    float NoiseDrift,
    float NoiseStrength,
    int Steps)
{
    /// <summary>No fog, which is what all but a handful of the corpus's rooms have.</summary>
    public static FogVolume None { get; }

    /// <summary>Whether there is anything here to draw.</summary>
    /// <remarks>
    /// Read before the pass is built as well as before it is recorded: a room with no fog in
    /// it should not pay for a pipeline, and two hundred of them have none.
    /// </remarks>
    public bool Any => Density > 0f && Steps > 0;

    /// <summary>The height above which there is not enough fog left to be worth marching.</summary>
    /// <remarks>
    /// Six falloffs, where a quarter of a percent of the density is left. The march is
    /// clipped to the part of the ray below this, which is what lets thirty-two steps
    /// resolve a layer a metre deep in a room forty metres long — spread over the whole ray
    /// the same thirty-two would put two of them in the fog and the rest in clear air.
    /// </remarks>
    public float Ceiling => Top + (6f * MathF.Max(Falloff, 0.001f));
}
