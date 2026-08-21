using GK3Reborn.Formats.Animation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for faces: what an animation says a face does, and how one is put together.
/// </summary>
/// <remarks>
/// GK3's characters have no facial geometry, so everything here is about names and pixel
/// offsets. That is exactly why it is worth testing: nothing about a wrong answer looks
/// like a mistake at runtime — the face simply does not move, or moves the wrong part.
/// </remarks>
public sealed class FaceTests
{
    private static AnimationFile Read(string text)
    {
        var bag = new DiagnosticBag();
        AnimationFile animation = AnimationFile.Parse(text, "TEST.YAK", bag);

        Assert.DoesNotContain(bag.Items, d => d.Severity >= DiagnosticSeverity.Error);
        return animation;
    }

    [Fact]
    public void A_line_of_dialogue_carries_its_own_mouth_shapes()
    {
        // The recording and the lip sync are in the same file against the same frame
        // numbers, which is why a mouth follows the words without anything analysing sound.
        AnimationFile line = Read(
            "[HEADER]\n56\n\n[SOUNDS]\n1\n0,A01K6L3W.HN1,100\n\n" +
            "[GK3]\n5\n0,SPEAKER,BUCHELLI\n0,CAPTION,Vorrei parlare.\n" +
            "0,LIPSYNCH,BUCHELLI,MOUTH01\n4,LIPSYNCH,BUCHELLI,MOUTH02\n55,DIALOGUECUE\n");

        Assert.Equal(2, line.Mouths.Count);
        Assert.Equal(new AnimationMouth(0, "BUCHELLI", "MOUTH01"), line.Mouths[0]);
        Assert.Equal(4, line.Mouths[1].Frame);

        // And the caption still comes out, which is the thing that used to be all there was.
        Assert.Equal("Vorrei parlare.", Assert.Single(line.Captions).Text);
    }

    [Fact]
    public void A_blink_is_an_animation_of_eyelid_textures_and_nothing_else()
    {
        AnimationFile blink = Read(
            "[HEADER]\n4\n\n[GK3]\n4\n" +
            "0,FACETEX,GABRIEL,GAB_BLINK_01,E\n1,FACETEX,GABRIEL,GAB_BLINK_02,E\n" +
            "2,FACETEX,GABRIEL,GAB_BLINK_01,E\n3,UNFACETEX,GABRIEL,E\n");

        Assert.Empty(blink.Actions);
        Assert.Equal(4, blink.Faces.Count);
        Assert.All(blink.Faces, f => Assert.Equal(FacePart.Eyelids, f.Part));

        // The last node takes the eyelids off again, which is what says the blink is over.
        Assert.Null(blink.Faces[3].Texture);
        Assert.Equal("GAB_BLINK_01", blink.Faces[0].Texture);
    }

    [Fact]
    public void The_three_face_parts_are_told_apart_by_one_letter()
    {
        AnimationFile animation = Read(
            "[HEADER]\n2\n\n[GK3]\n3\n" +
            "0,FACETEX, ABBE,ABE_BROW_DOWN_01, H\n" +
            "0,FACETEX,ABBE,ABE_MOUTH_SMILE,M\n" +
            "1,FACETEX,ABBE,ABE_SQUINT,L\n");

        // H is the forehead and M the mouth. L is one of the eyes, which nothing here
        // paints, and painting it into the wrong region would be worse than leaving it.
        Assert.Equal(2, animation.Faces.Count);
        Assert.Equal(FacePart.Forehead, animation.Faces[0].Part);
        Assert.Equal(FacePart.Mouth, animation.Faces[1].Part);
    }

    private const string Faces =
        """
        [Eyes]
        Eye_GreenX   = 4x4, DownSampleOnly

        [DEFAULT]
        Blink Frequency         = 5000,12000

        [GAB]
        Forehead Offset         = 90,77                    // texture coordinate in pixels
        Eyelids Offset          = 105,106
        Eyelids Alpha Channel   = gab_eyelids_alpha
        Blink Anims             = gabblink,90,gabblink2,10
        Blink Frequency         = 4000,9000
        Mouth Offset            = 90,132
        Mouth Size              = 78x82

        [VM1]
        Face Name               = va1_face   // does not follow the naming convention
        Forehead Offset         = 103,70
        Eyelids Offset          = 104,111
        Blink Anims             = vm1blink,90,vm1blink2,10
        Mouth Offset            = 103,145
        Mouth Size              = 51x69

        [CON-XXX]
        Mouth Offset            = 91,142
        Mouth Size              = 75x72
        """;

    [Fact]
    public void A_characters_face_is_a_set_of_pixel_offsets_into_one_bitmap()
    {
        FaceConfig gab = Assert.IsType<FaceConfig>(FaceLibrary.Parse(Faces).Of("gab"));

        Assert.Equal(new FaceSpot(90, 132), gab.MouthOffset);
        Assert.Equal(new FaceSpot(78, 82), gab.MouthSize);
        Assert.Equal(new FaceSpot(105, 106), gab.EyelidsOffset);
        Assert.Equal(new FaceSpot(90, 77), gab.ForeheadOffset);
        Assert.Equal("GAB_EYELIDS_ALPHA", gab.EyelidsAlpha);

        // The bitmaps follow a convention rather than being listed, and a mouth shape is
        // named by an animation without the character's code in front of it.
        Assert.Equal("GAB_FACE", gab.FaceTexture);
        Assert.Equal("GAB_MOUTH03", gab.MouthTexture("MOUTH03"));
        Assert.Equal("GAB_EYELIDS", gab.RestingTexture(FacePart.Eyelids));
        Assert.Equal("GAB_MOUTH00", gab.RestingTexture(FacePart.Mouth));
    }

    [Fact]
    public void A_face_bitmap_that_breaks_the_convention_says_so()
    {
        Assert.Equal("VA1_FACE", FaceLibrary.Parse(Faces).Of("vm1")?.FaceTexture);
    }

    [Fact]
    public void Blink_animations_come_with_the_odds_of_each_and_a_frequency()
    {
        FaceConfig gab = Assert.IsType<FaceConfig>(FaceLibrary.Parse(Faces).Of("GAB"));

        Assert.Equal(
            [new BlinkChoice("gabblink", 90), new BlinkChoice("gabblink2", 10)],
            gab.Blinks);

        // Written in milliseconds in the file and wanted in seconds here.
        Assert.Equal(4.0, gab.BlinkFrom, 3);
        Assert.Equal(9.0, gab.BlinkTo, 3);
    }

    [Fact]
    public void A_character_with_no_frequency_of_their_own_blinks_like_everybody_else()
    {
        FaceConfig vm1 = Assert.IsType<FaceConfig>(FaceLibrary.Parse(Faces).Of("vm1"));

        Assert.Equal(5.0, vm1.BlinkFrom, 3);
        Assert.Equal(12.0, vm1.BlinkTo, 3);
    }

    [Fact]
    public void A_clothing_variant_wears_the_same_face_as_the_character()
    {
        // A scene places gabclothes110a and the file lists GAB. The first three letters are
        // the character, which is the same rule CHARACTERS.TXT is read by.
        Assert.Equal("GAB", FaceLibrary.Parse(Faces).Of("gabclothes110a")?.Identifier);
        Assert.Null(FaceLibrary.Parse(Faces).Of("lbyfan"));
    }

    [Fact]
    public void Art_that_never_arrived_is_left_out_rather_than_half_read()
    {
        // The file's own header says to take the -XXX off a section name once the art
        // exists. Two sections still carry it, and they are not a character.
        FaceLibrary library = FaceLibrary.Parse(Faces);

        Assert.Equal(2, library.Count);
        Assert.Null(library.Of("CON"));
    }
}
