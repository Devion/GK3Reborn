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

    /// <summary>
    /// What the map itself is called as a location.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The map is a room in the original, not a panel over one: the retail engine's
    /// location table lists <c>map</c> alongside <c>lhe</c> and <c>mop</c>, and its driving
    /// layer holds that entry's index as its own location. Riding the moped is therefore
    /// leaving for the map and arriving from it, and that is what the game's own data
    /// expects — <c>LHE.SIF</c> puts Gabriel's moped in the yard on
    /// <c>WasLastLocation("Map")</c>, and ten of the compiled scene scripts ask the same
    /// question to decide where the player is standing when they get there.
    /// </para>
    /// <para>
    /// Written in capitals like every other location code this engine holds;
    /// <c>WasLastLocation</c> compares without case, as does everything else that reads
    /// one.
    /// </para>
    /// </remarks>
    public const string Location = "MAP";

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
    /// <remarks>
    /// In the map's own 640-by-480 pixels, and in the direction the segment's name reads:
    /// <c>Mop_In2</c> runs from the village to the junction below it. A link says which way
    /// round to take them.
    /// </remarks>
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
                story.WasEverInLocation(story.Ego, stop.Scene) ||
                Found(story, stop))
            {
                open.Add(stop);
            }
        }

        return open;
    }

    /// <summary>The verb the game writes on somebody worth following.</summary>
    /// <remarks>
    /// Its count is how the story remembers a chase: the room's own action sets it to one
    /// when the player gives chase, and the map counts it again when the chase arrives.
    /// Two, therefore, means "followed them all the way", which is what puts their
    /// destination on the map for good.
    /// </remarks>
    public const string Follow = "FOLLOW";

    /// <summary>
    /// Whether the story itself has put a place on the map, without the player going there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The retail driving layer decides this every time the map opens, from the point in
    /// the story and from three chases the player may have finished. The table is recovered
    /// from there. Without it, three of the sixteen places have no way onto the map at all:
    /// L'Ermitage is behind following Wilkes, and Coume Sourde and L'Homme Mort are behind
    /// following Madeleine, and the port had no chase to finish.
    /// </para>
    /// <para>
    /// <b>It only ever adds.</b> The original also takes places back off the map — the two
    /// arms of the hexagram are hidden again outside the block they matter in — and this
    /// does not, because the port already shows anywhere the player has been and a place
    /// that vanishes from a map the player has used is worse than one that lingers.
    /// </para>
    /// </remarks>
    private static bool Found(GameState story, DrivingStop stop)
    {
        int now = Story.TimeblockRules.Order(story.Timeblock);

        return stop.Sprite switch
        {
            // L'Ermitage, at the end of Wilkes's ride out of Blanchefort.
            "dm_ler" => now >= Order("104P") ||
                        story.GetNounVerbCount("WILKES", Follow) > 1,

            // Coume Sourde and L'Homme Mort, at the end of Madeleine's.
            "dm_csd" or "dm_lhm" => now >= Order("104P") ||
                                    story.GetNounVerbCount("BUTHANE", Follow) > 1,

            // Where Lady Howard and Estelle dig, at the end of theirs.
            "dm_wod" => now >= Order("307A") ||
                        story.GetNounVerbCount("LADY_HOWARD", Follow) > 1,

            "dm_arm" or "dm_pou" or "dm_cse" => now >= Order("202P"),
            "dm_bec" or "dm_mcb" or "dm_tre" => now >= Order("312P"),
            "dm_bmb" => now >= Order("303P"),
            _ => false,
        };
    }

    /// <summary>Where a timeblock comes in the story, by its code.</summary>
    private static int Order(string timeblock) =>
        Timeblock.TryParse(timeblock, out Timeblock parsed) ? Story.TimeblockRules.Order(parsed) : int.MaxValue;

    /// <summary>
    /// The game variable that says where the moped is parked.
    /// </summary>
    /// <remarks>
    /// Read by six of the game's scene files and three of its action files, and written by
    /// two of its scripts. See <see cref="ParkedAt"/> for what the number in it means.
    /// </remarks>
    public const string Parked = "BikeLocation";

    /// <summary>
    /// The number that means "the moped is standing at this place".
    /// </summary>
    /// <param name="scene">The room the place loads.</param>
    /// <returns>The number, or null when the moped cannot be ridden there.</returns>
    /// <remarks>
    /// <para>
    /// <b>It is the place's own position in this list.</b> The game's data gives six of the
    /// sixteen a number and every one of them is its index here: Coume Sourde is 3,
    /// L'Homme Mort 4, Chateau de Serras 9, Rennes-le-Château 10, Larry Chester's house 11
    /// and Blanchefort 12. The list is the retail driving layer's own order, recovered from
    /// its constructor, so the agreement is not a coincidence — it is the same table read
    /// two ways, and it is why this can be a lookup rather than sixteen more constants.
    /// </para>
    /// <para>
    /// <b>Why the port writes it and the original did not.</b> Nothing in the retail engine
    /// touches this variable — its name is in a table with no code reference — and only
    /// <c>LHE.SHP</c> and <c>MOP_ALL.SHP</c> set it, to 11 and 10, from their own arrival
    /// scripts. The other four places read a number nothing ever writes, so their moped is
    /// never drawn and, at three of them, the exit that asks whether the moped is here
    /// answers no: riding to Blanchefort, Coume Sourde or L'Homme Mort strands the player
    /// there. Writing it on arrival is what the six readers were plainly written against,
    /// and it agrees with both scripts that do write it rather than fighting them.
    /// </para>
    /// <para>
    /// <b>The first marker wins where two share a room.</b> "The Site" and Blanchefort both
    /// load <c>PLO</c>, and <c>PLO.SIF</c> asks for 12, which is Blanchefort's. Riding to
    /// either parks the moped at the one the room asks about.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// On the story rather than in the map, so it survives a save without the map having to
    /// be part of one.
    /// </remarks>
    private static string FlagFor(DrivingStop stop) => $"MapKnows:{stop.Sprite}";

    /// <summary>The places' names, from the game's own string table.</summary>
    /// <remarks>
    /// Whichever table the language reads — <c>FSTRINGS.TXT</c> in French — because these
    /// are the labels drawn on the map and reading the English one would put "Château de
    /// Blanchefort" on a French map in English. <see cref="GameStrings.Open"/> owns the
    /// choice of file; this asks it rather than repeating the rule.
    /// </remarks>
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
    /// <remarks>
    /// Junctions with a map position, and for each of them the roads out of it: which
    /// junction the road reaches, which stretch of road it is, and whether that stretch's
    /// points read forwards from here. The file names each stretch once, so exactly one of
    /// its two ends reads it backwards.
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
    /// <remarks>
    /// Four of the stretches the links name have no point list of their own — the two short
    /// roads down to L'Homme Mort and Coume Sourde, and the two along the top of the map —
    /// and one has a single point. A stretch with no shape is drawn as a straight line
    /// between its junctions, which on a 640-pixel painting of a valley is what those four
    /// look like anyway.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// The routes the game's own follow sequences are written as name junctions that are
    /// not always neighbours — Madeleine's drive to Coume Sourde is written
    /// <c>plo/pl3/rl1/in4/pl2</c>, and there is no road from <c>In4</c> to <c>Pl2</c>. So
    /// each leg is resolved as the shortest way through the network rather than assumed to
    /// be one road, which turns that leg into <c>In4, In3, Pl2</c> and puts the van on the
    /// road it plainly takes.
    /// </para>
    /// <para>
    /// A leg with no way through at all becomes a straight line to the next junction.
    /// Nothing in the shipped routes needs that, but a route is a string and a mod may
    /// write one this network cannot join up.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// Breadth-first over twenty junctions, which is small enough that the shape of the
    /// search does not matter. Null when the two are not joined at all, and empty when they
    /// are the same junction.
    /// </remarks>
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
