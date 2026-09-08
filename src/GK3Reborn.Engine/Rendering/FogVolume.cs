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
/// How many world units one cell of the density noise's larger octave spans. Zero switches
/// the noise off and with it the sixteen hashes a step costs. <b>Smaller is more visible,
/// not less</b>: what the eye sees is the field averaged along the ray, so a cell as wide as
/// the room modulates the whole picture by one number and vanishes, and a cell a ray crosses
/// several of is what reads as banks. A few metres — a hundred and fifty units or so — is
/// where a village street stops being a wall of milk.
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
    public bool Any => Density > 0f && Steps > 0;

    /// <summary>The height above which there is not enough fog left to be worth marching.</summary>
    public float Ceiling => Top + (6f * MathF.Max(Falloff, 0.001f));
}
