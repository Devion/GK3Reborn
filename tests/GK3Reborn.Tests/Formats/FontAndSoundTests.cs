using System.Buffers.Binary;
using System.Text;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ui;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for the two formats the interface and the audio needed.
/// </summary>
/// <remarks>
/// Both have a trap that only shows up on real data. A font's glyph widths are not written
/// down anywhere — they are the distance between marker pixels along the top of the sheet —
/// and a sound is usually not a sound at all but an MP3 stream inside a RIFF header.
/// </remarks>
public sealed class FontAndSoundTests
{
    /// <summary>
    /// A sheet whose top row marks three glyphs of different widths.
    /// </summary>
    /// <remarks>
    /// Black is the background, red is the marker. The first non-background pixel of the
    /// top row is where the first glyph starts, and every red pixel after it starts
    /// another, so this describes glyphs of three, four and two pixels.
    /// </remarks>
    private static DecodedImage Sheet(int lines = 1)
    {
        const int Width = 10;
        int height = 4 * lines;
        byte[] pixels = new byte[Width * height * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 3] = 255;
        }

        for (int line = 0; line < lines; line++)
        {
            foreach (int x in new[] { 1, 4, 8 })
            {
                int at = ((line * 4 * Width) + x) * 4;
                pixels[at] = 255;
            }
        }

        return new DecodedImage(Width, height, pixels, HasAlpha: false, "test");
    }

    [Fact]
    public void A_glyph_is_as_wide_as_the_gap_to_the_next_marker()
    {
        var bag = new DiagnosticBag();
        FontFile font = FontFile.Parse("Font=ABC\nChar Extra=1\n", Sheet(), "TEST", bag);

        Assert.Equal(3, font.Count);
        Assert.Equal(new Glyph(1, 1, 3, 3), font['A']);
        Assert.Equal(new Glyph(4, 1, 4, 3), font['B']);

        // The last glyph of a row runs to the edge of the sheet, because nothing marks its
        // end. Two of the font sheets in the game rely on that.
        Assert.Equal(new Glyph(8, 1, 2, 3), font['C']);
    }

    [Fact]
    public void A_glyph_is_a_pixel_shorter_than_the_row_that_holds_it()
    {
        // The top pixel of a row is the marker strip and not part of the letter.
        FontFile font = FontFile.Parse("Font=ABC\n", Sheet(), "TEST", new DiagnosticBag());

        Assert.Equal(3, font.Height);
    }

    [Fact]
    public void Measuring_counts_the_extra_spacing_between_characters()
    {
        FontFile font = FontFile.Parse("Font=ABC\nChar Extra=2\n", Sheet(), "TEST", new DiagnosticBag());

        // 3 + 4 + 2 glyph pixels, plus two extra for each of the three characters.
        Assert.Equal(9 + 6, font.Measure("ABC"));
    }

    [Fact]
    public void A_character_the_font_lacks_falls_back_rather_than_vanishing()
    {
        FontFile font = FontFile.Parse("Font=ABC\nDefault Char=C\n", Sheet(), "TEST", new DiagnosticBag());

        Assert.Equal(font['C'], font['z']);
    }

    [Fact]
    public void A_sheet_of_several_rows_reads_them_in_order()
    {
        FontFile font = FontFile.Parse(
            "Font=ABCDEF\nLine Count=2\n", Sheet(lines: 2), "TEST", new DiagnosticBag());

        Assert.Equal(6, font.Count);
        Assert.Equal(1, font['A']!.Value.Y);

        // Second row, one pixel down from the top of it.
        Assert.Equal(5, font['D']!.Value.Y);
    }

    /// <summary>
    /// A sheet of several rows whose markers are given outright, with optional ink.
    /// </summary>
    /// <remarks>
    /// For the terminator cases. Ink is a single lit pixel just under the sheet's last
    /// column, which is how a row whose final letter runs to the edge differs from one
    /// whose final mark is only saying where the letter before it stopped.
    /// </remarks>
    private static DecodedImage Marked(int[][] rows, bool inkAtEnd = false)
    {
        const int Width = 12;
        int height = 4 * rows.Length;
        byte[] pixels = new byte[Width * height * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 3] = 255;
        }

        for (int line = 0; line < rows.Length; line++)
        {
            foreach (int x in rows[line])
            {
                pixels[(((line * 4 * Width) + x) * 4) + 0] = 255;
            }

            if (inkAtEnd)
            {
                // White, under the row's last mark: a letter rather than padding.
                int at = ((((line * 4) + 2) * Width) + Width - 1) * 4;
                pixels[at] = 255;
                pixels[at + 1] = 255;
                pixels[at + 2] = 255;
            }
        }

        return new DecodedImage(Width, height, pixels, HasAlpha: false, "test");
    }

    [Fact]
    public void A_rows_last_marker_is_a_terminator_when_the_counts_say_so()
    {
        // Four marks a row over two rows is eight for six characters, which is one spare
        // per row: each row's last mark says where its last letter stopped rather than
        // being a letter of its own. Reading it as one cost the row a character and shifted
        // every character after it, which is how the caption sheets wrote "Gabqiel Lnnk".
        var bag = new DiagnosticBag();

        FontFile font = FontFile.Parse(
            "Font=ABCDEF\nLine Count=2\n",
            Marked([[1, 3, 6, 10], [1, 3, 6, 10]]),
            "TEST",
            bag);

        Assert.Equal(6, font.Count);

        // Three letters a row, and the last of each stops at the terminator rather than
        // running to the sheet's edge.
        Assert.Equal(new Glyph(6, 1, 4, 3), font['C']);
        Assert.Equal(new Glyph(1, 5, 2, 3), font['D']);
        Assert.Equal(new Glyph(6, 5, 4, 3), font['F']);
        Assert.DoesNotContain(bag.Items, d => d.Code == "GK3R1142");
    }

    [Fact]
    public void A_row_that_fills_its_width_keeps_its_last_marker_as_a_letter()
    {
        // The other convention, and the one every single-row sheet in the game uses: the
        // last letter ends at the sheet's edge and nothing has to say so.
        FontFile font = FontFile.Parse(
            "Font=ABCDEFGH\nLine Count=2\n",
            Marked([[1, 3, 6, 10], [1, 3, 6, 10]]),
            "TEST",
            new DiagnosticBag());

        Assert.Equal(8, font.Count);
        Assert.Equal(new Glyph(10, 1, 2, 3), font['D']);
        Assert.Equal(new Glyph(10, 5, 2, 3), font['H']);
    }

    [Fact]
    public void A_row_the_counts_cannot_settle_is_judged_on_whether_there_is_ink_after_it()
    {
        // Seven characters over two rows of four marks: neither "all terminated" nor "none
        // terminated" adds up, so each row is looked at. Both have ink under the last mark
        // here, so both keep it, and the count comes out one over — which is what the
        // mismatch diagnostic exists to say.
        var bag = new DiagnosticBag();

        FontFile font = FontFile.Parse(
            "Font=ABCDEFG\nLine Count=2\n",
            Marked([[1, 3, 6, 10], [1, 3, 6, 10]], inkAtEnd: true),
            "TEST",
            bag);

        Assert.Equal(7, font.Count);
        Assert.Equal(new Glyph(10, 1, 2, 3), font['D']);
    }

    [Fact]
    public void A_sheet_that_cuts_into_the_wrong_number_of_pieces_says_so()
    {
        // The check that would have caught the terminator the first time. A font whose
        // letters are all somebody else's looks entirely normal from every other angle.
        var bag = new DiagnosticBag();

        FontFile.Parse(
            "Font=ABCDEFGHIJ\nLine Count=2\n",
            Marked([[1, 3, 6, 10], [1, 3, 6, 10]]),
            "TEST",
            bag);

        Assert.Contains(bag.Items, d => d.Code == "GK3R1142");
    }

    [Fact]
    public void A_font_with_no_bitmap_says_so_instead_of_drawing_nothing_quietly()
    {
        var bag = new DiagnosticBag();

        FontFile font = FontFile.Parse(
            "Font=ABC\n", new DecodedImage(0, 0, [], false, "none"), "TEST", bag);

        Assert.Equal(0, font.Count);
        Assert.Contains(bag.Items, d => d.Code == "GK3R1140");
    }

    /// <summary>A minimal RIFF/WAVE file.</summary>
    private static byte[] Wave(int format, int channels, int rate, int bits, short[] samples)
    {
        byte[] data = new byte[samples.Length * (bits / 8)];

        for (int i = 0; i < samples.Length; i++)
        {
            if (bits == 16)
            {
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
            }
            else
            {
                data[i] = (byte)samples[i];
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8);
        writer.Write(36 + data.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)format);
        writer.Write((ushort)channels);
        writer.Write(rate);
        writer.Write(rate * channels * (bits / 8));
        writer.Write((ushort)(channels * (bits / 8)));
        writer.Write((ushort)bits);

        // A fact chunk between fmt and data, which is where GK3 puts one. A reader that
        // assumes the two are adjacent finds no samples at all.
        writer.Write("fact"u8);
        writer.Write(4);
        writer.Write(samples.Length / Math.Max(1, channels));

        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        return stream.ToArray();
    }

    [Fact]
    public void A_pcm_sound_reads_its_samples_and_its_length()
    {
        var bag = new DiagnosticBag();
        WavFile? sound = WavFile.Read(
            Wave(1, 1, 8000, 16, [0, 1000, -1000, 32767]), "TEST", bag);

        Assert.NotNull(sound);
        Assert.Equal(1, sound.Channels);
        Assert.Equal(8000, sound.SampleRate);
        Assert.Equal([0, 1000, -1000, 32767], sound.Samples);
        Assert.Equal(4 / 8000.0, sound.Duration, 6);
        Assert.Empty(bag.Items);
    }

    [Fact]
    public void Eight_bit_samples_are_unsigned_with_silence_in_the_middle()
    {
        // Read as signed, every sound in the game becomes a loud square wave.
        WavFile? sound = WavFile.Read(
            Wave(1, 1, 8000, 8, [128, 255, 0]), "TEST", new DiagnosticBag());

        Assert.NotNull(sound);
        Assert.Equal(0, sound.Samples[0]);
        Assert.True(sound.Samples[1] > 30000);
        Assert.True(sound.Samples[2] < -30000);
    }

    [Fact]
    public void An_mp3_header_with_no_stream_behind_it_is_refused()
    {
        // 97.5% of the game's sounds are an MP3 inside a RIFF header and they are decoded
        // in process. An empty one is a truncated archive entry, which is worth saying
        // rather than returning a sound of no length.
        var bag = new DiagnosticBag();

        Assert.Null(WavFile.Read(Wave(85, 1, 44100, 0, []), "TEST", bag));
        Assert.Contains(bag.Items, d => d.Code == "GK3R1123");
    }

    [Fact]
    public void An_mp3_stream_that_will_not_decode_is_refused_rather_than_thrown()
    {
        // Two of the game's own audio files are damaged. A reader that throws on those
        // takes the room down with them.
        var bag = new DiagnosticBag();
        short[] noise = [.. Enumerable.Range(0, 512).Select(i => (short)(i * 37))];

        Assert.Null(WavFile.Read(Wave(85, 1, 44100, 16, noise), "TEST", bag));
        Assert.Contains(bag.Items, d => d.Code is "GK3R1123" or "GK3R1124");
    }

    [Fact]
    public void A_format_nothing_decodes_is_refused_with_the_reason()
    {
        var bag = new DiagnosticBag();

        Assert.Null(WavFile.Read(Wave(2, 1, 44100, 4, []), "TEST", bag));
        Assert.Contains(bag.Items, d => d.Code == "GK3R1121");
    }

    [Fact]
    public void Something_that_is_not_a_riff_file_is_refused()
    {
        var bag = new DiagnosticBag();

        Assert.Null(WavFile.Read("not audio at all"u8, "TEST", bag));
        Assert.Contains(bag.Items, d => d.Code == "GK3R1120");
    }
}
