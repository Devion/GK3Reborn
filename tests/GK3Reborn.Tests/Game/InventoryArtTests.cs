using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the table that says what each thing in the bag looks like.
/// </summary>
/// <remarks>
/// The whole of what can go wrong here is the reading. <c>INVENTORYSPRITES.TXT</c> is the
/// one file in the game whose comments begin <c>; //</c>, and the half of that the reader
/// does not recognise arrives as a line of its own; the stems it holds bear no fixed
/// relation to the item names, so a lost line is an item that silently has no picture.
/// </remarks>
public sealed class InventoryArtTests
{
    private const string Data = """
        ; ////////////////////////////////////////////////////////////////////////////
        ; // inventory images

        NONE						= undefined
        CANDY						= candy
        MOSELYS_FINGERPRINT			= MoselyPrint
        GILT_GLOVE					= Gauntlet
        MS3I_PANEL1					= MS3I_PANEL1
        """;

    [Fact]
    public void The_files_own_comments_are_not_items()
    {
        InventoryArt art = InventoryArt.Parse(Data);

        Assert.Equal(5, art.Count);
        Assert.Null(art.StemOf(";"));
    }

    [Fact]
    public void An_items_picture_is_named_by_the_artists_rather_than_by_the_item()
    {
        // The reason the file exists: nothing about GILT_GLOVE leads to GAUNTLET.
        InventoryArt art = InventoryArt.Parse(Data);

        Assert.Equal("Gauntlet", art.StemOf("GILT_GLOVE"));
        Assert.Equal("candy", art.StemOf("candy"));
        Assert.Null(art.StemOf("PARCHMENT_1"));
    }

    [Fact]
    public void An_item_whose_stem_repeats_its_name_is_still_an_item()
    {
        // Le Serpent Rouge's panels are written that way, and dropping a line because its
        // two halves match would take twenty items out of the table.
        InventoryArt art = InventoryArt.Parse(Data);

        Assert.Equal("MS3I_PANEL1", art.StemOf("MS3I_PANEL1"));
    }

    [Fact]
    public void A_list_picture_is_looked_for_under_both_spellings()
    {
        // Most stems take the number straight and a handful put an underscore in first,
        // with nothing to tell them apart but trying.
        InventoryArt art = InventoryArt.Parse(Data);

        Assert.Equal(["candy9.BMP", "candy_9.BMP"], art.IconNames("CANDY"));
        Assert.Equal(["MoselyPrint9.BMP", "MoselyPrint_9.BMP"], art.IconNames("MOSELYS_FINGERPRINT"));
        Assert.Empty(art.IconNames("PARCHMENT_1"));
    }

    [Fact]
    public void A_close_up_is_looked_for_under_all_three_spellings()
    {
        // The picture the close-up screen shows is the "6", not the "9": the "9" is a
        // 94-pixel square and the "6" is the thing itself, painted to be read.
        InventoryArt art = InventoryArt.Parse(Data);

        Assert.Equal(
            ["candy6.BMP", "candy6_ALPHA.BMP", "candy_6_ALPHA.BMP"],
            art.CloseUpNames("CANDY"));

        // The one asset in the game that spells it the third way.
        Assert.Contains("MoselyPrint_6_ALPHA.BMP", art.CloseUpNames("MOSELYS_FINGERPRINT"));
        Assert.Empty(art.CloseUpNames("PARCHMENT_1"));
    }

    [Fact]
    public void Transparency_is_a_file_beside_the_picture()
    {
        Assert.Equal("CANDY9_OP.BMP", InventoryArt.MaskOf("CANDY9.BMP"));
        Assert.Equal("MOSELYPRINT_9_OP.BMP", InventoryArt.MaskOf("MOSELYPRINT_9.BMP"));
    }

    [Fact]
    public void A_file_that_is_not_there_leaves_every_item_drawn_by_its_name()
    {
        InventoryArt art = InventoryArt.Parse(string.Empty);

        Assert.Equal(0, art.Count);
        Assert.Empty(art.IconNames("CANDY"));
    }
}
