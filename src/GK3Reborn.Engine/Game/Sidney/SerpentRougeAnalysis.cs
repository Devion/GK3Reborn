// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Game.Sidney;

/// <summary>Something Sidney has to do outside its own screen when a step is confirmed.</summary>
public enum SerpentRougeCue
{
    /// <summary>Grace says a line: a dialogue licence plate and how many lines it has.</summary>
    Say,

    /// <summary>The story moves to a room, which is how the second evening ends.</summary>
    Leave,

    /// <summary>Sidney is put away, which is how the third morning can end.</summary>
    Close,
}

/// <summary>One thing for the frame loop to do, in the order the machine queued them.</summary>
/// <param name="Kind">What kind of thing.</param>
/// <param name="Plate">The dialogue plate to say, or the room to leave for.</param>
/// <param name="Lines">How many lines the plate has.</param>
public sealed record SidneySpeech(SerpentRougeCue Kind, string Plate, int Lines = 1);

/// <summary>
/// What a step of the analysis came to.
/// </summary>
/// <param name="Handled">Whether the machine recognised what was on the map as something.</param>
/// <param name="Note">The key of the analyze screen's note to show, or null for none.</param>
/// <param name="Argument">What the note's <c>%s</c> stands for, or null.</param>
/// <param name="Cues">What has to happen outside the screen, in order.</param>
public sealed record SerpentRougeOutcome(
    bool Handled,
    string? Note = null,
    string? Argument = null,
    IReadOnlyList<SidneySpeech>? Cues = null)
{
    /// <summary>Nothing recognised, nothing said.</summary>
    public static SerpentRougeOutcome Nothing { get; } = new(false);
}

/// <summary>
/// Le Serpent Rouge, worked out on Sidney's map: the thirteen verses as the retail engine
/// checks them off.
/// </summary>
/// <remarks>
/// <para>
/// The map puzzle is not geometry the machine measures for itself; it is a fixed sequence of
/// answers the retail engine holds in code and compares the player's marks and figures
/// against, one verse at a time, setting the zodiac flag for each — <c>Aquarius</c> through
/// <c>Sagittarius</c> — and the <c>LSRState</c> count the scripts read. Nothing in the game
/// data says any of it: the spots, the tolerances, the order and what Grace says are all the
/// engine's, read here out of the reference engine's <c>SidneyAnalyze_Map.cpp</c>.
/// </para>
/// <para>
/// Spots are in the map's own 1,368 pixels measured from the top left; the retail engine
/// measures from the bottom left, so every one of its Y values is turned over. Its
/// zoomed-out figures are a quarter the size, so its centre, radius and tolerances for the
/// circle, square and hexagram are four times what it writes. A marked place counts as one
/// of the answer's when it is within twenty pixels; a figure when its middle is within
/// eighty and its size within sixteen.
/// </para>
/// <para>
/// Some steps are checked when ANALYZE is pressed (Aquarius, the meridian line, Leo, Virgo,
/// the temple divisions, Sagittarius), some the moment the map changes (Pisces, Aries,
/// Taurus, Libra), one when a place is marked (Scorpio's Site) and one when a grid is
/// drawn (Gemini and Cancer). Ophiuchus is the anagram and Capricorn the temple; neither is
/// on the map.
/// </para>
/// </remarks>
public static class SerpentRougeAnalysis
{
    // -------------------------------------------------------------------------------------
    // The answers, in map pixels from the top left.
    // -------------------------------------------------------------------------------------

    /// <summary>Rennes-le-Château, where the sunrise line starts.</summary>
    public static readonly Vector2 Church = new(267f, 415f);

    /// <summary>The ruin at Blanchefort the sunrise line runs over.</summary>
    public static readonly Vector2 Ruin = new(652f, 307f);

    /// <summary>Where the sunrise line is drawn to, at the edge of the map.</summary>
    public static readonly Vector2 SunriseEnd = new(1336f, 121f);

    /// <summary>Coustaussa, one of the three the circle passes through.</summary>
    public static readonly Vector2 Coustaussa = new(404f, 273f);

    /// <summary>St-Just-et-le-Bézu, another.</summary>
    public static readonly Vector2 Bezu = new(301f, 982f);

    /// <summary>Bugarach, the third.</summary>
    public static readonly Vector2 Bugarach = new(990f, 1042f);

    /// <summary>The middle of the circle, the square and the hexagram.</summary>
    public static readonly Vector2 Centre = new(676f, 672f);

    /// <summary>The circle's radius, and the hexagram's.</summary>
    public const float Radius = 484f;

    /// <summary>The square's side: the circle's width and a little.</summary>
    public const float SquareSide = (Radius * 2f) + 4f;

    /// <summary>
    /// The square's turn once it is aligned with the meridian line, as the map's own
    /// corner angle: the retail engine's 1.185174 radians, turned over and taken from the
    /// corner rather than the side. Any quarter turn on from it is the same square.
    /// </summary>
    public const float SquareTurn = 67.095f;

    /// <summary>
    /// The hexagram's turn once it is right, as a corner angle: the retail engine's thirty
    /// three degrees, turned over and taken from a point to the north. Any sixth of a turn
    /// on from it is the same hexagram.
    /// </summary>
    public const float HexagramTurn = 57f;

    /// <summary>Serres, one end of the meridian line.</summary>
    public static readonly Vector2 Serres = new(808f, 200f);

    /// <summary>Where the meridian line meets the circle.</summary>
    public static readonly Vector2 Meridian = new(896f, 238f);

    /// <summary>L'Ermitage, one end of the line to the tomb.</summary>
    public static readonly Vector2 Ermitage = new(676f, 672f);

    /// <summary>Poussin's tomb, the other.</summary>
    public static readonly Vector2 Tomb = new(936f, 160f);

    /// <summary>The four corners of the temple, in order round it.</summary>
    public static readonly Vector2[] TempleCorners =
    [
        new(381f, 1076f), new(605f, 1167f), new(970f, 266f), new(745f, 175f),
    ];

    /// <summary>The four points the temple's divisions run between, as two pairs.</summary>
    public static readonly Vector2[] TempleDivisions =
    [
        new(654f, 401f), new(879f, 491f), new(471f, 850f), new(695f, 942f),
    ];

    /// <summary>The Site.</summary>
    public static readonly Vector2 Site = new(812f, 335f);

    /// <summary>The tail of the red serpent, which must be marked.</summary>
    public static readonly Vector2 SerpentTail = new(875f, 205f);

    /// <summary>Its head, which must be marked too.</summary>
    public static readonly Vector2 SerpentHead = new(735f, 3f);

    /// <summary>Four places along the serpent that are kept if they were marked.</summary>
    public static readonly Vector2[] SerpentAlong =
    [
        new(875f, 162f), new(837f, 130f), new(801f, 101f), new(765f, 67f),
    ];

    /// <summary>How near a figure's middle has to be to the answer's, in map pixels.</summary>
    private const float NearCentre = 80f;

    /// <summary>How near a figure's size has to be to the answer's, in map pixels.</summary>
    private const float NearSize = 16f;

    /// <summary>The thirteen signs, in the order the poem takes them.</summary>
    public static readonly string[] Signs =
    [
        "Aquarius", "Pisces", "Aries", "Taurus", "Gemini", "Cancer", "Leo",
        "Virgo", "Libra", "Scorpio", "Ophiuchus", "Sagittarius", "Capricorn",
    ];

    /// <summary>
    /// How many signs have been worked through in order, which the scripts read as
    /// <c>LSRState</c>.
    /// </summary>
    /// <param name="story">The game.</param>
    /// <returns>Nought to thirteen.</returns>
    public static int Solved(GameState story)
    {
        ArgumentNullException.ThrowIfNull(story);

        int solved = 0;

        while (solved < Signs.Length && story.GetFlag(Signs[solved]))
        {
            solved++;
        }

        return solved;
    }

    /// <summary>Writes the count the scripts read, as the retail engine does after every step.</summary>
    /// <param name="story">The game.</param>
    public static void UpdateState(GameState story)
    {
        ArgumentNullException.ThrowIfNull(story);

        story.SetVariable("LSRState", Solved(story));
    }

    // -------------------------------------------------------------------------------------
    // The checks.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// What pressing ANALYZE over the map comes to at this point in the story.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="story">The game.</param>
    /// <param name="scores">The score sheet, for what each step is worth; null awards nothing.</param>
    /// <returns>What happened. Never unhandled without a note: the map always answers.</returns>
    public static SerpentRougeOutcome Analyse(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(story);

        bool aquarius = story.GetFlag("Aquarius");
        bool pisces = story.GetFlag("Pisces");
        bool aries = story.GetFlag("Aries");
        bool taurus = story.GetFlag("Taurus");
        bool gemini = story.GetFlag("Gemini");
        bool cancer = story.GetFlag("Cancer");
        bool leo = story.GetFlag("Leo");
        bool virgo = story.GetFlag("Virgo");
        bool libra = story.GetFlag("Libra");
        bool scorpio = story.GetFlag("Scorpio");
        bool sagittarius = story.GetFlag("Sagittarius");

        SerpentRougeOutcome outcome = SerpentRougeOutcome.Nothing;

        if (!aquarius)
        {
            outcome = CheckAquarius(map, story, scores);
        }
        else if (!pisces)
        {
            // All three marked but no circle yet: "several possible linkages".
            if (map.HasNear(Coustaussa) && map.HasNear(Bezu) && map.HasNear(Bugarach))
            {
                outcome = new SerpentRougeOutcome(true, "MapSeveralPossNote");
            }
        }
        else if (!aries || !taurus)
        {
            outcome = CheckMeridianLine(map, story, scores);
        }
        else if (gemini && cancer && (!leo || !virgo))
        {
            // Leo before Virgo by design, either order in practice, and both sets of
            // places down at once completes Leo first and Virgo on the next press.
            bool justLeo = false;

            if (!leo)
            {
                outcome = CheckLeo(map, story, scores);
                justLeo = outcome.Handled;
            }

            if (!virgo && !justLeo)
            {
                outcome = CheckVirgo(map, story, scores);
            }
        }
        else if (libra && !scorpio)
        {
            outcome = CheckTempleDivisions(map, story, scores);
        }

        // Sagittarius may be tried at any time, and the game answers even when it is the
        // wrong time for it — the retail engine's own oddity, kept.
        if (!outcome.Handled && !sagittarius)
        {
            outcome = CheckSagittarius(map, story, scores);
        }

        if (outcome.Handled)
        {
            return outcome;
        }

        // Nothing recognised: one note for a map with marks on it, another for a map with none.
        return new SerpentRougeOutcome(
            false,
            map.Points.Count > 0 || map.Laid.Any(l => !l.Fixed && l.Points.Count > 0)
                ? "MapIndeterminateNote"
                : "MapNoPrimitiveNote");
    }

    /// <summary>
    /// What the map's figures come to as they stand: the steps the retail engine checks every
    /// frame rather than on a button. Run after anything on the map changes.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="story">The game.</param>
    /// <param name="scores">The score sheet, or null.</param>
    /// <returns>What happened, if anything.</returns>
    public static SerpentRougeOutcome Changed(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(story);

        bool aquarius = story.GetFlag("Aquarius");
        bool pisces = story.GetFlag("Pisces");
        bool aries = story.GetFlag("Aries");
        bool taurus = story.GetFlag("Taurus");
        bool leo = story.GetFlag("Leo");
        bool virgo = story.GetFlag("Virgo");
        bool libra = story.GetFlag("Libra");

        if (aquarius && !pisces)
        {
            return CheckPisces(map, story, scores);
        }

        if (pisces && !aries)
        {
            return CheckAries(map, story, scores);
        }

        if (aries && !taurus)
        {
            return CheckTaurus(map, story, scores);
        }

        if (leo && virgo && !libra)
        {
            return CheckLibra(map, story, scores);
        }

        return SerpentRougeOutcome.Nothing;
    }

    /// <summary>
    /// What marking a place comes to at once: only The Site, once the temple's divisions
    /// are down, answers the moment it is marked.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="story">The game.</param>
    /// <param name="scores">The score sheet, or null.</param>
    /// <returns>What happened, if anything.</returns>
    public static SerpentRougeOutcome Marked(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(story);

        if (story.GetFlag("Libra") && !story.GetFlag("Scorpio") && story.GetFlag("PlacedTempleDivisions"))
        {
            return CheckSite(map, story, scores);
        }

        return SerpentRougeOutcome.Nothing;
    }

    /// <summary>
    /// What ruling a grid comes to.
    /// </summary>
    /// <remarks>
    /// A grid is almost never the answer. Filling a shape is only allowed while Gemini is
    /// the verse in hand, and only the eight by eight is the chessboard; everything else
    /// draws the grid and has Grace doubt it.
    /// </remarks>
    /// <param name="map">The map, with the grid already drawn where it was allowed.</param>
    /// <param name="story">The game.</param>
    /// <param name="scores">The score sheet, or null.</param>
    /// <param name="cells">How many cells each way.</param>
    /// <param name="inShape">Whether it was ruled inside the figure.</param>
    /// <returns>What happened.</returns>
    public static SerpentRougeOutcome Ruled(
        SidneyMap map, GameState story, ScoreEvents? scores, int cells, bool inShape)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(story);

        bool workingOnGemini = story.GetFlag("Taurus") && !story.GetFlag("Gemini");

        if (!(workingOnGemini && inShape))
        {
            // "Hmm, not sure about that."
            return new SerpentRougeOutcome(true, Cues: [Say("02O0I27GT1")]);
        }

        if (cells != 8)
        {
            // "I'm not sure about the size of the grid."
            return new SerpentRougeOutcome(true, Cues: [Say("02OD32ZGW1")]);
        }

        // That's it! That's the chessboard. Gemini and Cancer in one, and the end of the
        // second afternoon: the retail engine sets the flags and walks Grace out to the
        // hallway once the line is said, which is where the next timeblock begins.
        map.FixGrid();

        Done(story, scores, "Gemini", "e_sidney_map_gemini", "PlacedGrid");
        story.SetFlag("Cancer");
        UpdateState(story);

        return new SerpentRougeOutcome(
            true,
            Cues: [Say("02OCL2ZJL1"), new SidneySpeech(SerpentRougeCue.Leave, "HAL")]);
    }

    /// <summary>
    /// Whether a grid may be ruled inside the figure at all just now.
    /// </summary>
    /// <param name="story">The game.</param>
    /// <returns>The line Grace says instead when it may not, or null when it may.</returns>
    public static string? RefusesShapeGrid(GameState story)
    {
        ArgumentNullException.ThrowIfNull(story);

        bool workingOnGemini = story.GetFlag("Taurus") && !story.GetFlag("Gemini");

        // "I don't think a grid will help me here."
        return workingOnGemini ? null : "02OD32ZNF1";
    }

    /// <summary>
    /// Whether the figure being worked on may be erased just now.
    /// </summary>
    /// <remarks>
    /// Between Aries and Taurus the square is right but not yet turned right, and erasing
    /// it would undo Aries; Grace says she thinks it is right and keeps it.
    /// </remarks>
    /// <param name="map">The map.</param>
    /// <param name="story">The game.</param>
    /// <returns>The line Grace says instead, or null when it may go.</returns>
    public static string? RefusesErase(SidneyMap map, GameState story)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(story);

        return map.Shape == MapShape.Square && story.GetFlag("Aries") && !story.GetFlag("Taurus")
            ? "02O0I27LN1"
            : null;
    }

    /// <summary>
    /// Whether places may be marked at all just now.
    /// </summary>
    /// <remarks>
    /// On the second afternoon Grace will not plot anything until she has the church
    /// pamphlet and the poem to plot from.
    /// </remarks>
    /// <param name="story">The game.</param>
    /// <returns>The line she says instead, or null when she will.</returns>
    public static string? RefusesMarking(GameState story)
    {
        ArgumentNullException.ThrowIfNull(story);

        if (story.Timeblock == new Timeblock(2, 5, IsAfternoon: true) &&
            (!story.Inventory.Has(story.Ego, "CHURCH_PAMPHLET") || !story.Inventory.Has(story.Ego, "LSR")))
        {
            return "02O8O2ZPI1";
        }

        return null;
    }

    /// <summary>
    /// The turn a figure being rotated should settle on if a step passes over it: the
    /// square's once the meridian line is down, the hexagram's once it sits in the circle.
    /// </summary>
    /// <remarks>
    /// The retail engine's player turns a figure by dragging and stops when it looks right,
    /// within a tenth of a radian for the square and two degrees for the hexagram. A turn
    /// taken in steps cannot stop there, so a step that sweeps past the right angle lands
    /// on it, and a step that does not leaves the figure where the step put it.
    /// </remarks>
    /// <param name="map">The map.</param>
    /// <param name="story">The game.</param>
    /// <param name="from">The turn before the step, in degrees.</param>
    /// <param name="to">The turn after it.</param>
    /// <returns>The turn to settle on, or null to keep <paramref name="to"/>.</returns>
    public static float? Snap(SidneyMap map, GameState story, float from, float to)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(story);

        if (map.Working is not { } working)
        {
            return null;
        }

        float target;
        float period;

        if (working.Shape == MapShape.Square &&
            story.GetFlag("Aries") && !story.GetFlag("Taurus") && story.GetFlag("PlacedMeridianLine"))
        {
            target = SquareTurn;
            period = 90f;
        }
        else if (working.Shape == MapShape.Hexagram &&
            story.GetFlag("Leo") && story.GetFlag("Virgo") && !story.GetFlag("Libra") &&
            AtTheCircle(working))
        {
            target = HexagramTurn;
            period = 60f;
        }
        else
        {
            return null;
        }

        // Whether the answer lies in the arc the step swept, in whichever direction it went.
        float swept = ((to - from) % 360f + 360f) % 360f;
        bool forward = swept <= 180f;
        float arc = forward ? swept : 360f - swept;
        float start = forward ? from : to;

        for (float candidate = target; candidate < 360f + period; candidate += period)
        {
            float ahead = ((candidate - start) % 360f + 360f) % 360f;

            if (ahead <= arc + 1e-3f)
            {
                return candidate % 360f;
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------------------
    // One verse each.
    // -------------------------------------------------------------------------------------

    private static SerpentRougeOutcome CheckAquarius(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        if (!map.HasNear(Church) || !map.HasNear(Ruin))
        {
            return SerpentRougeOutcome.Nothing;
        }

        map.TakeNear(Church);
        map.TakeNear(Ruin);
        map.Remove(MapShape.Line);

        // The sunrise line, drawn from the church out to the edge, with both places locked.
        map.Fix(Segment(Church, SunriseEnd, [Church, Ruin]));

        Done(story, scores, "Aquarius", "e_sidney_map_aquarius", "PlacedSunriseLine");
        UpdateState(story);

        // Grace says "Cool!"
        return new SerpentRougeOutcome(true, "MapLine1Note", Cues: [Say("02O3H2Z7F3")]);
    }

    private static SerpentRougeOutcome CheckPisces(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        if (!map.HasNear(Coustaussa) || !map.HasNear(Bezu) || !map.HasNear(Bugarach))
        {
            return SerpentRougeOutcome.Nothing;
        }

        LaidShape? circle = map.Laid.FirstOrDefault(l =>
            !l.Fixed && l.Shape == MapShape.Circle &&
            Vector2.Distance(l.At, Centre) < NearCentre && MathF.Abs(l.Size - Radius) < NearSize);

        if (circle is null)
        {
            return SerpentRougeOutcome.Nothing;
        }

        map.TakeNear(Coustaussa);
        map.TakeNear(Bezu);
        map.TakeNear(Bugarach);

        // The player may well have marked the church again on the way; it goes, to keep
        // the map clean.
        map.TakeNear(Church);
        map.Remove(MapShape.Circle);
        map.Select(MapShape.None);

        map.Fix(new LaidShape(MapShape.Circle, Centre, Radius, 0f, true, [Coustaussa, Bezu, Bugarach]));

        // The coordinates at its centre, on a piece of paper in the bag.
        story.Inventory.Add(story.Ego, "GRACE_COORDINATE_PAPER_1");

        Done(story, scores, "Pisces", "e_sidney_map_circle", "LockedCircle");
        UpdateState(story);

        return new SerpentRougeOutcome(
            true, "MapCircleConfirmNote", SidneyMap.Coordinates(Centre), [Say("02OAG2ZJU2", 2)]);
    }

    private static SerpentRougeOutcome CheckAries(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        LaidShape? square = map.Laid.FirstOrDefault(l =>
            !l.Fixed && l.Shape == MapShape.Square &&
            Vector2.Distance(l.At, Centre) < NearCentre &&
            MathF.Abs((l.Size * MathF.Sqrt(2f)) - SquareSide) < NearSize);

        if (square is null)
        {
            return SerpentRougeOutcome.Nothing;
        }

        // The right size in the right place. Not settled yet: it still has to be turned.
        map.Rework(square with { At = Centre, Size = SquareSide / MathF.Sqrt(2f), Locked = true, Points = [] });

        Done(story, scores, "Aries", "e_sidney_map_aries", "SizedSquare");
        UpdateState(story);

        return new SerpentRougeOutcome(true, Cues: [Say("02O7E2ZIS1")]);
    }

    private static SerpentRougeOutcome CheckMeridianLine(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        if (!map.HasNear(Serres) || !map.HasNear(Meridian))
        {
            return SerpentRougeOutcome.Nothing;
        }

        map.TakeNear(Serres);
        map.TakeNear(Meridian);
        map.Remove(MapShape.Line);

        // Marked again after the first time, the places just go; only the first gets a word.
        if (map.HasFixedNear(Serres))
        {
            return new SerpentRougeOutcome(true, "MapLine2Note");
        }

        map.Fix(Segment(Serres, Meridian, [Serres, Meridian]));

        story.SetFlag("PlacedMeridianLine");
        Award(story, scores, "e_sidney_map_serres");

        // "Oh yeah, that's what the riddle means." Taurus is not done: the square has to
        // be turned to it.
        return new SerpentRougeOutcome(true, "MapLine2Note", Cues: [Say("02O3H2ZQB2")]);
    }

    private static SerpentRougeOutcome CheckTaurus(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        if (!story.GetFlag("PlacedMeridianLine") || map.Working is not { Shape: MapShape.Square } square)
        {
            return SerpentRougeOutcome.Nothing;
        }

        if (!Aligned(square.Turn, SquareTurn, 90f, 5.7f))
        {
            return SerpentRougeOutcome.Nothing;
        }

        map.EraseShape();
        map.Select(MapShape.None);
        map.Fix(new LaidShape(MapShape.Square, Centre, SquareSide / MathF.Sqrt(2f), SquareTurn, true, []));

        Done(story, scores, "Taurus", "e_sidney_map_taurus", "LockedSquare");
        UpdateState(story);

        return new SerpentRougeOutcome(true, Cues: [Say("02O7E2ZQB1")]);
    }

    private static SerpentRougeOutcome CheckLeo(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        if (!map.HasNear(Ermitage) || !map.HasNear(Tomb))
        {
            return SerpentRougeOutcome.Nothing;
        }

        map.TakeNear(Ermitage);
        map.TakeNear(Tomb);
        map.Remove(MapShape.Line);

        if (map.HasFixedNear(Tomb))
        {
            return new SerpentRougeOutcome(true, "MapLine3Note");
        }

        map.Fix(Segment(Ermitage, Tomb, [Ermitage, Tomb]));

        Done(story, scores, "Leo", "e_sidney_map_poussin", "PlacedTombLine");
        UpdateState(story);

        // "Wow, it intersects the meridian at the same spot as the sunrise line!"
        return new SerpentRougeOutcome(true, "MapLine3Note", Cues: [Say("02O3H2ZBY2")]);
    }

    private static SerpentRougeOutcome CheckVirgo(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        if (TempleCorners.Any(corner => !map.HasNear(corner)))
        {
            return SerpentRougeOutcome.Nothing;
        }

        foreach (Vector2 corner in TempleCorners)
        {
            map.TakeNear(corner);
        }

        map.Remove(MapShape.Square);
        map.Remove(MapShape.Line);

        if (map.HasFixedNear(TempleCorners[0]))
        {
            return new SerpentRougeOutcome(true, "MapRectNote");
        }

        // The four walls, as four settled lines.
        for (int i = 0; i < 4; i++)
        {
            Vector2 from = TempleCorners[i];
            Vector2 to = TempleCorners[(i + 1) % 4];

            map.Fix(Segment(from, to, [from, to]));
        }

        Done(story, scores, "Virgo", "e_sidney_map_virgo", "PlacedWalls");
        UpdateState(story);

        // "That matches Wilkes' seismic charts!" — and the third morning may be over.
        return new SerpentRougeOutcome(true, "MapRectNote", Cues: [Say("02O3H2ZKI2"), .. ForcedExit(story)]);
    }

    private static SerpentRougeOutcome CheckLibra(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        if (map.Working is not { Shape: MapShape.Hexagram } hexagram || !AtTheCircle(hexagram))
        {
            return SerpentRougeOutcome.Nothing;
        }

        if (!Aligned(hexagram.Turn, HexagramTurn, 60f, 2f))
        {
            return SerpentRougeOutcome.Nothing;
        }

        map.EraseShape();
        map.Select(MapShape.None);
        map.Fix(new LaidShape(MapShape.Hexagram, Centre, Radius, HexagramTurn, true, []));

        Done(story, scores, "Libra", "e_sidney_map_libra", "LockedHexagram");
        UpdateState(story);

        // The arms of the hexagram go on to the paper, which is a new paper.
        story.Inventory.Remove(story.Ego, "GRACE_COORDINATE_PAPER_1");
        story.Inventory.Add(story.Ego, "GRACE_COORDINATE_PAPER_2");

        return new SerpentRougeOutcome(true, Cues: [Say("02O1K2ZC73", 2), .. ForcedExit(story)]);
    }

    private static SerpentRougeOutcome CheckTempleDivisions(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        // Only once the Temple of Solomon's divisions have been read in the mail.
        if (!story.GetFlag("OpenedTempleDiagram") || story.GetFlag("PlacedTempleDivisions"))
        {
            return SerpentRougeOutcome.Nothing;
        }

        if (TempleDivisions.Any(point => !map.HasNear(point)))
        {
            return SerpentRougeOutcome.Nothing;
        }

        foreach (Vector2 point in TempleDivisions)
        {
            map.TakeNear(point);
        }

        map.Remove(MapShape.Line);

        map.Fix(Segment(TempleDivisions[0], TempleDivisions[1], [TempleDivisions[0], TempleDivisions[1]]));
        map.Fix(Segment(TempleDivisions[2], TempleDivisions[3], [TempleDivisions[2], TempleDivisions[3]]));

        story.SetFlag("PlacedTempleDivisions");
        Award(story, scores, "e_sidney_map_temple");

        // "That matches the temple diagram!" Progress towards Scorpio, not Scorpio.
        return new SerpentRougeOutcome(true, Cues: [Say("02O3H2ZR82")]);
    }

    private static SerpentRougeOutcome CheckSite(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        if (!map.HasNear(Site))
        {
            return SerpentRougeOutcome.Nothing;
        }

        map.TakeNear(Site);
        map.Fix(new LaidShape(MapShape.Line, Site, 0f, 0f, true, [Site]));

        Done(story, scores, "Scorpio", "e_sidney_map_scorpio", "MarkedTheSite");
        UpdateState(story);

        // The Site goes on to the paper too.
        story.Inventory.Remove(story.Ego, "GRACE_COORDINATE_PAPER_1");
        story.Inventory.Remove(story.Ego, "GRACE_COORDINATE_PAPER_2");
        story.Inventory.Add(story.Ego, "GRACE_COORDINATE_PAPER_3");

        // "That's it, that's the site, I'll write down the coordinates" — and the label
        // typed on to the map, which is the note here.
        return new SerpentRougeOutcome(true, "SiteText", Cues: [Say("02O8O2ZRA1", 2)]);
    }

    private static SerpentRougeOutcome CheckSagittarius(SidneyMap map, GameState story, ScoreEvents? scores)
    {
        if (!map.HasNear(SerpentTail) || !map.HasNear(SerpentHead))
        {
            return SerpentRougeOutcome.Nothing;
        }

        // Before Ophiuchus the places are allowed down and swept away again: "I don't
        // think I'm ready for that shape yet."
        if (!story.GetFlag("Ophiuchus"))
        {
            map.ClearPoints();

            return new SerpentRougeOutcome(true, Cues: [Say("02O0I27NG1")]);
        }

        List<Vector2> along = [SerpentTail];

        foreach (Vector2 point in SerpentAlong)
        {
            if (map.HasNear(point))
            {
                along.Add(point);
            }
        }

        along.Add(SerpentHead);

        map.ClearPoints();
        map.Remove(MapShape.Line);
        map.Fix(Segment(SerpentTail, SerpentHead, along));

        Done(story, scores, "Sagittarius", "e_sidney_map_saggittarius", "PlacedSerpent");
        UpdateState(story);

        return new SerpentRougeOutcome(true, "MapLine4Note", Cues: [Say("0273H2ZRS2")]);
    }

    // -------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------

    /// <summary>Whether a figure sits where the circle is, at the circle's size.</summary>
    private static bool AtTheCircle(LaidShape figure) =>
        Vector2.Distance(figure.At, Centre) < NearCentre && MathF.Abs(figure.Size - Radius) < NearSize;

    /// <summary>Whether a turn is one of the right ones, to within a tolerance.</summary>
    private static bool Aligned(float turn, float target, float period, float within)
    {
        float apart = MathF.Abs(((turn - target) % period + period) % period);

        return apart <= within || period - apart <= within;
    }

    /// <summary>A settled line drawn between two places, as a figure.</summary>
    private static LaidShape Segment(Vector2 from, Vector2 to, IReadOnlyList<Vector2> places)
    {
        Vector2 along = to - from;

        return new LaidShape(
            MapShape.Line,
            (from + to) / 2f,
            MathF.Max(along.Length() / 2f, 1f),
            MathF.Atan2(along.Y, along.X) * 180f / MathF.PI,
            true,
            places);
    }

    private static SidneySpeech Say(string plate, int lines = 1) =>
        new(SerpentRougeCue.Say, plate, lines);

    /// <summary>A sign done: its flag, its score, and the flag the scripts read alongside.</summary>
    private static void Done(GameState story, ScoreEvents? scores, string sign, string score, string also)
    {
        story.SetFlag(sign);
        story.SetFlag(also);
        Award(story, scores, score);
    }

    private static void Award(GameState story, ScoreEvents? scores, string score) =>
        story.AwardScore(score, scores?.Worth(score));

    /// <summary>
    /// Whether the third morning is over now, which is read the way the room's own
    /// <c>END_TIME_BLOCK</c> case reads it, and puts Sidney away so the room can end it.
    /// </summary>
    private static IEnumerable<SidneySpeech> ForcedExit(GameState story)
    {
        if (story.Timeblock == new Timeblock(3, 7, IsAfternoon: false) &&
            story.GetFlag("TempleFloorPlan") &&
            story.GetFlag("LockedHexagram") &&
            story.GetFlag("SavedArcadiaText") &&
            story.GetFlag("PlacedWalls") &&
            story.GetFlag("UseCoordLER") &&
            story.GetNounVerbCount("CLUE_NOTE_1", "PICKUP") > 0)
        {
            yield return new SidneySpeech(SerpentRougeCue.Close, string.Empty, 0);
        }
    }
}
