// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Numerics;
using GK3Reborn.Content;

namespace GK3Reborn.Game;

/// <summary>One place the moped can be ridden to.</summary>
/// <param name="Sprite">
/// The picture drawn on the map for it — <c>dm_rlc</c> — which is a lit copy of that patch
/// of the map itself rather than a marker over it.
/// </param>
/// <param name="Scene">Which room arriving there loads.</param>
/// <param name="X">Where the picture goes on the 640-by-480 map, from the left.</param>
/// <param name="Y">The same, from the top.</param>
/// <param name="Known">Whether the player knows about it before the story says anything.</param>
public sealed record DrivingStop(string Sprite, string Scene, int X, int Y, bool Known)
{
    /// <summary>The location code, taken from the sprite's name.</summary>
    public string Code => Sprite.Length > 3 ? Sprite[3..].ToUpperInvariant() : Sprite.ToUpperInvariant();
}

/// <summary>A junction of the road network, where the moped can turn.</summary>
/// <param name="Name">What the road data calls it.</param>
/// <param name="At">Where it is on the map.</param>
/// <param name="Links">The junctions it joins to.</param>
public sealed record DrivingNode(string Name, Vector2 At, IReadOnlyList<string> Links);

/// <summary>
/// The map the moped is ridden around.
/// </summary>
/// <remarks>
/// <para>
/// A painting of the Rennes-le-Château countryside, 640 by 480, with sixteen places on it.
/// Each place's marker is a <em>lit copy of that patch of the map</em> rather than a pin
/// over it, which is why the markers look like part of the picture: <c>dm_rlc</c> is the
/// village, painted brighter.
/// </para>
/// <para>
/// <b>Where the positions come from.</b> The retail engine builds this list in the
/// constructor of its driving layer, sixteen calls with the coordinates as immediates. They
/// are recovered from there and written down here rather than read at runtime: nothing this
/// engine ships may depend on the original executable, and sixteen pairs of integers about
/// where a village sits on a painting are a fact about the map rather than a thing that can
/// be derived. The pictures themselves come out of the player's own <c>.BRN</c> archives,
/// like everything else.
/// </para>
/// <para>
/// <b>The road network is data.</b> <c>PATHDATA.TXT</c> is in the archives and describes
/// twenty junctions with their map positions and the roads between them, which is how the
/// moped rides along the roads rather than flying between towns in a straight line.
/// </para>
/// <para>
/// <b>What is open.</b> Five places are on the map from the first ride — Rennes-le-Château,
/// Larry Chester's house, Blanchefort, Rennes-les-Bains and the Couiza train station — and
/// the rest arrive as the story finds them. The original keeps a flag per marker and sets
/// it from its own script hooks; here a place is on the map once the player has been there
/// or a script has said so, which is the same set arrived at from state the save already
/// keeps.
/// </para>
/// </remarks>
public sealed class DrivingMap
{
    /// <summary>
    /// What the map picture is called, without an extension.
    /// </summary>
    /// <remarks>
    /// Without one because that is how every other texture in the game is named and looked
    /// up — the archives hold <c>DM_BASE.BMP</c> and the enhanced set holds
    /// <c>DM_BASE.PNG</c>, and the name that means both is neither.
    /// </remarks>
    public const string Background = "DM_BASE";

    /// <summary>How wide the map picture is, in its own pixels.</summary>
    public const int MapWidth = 640;

    /// <summary>How tall.</summary>
    public const int MapHeight = 480;

    /// <summary>
    /// The sixteen places, as the retail engine lists them.
    /// </summary>
    /// <remarks>
    /// In its own order, which is the order they are drawn in and so which one wins where
    /// two overlap. <c>dm_tre</c> — "The Site" — shares its destination with Blanchefort
    /// and appears once the dig is there.
    /// </remarks>
    private static readonly DrivingStop[] Stops =
    [
        new("dm_wod", "PL5", 44, 218, Known: false),
        new("dm_ler", "PL4", 387, 258, Known: false),
        new("dm_arm", "VGR", 458, 225, Known: false),
        new("dm_csd", "PL2", 396, 187, Known: false),
        new("dm_lhm", "PL1", 442, 155, Known: false),
        new("dm_bmb", "PL3", 499, 137, Known: false),
        new("dm_bec", "BEC", 94, 400, Known: false),
        new("dm_mcb", "MCB", 555, 72, Known: false),
        new("dm_pou", "POU", 578, 22, Known: false),
        new("dm_cse", "PL6", 520, 4, Known: false),
        new("dm_rlc", "MOP", 193, 119, Known: true),
        new("dm_lhe", "LHE", 454, 65, Known: true),
        new("dm_plo", "PLO", 447, 91, Known: true),
        new("dm_rl1", "RL1", 487, 170, Known: true),
        new("dm_tr1", "TR1", 54, 134, Known: true),
        new("dm_tre", "PLO", 506, 91, Known: false),
    ];

    private readonly Dictionary<string, string> _names;

    private DrivingMap(Dictionary<string, string> names, IReadOnlyList<DrivingNode> roads)
    {
        _names = names;
        Roads = roads;
    }

    /// <summary>Every place, in the order the map draws them.</summary>
    public static IReadOnlyList<DrivingStop> All => Stops;

    /// <summary>The junctions of the road network.</summary>
    public IReadOnlyList<DrivingNode> Roads { get; }

    /// <summary>Reads what the archives say about the map.</summary>
    /// <param name="archives">The game's data.</param>
    /// <returns>The map.</returns>
    public static DrivingMap Open(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        return new DrivingMap(Names(archives), ReadRoads(archives.ReadText("PATHDATA.TXT")));
    }

    /// <summary>An empty map, for a run with no game data.</summary>
    public static DrivingMap Empty { get; } = new([], []);

    /// <summary>What a place is called, in the player's language.</summary>
    /// <param name="stop">The place.</param>
    /// <returns>Its name, or its code when the strings are not loaded.</returns>
    public string NameOf(DrivingStop stop)
    {
        ArgumentNullException.ThrowIfNull(stop);

        return _names.TryGetValue(stop.Sprite, out string? name) ? name : stop.Code;
    }

    /// <summary>
    /// Which places the player may ride to.
    /// </summary>
    /// <param name="story">The game.</param>
    /// <param name="here">The room they are in, which is not offered.</param>
    /// <returns>The places, in map order.</returns>
    /// <remarks>
    /// A place is on the map when the story has made it known: the five it opens with, plus
    /// anywhere the player has already been, plus anywhere a script has named with
    /// <c>EngineOpenOnMap</c>. All three are read out of the game's own state, so the map
    /// after a load is the map before the save.
    /// </remarks>
    public static IReadOnlyList<DrivingStop> Open(GameState story, string? here = null)
    {
        ArgumentNullException.ThrowIfNull(story);

        List<DrivingStop> open = [];

        foreach (DrivingStop stop in Stops)
        {
            if (string.Equals(stop.Scene, here, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (stop.Known ||
                story.GetFlag(FlagFor(stop)) ||
                story.WasEverInLocation(story.Ego, stop.Scene))
            {
                open.Add(stop);
            }
        }

        return open;
    }

    /// <summary>Puts a place on the map for good.</summary>
    /// <param name="story">The game.</param>
    /// <param name="code">The place's code, as <see cref="DrivingStop.Code"/> gives it.</param>
    /// <returns>True when there is such a place.</returns>
    public static bool Reveal(GameState story, string code)
    {
        ArgumentNullException.ThrowIfNull(story);
        ArgumentNullException.ThrowIfNull(code);

        foreach (DrivingStop stop in Stops)
        {
            if (string.Equals(stop.Code, code, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stop.Scene, code, StringComparison.OrdinalIgnoreCase))
            {
                story.SetFlag(FlagFor(stop));

                return true;
            }
        }

        return false;
    }

    /// <summary>The flag that says a script has put a place on the map.</summary>
    /// <remarks>
    /// On the story rather than in the map, so it survives a save without the map having to
    /// be part of one.
    /// </remarks>
    private static string FlagFor(DrivingStop stop) => $"MapKnows:{stop.Sprite}";

    /// <summary>The places' names, from the game's own string table.</summary>
    private static Dictionary<string, string> Names(GameArchives archives)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (archives.ReadText("ESTRINGS.TXT") is not { } text)
        {
            return names;
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (!line.StartsWith("dm_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int equals = line.IndexOf('=');

            if (equals > 0)
            {
                names[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }

        return names;
    }

    /// <summary>
    /// The road network, from <c>PATHDATA.TXT</c>.
    /// </summary>
    /// <remarks>
    /// Junctions with a map position and the junctions they join to. The segment names and
    /// the direction flag on each link are read past: what they describe is the shape of
    /// the road between two junctions, and a straight line between junctions is close
    /// enough to it on a 640-pixel map that the difference is a pixel or two.
    /// </remarks>
    private static List<DrivingNode> ReadRoads(string? text)
    {
        if (text is not { Length: > 0 })
        {
            return [];
        }

        List<DrivingNode> nodes = [];
        string? name = null;
        Vector2 at = Vector2.Zero;
        List<string> links = [];

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                continue;
            }

            if (parts[0].Equals("NodeBegin", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
            {
                name = parts[1];
                links = [];
                at = Vector2.Zero;
            }
            else if (parts[0].Equals("Location", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
            {
                string[] pair = parts[1].Split(',');

                if (pair.Length == 2 &&
                    int.TryParse(pair[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
                    int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                {
                    at = new Vector2(x, y);
                }
            }
            else if (parts[0].Equals("NodeEnd", StringComparison.OrdinalIgnoreCase))
            {
                if (name is { Length: > 0 })
                {
                    nodes.Add(new DrivingNode(name, at, [.. links]));
                }

                name = null;
            }
            else if (name is not null &&
                     parts.Length >= 2 &&
                     !parts[0].Equals("LinksBegin", StringComparison.OrdinalIgnoreCase) &&
                     !parts[0].Equals("LinksEnd", StringComparison.OrdinalIgnoreCase))
            {
                links.Add(parts[0]);
            }
        }

        return nodes;
    }
}
