// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>
/// Which rooms have air worth drawing, and what it is like.
/// </summary>
public static class SceneFog
{
    /// <summary>
    /// The château's cellars: damp lying on the flagstones.
    /// </summary>
    private static readonly FogVolume Cellars = new(
        // Faintly green and faintly cool: lime, wet brick and no daylight for two hundred
        // years. Not grey, which is what a fog with no colour of its own comes out as.
        Colour: new Vector3(0.55f, 0.60f, 0.58f),

        // Along the tunnel — four hundred units of it, with the camera standing in the
        // layer and looking down its length — this closes the far end almost entirely.
        // Across it, where a ray is in the fog for the thirty units it takes to reach the
        // wall, it is barely there at all. That difference is the whole effect: the corridor
        // gains a depth it did not have, and the wall beside the player does not fog.
        Density: 0.0065f,
        Top: 4f,
        Falloff: 12f,

        // Water, and no more forward than that. The phase peaks at (1-g^2)/(1-g)^3 of its
        // isotropic value, which is two and a half here and seven and a half at 0.55: a
        // bracket lamp seen down the tunnel at the higher figure is not a halo, it is a
        // white hole with the doorway lost inside it.
        Anisotropy: 0.35f,
        Ambient: 1f,

        // Three and a half metres a cell, drifting at a slow walking pace. Large enough that
        // the layer billows along the tunnel rather than boiling, and the strength is high
        // because a mist with an even density reads as a gradient somebody applied.
        NoiseScale: 140f,
        NoiseDrift: 3f,
        NoiseStrength: 0.45f,
        Steps: 32);

    /// <summary>
    /// The temple's chasm: murk under the bridge, and cold air coming off it.
    /// </summary>
    private static readonly FogVolume Chasm = new(
        // Cold and blue, against the lantern light above it. The two lanterns on the far
        // wall are the warmest thing in the room and the pit is the coldest, and the fog is
        // what puts them on one scale.
        Colour: new Vector3(0.14f, 0.18f, 0.26f),

        // Denser than the cellars by a factor of two, and it has four hundred and fifty
        // units of shaft below the top to work in: the murk is opaque long before the floor
        // of it, which is what makes the bottom unfindable rather than merely dark.
        Density: 0.0140f,
        Top: -280f,

        // Wide, and it costs nothing: six falloffs above the top is still forty units below
        // the walkway, so the hall never enters the march at all and the taper is spent
        // entirely inside the shaft, where it reads as the murk having a surface that is
        // soft rather than a lid.
        Falloff: 40f,

        // The same as the cellars, and for the same reason. The light here arrives from
        // above and across the layer rather than through it, so the lobe shows as the sheen
        // on the top of the murk; anything sharper reads as a lid on it.
        Anisotropy: 0.35f,
        Ambient: 1f,

        // Six and a half metres a cell and slower, because this is a body of air in a shaft
        // rather than a film on a floor: it should turn over, not scud.
        NoiseScale: 260f,
        NoiseDrift: 1.8f,
        NoiseStrength: 0.40f,

        // More than the cellars, and the reason is the depth rather than the density: a ray
        // down the shaft is in fog for hundreds of units, so the steps have further to cover
        // before the transmittance closes them out.
        Steps: 40);

    /// <summary>The cemetery in the small hours: mist standing between the graves.</summary>
    private static readonly FogVolume Graveyard = new(
        // Cool and pale: this is water lit by the sky, with no lamp in the room at all.
        // The night rooms share an albedo because they are the same weather; what tells
        // them apart on screen is what each one has burning in it.
        Colour: new Vector3(0.50f, 0.55f, 0.62f),

        Density: 0.0022f,
        Top: 8f,
        Falloff: 16f,
        Anisotropy: 0.35f,
        Ambient: 1f,

        // Five and a half metres a cell. Larger than the cellars' because the room is, and
        // slower than a scud because still air is what a walled yard has.
        NoiseScale: 220f,
        NoiseDrift: 2.5f,
        NoiseStrength: 0.45f,
        Steps: 32);

    /// <summary>The village streets in the small hours: mist under the lamps.</summary>
    private static readonly FogVolume Village = new(
        Colour: new Vector3(0.50f, 0.55f, 0.62f),
        Density: 0.0004f,

        // Lower and tighter than the cemetery's. The lamps stand about eighty units up and
        // what should carry their light is the air near the ground, not the air round the
        // bulb: a layer breathing up to the lamp turns each one into a ball of cloud.
        //
        // Tighter than this was tried and comes out worse rather than lighter: a top of 3
        // over a falloff of 5 puts the whole layer under a metre, which is under every
        // camera in RC1 and RC4, and the two squares lose the mist entirely while the lanes
        // still have it.
        Top: 6f,
        Falloff: 12f,
        Anisotropy: 0.35f,
        Ambient: 1f,

        // Getting on for four metres a cell, against the cemetery's five and a half — and
        // small is what makes this visible, which is the opposite of how it reads. A ray
        // down the lane crosses a dozen of the cemetery's cells and averages them to
        // nothing; it crosses four of these, so what it meets is banks.
        NoiseScale: 150f,
        NoiseDrift: 2.5f,

        // High, and it can afford to be, because the mean does not move with it: the field
        // is symmetric about a half, so this says how far apart the clear air and the thick
        // of a bank are rather than how much fog there is.
        NoiseStrength: 0.85f,
        Steps: 32);

    /// <summary>The dig site in the small hours: mist across the hollow.</summary>
    private static readonly FogVolume Camp = new(
        Colour: new Vector3(0.50f, 0.55f, 0.62f),
        Density: 0.0010f,
        Top: 8f,
        Falloff: 14f,
        Anisotropy: 0.35f,
        Ambient: 1f,
        NoiseScale: 220f,
        NoiseDrift: 2.5f,
        NoiseStrength: 0.45f,
        Steps: 32);

    /// <summary>Poussin's Tomb in the small hours: mist lying below the road.</summary>
    private static readonly FogVolume Hollow = new(
        Colour: new Vector3(0.50f, 0.55f, 0.62f),
        Density: 0.0008f,
        Top: 8f,
        Falloff: 14f,
        Anisotropy: 0.35f,
        Ambient: 1f,
        NoiseScale: 220f,
        NoiseDrift: 2.5f,
        NoiseStrength: 0.45f,
        Steps: 32);

    /// <summary>The one block of the story that any of this happens in.</summary>
    public static Timeblock SmallHours { get; } = new(2, 2, IsAfternoon: false);

    /// <summary>What a room's air is like.</summary>
    /// <param name="scene">The scene's name, as the SIF has it.</param>
    /// <param name="when">
    /// Where the story stands, which decides the rooms that are only foggy at night. A
    /// caller with no story state gives none, and then only the rooms that are foggy at
    /// every hour have anything.
    /// </param>
    /// <returns>The layer, or <see cref="FogVolume.None"/> for a room with none.</returns>
    public static FogVolume For(string? scene, Timeblock? when = null)
    {
        FogVolume always = scene switch
        {
            not null when Named(scene, "CS5") => Cellars,
            not null when Named(scene, "TE5") => Chasm,
            _ => FogVolume.None,
        };

        // Underground is underground at every hour, and the rest of this is weather. An
        // unknown hour is treated as daylight rather than as night: a room drawn with no
        // fog is the room as it shipped, and a room wrongly drawn with it is the one
        // failure this table exists to avoid.
        if (always.Any || when != SmallHours)
        {
            return always;
        }

        return scene switch
        {
            not null when Named(scene, "CEM") => Graveyard,
            not null when Named(scene, "POU") => Hollow,
            not null when Named(scene, "WOD") => Camp,
            not null when InTheVillage(scene) => Village,
            _ => FogVolume.None,
        };
    }

    /// <summary>The rooms with a layer at every hour of the story.</summary>
    public static IReadOnlyList<string> Rooms { get; } = ["CS5", "TE5"];

    /// <summary>The rooms with a layer in the small hours and clear air at every other.</summary>
    public static IReadOnlyList<string> NightRooms { get; } =
        ["CEM", "POU", "RC1", "RC2", "RC3", "RC4", "WOD"];

    private static bool Named(string scene, string room) =>
        scene.Equals(room, StringComparison.OrdinalIgnoreCase);

    private static bool InTheVillage(string scene) =>
        Named(scene, "RC1") || Named(scene, "RC2") || Named(scene, "RC3") || Named(scene, "RC4");
}
