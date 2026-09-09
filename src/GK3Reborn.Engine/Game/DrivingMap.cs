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

/// <summary>One road out of a junction.</summary>
/// <param name="To">The junction at the other end.</param>
/// <param name="Segment">The stretch of road between them, which the file draws as points.</param>
/// <param name="Forward">
/// Whether the segment's points run from this junction to the other one. The file names
/// each stretch once and both ends refer to it, so one of them reads it backwards:
/// <c>Mop</c> lists <c>Mop_In2 TRUE</c> and <c>In2</c> lists the same stretch
/// <c>FALSE</c>.
/// </param>
public sealed record DrivingLink(string To, string Segment, bool Forward);

/// <summary>A junction of the road network, where the moped can turn.</summary>
/// <param name="Name">What the road data calls it.</param>
/// <param name="At">Where it is on the map.</param>
/// <param name="Links">The roads out of it.</param>
public sealed record DrivingNode(string Name, Vector2 At, IReadOnlyList<DrivingLink> Links);

/// <summary>
/// The map the moped is ridden around.
/// </summary>
public sealed class DrivingMap
{
    /// <summary>
    /// What the map picture is called, without an extension.
    /// </summary>
    public const string Background = "DM_BASE";

    /// <summary>
    /// What the map itself is called as a location.
    /// </summary>
    public const string Location = "MAP";

    /// <summary>How wide the map picture is, in its own pixels.</summary>
    public const int MapWidth = 640;

    /// <summary>How tall.</summary>
    public const int MapHeight = 480;

    /// <summary>
    /// The sixteen places, as the retail engine lists them.
    /// </summary>
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
    private readonly Dictionary<string, DrivingNode> _junctions;

    private DrivingMap(
        Dictionary<string, string> names,
        IReadOnlyList<DrivingNode> roads,
        IReadOnlyDictionary<string, IReadOnlyList<Vector2>> segments)
    {
        _names = names;
        Roads = roads;
        Segments = segments;

        _junctions = new Dictionary<string, DrivingNode>(StringComparer.OrdinalIgnoreCase);

        foreach (DrivingNode node in roads)
        {
            _junctions[node.Name] = node;
        }
    }

    /// <summary>Every place, in the order the map draws them.</summary>
    public static IReadOnlyList<DrivingStop> All => Stops;

    /// <summary>The junctions of the road network.</summary>
    public IReadOnlyList<DrivingNode> Roads { get; }

    /// <summary>
    /// The stretches of road between junctions, as the points they bend at.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Vector2>> Segments { get; }

    /// <summary>One junction, by the name the road data gives it.</summary>
    /// <param name="name">The junction's name, such as <c>Plo</c>.</param>
    /// <returns>The junction, or null when the road data has none by that name.</returns>
    public DrivingNode? Junction(string? name) =>
        name is { Length: > 0 } && _junctions.TryGetValue(name, out DrivingNode? node) ? node : null;

    /// <summary>Reads what the archives say about the map.</summary>
    /// <param name="archives">The game's data.</param>
    /// <returns>The map.</returns>
    public static DrivingMap Open(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        string? roads = archives.ReadText("PATHDATA.TXT");

        return new DrivingMap(Names(archives), ReadRoads(roads), ReadSegments(roads));
    }

    /// <summary>Reads the road network on its own, for tests.</summary>
    /// <param name="pathData">The contents of <c>PATHDATA.TXT</c>.</param>
    /// <returns>A map with roads and no names.</returns>
    public static DrivingMap Roading(string? pathData) =>
        new([], ReadRoads(pathData), ReadSegments(pathData));

    /// <summary>An empty map, for a run with no game data.</summary>
    public static DrivingMap Empty { get; } = new([], [], new Dictionary<string, IReadOnlyList<Vector2>>());

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

            // Being there is not the same as knowing the way: The Site and Blanchefort both
            // load PLO, and a visit to the second must not put the first on the map.
            if (stop.Known ||
                story.GetFlag(FlagFor(stop)) ||
                Found(story, stop))
            {
                open.Add(stop);
            }
        }

        return open;
    }

    /// <summary>The verb the game writes on somebody worth following.</summary>
    public const string Follow = "FOLLOW";

    /// <summary>
    /// Whether the story itself has put a place on the map, without the player going there.
    /// </summary>
    /// <remarks>
    /// The rules are the retail engine's own, read out of the function that refreshes the
    /// map's markers. They compare timeblocks chronologically, which is what its index
    /// does, and the three places that are only ever open for one timeblock are open for
    /// exactly that one.
    /// </remarks>
    private static bool Found(GameState story, DrivingStop stop)
    {
        Timeblock now = story.Timeblock;

        return stop.Sprite switch
        {
            // L'Ermitage, at the end of Wilkes's ride out of Blanchefort. Handed over
            // from the evening after; during the afternoon itself only the chase earns it.
            "dm_ler" => now > At("104P") ||
                        story.GetNounVerbCount("WILKES", Follow) > 1,

            // Coume Sourde and L'Homme Mort, at the end of Madeleine's.
            "dm_csd" or "dm_lhm" => now > At("104P") ||
                                    story.GetNounVerbCount("BUTHANE", Follow) > 1,

            // Where Lady Howard and Estelle dig, at the end of theirs.
            "dm_wod" => now >= At("307A") ||
                        story.GetNounVerbCount("LADY_HOWARD", Follow) > 1,

            "dm_arm" or "dm_pou" or "dm_cse" => now >= At("202P"),

            // The two arms of the hexagram are only worth a ride the one noon they matter.
            "dm_bec" or "dm_mcb" => now == At("312P"),

            // Orange Rock, once it has been looked at through the binoculars.
            "dm_bmb" => now == At("303P") ||
                        story.GetNounVerbCount("VIEW_OF_ORANGE_ROCK", "BINOCULARS") > 0,

            // The Site, only that noon and only once Le Serpent Rouge has given up ten of
            // its signs — Aquarius through Scorpio — which is what puts Cardou on the map.
            "dm_tre" => now == At("312P") && SerpentRougeSigns(story) >= 10,

            _ => false,
        };
    }

    /// <summary>A point in the story, by its code.</summary>
    private static Timeblock At(string timeblock) =>
        Timeblock.TryParse(timeblock, out Timeblock parsed) ? parsed : throw new ArgumentException(timeblock);

    /// <summary>
    /// The signs of Le Serpent Rouge in the order Sidney works through them.
    /// </summary>
    private static readonly string[] Signs =
    [
        "Aquarius", "Pisces", "Aries", "Taurus", "Gemini", "Cancer", "Leo",
        "Virgo", "Libra", "Scorpio", "Ophiuchus", "Sagittarius", "Capricorn",
    ];

    /// <summary>
    /// How many signs of Le Serpent Rouge have been solved in order.
    /// </summary>
    /// <remarks>
    /// The retail engine counts the sign flags set from Aquarius up to the first that is
    /// not, and its scripts read the same number back as <c>LSRState</c>; either is
    /// honoured, whichever the analysis writes.
    /// </remarks>
    /// <param name="story">The game.</param>
    /// <returns>The count, from none to all thirteen.</returns>
    public static int SerpentRougeSigns(GameState story)
    {
        ArgumentNullException.ThrowIfNull(story);

        int solved = 0;

        while (solved < Signs.Length && story.GetFlag(Signs[solved]))
        {
            solved++;
        }

        return Math.Max(solved, story.GetVariable("LSRState"));
    }

    /// <summary>
    /// The game variable that says where the moped is parked.
    /// </summary>
    public const string Parked = "BikeLocation";

    /// <summary>
    /// The number that means "the moped is standing at this place".
    /// </summary>
    /// <param name="scene">The room the place loads.</param>
    /// <returns>The number, or null when the moped cannot be ridden there.</returns>
    public static int? ParkedAt(string scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        int at = Array.FindIndex(
            Stops, s => string.Equals(s.Scene, scene, StringComparison.OrdinalIgnoreCase));

        return at >= 0 ? at : null;
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
    private static string FlagFor(DrivingStop stop) => $"MapKnows:{stop.Sprite}";

    /// <summary>The places' names, from the game's own string table.</summary>
    private static Dictionary<string, string> Names(GameArchives archives)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (archives.ReadText(GameStrings.TableFor(archives)) is not { } text)
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
    private static List<DrivingNode> ReadRoads(string? text)
    {
        if (text is not { Length: > 0 })
        {
            return [];
        }

        List<DrivingNode> nodes = [];
        string? name = null;
        Vector2 at = Vector2.Zero;
        List<DrivingLink> links = [];

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
                if (Point(parts[1]) is { } where)
                {
                    at = where;
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
                links.Add(new DrivingLink(
                    parts[0],
                    parts[1],
                    parts.Length > 2 && parts[2].Equals("TRUE", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return nodes;
    }

    /// <summary>
    /// The shape of each stretch of road, from the same file.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<Vector2>> ReadSegments(string? text)
    {
        var segments = new Dictionary<string, IReadOnlyList<Vector2>>(StringComparer.OrdinalIgnoreCase);

        if (text is not { Length: > 0 })
        {
            return segments;
        }

        string? name = null;
        List<Vector2> points = [];

        foreach (string raw in text.Split('\n'))
        {
            string[] parts = raw.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 || parts[0].StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (parts[0].Equals("SegmentBegin", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
            {
                name = parts[1];
                points = [];
            }
            else if (parts[0].Equals("SegmentEnd", StringComparison.OrdinalIgnoreCase))
            {
                if (name is { Length: > 0 })
                {
                    segments[name] = [.. points];
                }

                name = null;
            }
            else if (name is not null &&
                     parts.Length > 1 &&
                     parts[0].Equals("Point", StringComparison.OrdinalIgnoreCase) &&
                     Point(parts[1]) is { } bend)
            {
                points.Add(bend);
            }
        }

        return segments;
    }

    /// <summary>An <c>x,y</c> pair in the map's own pixels.</summary>
    private static Vector2? Point(string text)
    {
        string[] pair = text.Split(',');

        return pair.Length == 2 &&
               int.TryParse(pair[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
               int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            ? new Vector2(x, y)
            : null;
    }

    /// <summary>
    /// The road a vehicle takes past a list of junctions, in map pixels.
    /// </summary>
    /// <param name="junctions">The junctions to pass, in order.</param>
    /// <returns>The polyline, empty when none of them are on the map.</returns>
    public IReadOnlyList<Vector2> Route(IReadOnlyList<string> junctions)
    {
        ArgumentNullException.ThrowIfNull(junctions);

        List<Vector2> line = [];
        DrivingNode? standing = null;

        foreach (string name in junctions)
        {
            if (Junction(name) is not { } node)
            {
                continue;
            }

            if (standing is null)
            {
                line.Add(node.At);
                standing = node;

                continue;
            }

            standing = Extend(line, standing, node);
        }

        return line;
    }

    /// <summary>Extends a route to a junction, along the roads where there are any.</summary>
    /// <returns>The junction the route now stands at.</returns>
    private DrivingNode Extend(List<Vector2> line, DrivingNode from, DrivingNode to)
    {
        if (Between(from, to) is not { Count: > 0 } hops)
        {
            line.Add(to.At);

            return to;
        }

        DrivingNode standing = from;

        foreach (DrivingNode hop in hops)
        {
            foreach (DrivingLink link in standing.Links)
            {
                if (!string.Equals(link.To, hop.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Segments.TryGetValue(link.Segment, out IReadOnlyList<Vector2>? bends))
                {
                    line.AddRange(link.Forward ? bends : bends.Reverse());
                }

                break;
            }

            line.Add(hop.At);
            standing = hop;
        }

        return standing;
    }

    /// <summary>The junctions from one to another, not counting the first, by the shortest way.</summary>
    private List<DrivingNode>? Between(DrivingNode from, DrivingNode to)
    {
        if (string.Equals(from.Name, to.Name, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var cameFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [from.Name] = string.Empty,
        };

        var queue = new Queue<DrivingNode>();

        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            DrivingNode at = queue.Dequeue();

            foreach (DrivingLink link in at.Links)
            {
                if (cameFrom.ContainsKey(link.To) || Junction(link.To) is not { } next)
                {
                    continue;
                }

                cameFrom[next.Name] = at.Name;

                if (!string.Equals(next.Name, to.Name, StringComparison.OrdinalIgnoreCase))
                {
                    queue.Enqueue(next);

                    continue;
                }

                List<DrivingNode> back = [];

                for (DrivingNode? step = next;
                     step is not null &&
                     !string.Equals(step.Name, from.Name, StringComparison.OrdinalIgnoreCase);
                     step = Junction(cameFrom[step.Name]))
                {
                    back.Add(step);
                }

                back.Reverse();

                return back;
            }
        }

        return null;
    }
}
