using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for Sidney, running.
/// </summary>
/// <remarks>
/// The story runs through this machine, and the one thing that must be true is that
/// scanning a parchment makes <c>DoesSidneyFileExist("fileParchment1")</c> answer yes.
/// That condition is in <c>R31210A.NVC</c> and it was false for ever, because
/// <c>AddSidneyFile</c> had no caller at all.
/// </remarks>
public sealed class SidneyMachineTests
{
    private const string Text = """
        [Main Screen]
        MenuItem1   = ANALYZE
        MenuItem2   = ADD DATA
        MenuItem3   = E-MAIL
        MenuItem4   = EXIT
        NotImplemented = Not implemented yet.

        [Analyze Screen]
        AnalyzeParch1  = 1. Text appears to have irregularities in design.\n\nSee EXTRACT ANOMALIES.
        AnalyzeParch2  = 1. Text needs to be analyzed further.
        AnalyzeKPrint  = Recognized as a fingerprint.  Use on Suspects Screen.
        AnalyzeTape    = Recognized as an audio tape.  See TRANSLATE.
        AnalyzeTemp    = Analysis did not find any encoded references in the image.
        MapNoPrimitiveNote = Image is recognized as a MAP.
        ExtractParch1  = Letters are:\nadagobertiiroietasionestcetresoretilestlamort.\n\nSuggested language?
        Parch1French   = a dagobert ii roi et a sion est ce tresor et il est la mort.
        ParchEnglish   = Cannot decipher text breaks if language is English.
        ParchLatin     = Cannot decipher text breaks if language is Latin.
        GeometryParch1 = The shape has been saved.
        GeometryPous   = Second triangle forms hexagram shape.\n\nThe shape has been saved.
        ShapeList      = Shape List
        NoShapeNote    = No shape is selected.
        ShapeErasedNote  = Shape erased.
        MapShapeLockNote = Shape locked and confirmed.
        CirclePointsNote = Select points to lock down feature.
        MapIndeterminateNote = Analysis is indeterminate.
        MapEnterPointNote = Point at %s entered.
        MapLine1Note = A straight line marked between the two points intersects with meridian and point 'Arques'.
        MapLine2Note = Line tangential to circle and intersects with conjoining features on the meridian.
        MapLine3Note = A straight line between the two intersects with meridian and line marked.
        MapLineDisallow = Points can be joined by line, but no other features on map seem to suggest such a connection.
        Languages      = Languages:
        French         = FRENCH
        English        = ENGLISH
        Latin          = LATIN
        """;

    private static SidneyMachine Machine(out GameState state)
    {
        state = new GameState { Ego = "GRACE" };

        return new SidneyMachine(SidneyLibrary.From(Text), state);
    }

    [Fact]
    public void Scanning_a_parchment_is_what_makes_the_story_condition_true()
    {
        SidneyMachine sidney = Machine(out GameState state);

        Assert.False(state.HasSidneyFile("fileParchment1"));

        sidney.Scan("PARCHMENT_1");

        Assert.True(state.HasSidneyFile("fileParchment1"));
    }

    [Fact]
    public void Every_file_the_story_asks_about_by_name_can_be_produced()
    {
        // Eight names appear in the game's conditions and in its executable. A ninth would
        // be a condition that can never be true.
        foreach (string id in SidneyFiles.StoryFiles)
        {
            SidneyMachine sidney = Machine(out GameState state);

            foreach (string item in new[]
            {
                "PARCHMENT_1", "PARCHMENT_2", "MAP", "POUSSIN_POSTCARD",
                "TENIERS_POSTCARD_TEMP", "TENIERS_POSTCARD_NO_TEMP", "HERM_SYMBOLS", "I_AM_WORDS",
            })
            {
                sidney.Scan(item);
            }

            Assert.True(state.HasSidneyFile(id), $"nothing produces {id}");
        }
    }

    [Fact]
    public void The_scanner_refuses_what_it_is_not_for()
    {
        SidneyMachine sidney = Machine(out _);

        Assert.Null(sidney.Scan("TAPE_RECORDER"));
        Assert.False(sidney.CanScan("TAPE_RECORDER"));
        Assert.True(sidney.CanScan("PARCHMENT_1"));
    }

    [Fact]
    public void Nothing_is_scanned_twice()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("PARCHMENT_1");

        Assert.False(sidney.CanScan("PARCHMENT_1"));
        Assert.Single(sidney.Files);
    }

    [Fact]
    public void A_file_offers_the_operations_that_apply_to_it()
    {
        // The original left every menu item enabled and answered most of them with a note
        // about why not, which is making the player find the answer by exhaustion.
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("PARCHMENT_1");
        sidney.OpenFile(sidney.Files[0]);

        Assert.Contains(SidneyAction.ExtractAnomalies, sidney.Available());
        Assert.Contains(SidneyAction.ViewGeometry, sidney.Available());
        Assert.DoesNotContain(SidneyAction.RotateShape, sidney.Available());

        sidney.Scan("ABBE_TAPE");
        sidney.OpenFile(sidney.Files.First(f => f.Kind == SidneyKind.Tape));

        Assert.Contains(SidneyAction.Translate, sidney.Available());
        Assert.DoesNotContain(SidneyAction.ViewGeometry, sidney.Available());
    }

    [Fact]
    public void An_analysis_says_what_the_games_own_text_says()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("PARCHMENT_1");
        sidney.OpenFile(sidney.Files[0]);

        Assert.StartsWith(
            "1. Text appears to have irregularities",
            sidney.Perform(SidneyAction.Analyse).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_fingerprint_and_a_map_are_recognised_for_what_they_are()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("ABBE_FINGERPRINT");
        sidney.OpenFile(sidney.Files.First(f => f.Kind == SidneyKind.KnownPrint));
        Assert.Contains("fingerprint", sidney.Perform(SidneyAction.Analyse).Text, StringComparison.Ordinal);

        sidney.Scan("MAP");
        sidney.OpenFile(sidney.Files.First(f => f.Kind == SidneyKind.Map));
        Assert.Contains("MAP", sidney.Perform(SidneyAction.Analyse).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extracting_the_anomalies_ends_by_asking_which_language()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("PARCHMENT_1");
        sidney.OpenFile(sidney.Files[0]);

        SidneyResult asked = sidney.Perform(SidneyAction.ExtractAnomalies);

        Assert.Equal("Languages:", asked.Asks);
        Assert.Equal(
            ["FRENCH", "ENGLISH", "LATIN"],
            asked.Choices?.Select(choice => choice.Text));

        // Keyed on what the file calls each language rather than on what it says, because
        // every release relabels them and none of them renames the key.
        Assert.Equal(
            ["French", "English", "Latin"],
            asked.Choices?.Select(choice => choice.Key));
    }

    [Fact]
    public void The_language_the_player_suggests_decides_what_comes_back()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("PARCHMENT_1");
        sidney.OpenFile(sidney.Files[0]);
        sidney.Perform(SidneyAction.ExtractAnomalies);

        Assert.StartsWith("a dagobert", sidney.Answer("FRENCH").Text, StringComparison.Ordinal);
        Assert.Contains("English", sidney.Answer("ENGLISH").Text, StringComparison.Ordinal);
        Assert.Contains("Latin", sidney.Answer("LATIN").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void What_has_been_done_survives_a_save()
    {
        // Recorded on the story rather than kept in the machine, because what Sidney has
        // been asked to do is part of the game.
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Scan("PARCHMENT_1");
        sidney.OpenFile(sidney.Files[0]);
        sidney.Perform(SidneyAction.ViewGeometry);

        var reloaded = new GameState();
        reloaded.Restore(state.Capture());

        var after = new SidneyMachine(SidneyLibrary.From(Text), reloaded);

        Assert.Single(after.Files);
        Assert.True(after.HasDone(after.Files[0], SidneyAction.ViewGeometry));
        Assert.False(after.HasDone(after.Files[0], SidneyAction.ExtractAnomalies));
    }

    [Fact]
    public void An_operation_with_nothing_open_says_so_rather_than_throwing()
    {
        SidneyMachine sidney = Machine(out _);

        Assert.NotNull(sidney.Perform(SidneyAction.Analyse));
        Assert.Empty(sidney.Available());
    }

    [Fact]
    public void The_machine_goes_home()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Screen = SidneyScreen.Analyze;
        sidney.Scan("MAP");
        sidney.OpenFile(sidney.Files[0]);
        sidney.Perform(SidneyAction.Analyse);

        sidney.Home();

        Assert.Equal(SidneyScreen.Main, sidney.Screen);
        Assert.Null(sidney.Showing);
    }

    /// <summary>Opens a scanned file on the analyze screen.</summary>
    private static SidneyFile Opened(SidneyMachine sidney, string item)
    {
        sidney.Scan(item);

        SidneyFile file = sidney.Files.First(f => f.Item == item);

        sidney.OpenFile(file);

        return file;
    }

    [Fact]
    public void The_map_offers_no_shape_until_a_picture_has_given_one_up()
    {
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "MAP");

        // Nothing has been analysed, so there is nothing to lay: the shape items are not
        // offered, and asking for one anyway is refused rather than silently obeyed.
        // Only the line, which is always offered: no picture has given a figure up yet.
        Assert.Equal([MapShape.Line], sidney.Shapes);
        Assert.DoesNotContain(SidneyAction.UseShape, sidney.Available());
        Assert.Equal("No shape is selected.", sidney.LayShape(MapShape.Hexagram).Text);
    }

    [Fact]
    public void Viewing_a_paintings_geometry_is_what_earns_the_shapes_it_names()
    {
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "POUSSIN_POSTCARD");
        sidney.Perform(SidneyAction.ViewGeometry);

        // "Second triangle forms hexagram shape", says the analysis, and those are the two
        // it hands over. The line is always there: it is the tool the puzzle opens with and
        // nothing grants it.
        Assert.Equal([MapShape.Line, MapShape.Triangle, MapShape.Hexagram], sidney.Shapes);

        Opened(sidney, "MAP");

        // The figures themselves are offered beside the map rather than behind a USE SHAPE
        // button, so what the map screen adds is the two operations that act on one already
        // drawn — and neither means anything until one is.
        Assert.DoesNotContain(SidneyAction.RotateShape, sidney.Available());
        Assert.DoesNotContain(SidneyAction.EraseShape, sidney.Available());

        // Choosing a figure says what the places about to be marked are for; the figure is
        // drawn once it has them.
        sidney.LayShape(MapShape.Triangle);

        Assert.Equal(MapShape.Triangle, sidney.Map.Selected);
        Assert.DoesNotContain(SidneyAction.RotateShape, sidney.Available());

        sidney.Mark(new Vector2(500, 400));
        sidney.Mark(new Vector2(900, 500));
        sidney.Mark(new Vector2(700, 900));

        Assert.Contains(SidneyAction.RotateShape, sidney.Available());
        Assert.Contains(SidneyAction.EraseShape, sidney.Available());
    }

    [Fact]
    public void A_finding_is_recorded_under_the_name_the_game_asks_about()
    {
        // The action files ask GetFlag("AnalyzedGeomParchment1") and GetFlag("LockedHexagram");
        // the machine was setting SidneyDid:fileParchment1:ViewGeometry and
        // SidneyShape:Hexagram, which nothing in the game has ever heard of. Every such
        // condition answered no — and R25307A will not end its timeblock without the second
        // of them.
        SidneyMachine sidney = Machine(out GameState state);

        Opened(sidney, "PARCHMENT_1");
        sidney.Perform(SidneyAction.ViewGeometry);

        Assert.True(state.GetFlag("AnalyzedGeomParchment1"));

        Opened(sidney, "PARCHMENT_2");
        sidney.Perform(SidneyAction.ViewGeometry);

        Assert.True(state.GetFlag("AnalyzedGeomParchment2"));

        // filePainting1 is the Poussin, which is how the game numbers it.
        Opened(sidney, "POUSSIN_POSTCARD");
        sidney.Perform(SidneyAction.ViewGeometry);

        Assert.True(state.GetFlag("AnalyzedGeomPainting1"));

        // Only viewing geometry is asked about; the rest is the machine's own bookkeeping.
        Assert.False(state.GetFlag("AnalyzedGeomParchment1:Analyse"));
    }

    [Fact]
    public void A_figure_that_locks_sets_the_flag_the_timeblock_waits_on()
    {
        SidneyMachine sidney = Machine(out GameState state);

        Opened(sidney, "POUSSIN_POSTCARD");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        for (int i = 0; i < 6; i++)
        {
            float angle = i * 60 * MathF.PI / 180f;

            sidney.Mark(new Vector2(
                700 + (300 * MathF.Cos(angle)), 700 + (300 * MathF.Sin(angle))));
        }

        sidney.LayShape(MapShape.Hexagram);

        Assert.True(sidney.Map.Locked);
        Assert.True(state.GetFlag("LockedHexagram"));
    }

    [Fact]
    public void A_circle_is_fitted_to_every_marked_place_not_the_first_three()
    {
        // Reported: five places marked, and the circle sailed off the top of the map
        // through three of them ignoring the two at the bottom. The three it took were
        // whichever had been clicked first.
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "PARCHMENT_2");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        // Eight places on one circle of radius 300 about (700, 700), so the fit has a right
        // answer to find and any three of them would find a different one.
        for (int i = 0; i < 8; i++)
        {
            float angle = i * 45 * MathF.PI / 180f;

            sidney.Mark(new Vector2(
                700 + (300 * MathF.Cos(angle)), 700 + (300 * MathF.Sin(angle))));
        }

        sidney.LayShape(MapShape.Circle);

        Assert.Equal(700f, sidney.Map.ShapeAt.X, 1f);
        Assert.Equal(700f, sidney.Map.ShapeAt.Y, 1f);
        Assert.Equal(300f, sidney.Map.ShapeSize, 1f);
        Assert.True(sidney.Map.Locked);
    }

    [Fact]
    public void Places_in_a_line_are_refused_a_circle_rather_than_given_an_enormous_one()
    {
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "PARCHMENT_2");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        sidney.Mark(new Vector2(400, 400));
        sidney.Mark(new Vector2(700, 410));
        sidney.Mark(new Vector2(1000, 420));
        sidney.LayShape(MapShape.Circle);

        // Not the circle whose centre is off in the next country: the ordinary fit, which
        // stays on the map the player is looking at.
        Assert.InRange(sidney.Map.ShapeSize, 1f, SidneyMap.Extent);
        Assert.InRange(sidney.Map.ShapeAt.X, 0f, SidneyMap.Extent);
        Assert.InRange(sidney.Map.ShapeAt.Y, 0f, SidneyMap.Extent);
    }


    [Fact]
    public void The_sunrise_line_is_the_church_and_the_ruin_not_a_coincidence_of_geometry()
    {
        // "A straight line marked between the two points intersects with meridian and point
        // 'Arques'" is what the game says about the line from the church at
        // Rennes-le-Château over the ruin at Blanchefort. Testing that by geometry refuses
        // the right answer: on this survey the line misses Arques by a hundred and twelve
        // pixels, because the map is drawn rather than surveyed. What the note is about is
        // which two places were picked.
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "MAP");
        sidney.Mark(SidneyMap.Church);
        sidney.Mark(SidneyMap.Blanchefort);

        Assert.Contains("Arques", sidney.Showing!.Text, StringComparison.Ordinal);

        // Either way round, and near enough for a click by eye.
        sidney.Perform(SidneyAction.ClearPoints);
        sidney.Mark(SidneyMap.Blanchefort + new Vector2(15, -12));
        sidney.Mark(SidneyMap.Church + new Vector2(-9, 14));

        Assert.Contains("Arques", sidney.Showing!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_assist_asks_before_it_draws_anything()
    {
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "PARCHMENT_2");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        SidneyResult asked = sidney.Assist();

        Assert.NotNull(asked.Asks);
        Assert.Equal(2, asked.Choices!.Count);
        Assert.Empty(sidney.Map.Laid);

        // Said no, and nothing happened.
        sidney.Finish(yes: false);

        Assert.Empty(sidney.Map.Laid);
    }

    [Fact]
    public void The_assist_finishes_the_map_with_the_places_the_survey_marks()
    {
        // A player can be genuinely stuck in front of this, with a timeblock that will not
        // end until the map is done. Being stuck for good is worse than being told.
        SidneyMachine sidney = Machine(out GameState state);

        Opened(sidney, "PARCHMENT_2");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        sidney.Assist();
        sidney.Finish(yes: true);

        // The sunrise line, the circle through the four crosses, and the square round it.
        Assert.Equal(
            [MapShape.Line, MapShape.Circle, MapShape.Square],
            sidney.Map.Laid.Select(laid => laid.Shape));

        Assert.All(sidney.Map.Laid, laid => Assert.True(laid.Locked));

        // The chessboard is ruled inside the square, as the Gemini passage asks.
        Assert.Equal(8, sidney.Map.Grid);
        Assert.True(sidney.Map.GridInShape);

        // And the flags the story reads are set by the ordinary path, not written directly.
        Assert.True(state.GetFlag("LockedCircle"));
        Assert.True(state.GetFlag("LockedSquare"));
    }

    [Fact]
    public void The_assist_draws_only_the_figures_that_have_been_earned()
    {
        // A figure is offered by a picture the player has analysed. One they have not earned
        // is one the machine has no business knowing about.
        SidneyMachine sidney = Machine(out GameState state);

        Opened(sidney, "MAP");
        sidney.Assist();
        sidney.Finish(yes: true);

        // Only the line, which is always offered because nothing grants it.
        Assert.Equal([MapShape.Line], sidney.Map.Laid.Select(laid => laid.Shape));
        Assert.False(state.GetFlag("LockedSquare"));
    }

    [Fact]
    public void Two_places_that_reach_nothing_are_told_so()
    {
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "MAP");
        sidney.Mark(new Vector2(120, 1200));
        sidney.Mark(new Vector2(300, 1260));

        Assert.DoesNotContain("Arques", sidney.Showing!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_marked_place_can_be_picked_up_and_put_down_somewhere_else()
    {
        // The original can only clear every place and start again, which for a puzzle played
        // by clicking villages on a photograph is a lot to lose to one stray pixel.
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "PARCHMENT_2");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        for (int i = 0; i < 4; i++)
        {
            float angle = i * 90 * MathF.PI / 180f;

            sidney.Mark(new Vector2(
                700 + (300 * MathF.Cos(angle)), 700 + (300 * MathF.Sin(angle))));
        }

        // Marked first and named after, which is one of the two ways round that work.
        sidney.LayShape(MapShape.Circle);

        Assert.True(sidney.Map.Locked);

        // The four places became the circle's when it was named, so it is the circle's
        // second place that is picked up — and moving it re-fits that circle and nothing
        // else.
        sidney.StartDrag(0, 2);
        sidney.DragTo(new Vector2(120, 120));

        Assert.Equal(new Vector2(120, 120), sidney.Map.Laid[0].Points[2]);

        // Dragged off the circle, so the confirmation it had cannot survive being put down.
        sidney.EndDrag();

        Assert.Equal(-1, sidney.Dragging);
        Assert.False(sidney.Map.Locked);

        // And back again, which re-earns it.
        sidney.StartDrag(0, 2);
        sidney.DragTo(new Vector2(400, 700));
        sidney.EndDrag();

        Assert.True(sidney.Map.Locked);
    }

    [Fact]
    public void A_place_cannot_be_dragged_off_the_map()
    {
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "MAP");
        sidney.Mark(new Vector2(700, 700));
        sidney.StartDrag(-1, 0);
        sidney.DragTo(new Vector2(-500, 90000));
        sidney.EndDrag();

        Assert.InRange(sidney.Map.Points[0].X, 0, SidneyMap.Extent);
        Assert.InRange(sidney.Map.Points[0].Y, 0, SidneyMap.Extent);
    }



    [Fact]
    public void A_grid_can_be_ruled_inside_the_figure_and_a_save_keeps_which()
    {
        // The chessboard the Gemini and Cancer passages are about is eight by eight ruled
        // inside the tilted square. A grid that can only cover the whole map cannot draw it,
        // and ESIDNEY.TXT offers "Fill shape" against "Fill entire screen" for exactly this.
        SidneyMachine sidney = Machine(out GameState state);

        Opened(sidney, "PARCHMENT_2");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        sidney.Mark(new Vector2(430, 430));
        sidney.Mark(new Vector2(900, 400));
        sidney.Mark(new Vector2(930, 930));
        sidney.Mark(new Vector2(450, 900));
        sidney.LayShape(MapShape.Square);

        sidney.RuleInShape = true;
        sidney.Rule(8);

        Assert.Equal(8, sidney.Map.Grid);
        Assert.True(sidney.Map.GridInShape);

        var loaded = new GameState { Ego = "GRACE" };

        loaded.Restore(state.Capture("test"));

        var after = new SidneyMachine(SidneyLibrary.From(Text), loaded);

        Assert.Equal(8, after.Map.Grid);
        Assert.True(after.Map.GridInShape);
    }

    [Fact]
    public void What_is_on_the_map_survives_a_save()
    {
        // The map puzzle runs over several sittings: mark a village, go and read a
        // painting's geometry, come back and lay the figure it saved. A map that forgot
        // itself when the game was saved would make the whole of it one sitting long.
        SidneyMachine sidney = Machine(out GameState state);

        Opened(sidney, "POUSSIN_POSTCARD");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        for (int i = 0; i < 6; i++)
        {
            float angle = i * 60 * MathF.PI / 180f;

            sidney.Mark(new Vector2(
                700 + (300 * MathF.Cos(angle)), 700 + (300 * MathF.Sin(angle))));
        }

        // DRAW GRID offers the sizes the game lists rather than choosing one; Rule is what
        // picking 8x8 off that list does.
        sidney.Perform(SidneyAction.DrawGrid);

        Assert.True(sidney.Ruling);

        sidney.Rule(8);
        sidney.LayShape(MapShape.Hexagram);

        // The six places already marked became the hexagram's own when it was chosen.
        Assert.Equal(6, sidney.Map.Laid[0].Points.Count);

        // Choosing another figure leaves the first alone, and the new one starts empty
        // because the six belong to the hexagram now.
        sidney.LayShape(MapShape.Triangle);
        sidney.Mark(new Vector2(200, 200));
        sidney.Mark(new Vector2(400, 200));
        sidney.Mark(new Vector2(300, 400));

        Assert.Equal(2, sidney.Map.Laid.Count);

        SaveGame saved = state.Capture("test");

        // A fresh game and a fresh machine, as loading a save gives you.
        var loaded = new GameState { Ego = "GRACE" };

        loaded.Restore(saved);

        var after = new SidneyMachine(SidneyLibrary.From(Text), loaded);

        Assert.Equal(6, after.Map.Laid[0].Points.Count);
        Assert.Equal(8, after.Map.Grid);
        Assert.Equal(
            [MapShape.Hexagram, MapShape.Triangle],
            after.Map.Laid.Select(laid => laid.Shape));

        // And whether a figure is confirmed is worked out again from the marks it was
        // restored beside, rather than taken on trust from the save.
        Assert.True(after.Map.Laid[0].Locked);
    }

    [Fact]
    public void Choosing_a_figure_that_is_already_drawn_picks_it_up_rather_than_erasing_it()
    {
        // Pressing the same button again used to take the figure off and its places with
        // it, which is a lot to lose to a stray click on the step that took longest.
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "POUSSIN_POSTCARD");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        sidney.LayShape(MapShape.Triangle);
        sidney.Mark(new Vector2(500, 400));
        sidney.Mark(new Vector2(900, 500));
        sidney.Mark(new Vector2(700, 900));

        Assert.Single(sidney.Map.Laid);

        // Again: still there, and its places are back in hand to be adjusted.
        sidney.LayShape(MapShape.Triangle);

        Assert.Single(sidney.Map.Laid);
        Assert.Equal(3, sidney.Map.Points.Count);

        // Erasing is what takes one off, and it is its own deliberate act.
        sidney.Perform(SidneyAction.EraseShape);

        Assert.Empty(sidney.Map.Laid);
    }

    [Fact]
    public void A_figure_takes_no_more_places_than_its_answer_is_made_of()
    {
        // A circle through four villages is four places. Letting the player put eleven on
        // the map and then wonder why nothing confirms is a puzzle made of arithmetic they
        // cannot see.
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "PARCHMENT_2");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");
        sidney.LayShape(MapShape.Circle);

        for (int i = 0; i < 4; i++)
        {
            float angle = i * 90 * MathF.PI / 180f;

            sidney.Mark(new Vector2(
                700 + (300 * MathF.Cos(angle)), 700 + (300 * MathF.Sin(angle))));
        }

        Assert.Equal(4, sidney.Map.Points.Count);
        Assert.True(sidney.Map.Complete);

        // A fifth is refused rather than quietly throwing the fit off.
        sidney.Mark(new Vector2(100, 100));

        Assert.Equal(4, sidney.Map.Points.Count);
    }

    [Fact]
    public void More_than_one_figure_can_be_laid_over_the_country_at_once()
    {
        // What the books this game is built on actually do: a circle over a square, read
        // off where the lines cross. One figure at a time made the player remember the
        // last one.
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "POUSSIN_POSTCARD");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        sidney.Mark(new Vector2(400, 400));
        sidney.Mark(new Vector2(900, 900));

        sidney.LayShape(MapShape.Triangle);
        sidney.LayShape(MapShape.Hexagram);

        Assert.Equal(
            [MapShape.Triangle, MapShape.Hexagram],
            sidney.Map.Laid.Select(laid => laid.Shape));

        // And choosing one that is already there re-fits it rather than stacking a second.
        sidney.LayShape(MapShape.Hexagram);
        sidney.LayShape(MapShape.Hexagram);

        Assert.Equal(2, sidney.Map.Laid.Count);
    }

    [Fact]
    public void A_shape_the_marked_places_confirm_sets_a_flag_the_story_can_read()
    {
        SidneyMachine sidney = Machine(out GameState state);

        Opened(sidney, "POUSSIN_POSTCARD");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        // Six places on one circle, sixty degrees apart: a hexagram, exactly.
        for (int i = 0; i < 6; i++)
        {
            float angle = i * 60 * MathF.PI / 180f;

            sidney.Mark(new Vector2(
                700 + (300 * MathF.Cos(angle)), 700 + (300 * MathF.Sin(angle))));
        }

        Assert.Equal("Shape locked and confirmed.", sidney.LayShape(MapShape.Hexagram).Text);
        Assert.True(state.GetFlag("SidneyShape:Hexagram"));

        // And taking it off says so, and leaves nothing laid.
        Assert.Equal("Shape erased.", sidney.Perform(SidneyAction.EraseShape).Text);
        Assert.Equal(MapShape.None, sidney.Map.Shape);
    }

    [Fact]
    public void Rotating_turns_the_template_on_the_map_and_reads_the_parchment_elsewhere()
    {
        SidneyMachine sidney = Machine(out _);

        Opened(sidney, "POUSSIN_POSTCARD");
        sidney.Perform(SidneyAction.ViewGeometry);
        Opened(sidney, "MAP");

        // Nothing laid yet, so there is nothing to turn.
        Assert.Equal("No shape is selected.", sidney.Perform(SidneyAction.RotateShape).Text);

        sidney.Mark(new Vector2(400, 400));
        sidney.LayShape(MapShape.Triangle);

        float before = sidney.Map.ShapeTurn;

        sidney.Perform(SidneyAction.RotateShape);

        Assert.NotEqual(before, sidney.Map.ShapeTurn, 3);
    }
}
