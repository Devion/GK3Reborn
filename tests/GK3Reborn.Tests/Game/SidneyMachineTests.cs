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
        Assert.Equal(["FRENCH", "ENGLISH", "LATIN"], asked.Choices);
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
        Assert.Empty(sidney.Shapes);
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
        // it hands over.
        Assert.Equal([MapShape.Triangle, MapShape.Hexagram], sidney.Shapes);

        Opened(sidney, "MAP");

        Assert.Contains(SidneyAction.UseShape, sidney.Available());
        Assert.Contains(SidneyAction.EraseShape, sidney.Available());
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
