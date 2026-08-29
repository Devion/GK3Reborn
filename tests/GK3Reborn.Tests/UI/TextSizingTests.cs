using GK3Reborn.Game;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for how large the interface's letters come out.
/// </summary>
/// <remarks>
/// The complaint this answers is that the menu is too big on a large window, and the reason
/// it was too big is a cap: past about 1440 lines the share of the window stops growing and
/// every larger display gets the same 36-pixel em. So the test that matters is not that a
/// multiplier multiplies — it is that the multiplier is felt <em>on the far side of that
/// cap</em>, which is the only place a player would ever reach for it.
/// </remarks>
public sealed class TextSizingTests
{
    private const int Fullhd = 1080;
    private const int FourK = 2160;

    [Fact]
    public void The_automatic_size_is_a_share_of_the_window_until_it_is_capped()
    {
        // A share, so a small window gets small letters.
        Assert.True(
            TextSizing.Em(720, menu: true) < TextSizing.Em(Fullhd, menu: true),
            "the menu should grow with the window below the cap");

        // And then the cap, which is the whole of the original complaint: two displays with
        // twice the pixels between them get letters of exactly the same size.
        Assert.Equal(TextSizing.Em(Fullhd, menu: true), TextSizing.Em(FourK, menu: true));
    }

    [Fact]
    public void The_players_correction_is_felt_past_the_cap()
    {
        int capped = TextSizing.Em(FourK, menu: true);

        // Applied before the cap this would do nothing at all: three quarters of a
        // twenty-sixth of 4K is 62 pixels, which caps straight back to the same number.
        Assert.True(
            TextSizing.Em(FourK, menu: true, 0.75f) < capped,
            "a smaller text size should reach a display that is already at the cap");

        Assert.True(
            TextSizing.Em(FourK, menu: true, Settings.LargestText) > capped,
            "and a larger one should too");
    }

    [Fact]
    public void Both_atlases_move_together_and_the_menu_stays_the_larger()
    {
        foreach (float scale in (float[])[Settings.SmallestText, 1f, Settings.LargestText])
        {
            foreach (int height in (int[])[480, 720, Fullhd, 1440, FourK])
            {
                Assert.True(
                    TextSizing.Em(height, menu: true, scale) >=
                        TextSizing.Em(height, menu: false, scale),
                    $"the menu should not be smaller than a caption at {height}/{scale}");
            }
        }

        // Moving together means the captions shrink as well, not only the menu: the row
        // says both, and a row that quietly moved one of them would be a lie.
        Assert.True(
            TextSizing.Em(FourK, menu: false, Settings.SmallestText) <
                TextSizing.Em(FourK, menu: false),
            "captions should follow the text size too");
    }

    [Fact]
    public void No_setting_can_ask_for_letters_that_cannot_be_drawn()
    {
        // A hand-written settings file is a text file somebody may edit, and an em of nought
        // is a game with no interface rather than one with an ugly menu.
        foreach (float scale in (float[])[0f, -4f, 1e9f, float.NaN, float.PositiveInfinity])
        {
            foreach (bool menu in (bool[])[false, true])
            {
                int em = TextSizing.Em(FourK, menu, scale);

                Assert.InRange(em, 8, 64);
                Assert.InRange(TextSizing.Em(1, menu, scale), 8, 64);
            }

            Assert.True(TextSizing.Sheet(FourK, scale) >= 12);
            Assert.True(TextSizing.MenuMagnification(FourK, 26, scale) >= 1);
        }
    }

    [Fact]
    public void An_unreadable_scale_is_treated_as_the_automatic_one()
    {
        Assert.Equal(1f, TextSizing.Sane(float.NaN));
        Assert.Equal(Settings.SmallestText, TextSizing.Sane(0.01f));
        Assert.Equal(Settings.LargestText, TextSizing.Sane(99f));
        Assert.Equal(1f, TextSizing.Sane(1f));
    }

    [Fact]
    public void The_bitmap_ladder_and_its_magnification_follow_the_same_correction()
    {
        // GK3's own sheets cannot be re-cut, so the text size reaches them twice: which
        // rung of the ladder is asked for, and how many screen pixels a sheet pixel covers.
        Assert.True(
            TextSizing.Sheet(FourK, Settings.SmallestText) < TextSizing.Sheet(FourK),
            "a smaller text size should ask for a smaller rung");

        Assert.True(
            TextSizing.MenuMagnification(FourK, 17, Settings.SmallestText) <=
                TextSizing.MenuMagnification(FourK, 17),
            "and for no more magnification than the automatic size wanted");

        Assert.True(
            TextSizing.MenuMagnification(FourK, 17, Settings.LargestText) >
                TextSizing.MenuMagnification(FourK, 17),
            "a larger one should magnify further");
    }
}
