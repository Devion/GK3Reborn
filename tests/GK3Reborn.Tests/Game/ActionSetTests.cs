using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for which action files a scene brings into scope.
/// </summary>
/// <remarks>
/// The name of a file is the condition on it — <c>R25_23ALL.NVC</c> applies on days two
/// and three — so getting the grammar wrong does not fail loudly. It gives an object verbs
/// it should not have yet, or takes away the one the puzzle needed.
/// </remarks>
public sealed class ActionSetTests
{
    private static Timeblock At(string code)
    {
        Assert.True(Timeblock.TryParse(code, out Timeblock timeblock), code);
        return timeblock;
    }

    [Theory]
    [InlineData("R25_ALL.NVC", "110A", true)]
    [InlineData("R25_ALL.NVC", "312P", true)]
    [InlineData("R25_1ALL.NVC", "110A", true)]
    [InlineData("R25_1ALL.NVC", "202P", false)]
    [InlineData("R25_23ALL.NVC", "110A", false)]
    [InlineData("R25_23ALL.NVC", "202P", true)]
    [InlineData("R25_23ALL.NVC", "307A", true)]
    [InlineData("R25_12ALL.NVC", "202P", true)]
    [InlineData("R25_12ALL.NVC", "306P", false)]
    public void A_name_saying_ALL_covers_the_days_its_digits_name(
        string name, string timeblock, bool applies) =>
        Assert.Equal(applies, TimeblockRange.Applies(name, At(timeblock)));

    [Theory]
    [InlineData("R25202P.NVC", "202P", true)]
    [InlineData("R25202P.NVC", "205P", false)]
    [InlineData("R25202P.NVC", "302P", false)]
    public void A_name_ending_in_one_code_covers_that_timeblock_alone(
        string name, string timeblock, bool applies) =>
        Assert.Equal(applies, TimeblockRange.Applies(name, At(timeblock)));

    [Theory]
    [InlineData("110A", true)]
    [InlineData("112P", true)]
    [InlineData("102P", true)]
    [InlineData("104P", true)]
    [InlineData("107A", false)]
    [InlineData("105P", false)]
    [InlineData("210A", false)]
    public void A_name_ending_in_two_codes_covers_the_span_between_them(
        string timeblock, bool applies)
    {
        // The second code leaves its day off and borrows the first's, so this is day one
        // from ten in the morning until four in the afternoon and nothing on day two.
        Assert.Equal(applies, TimeblockRange.Applies("HAL110A04P", At(timeblock)));
    }

    [Theory]
    [InlineData("SHORT")]
    [InlineData("R25.NVC")]
    [InlineData("R25_XALL.NVC")]
    [InlineData("CH312P06P.NVC")]
    public void A_name_that_says_nothing_covers_nothing(string name)
    {
        // The last is real: CHU lists it, and no reading of the grammar finds a timeblock
        // in it. It is listed by a timeblock file, where the question is never asked, so
        // the original never notices either.
        Assert.False(TimeblockRange.TryParse(name, out _));
        Assert.False(TimeblockRange.Applies(name, At("312P")));
    }

    /// <summary>A scene defined by the two files' <c>[ACTIONS]</c> sections.</summary>
    private static SceneDefinition Scene(string general, string? specific = null) =>
        new(
            SceneInitFile.Parse($"[ACTIONS]\n{general}\n", "R25.SIF"),
            specific is null ? null : SceneInitFile.Parse($"[ACTIONS]\n{specific}\n", "R25202P.SIF"));

    [Fact]
    public void The_timeblock_files_own_sets_come_first_and_are_never_name_checked()
    {
        IReadOnlyList<string> names = ActionSets.For(
            Scene("r25_all.nvc\nr25_1all.nvc\nr25_23all.nvc", "anything.nvc"), At("202P"));

        // Its own file first, then the location's that apply; r25_1all is day one.
        Assert.Equal("anything.nvc", names[0]);
        Assert.Equal(["anything.nvc", "r25_all.nvc", "r25_23all.nvc"], names.Take(3));
        Assert.DoesNotContain("r25_1all.nvc", names);
    }

    [Fact]
    public void The_global_and_inventory_sets_are_in_scope_everywhere()
    {
        IReadOnlyList<string> names = ActionSets.For(Scene("r25_all.nvc"), At("202P"));

        Assert.Contains("GLB_ALL.NVC", names);
        Assert.Contains("GLB202P.NVC", names);
        Assert.Contains("INV_23ALL.NVC", names);

        // Named for other points in the story.
        Assert.DoesNotContain("GLB102P.NVC", names);
        Assert.DoesNotContain("INV_1ALL.NVC", names);
    }

    [Fact]
    public void The_locations_files_are_consulted_before_the_global_ones()
    {
        IReadOnlyList<string> names = ActionSets.For(Scene("r25_all.nvc"), At("202P"));

        Assert.True(
            names.ToList().IndexOf("r25_all.nvc") < names.ToList().IndexOf("GLB_ALL.NVC"),
            "a rule the location writes should win over the one the game writes for everywhere");
    }

    [Fact]
    public void With_no_point_in_the_story_every_one_of_the_locations_files_is_taken()
    {
        // The same union the loader falls back to elsewhere: nothing can be decided, so
        // nothing is ruled out. The global sets stay out, because choosing among them is
        // the only thing their names are for.
        IReadOnlyList<string> names = ActionSets.For(
            Scene("r25_all.nvc\nr25_1all.nvc\nr25_3all.nvc"), at: null);

        Assert.Equal(["r25_all.nvc", "r25_1all.nvc", "r25_3all.nvc"], names);
    }

    [Fact]
    public void A_file_named_twice_is_consulted_once()
    {
        IReadOnlyList<string> names = ActionSets.For(
            Scene("r25_all.nvc\nR25_ALL.NVC", "r25_all.nvc"), At("202P"));

        Assert.Single(names, n => n.Equals("r25_all.nvc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_files_a_scene_lists_are_read_off_its_own_sections()
    {
        SceneInitFile file = SceneInitFile.Parse(
            """
            [AMBIENT]
            R25SNDTRKL.STK

            [ACTIONS]
            r25_all.nvc
            r25_23all.nvc
            """,
            "R25.SIF");

        Assert.Equal(["r25_all.nvc", "r25_23all.nvc"], file.ActionFiles());
        Assert.Equal(["R25SNDTRKL.STK"], file.Soundtracks());
    }
}
