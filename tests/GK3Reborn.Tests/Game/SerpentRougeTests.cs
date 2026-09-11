using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for Le Serpent Rouge, read a page at a time.
/// </summary>
public sealed class SerpentRougeTests
{
    [Fact]
    public void The_poem_and_its_verses_are_the_reader_and_nothing_else_is()
    {
        Assert.True(SerpentRouge.IsReader("LSR"));
        Assert.True(SerpentRouge.IsReader("lsr_aquarius"));
        Assert.False(SerpentRouge.IsReader("LSR_ENVELOPE_INV"));
        Assert.False(SerpentRouge.IsReader(null));

        Assert.Equal(13, SerpentRouge.Verses.Count);
        Assert.Equal("Capricorn", SerpentRouge.VerseOf("LSR_CAPRICORN")!.Flag);
        Assert.Null(SerpentRouge.VerseOf("LSR"));
    }

    /// <summary>The book opens at the page of the verse in hand, as the original's does.</summary>
    [Fact]
    public void The_book_opens_at_the_verse_in_hand()
    {
        var story = new GameState();

        Assert.Equal(1, SerpentRouge.Opens(story));

        story.SetFlag("Aquarius");
        Assert.Equal(1, SerpentRouge.Opens(story));

        story.SetFlag("Pisces");
        Assert.Equal(2, SerpentRouge.Opens(story));

        story.SetFlag("Taurus");
        Assert.Equal(3, SerpentRouge.Opens(story));

        story.SetFlag("Ophiuchus");
        Assert.Equal(6, SerpentRouge.Opens(story));
    }

    /// <summary>
    /// The first verse is lit from the start; the next lights once it is done; a verse done
    /// is struck through; a verse not reached is the page as printed, but still there.
    /// </summary>
    [Fact]
    public void A_verse_is_lit_in_its_turn_and_struck_through_when_done()
    {
        var story = new GameState();

        SerpentRougePage first = SerpentRouge.Show(story, 1);

        Assert.Equal("LSR_PG1_BASE.BMP", first.Picture);
        Assert.Equal(["LSR_AQUARIUS", "LSR_PISCES"], first.Regions.Select(r => r.Verse.Noun));
        Assert.Equal("LSR_PG1_AQU_LIT.BMP", first.Regions[0].Picture);
        Assert.Null(first.Regions[1].Picture);

        story.SetFlag("Aquarius");
        first = SerpentRouge.Show(story, 1);

        Assert.Equal("LSR_PG1_AQU_FIN.BMP", first.Regions[0].Picture);
        Assert.Equal("LSR_PG1_PIS_LIT.BMP", first.Regions[1].Picture);

        SerpentRougePage third = SerpentRouge.Show(story, 3);

        Assert.Equal(3, third.Regions.Count);
        Assert.All(third.Regions, r => Assert.Null(r.Picture));
    }

    /// <summary>Turning stays inside the book, and closing it forgets the page.</summary>
    [Fact]
    public void Turning_stays_inside_the_book_and_closing_forgets_the_page()
    {
        var story = new GameState();

        Assert.Equal(1, SerpentRouge.Page(story));
        Assert.Equal(1, SerpentRouge.Turn(story, -1));
        Assert.Equal(3, SerpentRouge.Turn(story, 2));
        Assert.Equal(3, SerpentRouge.Page(story));
        Assert.Equal(6, SerpentRouge.Turn(story, 9));

        SerpentRouge.Close(story);

        Assert.Equal(1, SerpentRouge.Page(story));
    }

    /// <summary>
    /// The retail engine anchors a verse from the foot of the page; the port measures from
    /// the top. Aquarius sits at the head of page one and Pisces below it.
    /// </summary>
    [Fact]
    public void A_verse_is_placed_from_the_top_of_the_page()
    {
        SerpentRougeVerse aquarius = SerpentRouge.VerseOf("LSR_AQUARIUS")!;
        SerpentRougeVerse pisces = SerpentRouge.VerseOf("LSR_PISCES")!;

        Assert.Equal(70, aquarius.Bounds.Y);
        Assert.Equal(210, pisces.Bounds.Y);
        Assert.True(aquarius.Bounds.Y + aquarius.Bounds.W <= pisces.Bounds.Y + 5);
    }
}
