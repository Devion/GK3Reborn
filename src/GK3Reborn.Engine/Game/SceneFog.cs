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
/// <remarks>
/// <para>
/// <b>A table, and it has to be.</b> Everything else this renderer adds to a room is derived
/// from something the room itself says — a flame is found by the bitmap it is painted with,
/// a railing by the holes in its texture, a window light by the name the artists gave it.
/// GK3 says nothing anywhere about fog: no scene file has a word for it, no texture implies
/// it, and no measurement of the geometry distinguishes a dry cellar from a damp one. What
/// decides that a place is damp is what the place <em>is</em>, which is a reading of the
/// game and not a property of its files.
/// </para>
/// <para>
/// So this is a short list that says so out loud, rather than a heuristic that would have to
/// pretend it had found something. It is deliberately short: fog in a room that does not want
/// it is worse than no fog at all, because it is the one effect here that touches every pixel
/// of the frame.
/// </para>
/// <para>
/// <b>Heights are absolute and the corpus makes that safe.</b> Every room below stands its
/// walking floor within half a unit of <c>y = 0</c> — measured with <c>render-scene
/// --pick</c>, not assumed — and none of them is more than one storey, so a layer placed
/// against a world height lies on the floor everywhere in it. A room that climbed would need
/// the layer to climb with it, which is a heightfield rather than a plane and is not what
/// any of these wants. The outdoor ones roll rather than climb, and a plane through rolling
/// ground is what a mist lying in it actually is.
/// </para>
/// <para>
/// <b>Two things a room is asked, not one.</b> Underground is underground at every hour, so
/// the cellars and the chasm answer to their name alone. The rest is weather: the cemetery,
/// the village, the dig site and the tomb are foggy in the small hours and clear in daylight,
/// and they are walked into in daylight far more often than not. See
/// <see cref="SmallHours"/>.
/// </para>
/// <para>
/// <b>And every density belongs to its own room.</b> There is no outdoor preset here. What a
/// layer costs the picture is set by how far a ray travels inside it before it hits
/// something, so the walled cemetery carries nearly four times the village's density and the
/// same figure in open country would close the view at thirty metres. Each number below was
/// arrived at by rendering the room it is for, several cameras at a time.
/// </para>
/// </remarks>
public static class SceneFog
{
    /// <summary>
    /// The château's cellars: damp lying on the flagstones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CS5 is the tunnel under Château de Serras — brick barrel vaults, a stone floor at
    /// <c>y = 0.3</c> to <c>0.5</c> throughout, and the only light in it standing in the
    /// wall brackets. It is the room in the game most obviously wanting fog and the one the
    /// request named, and what it wants is the ground mist a cold stone floor under a hill
    /// actually carries: knee deep, sitting still, thinning to nothing by chest height.
    /// </para>
    /// <para>
    /// <b>Low is the whole of it, and low is a smaller number than it looks.</b> A top four
    /// units over the flags with a falloff of twelve leaves a tenth of the density at knee
    /// height and a hundredth at the eye. The first attempt used twenty-two, which is a
    /// perfectly reasonable-looking depth and put a percent of the layer at head height —
    /// enough, against a bracket lamp seen nearly end-on, to wash the vault and half the
    /// wall behind it. What is above the mist has to be <em>clear</em>, or the room reads as
    /// dusty rather than as wet.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// TE5 is the bridge room at Rennes-le-Château's temple. Its walking floor is at
    /// <c>y = 0</c>, the bridge deck at <c>0.6</c> with its parapet topping out at
    /// <c>8</c>, and the shaft the bridge crosses drops to <c>te5_chasm_bottom</c> at
    /// <c>y = -725</c> — eighteen metres, and nothing the player is ever meant to find the
    /// floor of. Left alone it is a black shape, which reads as an absence of geometry
    /// rather than as a drop.
    /// </para>
    /// <para>
    /// <b>The layer is a long way down the shaft, not a lid on it.</b> Fifteen units under
    /// the flags was the first attempt and it was wrong in a way that only a second pair of
    /// eyes catches: the layer is technically in the pit, but it laps at the lip, so the
    /// drop still ends where the floor does and the bridge appears to span a bank of cloud.
    /// At two hundred and eighty the shaft has three or four metres of visible wall in it
    /// before the murk closes it, the bridge and the whole of its underside stand clear
    /// above, and what the player looks down into is a depth that goes on rather than a
    /// surface at arm's length.
    /// </para>
    /// <para>
    /// <b>Not lower than that.</b> At four hundred the murk is a faint band at the bottom of
    /// the shot from every camera that can see the pit at all, and the pit is back to being
    /// an ordinary dark hole — which is the fault this started as.
    /// </para>
    /// <para>
    /// <b>Dark, and that is a colour rather than a density.</b> The murk is half as bright
    /// again as it is thick: a scattering albedo near a sixth, against the cellars' half.
    /// Fog is normally pale because it is lit by a sky, and this one is lit by two lanterns
    /// forty units over a shaft with nothing in it. At the cellars' albedo the pit came out
    /// whiter than the hall around it — a lit cloud sitting in a temple, which is the
    /// opposite of what a chasm is for.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// CEM is walled on every side and its ground is flat at <c>y = 0</c> — picked, not
    /// assumed — which is what lets it carry three times the village's density without
    /// closing: nothing in it is more than a few hundred units away, so a ray is out of the
    /// layer and into a wall long before the transmittance runs out. The stones stand out
    /// of the mist to their shoulders and the wall goes off into it, which is the depth the
    /// room has never had at night.
    /// </para>
    /// <para>
    /// <b>Its density belongs to the room and not to the hour.</b> The same numbers in the
    /// open wood at L'Fauteuil du Diable swallow the trees at thirty metres, because there
    /// the ray has a kilometre of layer to cross rather than a courtyard's worth. Every
    /// figure below was chosen by rendering the room it is for; there is no outdoor preset
    /// here and there should not be one.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// The four RC rooms are one place seen from four sides, so they carry one layer: a
    /// street that fogs and the corner it turns into that does not would be worse than
    /// neither. What this is worth is almost entirely the halo it puts round the street
    /// lamps — the only warm things in Rennes-le-Château at two in the morning — and the
    /// mist standing in the alley beyond them.
    /// </para>
    /// <para>
    /// <b>Thin, and thinner than it looks like it should be.</b> At the cemetery's density
    /// the streets are pea soup with the cobbles gone by the second house. A village is
    /// enclosed at eye level and open along its length, and it is the length that decides
    /// this.
    /// </para>
    /// <para>
    /// <b>And thinner again than the first answer to that.</b> 0.0006 reads on the square,
    /// where a ray is in the layer for a few hundred units before a house stops it, and is
    /// a wall of milk in the lane RC3 runs between the church and the cemetery, where the
    /// same ray has two thousand units of open street and the camera stands at knee height
    /// inside the layer. There is nothing to trade off there: the lanes are where the layer
    /// is longest, so the lanes are what the density has to be set by.
    /// </para>
    /// <para>
    /// <b>What buys the square back is the grain, and the grain had to be made to work
    /// first.</b> Cutting the density alone gives a thinner wash of the same smooth wash —
    /// what the eye was reading as heavy was not only how much fog there was but that it
    /// was the same everywhere. The layer only reads as mist standing in a street once the
    /// field it is modulated by has features a ray crosses a few of rather than a wobble it
    /// averages away; see <c>FogShaders.Field</c>. The cell below is two thirds of what it
    /// was and the strength nearly double, and both of those did nothing at all here until
    /// that was fixed.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// WOD is a bowl of open ground with a lit tent standing in the middle of it, and the
    /// tent is the whole reason this is worth having: fog is the only thing in the renderer
    /// that shows a light as having a distance. Half the cemetery's density, because the
    /// far side of the hollow is four times further away than the cemetery's wall.
    /// </remarks>
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
    /// <remarks>
    /// The thinnest of the four, and it is the ground that asks for it: the tomb stands on
    /// a shoulder with the land dropping away under the road, so what a layer at eight
    /// units does here is fill the hollow and leave the road clear. Thinner than the camp
    /// again because there is nothing to stop a ray — the far hill is most of a kilometre
    /// off and has to stay a hill.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Day two at two in the morning. Sixteen of the corpus's seventeen blocks run between
    /// seven in the morning and six in the evening; the seventeenth, <c>309P</c>, is nine
    /// at night and reaches nothing but two hotel bedrooms. So the outdoor rooms below are
    /// in daylight at every hour they can be walked into except this one, and a layer that
    /// did not ask which hour it was would put a mist under a two o'clock sun.
    /// </para>
    /// <para>
    /// That is not a subtlety. It is what the cemetery looks like at <c>102P</c> with the
    /// night layer forced on: a bright afternoon, hard shadows on the grass, and a bank of
    /// fog between the stones.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// For the tests and for the report, so that "which rooms have fog" is answered from the
    /// same place the fog comes from rather than by reading the switch above twice.
    /// </remarks>
    public static IReadOnlyList<string> Rooms { get; } = ["CS5", "TE5"];

    /// <summary>The rooms with a layer in the small hours and clear air at every other.</summary>
    public static IReadOnlyList<string> NightRooms { get; } =
        ["CEM", "POU", "RC1", "RC2", "RC3", "RC4", "WOD"];

    private static bool Named(string scene, string room) =>
        scene.Equals(room, StringComparison.OrdinalIgnoreCase);

    private static bool InTheVillage(string scene) =>
        Named(scene, "RC1") || Named(scene, "RC2") || Named(scene, "RC3") || Named(scene, "RC4");
}
