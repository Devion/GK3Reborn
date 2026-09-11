using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for Le Serpent Rouge worked out on Sidney's map, verse by verse, the way the
/// retail engine checks it off.
/// </summary>
public sealed class SerpentRougeAnalysisTests
{
    private const string Text = """
        [Analyze Screen]
        MapNoPrimitiveNote = Image is recognized as a MAP.
        MapIndeterminateNote = Analysis of points entered indeterminate.
        MapSeveralPossNote = Several linkage patterns available.
        MapLine1Note = A straight line marked between the two points intersects with meridian and point 'Arques'.
        MapLine2Note = Line tangential to circle.
        MapLine3Note = A straight line between the two intersects with meridian and line marked.
        MapLine4Note = Landmark feature connects points.
        MapRectNote = Points define 4-to-1 rectangle.
        MapCircleConfirmNote = Image confirmed. Coordinates at circle center are %s.
        MapEnterPointNote = Point entered at %s.
        MapShapeLockNote = Shape locked and confirmed.
        MapGridPointsNote = Connecting grid points.
        GridList = Grid List
        GridDispNote = A grid is already displayed.
        NoGridEraseNote = There is no grid to be erased.
        ShapeErasedNote = Shape erased.
        NoShapeNote = No shape is selected.
        EnterPointsNote = Enter points on the map
        CirclePointsNote = Select points to lock down feature.
        SiteTextTitle = ** ADD TEXT LABEL **
        SiteTextPrompt = ENTER TEXT:
        SiteText = The Site
        GeometryParch2 = Circle.
        GeometryPous = Hexagram.
        GeometryTenier2 = Square.
        Grid2 = 2x2
        Grid4 = 4x4
        Grid8 = 8x8
        """;

    private static SidneyMachine Machine(out GameState state, string timeblock = "307A")
    {
        state = new GameState { Ego = "GRACE", Location = "R25" };
        Assert.True(Timeblock.TryParse(timeblock, out Timeblock when));
        state.Timeblock = when;
        state.Inventory.Add("GRACE", "CHURCH_PAMPHLET");
        state.Inventory.Add("GRACE", "LSR");

        var sidney = new SidneyMachine(SidneyLibrary.From(Text), state);

        // The pictures that hand over the circle, the square and the hexagram.
        foreach (string item in new[] { "PARCHMENT_2", "TENIERS_POSTCARD_TEMP", "POUSSIN_POSTCARD" })
        {
            sidney.Scan(item);
            sidney.OpenFile(sidney.Files.First(f => f.Item == item));
            sidney.Perform(SidneyAction.ViewGeometry);
        }

        sidney.Scan("MAP");
        sidney.OpenFile(sidney.Files.First(f => f.Item == "MAP"));

        return sidney;
    }

    private static string Analyse(SidneyMachine sidney) => sidney.Perform(SidneyAction.Analyse).Text;

    private static List<string> Lines(SidneyMachine sidney)
    {
        List<string> said = [];

        while (sidney.TakeCue() is { } cue)
        {
            said.Add(cue.Kind == SerpentRougeCue.Say ? cue.Plate : cue.Kind + ":" + cue.Plate);
        }

        return said;
    }

    /// <summary>Everything up to and including one verse, by the same road the player takes.</summary>
    private static void SolveThrough(SidneyMachine sidney, GameState state, string sign)
    {
        int upTo = Array.IndexOf(SerpentRougeAnalysis.Signs, sign);

        for (int step = 0; step <= upTo; step++)
        {
            switch (step)
            {
                case 0:
                    sidney.Mark(SerpentRougeAnalysis.Church);
                    sidney.Mark(SerpentRougeAnalysis.Ruin);
                    Analyse(sidney);
                    break;
                case 1:
                    sidney.Mark(SerpentRougeAnalysis.Coustaussa);
                    sidney.Mark(SerpentRougeAnalysis.Bezu);
                    sidney.Mark(SerpentRougeAnalysis.Bugarach);
                    sidney.LayShape(MapShape.Circle);
                    break;
                case 2:
                    sidney.LayShape(MapShape.Square);
                    break;
                case 3:
                    sidney.Mark(SerpentRougeAnalysis.Serres);
                    sidney.Mark(SerpentRougeAnalysis.Meridian);
                    Analyse(sidney);
                    sidney.LayShape(MapShape.Square);
                    sidney.Perform(SidneyAction.RotateShape);
                    sidney.Perform(SidneyAction.RotateShape);
                    break;
                case 4:
                    sidney.RuleInShape = true;
                    sidney.Rule(8);
                    break;
                case 5:
                    break;
                case 6:
                    sidney.Mark(SerpentRougeAnalysis.Ermitage);
                    sidney.Mark(SerpentRougeAnalysis.Tomb);
                    Analyse(sidney);
                    break;
                case 7:
                    foreach (Vector2 corner in SerpentRougeAnalysis.TempleCorners)
                    {
                        sidney.Mark(corner);
                    }

                    Analyse(sidney);
                    break;
                case 8:
                    sidney.LayShape(MapShape.Hexagram);
                    sidney.Perform(SidneyAction.RotateShape);
                    sidney.Perform(SidneyAction.RotateShape);
                    break;
                case 9:
                    state.SetFlag("OpenedTempleDiagram");

                    foreach (Vector2 point in SerpentRougeAnalysis.TempleDivisions)
                    {
                        sidney.Mark(point);
                    }

                    Analyse(sidney);
                    sidney.Mark(SerpentRougeAnalysis.Site);
                    break;
                case 10:
                    state.SetFlag("Ophiuchus");
                    break;
                case 11:
                    sidney.Mark(SerpentRougeAnalysis.SerpentTail);
                    sidney.Mark(SerpentRougeAnalysis.SerpentAlong[1]);
                    sidney.Mark(SerpentRougeAnalysis.SerpentHead);
                    Analyse(sidney);
                    break;
                default:
                    break;
            }

            Lines(sidney);
        }
    }

    [Fact]
    public void An_empty_map_says_it_is_a_map_and_marks_alone_are_indeterminate()
    {
        SidneyMachine sidney = Machine(out _);

        Assert.Equal("Image is recognized as a MAP.", Analyse(sidney));

        sidney.Mark(new Vector2(200, 200));

        Assert.Equal("Analysis of points entered indeterminate.", Analyse(sidney));
        Assert.Empty(Lines(sidney));
    }

    /// <summary>
    /// Aquarius: the church and the ruin, and ANALYZE. The line is drawn to the edge, both
    /// places are settled, the flag and the count the scripts read are set, and Grace says so.
    /// </summary>
    [Fact]
    public void Aquarius_is_the_church_and_the_ruin_analysed()
    {
        SidneyMachine sidney = Machine(out GameState state);

        // Near enough for a click by eye, either way round.
        sidney.Mark(SerpentRougeAnalysis.Ruin + new Vector2(9, -7));
        sidney.Mark(SerpentRougeAnalysis.Church + new Vector2(-6, 8));

        string note = Analyse(sidney);

        Assert.Contains("Arques", note, StringComparison.Ordinal);
        Assert.True(state.GetFlag("Aquarius"));
        Assert.True(state.GetFlag("PlacedSunriseLine"));
        Assert.Equal(1, state.GetVariable("LSRState"));
        Assert.Equal(["02O3H2Z7F3"], Lines(sidney));

        LaidShape line = Assert.Single(sidney.Map.Laid);

        Assert.True(line.Fixed);
        Assert.Equal(MapShape.Line, line.Shape);
        Assert.Equal([SerpentRougeAnalysis.Church, SerpentRougeAnalysis.Ruin], line.Points);
        Assert.Empty(sidney.Map.Points);

        // Settled means settled: it cannot be erased, turned or picked up.
        sidney.Perform(SidneyAction.EraseShape);
        Assert.Single(sidney.Map.Laid);
        Assert.False(sidney.Map.MovePoint(0, 0, Vector2.Zero));
    }

    [Fact]
    public void Aquarius_needs_both_places_and_the_wrong_two_are_indeterminate()
    {
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Mark(SerpentRougeAnalysis.Church);
        sidney.Mark(new Vector2(900, 900));

        Assert.Equal("Analysis of points entered indeterminate.", Analyse(sidney));
        Assert.False(state.GetFlag("Aquarius"));
    }

    /// <summary>
    /// Pisces: the three villages and a circle through them, which the machine sees the
    /// moment the circle is laid. All three marked with no circle is "several linkages".
    /// </summary>
    [Fact]
    public void Pisces_is_a_circle_through_three_villages()
    {
        SidneyMachine sidney = Machine(out GameState state);
        SolveThrough(sidney, state, "Aquarius");

        sidney.Mark(SerpentRougeAnalysis.Coustaussa);
        sidney.Mark(SerpentRougeAnalysis.Bezu);
        sidney.Mark(SerpentRougeAnalysis.Bugarach);

        Assert.Equal("Several linkage patterns available.", Analyse(sidney));
        Assert.False(state.GetFlag("Pisces"));

        // The circle fitted through the three is the circle the answer wants.
        string note = sidney.LayShape(MapShape.Circle).Text;

        Assert.StartsWith("Image confirmed.", note, StringComparison.Ordinal);
        Assert.True(state.GetFlag("Pisces"));
        Assert.True(state.GetFlag("LockedCircle"));
        Assert.Equal(2, state.GetVariable("LSRState"));
        Assert.True(state.Inventory.Has("GRACE", "GRACE_COORDINATE_PAPER_1"));
        Assert.Equal(["02OAG2ZJU2"], Lines(sidney));

        LaidShape circle = sidney.Map.Laid.Single(l => l.Shape == MapShape.Circle);

        Assert.True(circle.Fixed);
        Assert.Equal(SerpentRougeAnalysis.Centre, circle.At);
        Assert.Equal(SerpentRougeAnalysis.Radius, circle.Size);
    }

    /// <summary>
    /// Aries: the square round the circle is right the moment it is laid, and cannot be
    /// erased until it has been turned right.
    /// </summary>
    [Fact]
    public void Aries_is_the_square_round_the_circle_which_then_cannot_be_erased()
    {
        SidneyMachine sidney = Machine(out GameState state);
        SolveThrough(sidney, state, "Pisces");

        sidney.LayShape(MapShape.Square);

        Assert.True(state.GetFlag("Aries"));
        Assert.True(state.GetFlag("SizedSquare"));
        Assert.Equal(["02O7E2ZIS1"], Lines(sidney));

        LaidShape square = sidney.Map.Working!;

        Assert.Equal(MapShape.Square, square.Shape);
        Assert.False(square.Fixed);
        Assert.Equal(SerpentRougeAnalysis.Centre, square.At);

        // "I think that's right — I don't want to erase it."
        sidney.Perform(SidneyAction.EraseShape);

        Assert.NotNull(sidney.Map.Working);
        Assert.Equal(["02O0I27LN1"], Lines(sidney));
    }

    /// <summary>
    /// Taurus: the meridian line down first, then the square turned to it. A fifteen degree
    /// step that sweeps past the right turn lands on it.
    /// </summary>
    [Fact]
    public void Taurus_is_the_meridian_line_and_the_square_turned_to_it()
    {
        SidneyMachine sidney = Machine(out GameState state);
        SolveThrough(sidney, state, "Aries");

        // Turning before the line is down turns and finds nothing.
        sidney.Perform(SidneyAction.RotateShape);
        Assert.False(state.GetFlag("Taurus"));
        Assert.Empty(Lines(sidney));

        sidney.Mark(SerpentRougeAnalysis.Serres);
        sidney.Mark(SerpentRougeAnalysis.Meridian);

        Assert.Equal("Line tangential to circle.", Analyse(sidney));
        Assert.True(state.GetFlag("PlacedMeridianLine"));
        Assert.Equal(["02O3H2ZQB2"], Lines(sidney));
        Assert.False(state.GetFlag("Taurus"));

        // Back to the square, and round: from sixty degrees a step passes 67.1 and lands there.
        sidney.LayShape(MapShape.Square);

        for (int i = 0; i < 6 && !state.GetFlag("Taurus"); i++)
        {
            sidney.Perform(SidneyAction.RotateShape);
        }

        Assert.True(state.GetFlag("Taurus"));
        Assert.True(state.GetFlag("LockedSquare"));
        Assert.Equal(4, state.GetVariable("LSRState"));
        Assert.Equal(["02O7E2ZQB1"], Lines(sidney));

        LaidShape square = sidney.Map.Laid.Single(l => l.Shape == MapShape.Square);

        Assert.True(square.Fixed);
        Assert.Equal(SerpentRougeAnalysis.SquareTurn, square.Turn, 0.01f);
        Assert.Null(sidney.Map.Working);
    }

    /// <summary>
    /// Gemini and Cancer: the eight by eight ruled inside the square, and the end of the
    /// second afternoon — Grace goes out to the hallway after her line. Any other grid is
    /// doubted, and a grid in the shape any other time is refused.
    /// </summary>
    [Fact]
    public void Gemini_and_Cancer_are_the_chessboard_and_the_way_out_to_the_hallway()
    {
        SidneyMachine sidney = Machine(out GameState state, "205P");

        // Not yet: "I don't think a grid will help me here", and nothing drawn.
        sidney.RuleInShape = true;
        sidney.Rule(8);

        Assert.Equal(0, sidney.Map.Grid);
        Assert.Equal(["02OD32ZNF1"], Lines(sidney));

        SolveThrough(sidney, state, "Taurus");

        // The wrong size is drawn and doubted.
        sidney.RuleInShape = true;
        sidney.Rule(4);

        Assert.Equal(4, sidney.Map.Grid);
        Assert.False(state.GetFlag("Gemini"));
        Assert.Equal(["02OD32ZGW1"], Lines(sidney));

        sidney.Perform(SidneyAction.EraseGrid);
        sidney.RuleInShape = true;
        sidney.Rule(8);

        Assert.True(state.GetFlag("Gemini"));
        Assert.True(state.GetFlag("Cancer"));
        Assert.True(state.GetFlag("PlacedGrid"));
        Assert.True(sidney.Map.GridFixed);
        Assert.Equal(6, state.GetVariable("LSRState"));
        Assert.Equal(["02OCL2ZJL1", "Leave:HAL"], Lines(sidney));

        // The chessboard stays.
        sidney.Perform(SidneyAction.EraseGrid);
        Assert.Equal(8, sidney.Map.Grid);
    }

    [Fact]
    public void A_grid_over_the_whole_map_is_drawn_and_doubted()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.RuleInShape = false;
        sidney.Rule(4);

        Assert.Equal(4, sidney.Map.Grid);
        Assert.Equal(["02O0I27GT1"], Lines(sidney));
    }

    /// <summary>
    /// Leo and Virgo: the line to the tomb and the temple's four walls, in either order,
    /// each on ANALYZE. Marking the same places again just takes them off.
    /// </summary>
    [Fact]
    public void Leo_and_Virgo_are_the_tomb_line_and_the_temple_walls()
    {
        SidneyMachine sidney = Machine(out GameState state);
        SolveThrough(sidney, state, "Cancer");

        foreach (Vector2 corner in SerpentRougeAnalysis.TempleCorners)
        {
            sidney.Mark(corner);
        }

        Assert.Equal("Points define 4-to-1 rectangle.", Analyse(sidney));
        Assert.True(state.GetFlag("Virgo"));
        Assert.True(state.GetFlag("PlacedWalls"));
        Assert.False(state.GetFlag("Leo"));
        Assert.Equal(4, sidney.Map.Fixed.Count(l => l.Shape == MapShape.Line) - 2);
        Assert.Equal(["02O3H2ZKI2"], Lines(sidney));

        sidney.Mark(SerpentRougeAnalysis.Ermitage);
        sidney.Mark(SerpentRougeAnalysis.Tomb);

        Assert.StartsWith("A straight line between the two", Analyse(sidney), StringComparison.Ordinal);
        Assert.True(state.GetFlag("Leo"));
        Assert.True(state.GetFlag("PlacedTombLine"));
        Assert.Equal(8, state.GetVariable("LSRState"));
        Assert.Equal(["02O3H2ZBY2"], Lines(sidney));
    }

    /// <summary>
    /// The meridian line may be put down during Aries and again during Taurus; the second
    /// time the places are taken off with the note and no word from Grace, as the original
    /// does, and the line is not drawn twice.
    /// </summary>
    [Fact]
    public void The_meridian_line_marked_twice_is_taken_off_quietly()
    {
        SidneyMachine sidney = Machine(out GameState state);
        SolveThrough(sidney, state, "Pisces");

        sidney.Mark(SerpentRougeAnalysis.Serres);
        sidney.Mark(SerpentRougeAnalysis.Meridian);

        Assert.Equal("Line tangential to circle.", Analyse(sidney));
        Assert.True(state.GetFlag("PlacedMeridianLine"));
        Assert.Equal(["02O3H2ZQB2"], Lines(sidney));

        sidney.Mark(SerpentRougeAnalysis.Serres);
        sidney.Mark(SerpentRougeAnalysis.Meridian);

        Assert.Equal("Line tangential to circle.", Analyse(sidney));
        Assert.Empty(sidney.Map.Points);
        Assert.Empty(Lines(sidney));
        Assert.Equal(1, sidney.Map.Fixed.Count(l => l.Points.Contains(SerpentRougeAnalysis.Serres)));
    }

    /// <summary>
    /// Libra: the hexagram inside the circle, turned to thirty three degrees, which a
    /// step lands on. The paper in the bag becomes the second paper.
    /// </summary>
    [Fact]
    public void Libra_is_the_hexagram_in_the_circle_turned_right()
    {
        SidneyMachine sidney = Machine(out GameState state);
        SolveThrough(sidney, state, "Virgo");

        Assert.False(state.GetFlag("LockedHexagram"));

        // Laid inside the circle, a point to the north, as the original starts it.
        sidney.LayShape(MapShape.Hexagram);

        LaidShape hexagram = sidney.Map.Working!;

        Assert.Equal(SerpentRougeAnalysis.Centre, hexagram.At);
        Assert.Equal(SerpentRougeAnalysis.Radius, hexagram.Size);
        Assert.False(state.GetFlag("Libra"));

        for (int i = 0; i < 4 && !state.GetFlag("Libra"); i++)
        {
            sidney.Perform(SidneyAction.RotateShape);
        }

        Assert.True(state.GetFlag("Libra"));
        Assert.True(state.GetFlag("LockedHexagram"));
        Assert.Equal(9, state.GetVariable("LSRState"));
        Assert.False(state.Inventory.Has("GRACE", "GRACE_COORDINATE_PAPER_1"));
        Assert.True(state.Inventory.Has("GRACE", "GRACE_COORDINATE_PAPER_2"));
        Assert.Equal(["02O1K2ZC73"], Lines(sidney));
        Assert.True(sidney.Map.Laid.Single(l => l.Shape == MapShape.Hexagram).Fixed);
    }

    /// <summary>
    /// A hexagram that fits six marks anywhere is confirmed as a figure but is not Libra:
    /// the story's flag is the verse's alone.
    /// </summary>
    [Fact]
    public void A_hexagram_anywhere_else_is_not_Libra()
    {
        SidneyMachine sidney = Machine(out GameState state);

        for (int i = 0; i < 6; i++)
        {
            float angle = i * 60 * MathF.PI / 180f;

            sidney.Mark(new Vector2(700 + (300 * MathF.Cos(angle)), 700 + (300 * MathF.Sin(angle))));
        }

        sidney.LayShape(MapShape.Hexagram);

        Assert.True(sidney.Map.Locked);
        Assert.True(state.GetFlag("SidneyShape:Hexagram"));
        Assert.False(state.GetFlag("LockedHexagram"));
    }

    /// <summary>
    /// Scorpio: the temple's divisions, only once the mail about them has been read, and
    /// then The Site, which answers the moment it is marked. The paper becomes the third.
    /// </summary>
    [Fact]
    public void Scorpio_is_the_temple_divisions_then_the_Site()
    {
        SidneyMachine sidney = Machine(out GameState state);
        SolveThrough(sidney, state, "Libra");

        foreach (Vector2 point in SerpentRougeAnalysis.TempleDivisions)
        {
            sidney.Mark(point);
        }

        // Not without the mail.
        Assert.Equal("Analysis of points entered indeterminate.", Analyse(sidney));
        Assert.False(state.GetFlag("PlacedTempleDivisions"));

        state.SetFlag("OpenedTempleDiagram");
        Analyse(sidney);

        Assert.True(state.GetFlag("PlacedTempleDivisions"));
        Assert.False(state.GetFlag("Scorpio"));
        Assert.Equal(["02O3H2ZR82"], Lines(sidney));

        // Elsewhere is nothing; The Site is the label typed on to the map.
        sidney.Mark(new Vector2(300, 300));
        Assert.False(state.GetFlag("Scorpio"));
        sidney.Perform(SidneyAction.ClearPoints);

        string note = sidney.Mark(SerpentRougeAnalysis.Site + new Vector2(5, -5)).Text;

        Assert.Contains("The Site", note, StringComparison.Ordinal);
        Assert.True(state.GetFlag("Scorpio"));
        Assert.True(state.GetFlag("MarkedTheSite"));
        Assert.True(sidney.ShowsSite);
        Assert.Equal(10, state.GetVariable("LSRState"));
        Assert.True(state.Inventory.Has("GRACE", "GRACE_COORDINATE_PAPER_3"));
        Assert.False(state.Inventory.Has("GRACE", "GRACE_COORDINATE_PAPER_2"));
        Assert.Equal(["02O8O2ZRA1"], Lines(sidney));
    }

    /// <summary>
    /// Sagittarius: the serpent's tail and head, and any of four places along it that were
    /// marked. Before Ophiuchus the places are swept away with a line instead.
    /// </summary>
    [Fact]
    public void Sagittarius_is_the_serpent_and_waits_on_Ophiuchus()
    {
        SidneyMachine sidney = Machine(out GameState state);
        SolveThrough(sidney, state, "Scorpio");

        sidney.Mark(SerpentRougeAnalysis.SerpentTail);
        sidney.Mark(SerpentRougeAnalysis.SerpentHead);
        Analyse(sidney);

        Assert.False(state.GetFlag("Sagittarius"));
        Assert.Empty(sidney.Map.Points);
        Assert.Equal(["02O0I27NG1"], Lines(sidney));

        state.SetFlag("Ophiuchus");
        sidney.Mark(SerpentRougeAnalysis.SerpentTail);
        sidney.Mark(SerpentRougeAnalysis.SerpentAlong[0]);
        sidney.Mark(SerpentRougeAnalysis.SerpentAlong[3]);
        sidney.Mark(SerpentRougeAnalysis.SerpentHead);

        Assert.Equal("Landmark feature connects points.", Analyse(sidney));
        Assert.True(state.GetFlag("Sagittarius"));
        Assert.True(state.GetFlag("PlacedSerpent"));
        Assert.True(sidney.ShowsSerpent);
        Assert.Equal(12, state.GetVariable("LSRState"));
        Assert.Equal(["0273H2ZRS2"], Lines(sidney));

        LaidShape serpent = sidney.Map.Fixed.Last();

        Assert.Equal(4, serpent.Points.Count);
    }

    /// <summary>
    /// The whole road, end to end, and the count the map and the scripts read at the end
    /// of it; then what a save keeps of it.
    /// </summary>
    [Fact]
    public void The_whole_poem_survives_a_save()
    {
        SidneyMachine sidney = Machine(out GameState state);
        SolveThrough(sidney, state, "Sagittarius");

        Assert.Equal(12, SerpentRougeAnalysis.Solved(state));
        Assert.Equal(12, DrivingMap.SerpentRougeSigns(state));

        var loaded = new GameState { Ego = "GRACE" };

        loaded.Restore(state.Capture("test"));

        var after = new SidneyMachine(SidneyLibrary.From(Text), loaded);

        Assert.Equal(sidney.Map.Laid.Count, after.Map.Laid.Count);
        Assert.All(after.Map.Fixed, laid => Assert.True(laid.Locked));
        Assert.Equal(sidney.Map.Fixed.Count(), after.Map.Fixed.Count());
        Assert.True(after.Map.GridFixed);
        Assert.Equal(8, after.Map.Grid);
        Assert.Equal(
            SerpentRougeAnalysis.SquareTurn,
            after.Map.Laid.Single(l => l.Shape == MapShape.Square).Turn,
            0.01f);
    }

    /// <summary>On the second afternoon nothing is plotted until Grace has the pamphlet and the poem.</summary>
    [Fact]
    public void Marking_waits_on_the_pamphlet_and_the_poem_on_the_second_afternoon()
    {
        SidneyMachine sidney = Machine(out GameState state, "205P");
        state.Inventory.Remove("GRACE", "LSR");

        sidney.Mark(SerpentRougeAnalysis.Church);

        Assert.Empty(sidney.Map.Points);
        Assert.Equal(["02O8O2ZPI1"], Lines(sidney));

        state.Inventory.Add("GRACE", "LSR");
        sidney.Mark(SerpentRougeAnalysis.Church);

        Assert.Single(sidney.Map.Points);
    }

    /// <summary>
    /// The third morning ends from the map when the last of its conditions is Virgo or
    /// Libra: Sidney is put away after the line, so the room can end it.
    /// </summary>
    [Fact]
    public void The_third_morning_can_end_from_the_map()
    {
        SidneyMachine sidney = Machine(out GameState state);
        SolveThrough(sidney, state, "Leo");

        state.SetFlag("TempleFloorPlan");
        state.SetFlag("SavedArcadiaText");
        state.SetFlag("UseCoordLER");
        state.SetNounVerbCount("CLUE_NOTE_1", "PICKUP", 1);

        foreach (Vector2 corner in SerpentRougeAnalysis.TempleCorners)
        {
            sidney.Mark(corner);
        }

        Analyse(sidney);

        // Not yet: the hexagram is still wanted.
        Assert.Equal(["02O3H2ZKI2"], Lines(sidney));

        sidney.LayShape(MapShape.Hexagram);

        for (int i = 0; i < 4 && !state.GetFlag("Libra"); i++)
        {
            sidney.Perform(SidneyAction.RotateShape);
        }

        Assert.Equal(["02O1K2ZC73", "Close:"], Lines(sidney));
    }

    /// <summary>
    /// The assist does one verse at a time by the same road, and says when the road is
    /// not the map's.
    /// </summary>
    [Fact]
    public void The_assist_does_the_next_verse_and_no_more()
    {
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Assist();
        sidney.Finish(yes: true);

        Assert.True(state.GetFlag("Aquarius"));
        Assert.False(state.GetFlag("Pisces"));

        sidney.Finish(yes: true);
        Assert.True(state.GetFlag("Pisces"));

        sidney.Finish(yes: true);
        Assert.True(state.GetFlag("Aries"));

        sidney.Finish(yes: true);
        Assert.True(state.GetFlag("Taurus"));

        sidney.Finish(yes: true);
        Assert.True(state.GetFlag("Gemini"));
        Assert.True(state.GetFlag("Cancer"));

        sidney.Finish(yes: true);
        Assert.True(state.GetFlag("Leo"));

        sidney.Finish(yes: true);
        Assert.True(state.GetFlag("Virgo"));

        sidney.Finish(yes: true);
        Assert.True(state.GetFlag("Libra"));

        // Scorpio waits on the mail, and says so.
        Assert.Equal("Nothing more can be done on the map just now.", sidney.Finish(yes: true).Text);
        Assert.False(state.GetFlag("PlacedTempleDivisions"));

        state.SetFlag("OpenedTempleDiagram");
        sidney.Finish(yes: true);
        Assert.True(state.GetFlag("PlacedTempleDivisions"));

        sidney.Finish(yes: true);
        Assert.True(state.GetFlag("Scorpio"));

        // Ophiuchus is the anagram, not the map.
        Assert.Equal("Nothing more can be done on the map just now.", sidney.Finish(yes: true).Text);

        state.SetFlag("Ophiuchus");
        sidney.Finish(yes: true);
        Assert.True(state.GetFlag("Sagittarius"));

        Assert.Equal(12, state.GetVariable("LSRState"));
    }
}
