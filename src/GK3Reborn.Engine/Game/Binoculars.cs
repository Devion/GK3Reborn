// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Ui;

namespace GK3Reborn.Game;

/// <summary>Something the binoculars can be pointed at.</summary>
/// <param name="Location">
/// The place, as the data names it — <c>MA3_a</c>, <c>LHM_a_a</c>. The leading three letters
/// are the room; what follows is which variant of it.
/// </param>
/// <param name="From">Lower corner of the patch of sky it occupies, in degrees.</param>
/// <param name="To">Upper corner.</param>
/// <param name="Angle">Where the camera looks once the player zooms in: heading and pitch.</param>
/// <param name="Position">Where the camera stands once they do.</param>
/// <param name="Floor">The floor object of the place being looked at.</param>
/// <param name="Entering">A script to run on the way in, or empty.</param>
/// <param name="Leaving">A script to run on the way out.</param>
public sealed record Sight(
    string Location,
    Vector2 From,
    Vector2 To,
    Vector2 Angle,
    Vector3 Position,
    string Floor,
    string Entering,
    string Leaving)
{
    /// <summary>The room this is a view of.</summary>
    public string Scene => Location.Length >= 3 ? Location[..3].ToUpperInvariant() : Location;

    /// <summary>The middle of the patch it occupies.</summary>
    public Vector2 Middle => (From + To) / 2;

    /// <summary>Whether a direction falls inside it.</summary>
    /// <param name="heading">Where the player is looking, in degrees.</param>
    /// <param name="pitch">How far up or down, in degrees.</param>
    /// <returns>True when it is centred well enough to zoom.</returns>
    public bool Holds(float heading, float pitch) =>
        heading >= From.X && heading <= To.X && pitch >= From.Y && pitch <= To.Y;
}

/// <summary>Somewhere a voice-over plays when the binoculars settle on it.</summary>
/// <param name="From">Lower corner, in degrees.</param>
/// <param name="To">Upper corner.</param>
/// <param name="Licence">The licence plate of the line to play.</param>
public sealed record Remark(Vector2 From, Vector2 To, string Licence)
{
    /// <summary>Whether a direction falls inside it.</summary>
    public bool Holds(float heading, float pitch) =>
        heading >= From.X && heading <= To.X && pitch >= From.Y && pitch <= To.Y;
}

/// <summary>What the binoculars can see from one place at one time of day.</summary>
/// <param name="Sights">The places that can be zoomed into.</param>
/// <param name="Remarks">The places that have something to say.</param>
/// <param name="PutAway">The animation that lowers the binoculars.</param>
public sealed record Panorama(
    IReadOnlyList<Sight> Sights, IReadOnlyList<Remark> Remarks, string PutAway)
{
    /// <summary>Nothing to look at.</summary>
    public static Panorama Nothing { get; } = new([], [], string.Empty);

    /// <summary>Whether there is anything here at all.</summary>
    public bool Any => Sights.Count > 0 || Remarks.Count > 0;

    /// <summary>What the binoculars are pointed at, or null.</summary>
    /// <param name="heading">Where the player is looking, in degrees.</param>
    /// <param name="pitch">How far up or down.</param>
    /// <returns>The sight, or null when they are looking at scenery.</returns>
    public Sight? At(float heading, float pitch)
    {
        foreach (Sight sight in Sights)
        {
            if (sight.Holds(heading, pitch))
            {
                return sight;
            }
        }

        return null;
    }

    /// <summary>What there is to say about where they are looking, or null.</summary>
    public Remark? Heard(float heading, float pitch)
    {
        foreach (Remark remark in Remarks)
        {
            if (remark.Holds(heading, pitch))
            {
                return remark;
            }
        }

        return null;
    }
}

/// <summary>
/// The binoculars.
/// </summary>
/// <remarks>
/// <para>
/// Two places in the game have them — the Armchair of the Devil and the tower at Château de
/// Blanchefort — and from each, at each time of day, a handful of other places can be
/// picked out and zoomed into. <c>BINOCS.TXT</c> describes all of it: twenty-one
/// vantage points, forty-seven things to look at, and four spots that have a line of
/// dialogue rather than a destination.
/// </para>
/// <para>
/// <b>The panorama is the room, not a picture.</b> The binoculars do not show a painted
/// backdrop; they narrow the view and let the player pan the camera they already have.
/// Each thing worth seeing is a rectangle in degrees — heading across, pitch up and down —
/// and the file's numbers say so: they run from 1 to 189 across and from -7 to 11 up, which
/// is an arc of hillside and a few degrees either side of the horizon rather than any kind
/// of image coordinate.
/// </para>
/// <para>
/// <b>Zooming in is a camera, and sometimes a room.</b> Each sight carries the position and
/// angle the camera takes when the player zooms — usually inside another room entirely,
/// which is why it also names that room's floor. The enter and exit scripts are the
/// original's own hooks for hiding and showing whatever has to be in place before the cut;
/// they are recorded and run through the ordinary script host.
/// </para>
/// </remarks>
public sealed class Binoculars
{
    private readonly Dictionary<string, Panorama> _views;

    private Binoculars(Dictionary<string, Panorama> views) => _views = views;

    /// <summary>No binoculars, for a run with no game data.</summary>
    public static Binoculars Empty { get; } = new([]);

    /// <summary>How many vantage points there are.</summary>
    public int Count => _views.Count;

    /// <summary>Reads the binoculars out of the archives.</summary>
    /// <param name="archives">The game's data.</param>
    /// <returns>What can be seen from where.</returns>
    public static Binoculars Open(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        return From(archives.ReadText("BINOCS.TXT") ?? string.Empty);
    }

    /// <summary>Reads the binoculars from a string, for tests.</summary>
    /// <param name="text">The contents of <c>BINOCS.TXT</c>.</param>
    /// <returns>What can be seen from where.</returns>
    public static Binoculars From(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        KeyedText file = KeyedText.Parse(text, "BINOCS.TXT");
        var views = new Dictionary<string, Panorama>(StringComparer.OrdinalIgnoreCase);

        foreach (string section in file.SectionNames)
        {
            // A vantage point is a room and a timeblock and nothing more — CD1102P. The
            // sections that name a place as well — CD1102PMA3_a — belong to one of these.
            if (section.Length != 7 || file.Value(section, "LOC") is not { Length: > 0 } places)
            {
                continue;
            }

            List<Sight> sights = [];

            foreach (string place in places.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string where = place.Trim();
                string body = section + where;

                // The file is inconsistent about the case of the timeblock's last letter
                // between a heading and the sections under it — CD1102p names CD1102pPL3 —
                // so the lookup has to be case-insensitive, which KeyedText is.
                if (!file.Has(body) || file.Value(body, "ZOOMRECT") is not { } rect)
                {
                    continue;
                }

                float[] corners = Numbers(rect, 4);
                float[] angle = Numbers(file.Value(body, "CAMANGLE"), 2);
                float[] position = Numbers(file.Value(body, "CAMPOS"), 3);

                sights.Add(new Sight(
                    where,
                    new Vector2(corners[0], corners[1]),
                    new Vector2(corners[2], corners[3]),
                    new Vector2(angle[0], angle[1]),
                    new Vector3(position[0], position[1], position[2]),
                    file.Value(body, "FLOOR") ?? string.Empty,
                    file.Value(body, "ENTERSHEEP") ?? string.Empty,
                    file.Value(body, "EXITSHEEP") ?? string.Empty));
            }

            List<Remark> remarks = [];

            // A voice-over spot may be declared beside a sight or in a section of its own,
            // so both are swept rather than only the places LOC names.
            foreach (string body in file.SectionNames)
            {
                if (!body.StartsWith(section, StringComparison.OrdinalIgnoreCase) ||
                    file.Value(body, "VORECT") is not { } spot)
                {
                    continue;
                }

                float[] corners = Numbers(spot, 4);

                remarks.Add(new Remark(
                    new Vector2(corners[0], corners[1]),
                    new Vector2(corners[2], corners[3]),
                    file.Value(body, "LIC#") ?? string.Empty));
            }

            views[section] = new Panorama(sights, remarks, file.Value(section, "ANIM") ?? string.Empty);
        }

        return new Binoculars(views);
    }

    /// <summary>What can be seen from a room at a time of day.</summary>
    /// <param name="location">The room's three-letter code.</param>
    /// <param name="timeblock">The timeblock, as <c>102P</c>.</param>
    /// <returns>The panorama, which has nothing in it where the binoculars are not used.</returns>
    public Panorama For(string? location, string? timeblock)
    {
        if (location is not { Length: > 0 } || timeblock is not { Length: > 0 })
        {
            return Panorama.Nothing;
        }

        return _views.TryGetValue(location + timeblock, out Panorama? view) ? view : Panorama.Nothing;
    }

    /// <summary>Whether the binoculars are worth raising here.</summary>
    /// <param name="location">The room's three-letter code.</param>
    /// <param name="timeblock">The timeblock.</param>
    /// <returns>True when there is something to see.</returns>
    public bool Usable(string? location, string? timeblock) => For(location, timeblock).Any;

    /// <summary>A comma-separated list of numbers, padded where the file is short.</summary>
    private static float[] Numbers(string? text, int wanted)
    {
        var found = new float[wanted];

        if (text is not { Length: > 0 })
        {
            return found;
        }

        string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < wanted && i < parts.Length; i++)
        {
            _ = float.TryParse(
                parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out found[i]);
        }

        return found;
    }
}
