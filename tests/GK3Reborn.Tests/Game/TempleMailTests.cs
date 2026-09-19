using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the plan attached to Sidney's message about the Temple of Solomon.
/// </summary>
public sealed class TempleMailTests
{
    /// <summary>ESIDNEY.TXT's ten lines, as every release writes them.</summary>
    private const string Sidney = """
        [EMail Screen]
        SolomonFile1  = Temple of Solomon Layout
        SolomonFile2  = Holy of Holies
        SolomonFile3  = 30x30 cubits
        SolomonFile4  = (1/4 length of temple)
        SolomonFile5  = Sanctuary
        SolomonFile6  = 30x60 cubits
        SolomonFile7  = (middle 1/2 of temple length)
        SolomonFile8  = Porch
        SolomonFile9  = 30x30 cubits
        SolomonFile10 = (1/4 length of temple)
        """;

    [Fact]
    public void The_temple_message_carries_a_heading_and_three_chambers_of_three()
    {
        // The shape the plan is drawn from: line one names it, and the nine after it are
        // three chambers of name, size and share. All eight releases write exactly these
        // ten, so a count that is not ten means a table nobody has seen and the screen
        // falls back to listing the lines.
        IReadOnlyList<SidneyLibrary.MailLine> attached =
            SidneyLibrary.From(Sidney).Attachment("EMail4");

        Assert.Equal(10, attached.Count);
        Assert.Equal("Temple of Solomon Layout", attached[0].Text);
        Assert.Equal("Holy of Holies", attached[1].Text);
        Assert.Equal("Sanctuary", attached[4].Text);
        Assert.Equal("Porch", attached[7].Text);

        // And the shares the map actually wants: a quarter, a half, a quarter.
        Assert.Contains("1/4", attached[3].Text, StringComparison.Ordinal);
        Assert.Contains("1/2", attached[6].Text, StringComparison.Ordinal);
        Assert.Contains("1/4", attached[9].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_other_message_has_nothing_attached()
    {
        SidneyLibrary library = SidneyLibrary.From(Sidney);

        Assert.Empty(library.Attachment("EMail1"));
        Assert.Empty(library.Attachment("EMail3"));
    }
}
