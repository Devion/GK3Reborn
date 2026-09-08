using GK3Reborn.Formats.Animation;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for the <c>.SEQ</c> reader.
/// </summary>
public sealed class SequenceFileTests
{
    [Fact]
    public void A_sprite_list_is_one_value_and_not_a_row_of_settings()
    {
        SequenceFile sequence = SequenceFile.Parse(
            "Sequence Type=Series\r\nSprite List=d104p_01,d104p_02,d104p_03\r\n",
            "D104P.SEQ");

        Assert.Equal("Series", sequence.Type);
        Assert.Equal(["d104p_01", "d104p_02", "d104p_03"], sequence.Sprites);
    }

    [Fact]
    public void A_repeated_frame_is_kept_because_the_repeat_is_the_timing()
    {
        // D312P.SEQ ends on its last frame twice, which holds the finished name for a
        // frame longer. Collapsing the duplicate would shorten the animation.
        SequenceFile sequence = SequenceFile.Parse(
            "Sequence Type=Series\nSprite List=a_01,a_02,a_02\n");

        Assert.Equal(["a_01", "a_02", "a_02"], sequence.Sprites);
    }

    [Fact]
    public void A_file_of_animation_names_lists_no_sprites()
    {
        // Most .SEQ files are a bare list of animation names with no keys at all. Read for
        // pictures, one has to come back empty rather than turning a name into a key.
        SequenceFile sequence = SequenceFile.Parse("CatStandFidg1\r\nCatStandFidg2\r\n");

        Assert.Empty(sequence.Sprites);
        Assert.Null(sequence.Type);
    }

    [Fact]
    public void Whitespace_and_a_trailing_comma_do_not_become_a_frame()
    {
        SequenceFile sequence = SequenceFile.Parse("Sprite List= a_01 , a_02 ,\n");

        Assert.Equal(["a_01", "a_02"], sequence.Sprites);
    }

    [Fact]
    public void The_keys_are_matched_however_they_are_cased()
    {
        SequenceFile sequence = SequenceFile.Parse("SEQUENCE TYPE=Series\nsprite list=a_01\n");

        Assert.Equal("Series", sequence.Type);
        Assert.Equal(["a_01"], sequence.Sprites);
    }
}
