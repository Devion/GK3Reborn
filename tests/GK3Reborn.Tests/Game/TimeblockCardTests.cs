using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for lifting a timeblock card's lettering off its painting.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures are synthetic and have to be: the real frames are original game art. What
/// they reproduce is the one property the whole thing rests on — a frame of lettering is
/// the painting itself with white blended into it, so subtracting the painting gives the
/// coverage back exactly and no pixel of a frame is ever darker than the painting under it.
/// </para>
/// <para>
/// The painting here is noise rather than a gradient on purpose. A smooth picture matches
/// itself at several offsets and would let a placement test pass by accident.
/// </para>
/// </remarks>
public sealed class TimeblockCardTests
{
    /// <summary>Where 102P's lettering sits from the left, which is where the engine looks.</summary>
    private const int HintLeft = 8;

    /// <summary>And up from the bottom.</summary>
    private const int HintUp = 63;

    private const int LetterWidth = 96;
    private const int LetterHeight = 24;

    /// <summary>Where that puts the top of the lettering on a painting of a given height.</summary>
    /// <param name="paintingHeight">How tall the painting is.</param>
    /// <returns>The top, in the painting's pixels.</returns>
    private static int Top(int paintingHeight) => paintingHeight - HintUp - LetterHeight;

    [Fact]
    public void The_lettering_comes_back_off_the_painting_it_was_blended_into()
    {
        DecodedImage painting = Painting(seed: 7);
        byte[] mask = Mask();

        DecodedImage[] frames = Frames(
            painting, mask, HintLeft, Top(TimeblockCard.CardHeight), count: 5);

        TimeblockCard card = Assert.IsType<TimeblockCard>(
            TimeblockCard.From("102P", frames, painting));

        Assert.Equal(HintLeft, card.Left);
        Assert.Equal(Top(TimeblockCard.CardHeight), card.Top);
        Assert.Equal(5, card.Frames.Count);

        // The last frame is the whole of the lettering, and it should be the mask again.
        DecodedImage last = card.Frames[^1];

        for (int i = 0; i < mask.Length; i++)
        {
            Assert.True(
                Math.Abs(last.Pixels[(i * 4) + 3] - mask[i]) <= 4,
                $"pixel {i} came back as {last.Pixels[(i * 4) + 3]} rather than {mask[i]}");

            Assert.Equal(255, last.Pixels[i * 4]);
            Assert.Equal(255, last.Pixels[(i * 4) + 1]);
            Assert.Equal(255, last.Pixels[(i * 4) + 2]);
        }
    }

    [Fact]
    public void A_painting_a_row_taller_than_the_rest_still_letters_itself()
    {
        // TBT306P.BMP is 640x481 where its sixteen siblings are 640x480. The offsets are
        // measured up from the bottom for exactly this reason, and measuring them down
        // from the top put that one card's lettering a pixel out.
        const int Odd = TimeblockCard.CardHeight + 1;

        // 306P's own anchor, 64 up from the bottom of a painting 481 rows tall.
        int top = Odd - 64 - LetterHeight;

        DecodedImage painting = Painting(seed: 23, height: Odd);
        DecodedImage[] frames = Frames(painting, Mask(), 14, top, count: 3);

        TimeblockCard card = Assert.IsType<TimeblockCard>(
            TimeblockCard.From("306P", frames, painting));

        Assert.Equal(top, card.Top);
        Assert.Equal(Odd, card.PaintingHeight);

        // And its lettering is placed against 481 rows rather than 480, so a painting drawn
        // at twice its size puts the lettering at twice the offset and not near it.
        Assert.Equal(top * 2, card.Over(new Vector4(0, 0, 1280, Odd * 2)).Y, 3);
    }

    [Fact]
    public void An_earlier_frame_carries_only_the_letters_typed_so_far()
    {
        DecodedImage painting = Painting(seed: 11);
        DecodedImage[] frames = Frames(
            painting, Mask(), HintLeft, Top(TimeblockCard.CardHeight), count: 4);

        TimeblockCard card = Assert.IsType<TimeblockCard>(
            TimeblockCard.From("102P", frames, painting));

        // Each frame reveals another quarter of the width, so the coverage only ever grows.
        int before = -1;

        foreach (DecodedImage frame in card.Frames)
        {
            int lit = 0;

            for (int i = 3; i < frame.Pixels.Length; i += 4)
            {
                lit += frame.Pixels[i];
            }

            Assert.True(lit > before, "a later frame carried less lettering than an earlier one");
            before = lit;
        }
    }

    [Fact]
    public void Lettering_the_table_has_in_the_wrong_place_is_found_anyway()
    {
        // Two right and four up from where the engine expects 102P's lettering, which is
        // what a language pack that nudged its own art looks like.
        int left = HintLeft + 2;
        int top = Top(TimeblockCard.CardHeight) - 4;

        DecodedImage painting = Painting(seed: 3);
        DecodedImage[] frames = Frames(painting, Mask(), left, top, count: 3);

        TimeblockCard card = Assert.IsType<TimeblockCard>(
            TimeblockCard.From("102P", frames, painting));

        Assert.Equal(left, card.Left);
        Assert.Equal(top, card.Top);
    }

    [Fact]
    public void Lettering_that_does_not_belong_to_the_painting_is_refused()
    {
        DecodedImage painting = Painting(seed: 5);

        // Frames cut out of a different picture. Subtracting this painting from them would
        // recover noise in the shape of neither, so nothing is drawn and the caller writes
        // the name out instead.
        DecodedImage[] frames = Frames(
            Painting(seed: 9), Mask(), HintLeft, Top(TimeblockCard.CardHeight), count: 3);

        Assert.Null(TimeblockCard.From("102P", frames, painting));
    }

    [Fact]
    public void Lettering_too_big_for_its_painting_is_refused()
    {
        DecodedImage painting = Painting(seed: 5);
        DecodedImage[] frames = Frames(
            painting, Mask(), HintLeft, Top(TimeblockCard.CardHeight), count: 3);

        var tiny = new DecodedImage(64, 32, new byte[64 * 32 * 4], HasAlpha: false, "test");

        Assert.Null(TimeblockCard.From("102P", frames, tiny));
    }

    [Fact]
    public void Frames_of_two_different_sizes_are_refused()
    {
        DecodedImage painting = Painting(seed: 5);

        List<DecodedImage> frames =
        [
            .. Frames(painting, Mask(), HintLeft, Top(TimeblockCard.CardHeight), count: 2),
            new DecodedImage(
                LetterWidth - 1, LetterHeight, new byte[(LetterWidth - 1) * LetterHeight * 4],
                HasAlpha: false, "test"),
        ];

        Assert.Null(TimeblockCard.From("102P", frames, painting));
    }

    [Fact]
    public void The_typing_runs_at_fifteen_frames_a_second_and_stops_on_the_last()
    {
        DecodedImage painting = Painting(seed: 13);
        DecodedImage[] frames = Frames(
            painting, Mask(), HintLeft, Top(TimeblockCard.CardHeight), count: 5);

        TimeblockCard card = Assert.IsType<TimeblockCard>(
            TimeblockCard.From("102P", frames, painting));

        Assert.Equal(5 / 15.0, card.Seconds, 6);

        Assert.Equal(0, card.At(0));
        Assert.Equal(0, card.At(-1));
        Assert.Equal(1, card.At(1 / 15.0));
        Assert.Equal(4, card.At(4 / 15.0));

        // It holds the finished name rather than typing it again.
        Assert.Equal(4, card.At(30));
    }

    [Fact]
    public void The_lettering_follows_the_painting_wherever_the_painting_went()
    {
        int top = Top(TimeblockCard.CardHeight);

        DecodedImage painting = Painting(seed: 17);
        DecodedImage[] frames = Frames(painting, Mask(), HintLeft, top, count: 2);

        TimeblockCard card = Assert.IsType<TimeblockCard>(
            TimeblockCard.From("102P", frames, painting));

        // The painting at twice its size, top-left of the window.
        Vector4 twice = card.Over(new Vector4(0, 0, 1280, 960));

        Assert.Equal(HintLeft * 2, twice.X, 3);
        Assert.Equal(top * 2, twice.Y, 3);
        Assert.Equal(LetterWidth * 2, twice.Z, 3);
        Assert.Equal(LetterHeight * 2, twice.W, 3);

        // And cropped into a widescreen window, where the painting overruns the top and the
        // bottom. The lettering goes up with it rather than staying where it was.
        Vector4 cropped = card.Over(
            PictureFit.Rectangle(640, 480, 1280, 720, cover: true));

        Assert.Equal(HintLeft * 2, cropped.X, 3);
        Assert.Equal(-120 + (top * 2), cropped.Y, 3);
    }

    /// <summary>A card-sized picture of noise, which no offset but the right one matches.</summary>
    /// <param name="seed">Which noise.</param>
    /// <param name="height">How tall, for the one painting that is not the usual height.</param>
    /// <returns>The picture.</returns>
    /// <remarks>
    /// Kept below 200 so that every channel has room for the lettering to lighten it. Where
    /// a painting is already white there is no room and no answer, which is a real case and
    /// not the one these tests are about.
    /// </remarks>
    private static DecodedImage Painting(int seed, int height = TimeblockCard.CardHeight)
    {
        var random = new Random(seed);
        var pixels = new byte[TimeblockCard.CardWidth * height * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)random.Next(0, 200);
            pixels[i + 1] = (byte)random.Next(0, 200);
            pixels[i + 2] = (byte)random.Next(0, 200);
            pixels[i + 3] = 255;
        }

        return new DecodedImage(
            TimeblockCard.CardWidth, height, pixels, HasAlpha: false, "test");
    }

    /// <summary>The coverage of a line of lettering, softened at its edges.</summary>
    /// <returns>One byte a pixel.</returns>
    private static byte[] Mask()
    {
        var mask = new byte[LetterWidth * LetterHeight];

        for (int y = 0; y < LetterHeight; y++)
        {
            for (int x = 0; x < LetterWidth; x++)
            {
                // Strokes with soft sides, which is what an antialiased letter is and what
                // makes the alpha worth recovering rather than thresholding.
                int across = x % 8;
                int down = Math.Min(y, LetterHeight - 1 - y);

                mask[(y * LetterWidth) + x] = across switch
                {
                    0 or 1 => 0,
                    2 or 7 => (byte)Math.Min(255, 60 + (down * 12)),
                    _ => (byte)Math.Min(255, 140 + (down * 20)),
                };
            }
        }

        return mask;
    }

    /// <summary>
    /// Frames as the archives hold them: the painting, with more of the lettering blended
    /// into it each time.
    /// </summary>
    /// <param name="painting">The picture to cut them out of.</param>
    /// <param name="mask">The finished lettering's coverage.</param>
    /// <param name="left">Where the lettering sits on the painting.</param>
    /// <param name="top">And down.</param>
    /// <param name="count">How many frames the name types itself over.</param>
    /// <returns>The frames, in playing order.</returns>
    private static DecodedImage[] Frames(
        DecodedImage painting, byte[] mask, int left, int top, int count)
    {
        var frames = new DecodedImage[count];

        for (int frame = 0; frame < count; frame++)
        {
            // As many whole columns as have been typed by now.
            int typed = LetterWidth * (frame + 1) / count;
            var pixels = new byte[LetterWidth * LetterHeight * 4];

            for (int y = 0; y < LetterHeight; y++)
            {
                for (int x = 0; x < LetterWidth; x++)
                {
                    int into = ((y * LetterWidth) + x) * 4;
                    int from = (((top + y) * painting.Width) + left + x) * 4;
                    int alpha = x < typed ? mask[(y * LetterWidth) + x] : 0;

                    for (int channel = 0; channel < 3; channel++)
                    {
                        int ground = painting.Pixels[from + channel];

                        pixels[into + channel] = (byte)Math.Clamp(
                            ground + (int)Math.Round(alpha / 255.0 * (255 - ground)), 0, 255);
                    }

                    pixels[into + 3] = 255;
                }
            }

            frames[frame] = new DecodedImage(
                LetterWidth, LetterHeight, pixels, HasAlpha: false, "test");
        }

        return frames;
    }
}
