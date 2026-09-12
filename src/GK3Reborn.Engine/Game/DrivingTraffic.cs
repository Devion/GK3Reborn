// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Game;

/// <summary>Somebody else out on the roads, and where they are going.</summary>
/// <param name="Noun">
/// Who they are, as the action files spell it — <c>WILKES</c>, <c>BUTHANE</c> — so that the
/// map names them out of the same table as everything else.
/// </param>
/// <param name="Colour">Their colour, as the retail driving layer sets it.</param>
/// <param name="Portrait">
/// The picture the map draws them as — Sidney's own suspect portrait, rendered from the
/// character's own head, or <see cref="DrivingTraffic.EgoFace"/> for the player, who is
/// nobody's suspect and has one anyway. Empty only where there is no face to draw; and an
/// installation with no enhanced content has none of them, where the colour is all there is.
/// </param>
/// <param name="Junctions">The road junctions they pass, in order.</param>
/// <param name="Loops">Whether they go round again on arriving, rather than stopping.</param>
/// <param name="Follow">
/// Which of the game's own follow sequences giving chase starts, or zero where there is
/// none. It is the number <c>FollowOnDrivingMap</c> takes.
/// </param>
/// <param name="Reveals">
/// The places giving chase all the way puts on the map, by scene code. Empty for a chase
/// that ends somewhere the player already knows.
/// </param>
public sealed record Traveller(
    string Noun,
    Vector4 Colour,
    string Portrait,
    IReadOnlyList<string> Junctions,
    bool Loops,
    int Follow,
    IReadOnlyList<string> Reveals)
{
    /// <summary>
    /// The noun the story keeps the count of following them under.
    /// </summary>
    public string Counted { get; init; } = Noun;

    /// <summary>
    /// Where the story records them as being once the chase has ended, as a scene code,
    /// or null where nothing asks. Nothing else places them: no script and no scene sets
    /// Madeleine at Coume Sourde or Wilkes at L'Ermitage, and the retail driving layer
    /// writes it as the chase ends — which is what the end of the first afternoon reads,
    /// so a chase that left it unwritten could never be over.
    /// </summary>
    public string? LeavesThemAt { get; init; }

    /// <summary>
    /// What the player says as the quarry stops, as a dialogue licence plate, or null for
    /// nothing. Said over the map before it closes: the retail driving layer plays it from
    /// the chase's own end, so no script has it, and it is the whole of what tells the
    /// player what the chase was worth. Lady Howard's is "they are just driving round the
    /// valley" — without it a chase that puts the player back where they started reads as
    /// a chase that did not work, which is how it was reported.
    /// </summary>
    public string? Says { get; init; }

    /// <summary>
    /// Whether the map stays up once the chase is over, for the player to choose where to
    /// go next, rather than the ride carrying on to wherever the quarry stopped. The retail
    /// driving layer leaves it up after every chase but Lady Howard's, whose circuit puts
    /// the player back where they set out: Madeleine's and Wilkes's end with a new place
    /// on the map to pick, and the two men's ends outside Larry Chester's, where pulling
    /// in after them is refused — Gabriel parks at Blanchefort and walks over. Riding on
    /// put the moped in Larry's driveway while he was meant to be hiding.
    /// </summary>
    public bool LeavesMapOpen { get; init; }

    /// <summary>Where the road ends, as a scene code, or null for a route that loops.</summary>
    public string? Arrives =>
        Loops || Junctions.Count == 0 ? null : Junctions[^1].ToUpperInvariant();
}

/// <summary>
/// Who else is on the roads while the map is open.
/// </summary>
public sealed class DrivingTraffic
{
    /// <summary>How fast somebody circling covers the map, in its own pixels a second.</summary>
    private const float Wandering = 42f;

    /// <summary>How fast a chase moves.</summary>
    private const float Chasing = 170f;

    /// <summary>How far behind the quarry the player rides, in map pixels.</summary>
    private const float Behind = 46f;

    private readonly DrivingMap _map;
    private readonly List<Rider> _riders = [];
    private Rider? _quarry;
    private Rider? _player;

    private DrivingTraffic(DrivingMap map, Traveller? chase, string? from, string? destination = null)
    {
        _map = map;
        Chase = chase;
        From = from;
        Destination = destination;
    }

    /// <summary>Nobody on the roads.</summary>
    public static DrivingTraffic Nobody { get; } = new(DrivingMap.Empty, null, null);

    /// <summary>Who is being chased, or null when the player is riding where they like.</summary>
    public Traveller? Chase { get; }

    /// <summary>Where the chase set out from, as a scene code.</summary>
    public string? From { get; }

    /// <summary>Whether a chase is under way.</summary>
    public bool Following => Chase is not null;

    /// <summary>
    /// Whether what the player says as a chase ends has been started. The frame loop's
    /// mark, kept here because it belongs to this chase and to no other.
    /// </summary>
    public bool Said { get; set; }

    /// <summary>Where the player is riding to, as a scene code, or null when nowhere.</summary>
    public string? Destination { get; }

    /// <summary>Whether the player is on the road to somewhere they chose.</summary>
    public bool Riding => Destination is not null;

    /// <summary>Whether the picture is one to watch rather than one to click.</summary>
    public bool Moving => Following || Riding;

    /// <summary>Whether the chase has run its course.</summary>
    public bool Arrived { get; private set; }

    /// <summary>Everyone to draw, and where they are in the map's own pixels.</summary>
    public IReadOnlyList<Rider> Riders => _riders;

    /// <summary>Somebody on the roads, at a point on them.</summary>
    public sealed class Rider
    {
        /// <summary>Who they are.</summary>
        public required Traveller Who { get; init; }

        /// <summary>Whether this is the player's own moped rather than somebody to follow.</summary>
        public required bool IsPlayer { get; init; }

        /// <summary>Where they are, in the map's own 640-by-480 pixels.</summary>
        public Vector2 At { get; internal set; }

        /// <summary>The road they are on, as points on the map.</summary>
        internal IReadOnlyList<Vector2> Road { get; init; } = [];

        /// <summary>How far along it they are, in pixels. Negative means not yet started.</summary>
        internal float Travelled { get; set; }
    }

    /// <summary>
    /// The map's traffic as the story now stands.
    /// </summary>
    /// <param name="story">The game.</param>
    /// <param name="map">The map, for its road network.</param>
    /// <param name="follow">
    /// Which chase to run, as <c>FollowOnDrivingMap</c> numbers them, or zero for an
    /// ordinary ride.
    /// </param>
    /// <param name="ride">
    /// Where the player is riding to, as a scene code, or null for a map to choose from.
    /// </param>
    /// <returns>The traffic, which has nobody in it where the story has nobody out.</returns>
    public static DrivingTraffic For(GameState story, DrivingMap map, int follow = 0, string? ride = null)
    {
        ArgumentNullException.ThrowIfNull(story);
        ArgumentNullException.ThrowIfNull(map);

        string from = story.Location is { Length: > 0 } where &&
                      !string.Equals(where, DrivingMap.Location, StringComparison.OrdinalIgnoreCase)
            ? where
            : story.LastLocation is { Length: > 0 } before ? before : "MOP";

        // The player's own ride, along the roads from where the moped stands to where
        // they pointed, seen the way a chase is. Over at once where the roads do not
        // reach: a place with no junction, or a run with no road network at all.
        if (ride is { Length: > 0 } going)
        {
            var riding = new DrivingTraffic(map, null, from, going.ToUpperInvariant());

            riding.Ride(
                new Traveller(story.Ego, Ego, EgoFace, [from, going], Loops: false, 0, []),
                [from, going],
                player: true);

            riding.Arrived = riding._player is null || riding._player.Road.Count < 2;

            return riding;
        }

        Traveller? chase = follow > 0 ? Chased(follow, from) : null;
        var traffic = new DrivingTraffic(map, chase, from);

        if (chase is not null)
        {
            traffic.Ride(chase, chase.Junctions, player: false);

            // The player, on the same road and a little way back. Behind rather than
            // beside: two dots on one road at one speed read as one dot, and the whole
            // point of the picture is that somebody is being followed.
            traffic.Ride(
                new Traveller(story.Ego, Ego, EgoFace, chase.Junctions, Loops: false, 0, []),
                chase.Junctions,
                player: true);

            if (traffic._player is { } trailing)
            {
                trailing.Travelled = -Behind;
            }

            // A chase with no road under it is over before it starts. That is a run with no
            // road network — a headless one, or archives without PATHDATA.TXT — and the
            // alternative is a map showing nobody that the story is waiting on.
            traffic.Arrived = traffic._quarry is null;

            return traffic;
        }

        foreach (Traveller circling in Circling(story))
        {
            traffic.Ride(circling, circling.Junctions, player: false);
        }

        return traffic;
    }

    /// <summary>Moves everyone on.</summary>
    /// <param name="seconds">How long since the last frame.</param>
    public void Advance(double seconds)
    {
        float step = (float)seconds * (Moving ? Chasing : Wandering);

        foreach (Rider one in _riders)
        {
            Walk(one, step);
        }

        // The chase is over when the quarry stops, not when the player catches up: the
        // player is deliberately behind, and waiting for them to arrive would leave the
        // picture still for the second it takes. A ride is over when the player stops.
        if (_quarry is { } front && front.Travelled >= Length(front.Road))
        {
            Arrived = true;
        }

        if (Riding && _player is { } rider && rider.Travelled >= Length(rider.Road))
        {
            Arrived = true;
        }
    }

    /// <summary>Puts a chase or a ride at its end, for a player who does not want to watch it.</summary>
    public void Skip()
    {
        if (!Moving)
        {
            return;
        }

        foreach (Rider one in _riders)
        {
            // Put at the end rather than moved a road's length along it: the player rides
            // deliberately behind, so moving everyone the same distance would skip the
            // chase and still leave them short of it.
            one.Travelled = Length(one.Road);
            one.At = one.Road.Count > 0 ? one.Road[^1] : one.At;
        }

        Arrived = true;
    }

    /// <summary>Where somebody is, once they have been moved on.</summary>
    private static void Walk(Rider one, float step)
    {
        float road = Length(one.Road);

        one.Travelled += step;

        if (one.Who.Loops && road > 0)
        {
            one.Travelled %= road;
        }

        one.At = Along(one.Road, Math.Clamp(one.Travelled, 0, road));
    }

    /// <summary>Adds somebody to the traffic, on the road their route describes.</summary>
    private void Ride(Traveller who, IReadOnlyList<string> junctions, bool player)
    {
        IReadOnlyList<Vector2> road = _map.Route(junctions);

        if (road.Count == 0)
        {
            return;
        }

        var one = new Rider { Who = who, IsPlayer = player, Road = road, At = road[0] };

        _riders.Add(one);

        if (player)
        {
            _player = one;
        }
        else if (Following)
        {
            _quarry = one;
        }
    }

    /// <summary>How long a road is, in map pixels.</summary>
    private static float Length(IReadOnlyList<Vector2> road)
    {
        float total = 0;

        for (int i = 1; i < road.Count; i++)
        {
            total += Vector2.Distance(road[i - 1], road[i]);
        }

        return total;
    }

    /// <summary>The point a distance along a road.</summary>
    private static Vector2 Along(IReadOnlyList<Vector2> road, float along)
    {
        if (road.Count == 0)
        {
            return Vector2.Zero;
        }

        float left = Math.Max(0, along);

        for (int i = 1; i < road.Count; i++)
        {
            float leg = Vector2.Distance(road[i - 1], road[i]);

            if (leg <= 0)
            {
                continue;
            }

            if (left <= leg)
            {
                return Vector2.Lerp(road[i - 1], road[i], left / leg);
            }

            left -= leg;
        }

        return road[^1];
    }

    // -------------------------------------------------------------------------------------
    // What the retail driving layer builds, recovered from it.
    //
    // Two functions do all of this: the one that runs when the map opens, which creates the
    // circling travellers, and the one FollowOnDrivingMap feeds, which creates the chase.
    // The route strings below are theirs verbatim, minus the leading name the engine gives
    // each one; the colours are its own five constants.
    // -------------------------------------------------------------------------------------

    /// <summary>Wilkes, on a moped.</summary>
    private static Vector4 Wilkes { get; } = Paint(0x3C97F4);

    /// <summary>Madeleine Buthane, in a van.</summary>
    private static Vector4 Buthane { get; } = Paint(0xFBC0C4);

    /// <summary>Lady Howard and Estelle, in a car.</summary>
    private static Vector4 Howard { get; } = Paint(0x8B3827);

    /// <summary>Mallory and MacDougall, Prince James's men, in a sedan.</summary>
    private static Vector4 TwoMen { get; } = Paint(0x34931B);

    /// <summary>The player.</summary>
    private static Vector4 Ego { get; } = Paint(0x00FF00);

    /// <summary>
    /// The faces the map draws, which are Sidney's.
    /// </summary>
    private const string WilkesFace = "PORTRAIT_WIL";

    /// <inheritdoc cref="WilkesFace"/>
    private const string ButhaneFace = "PORTRAIT_MAD";

    /// <inheritdoc cref="WilkesFace"/>
    private const string HowardFace = "PORTRAIT_LMO";

    /// <inheritdoc cref="WilkesFace"/>
    private const string EstelleFace = "PORTRAIT_EST";

    /// <summary>
    /// Prince James's men are nobody's suspect, so Sidney has no portrait of either; this
    /// one is Mallory's, rendered from his own model the way the suspects' were
    /// (<c>render-model --model HE1 --portrait</c>). The map drew Buchelli's face for a
    /// while, which is how it was reported.
    /// </summary>
    public const string TwoMenFace = "PORTRAIT_MAL";

    /// <summary>
    /// Gabriel, which is the marker the player is watching.
    /// </summary>
    public const string EgoFace = "PORTRAIT_GAB";

    /// <summary>A colour the retail engine holds as one number.</summary>
    private static Vector4 Paint(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    /// <summary>The long way round the valley, which three of the four circling routes are.</summary>
    private static readonly string[] Clockwise =
        ["plo", "pl3", "rl1", "in4", "vgr", "pl4", "bec", "pl5", "tr1", "in1", "lhe", "plo"];

    /// <summary>
    /// A circling route, begun where the traveller actually is.
    /// </summary>
    /// <param name="loop">The route, whose first and last junctions are the same.</param>
    /// <param name="start">The junction they set out from.</param>
    /// <returns>The same loop, turned so that it begins there.</returns>
    private static string[] Begun(string[] loop, string start)
    {
        int at = -1;

        for (int i = 1; i < loop.Length - 1; i++)
        {
            if (string.Equals(loop[i], start, StringComparison.OrdinalIgnoreCase))
            {
                at = i;

                break;
            }
        }

        // Already there, or somewhere the loop does not pass — the retail engine has one of
        // those too, and leaves the traveller to start where the route is written.
        return at < 0
            ? loop
            : [.. loop.Skip(at).Take(loop.Length - 1 - at), .. loop.Take(at + 1)];
    }

    /// <summary>
    /// Who is out on the roads at this point in the story.
    /// </summary>
    public static IReadOnlyList<Traveller> Circling(GameState story)
    {
        ArgumentNullException.ThrowIfNull(story);

        List<Traveller> travelling = [];
        string now = story.Timeblock.ToString();

        if (string.Equals(now, "102P", StringComparison.OrdinalIgnoreCase))
        {
            if (story.GetNounVerbCount("WILKES", DrivingMap.Follow) == 0)
            {
                travelling.Add(new Traveller(
                    "WILKES",
                    Wilkes,
                    WilkesFace,
                    ["lhe", "plo", "pl3", "rl1", "in4", "vgr", "pl4", "bec", "pl5", "tr1", "in1", "lhe"],
                    Loops: true,
                    Follow: 2,
                    ["PL4"]));
            }

            if (story.GetNounVerbCount("BUTHANE", DrivingMap.Follow) == 0)
            {
                // Out of the Armchair of the Devil, which is where the story has her.
                travelling.Add(new Traveller(
                    "BUTHANE",
                    Buthane,
                    ButhaneFace,
                    Begun(Clockwise, "vgr"),
                    Loops: true,
                    Follow: 1,
                    ["PL2", "PL1"]));
            }
        }

        // Day 1, four in the afternoon: Lady Howard drives about whether or not she has
        // been followed, which is what the retail layer does with her — she is created
        // outside the chase rather than instead of it.
        if (string.Equals(now, "104P", StringComparison.OrdinalIgnoreCase))
        {
            travelling.Add(new Traveller(
                "LADY_HOWARD",
                Howard,
                HowardFace,
                Begun(Clockwise, "lhe"),
                Loops: true,
                Follow: 4,
                []));
        }

        // Day 1, six: Prince James's men, but only once the story has them out on the
        // road. TwoMenState is the game's own variable and four is its own number for it.
        if (string.Equals(now, "106P", StringComparison.OrdinalIgnoreCase) &&
            story.GetVariable("TwoMenState") == 4)
        {
            travelling.Add(new Traveller(
                "TWO_MEN",
                TwoMen,
                TwoMenFace,
                ["lhe", "in1", "tr1", "pl5", "bec", "pl4", "vgr", "in4", "rl1", "pl3", "plo", "lhe"],
                Loops: true,
                Follow: 5,
                []));
        }

        // Day 2, two: Estelle, until the player has followed her to the dig.
        //
        // Followed all the way rather than followed at all, which is where this parts from
        // the original. Lady Howard and Estelle share one count, and the original asks
        // whether it is set — so a player who followed Lady Howard round the valley on Day
        // 1, which leads nowhere and is worth nothing, lost the Day 2 chase that finds the
        // dig. See Application.Arrive for the two values and what each of them means.
        if (string.Equals(now, "202P", StringComparison.OrdinalIgnoreCase) &&
            story.GetNounVerbCount("LADY_HOWARD", DrivingMap.Follow) < 2)
        {
            travelling.Add(new Traveller(
                "ESTELLE",
                Howard,
                EstelleFace,
                Begun(Clockwise, "mop"),
                Loops: true,
                Follow: 6,
                ["PL5"])
            {
                Counted = "LADY_HOWARD",
            });
        }

        return travelling;
    }

    /// <summary>
    /// Records where every chase already followed all the way left its quarry.
    /// </summary>
    /// <param name="story">The game.</param>
    public static void RecordWhereChasesEnded(GameState story)
    {
        ArgumentNullException.ThrowIfNull(story);

        for (int follow = 1; follow <= 7; follow++)
        {
            if (Chased(follow, from: null) is { LeavesThemAt: { } at } chase &&
                story.GetNounVerbCount(chase.Counted, DrivingMap.Follow) >= 2 &&
                story.GetActorLocation(chase.Noun).Length == 0)
            {
                story.SetActorLocation(chase.Noun, at);
            }
        }
    }

    /// <summary>
    /// The chase one of the game's own follow numbers describes.
    /// </summary>
    /// <param name="follow">The number <c>FollowOnDrivingMap</c> was given.</param>
    /// <param name="from">Where the player is riding out of, as a scene code.</param>
    /// <returns>The chase, or null where the number describes none.</returns>
    public static Traveller? Chased(int follow, string? from)
    {
        bool At(string where) => string.Equals(from, where, StringComparison.OrdinalIgnoreCase);

        return follow switch
        {
            // Madeleine, to Coume Sourde. Which also puts L'Homme Mort on the map: the two
            // are a mile apart on one road, and the retail layer reveals them together.
            1 => new Traveller(
                "BUTHANE",
                Buthane,
                ButhaneFace,
                At("PLO")
                    ? ["plo", "pl3", "rl1", "in4", "pl2"]
                    : ["pl4", "bec", "pl5", "tr1", "in1", "lhe", "plo", "pl3", "rl1", "in4", "pl2"],
                Loops: false,
                Follow: 1,
                ["PL2", "PL1"])
            {
                LeavesThemAt = "CSD",
                Says = "2196L3WL71",
                LeavesMapOpen = true,
            },

            // Wilkes, to L'Ermitage.
            2 => new Traveller(
                "WILKES",
                Wilkes,
                WilkesFace,
                ["plo", "pl3", "rl1", "in4", "vgr", "pl4"],
                Loops: false,
                Follow: 2,
                ["PL4"])
            {
                LeavesThemAt = "LER",
                Says = "2196L3WLM1",
                LeavesMapOpen = true,
            },

            // Lady Howard, all the way round and back to where she started. She is not
            // going anywhere the player does not know, which is the point of her.
            4 => new Traveller(
                "LADY_HOWARD",
                Howard,
                HowardFace,
                At("PLO")
                    ? Clockwise
                    : ["pl4", "bec", "pl5", "tr1", "in1", "lhe", "plo", "pl3", "rl1", "in4", "vgr", "pl4"],
                Loops: false,
                Follow: 4,
                [])
            {
                Says = "21A6L3WLX1",
            },

            // Mallory and MacDougall, to Larry Chester's house.
            5 => new Traveller(
                "TWO_MEN",
                TwoMen,
                TwoMenFace,
                At("PL4") ? ["pl4", "vgr", "in4", "rl1", "pl3", "plo", "lhe"]
                : At("PLO") ? ["plo", "lhe"]
                : ["mop", "in2", "in1", "tr1", "pl5", "bec", "pl4", "vgr", "in4", "rl1", "pl3", "plo", "lhe"],
                Loops: false,
                Follow: 5,
                [])
            {
                Says = "21F6L3WBH1",
                LeavesMapOpen = true,
            },

            // Estelle, to where she and Lady Howard are digging.
            6 => new Traveller(
                "ESTELLE",
                Howard,
                EstelleFace,
                At("PL4") ? ["pl4", "bec", "pl5"]
                : At("VGR") ? ["vgr", "pl4", "bec", "pl5"]
                : At("PLO") ? ["plo", "pl3", "rl1", "in4", "vgr", "pl4", "bec", "pl5"]
                : ["mop", "in1", "lhe", "plo", "pl3", "rl1", "in4", "vgr", "pl4", "bec", "pl5"],
                Loops: false,
                Follow: 6,
                ["PL5"])
            {
                Counted = "LADY_HOWARD",
                LeavesThemAt = "WOD",
                Says = "21K6L3WJI1",
                LeavesMapOpen = true,
            },

            // Madeleine again, out of Rennes-le-Château to Poussin's tomb.
            7 => new Traveller(
                "BUTHANE",
                Buthane,
                ButhaneFace,
                ["mop", "in1", "lhe", "pl6", "mcb", "pou"],
                Loops: false,
                Follow: 7,
                ["POU"])
            {
                LeavesMapOpen = true,
            },

            _ => null,
        };
    }
}
