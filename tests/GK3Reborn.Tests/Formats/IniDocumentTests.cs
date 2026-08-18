using GK3Reborn.Formats.Ini;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for the INI dialect GK3's text assets use.
/// </summary>
/// <remarks>
/// The cases here are the ones that distinguish this dialect from ordinary INI. Each was
/// taken from a real asset, because every one of them is a way a naive parser silently
/// produces plausible-looking wrong data rather than failing.
/// </remarks>
public sealed class IniDocumentTests
{
    [Fact]
    public void Commas_inside_braces_belong_to_the_value()
    {
        IniDocument document = IniDocument.Parse(
            """
            [POSITIONS]
            START, pos={235.26, 2.72, 57.99}, heading=-16.04, camera=START
            """);

        IniLine line = Assert.Single(document.LinesOf("POSITIONS"));

        Assert.Equal("START", line.Head.Key);
        Assert.Equal(new System.Numerics.Vector3(235.26f, 2.72f, 57.99f), line.Vector("pos"));
        Assert.Equal(-16.04f, line.Number("heading"));
        Assert.Equal("START", line.Value("camera"));
    }

    [Fact]
    public void A_bare_keyword_becomes_a_flag()
    {
        IniDocument document = IniDocument.Parse(
            """
            [MODELS]
            model=luggage_, type=scene, hidden
            """);

        IniLine line = Assert.Single(document.LinesOf("MODELS"));

        Assert.True(line.HasFlag("hidden"));
        Assert.False(line.HasFlag("ego"));
        Assert.Equal("luggage_", line.Value("model"));
    }

    [Fact]
    public void Single_pair_lines_keep_bare_vectors_intact()
    {
        // Scene assets write vectors without braces. Splitting this line on commas would
        // leave Position as just its first component, which is how a scene ends up
        // reporting no lights at all rather than reporting an error.
        IniDocument document = IniDocument.Parse(
            """
            [Light_omni01]
            Position=45.533920,60.044212,22.611023
            """,
            multipleEntriesPerLine: false);

        IniLine line = Assert.Single(document.LinesOf("Light_omni01"));

        Assert.Equal(
            new System.Numerics.Vector3(45.53392f, 60.044212f, 22.611023f),
            line.Vector("Position"));
    }

    [Fact]
    public void The_same_line_split_the_other_way_loses_its_vector()
    {
        IniDocument document = IniDocument.Parse(
            """
            [Light_omni01]
            Position=45.533920,60.044212,22.611023
            """);

        Assert.Null(Assert.Single(document.LinesOf("Light_omni01")).Vector("Position"));
    }

    [Fact]
    public void Section_conditions_are_kept_verbatim()
    {
        IniDocument document = IniDocument.Parse(
            """
            [MODELS]
            model=always

            [MODELS={IsCurrentTime("202p") && GetEgoCurrentLocationCount() < 1}]
            model=sometimes
            """);

        Assert.Equal(2, document.Sections.Count);
        Assert.Null(document.Sections[0].Condition);

        Assert.Equal(
            "IsCurrentTime(\"202p\") && GetEgoCurrentLocationCount() < 1",
            document.Sections[1].Condition);

        Assert.Single(document.LinesOf("MODELS"));
        Assert.Equal(2, document.LinesOf("MODELS", includeConditional: true).Count());
    }

    [Fact]
    public void Line_and_block_comments_are_removed()
    {
        IniDocument document = IniDocument.Parse(
            """
            //
            // GEngine Scene File
            //
            BSP=r25 // trailing
            /* a block
               spanning lines */
            Version=0x202
            """,
            multipleEntriesPerLine: false);

        IniLine[] lines = document.Sections.SelectMany(s => s.Lines).ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Equal("r25", lines[0].Value("BSP"));
        Assert.Equal("0x202", lines[1].Value("Version"));
    }

    [Fact]
    public void Sections_can_repeat_and_are_kept_in_order()
    {
        IniDocument document = IniDocument.Parse(
            """
            [GENERAL]
            scene=r25_n

            [GENERAL={IsCurrentTime("110a")}]
            scene=r25_m
            """);

        Assert.Equal(2, document.Sections.Count);
        Assert.All(document.Sections, s => Assert.Equal("GENERAL", s.Name));
    }

    [Fact]
    public void Sections_can_be_found_by_prefix()
    {
        IniDocument document = IniDocument.Parse(
            """
            [Lights]
            omni01

            [Light_omni01]
            Type=0

            [Light_moon(key)]
            Type=1
            """,
            multipleEntriesPerLine: false);

        string[] names = document.SectionsStartingWith("Light_").Select(s => s.Name).ToArray();

        Assert.Equal(["Light_omni01", "Light_moon(key)"], names);
    }
}
