// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Game;

/// <summary>
/// The lettering that types itself across the card between two parts of the day.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is in the archives.</b> Each of the seventeen timeblocks has a painting,
/// <c>TBT102P.BMP</c>, and a run of thirteen to eighteen small bitmaps, <c>D102P_01.BMP</c>
/// upwards, listed in playing order by <c>D102P.SEQ</c>. The small ones are the name of the
/// timeblock appearing a few letters at a time, as if typed. The port drew the name in its
/// own face instead and threw the animation away; this is the animation.
/// </para>
/// <para>
/// <b>The frames are opaque, and that is the whole problem.</b> Each one is not lettering on
/// a transparent ground — it is a rectangle of the painting itself with white text blended
/// over it. Drawn as they are, they only line up over the original 640x480 painting, so on
/// an installation with the upscaled paintings a player would get a soft postage stamp of
/// 1999 artwork sitting in the middle of a sharp one. That is the reason none of this is
/// simply shipped as enhanced textures: at four times the size the lettering would have to
/// be repainted, and the frames already say exactly what the lettering is if you subtract
/// the painting back out.
/// </para>
/// <para>
/// So that is what happens here. A frame is a crop of the painting at a known offset, and
/// the difference between the two is nothing at all outside the letters — checked, not
/// assumed, and the check is what <see cref="Locate"/> uses to confirm the offset. Inside
/// them the frame is the painting blended towards white, so
/// <c>alpha = (frame - painting) / (255 - painting)</c> recovers the coverage the artists
/// drew. What comes out is white lettering with a real alpha channel, which lays over a
/// painting of any size, at any window size, and over the upscaled one as readily as the
/// original.
/// </para>
/// <para>
/// The offsets are adapted from G-Engine's <c>TimeblockScreen</c> by Clark Kromenaker
/// (https://github.com/kromenak/gengine), GNU General Public License version 3. See NOTICE.
/// They are given there as an anchor from the bottom-left corner; here they are the
/// top-left of the lettering in the card, which is the same thing said in the coordinates
/// everything else in this engine uses. Two of them differ, and both are checked at load —
/// see <see cref="Where"/>.
/// </para>
/// </remarks>
public sealed class TimeblockCard
{
    private TimeblockCard(
        string timeblock,
        IReadOnlyList<DecodedImage> frames,
        int left,
        int top,
        int paintingWidth,
        int paintingHeight)
    {
        Timeblock = timeblock;
        Frames = frames;
        Left = left;
        Top = top;
        PaintingWidth = paintingWidth;
        PaintingHeight = paintingHeight;
    }

    /// <summary>The width every one of the paintings is.</summary>
    public const int CardWidth = 640;

    /// <summary>
    /// The height all but one of them are.
    /// </summary>
    /// <remarks>
    /// <c>TBT306P.BMP</c> is 640x481 — one row taller than its sixteen siblings, and the
    /// enhanced set upscales it to 2048x1539 rather than quietly squaring it up. Nothing
    /// here assumes either number: <see cref="PaintingHeight"/> is whatever the painting
    /// turned out to be, which is why the offsets below are measured from the bottom.
    /// </remarks>
    public const int CardHeight = 480;

    /// <summary>Which part of the day this belongs to, as <c>102P</c>.</summary>
    public string Timeblock { get; }

    /// <summary>
    /// The lettering, a frame at a time: white, with the coverage in the alpha channel.
    /// </summary>
    public IReadOnlyList<DecodedImage> Frames { get; }

    /// <summary>Where the lettering starts from the left of the painting, in its pixels.</summary>
    public int Left { get; }

    /// <summary>Where the lettering starts from the top of the painting, in its pixels.</summary>
    public int Top { get; }

    /// <summary>How wide the painting the lettering was lifted off is.</summary>
    public int PaintingWidth { get; }

    /// <summary>How tall it is.</summary>
    public int PaintingHeight { get; }

    /// <summary>How wide the lettering is, in the painting's pixels.</summary>
    public int Width => Frames.Count > 0 ? Frames[0].Width : 0;

    /// <summary>How tall it is.</summary>
    public int Height => Frames.Count > 0 ? Frames[0].Height : 0;

    /// <summary>How long the typing takes, in seconds.</summary>
    public double Seconds => Frames.Count / SequenceFile.FramesPerSecond;

    /// <summary>
    /// Where the lettering goes, given where the painting went.
    /// </summary>
    /// <param name="painting">The painting's rectangle on screen, in window pixels.</param>
    /// <returns>The lettering's rectangle, in the same pixels.</returns>
    /// <remarks>
    /// <para>
    /// The offsets are in the painting's own pixels, so the rectangle it was drawn into is
    /// what turns them into window pixels — the rectangle, not the size of whatever picture
    /// went into it. An enhanced painting is ten times as many pixels and lands in exactly
    /// the same place, and a covered painting runs off the top and the bottom of the window
    /// with the lettering following it up rather than being nudged back into view on its own.
    /// </para>
    /// <para>
    /// It does assume the painting on screen has the shape of the one the lettering came
    /// off, which the enhanced set keeps down to <c>306P</c>'s odd extra row. A painting of
    /// some other shape puts the lettering somewhere plausible and slightly wrong, which is
    /// the right failure for a decoration.
    /// </para>
    /// </remarks>
    public Vector4 Over(Vector4 painting)
    {
        float across = painting.Z / PaintingWidth;
        float down = painting.W / PaintingHeight;

        return new Vector4(
            painting.X + (Left * across),
            painting.Y + (Top * down),
            Width * across,
            Height * down);
    }

    /// <summary>Which frame is showing.</summary>
    /// <param name="seconds">How long the card has been up.</param>
    /// <returns>An index into <see cref="Frames"/>.</returns>
    /// <remarks>
    /// It stops on the last frame rather than looping. The last frame is the finished name,
    /// and a name that types itself over and over is a screensaver.
    /// </remarks>
    public int At(double seconds) => Math.Clamp(
        (int)(Math.Max(0, seconds) * SequenceFile.FramesPerSecond), 0, Frames.Count - 1);

    /// <summary>
    /// Reads the lettering for a part of the day out of the archives.
    /// </summary>
    /// <param name="archives">The game's own barns.</param>
    /// <param name="timeblock">Which one, as <c>102P</c>.</param>
    /// <returns>The lettering, or null when the archives cannot supply it.</returns>
    /// <remarks>
    /// <b>The originals, whatever the paintings on screen are.</b> The lettering is
    /// recovered by subtracting the picture it was blended into, and an upscale of that
    /// picture is a different set of pixels — subtracting it would leave the letters full
    /// of the difference between the two rather than of the letters.
    /// </remarks>
    public static TimeblockCard? Read(GameArchives archives, string timeblock)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(timeblock);

        string code = timeblock.ToUpperInvariant();

        if (archives.ReadText($"D{code}.SEQ") is not { Length: > 0 } script)
        {
            return null;
        }

        IReadOnlyList<string> names = SequenceFile.Parse(script, $"D{code}.SEQ").Sprites;
        if (names.Count == 0)
        {
            return null;
        }

        if (Picture(archives, $"TBT{code}.BMP") is not { } painting)
        {
            return null;
        }

        List<DecodedImage> frames = [];

        foreach (string name in names)
        {
            if (Picture(archives, name.ToUpperInvariant() + ".BMP") is not { } frame)
            {
                Log.Warning(
                    $"WARNING GK3R3458: {code}'s lettering is missing at {name}, so the "
                    + "name is written out instead.");

                return null;
            }

            frames.Add(frame);
        }

        return From(code, frames, painting);
    }

    /// <summary>
    /// Lifts the lettering out of a card's frames.
    /// </summary>
    /// <param name="timeblock">Which part of the day, as <c>102P</c>.</param>
    /// <param name="frames">The frames, in playing order, as they are in the archives.</param>
    /// <param name="painting">The card's painting, which the frames were cut out of.</param>
    /// <returns>The lettering, or null when the two do not go together.</returns>
    /// <remarks>
    /// Null is a fair answer and the caller falls back to writing the name out in the
    /// port's own face. It happens for frames that are not all one size, for frames too big
    /// for the painting, and — the case worth having the check for — for a language pack
    /// that ships its own lettering over a painting it did not also ship. In that last case
    /// the subtraction would be against the wrong picture and would recover noise, which
    /// <see cref="Locate"/> refuses rather than draws.
    /// </remarks>
    public static TimeblockCard? From(
        string timeblock, IReadOnlyList<DecodedImage> frames, DecodedImage painting)
    {
        ArgumentNullException.ThrowIfNull(timeblock);
        ArgumentNullException.ThrowIfNull(frames);

        string code = timeblock.ToUpperInvariant();

        if (frames.Count == 0 ||
            frames[0].Width <= 0 || frames[0].Height <= 0 ||
            frames[0].Width > painting.Width || frames[0].Height > painting.Height ||
            frames.Any(f => f.Width != frames[0].Width || f.Height != frames[0].Height))
        {
            Log.Warning(
                $"WARNING GK3R3458: {code}'s lettering is misshapen, or is bigger than the "
                + "painting it is meant to sit on. The name is written out instead.");

            return null;
        }

        (int hintLeft, int hintUp) = Where(code);

        // Up from the bottom, which is how the original anchors it and the only way that
        // survives 306P being a row taller than every other painting.
        int hintTop = painting.Height - hintUp - frames[0].Height;

        if (Locate(frames[0], painting, hintLeft, hintTop) is not { } placed)
        {
            Log.Warning(
                $"WARNING GK3R3458: {code}'s lettering does not sit anywhere on its own "
                + "painting, so it cannot be lifted off it. The name is written out instead.");

            return null;
        }

        (int left, int top) = placed;

        if (left != hintLeft || top != hintTop)
        {
            Log.Info(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Card: {code}'s lettering is at {left},{top} rather than {hintLeft},{hintTop}"));
        }

        var lifted = new DecodedImage[frames.Count];

        for (int i = 0; i < frames.Count; i++)
        {
            lifted[i] = Lettering(frames[i], painting, left, top);
        }

        return new TimeblockCard(code, lifted, left, top, painting.Width, painting.Height);
    }
    /// <summary>Reads a bitmap out of the archives, or nothing when it will not decode.</summary>
    /// <param name="archives">The barns.</param>
    /// <param name="name">The asset name, with extension.</param>
    /// <returns>The picture, or null.</returns>
    private static DecodedImage? Picture(GameArchives archives, string name)
    {
        try
        {
            return archives.Read(name) is { } bytes
                ? BitmapDecoder.Decode(bytes, name)
                : null;
        }
        catch (FormatException error)
        {
            Log.Warning($"WARNING GK3R3458: {name} would not decode. ({error.Message})");
            return null;
        }
    }

    /// <summary>
    /// Where a timeblock's lettering sits on its painting, up from the bottom-left corner.
    /// </summary>
    /// <param name="code">The timeblock, as <c>102P</c>.</param>
    /// <returns>The offset to start looking at, in the painting's own pixels.</returns>
    /// <remarks>
    /// <para>
    /// A table, because the artists placed each one by hand and nothing in the data says
    /// where. It is a hint rather than the answer: <see cref="Locate"/> proves it against
    /// the art at load and searches a few pixels around it if it is wrong, so a language
    /// pack that nudged its lettering does not need an entry here.
    /// </para>
    /// <para>
    /// <b>Up from the bottom, not down from the top.</b> That is how the original anchors
    /// it, and it is not an arbitrary choice: <c>TBT306P.BMP</c> is 640x481 where every
    /// other painting is 640x480, and measuring from the top would put that one card's
    /// lettering a pixel out — which is exactly what happened when this was written down
    /// as a distance from the top instead.
    /// </para>
    /// <para>
    /// Nearly all of them are the same 14,64. <c>102P</c> starts six pixels further left and
    /// <c>312P</c> one, and a handful sit a pixel up or down from the rest.
    /// </para>
    /// </remarks>
    private static (int Left, int Up) Where(string code) => code switch
    {
        "110A" => (14, 64),
        "112P" => (14, 63),
        "102P" => (8, 63),
        "104P" => (14, 63),
        "106P" => (14, 64),
        "202A" => (14, 64),
        "207A" => (14, 64),
        "210A" => (14, 65),
        "212P" => (14, 64),
        "202P" => (14, 63),
        "205P" => (14, 64),
        "307A" => (14, 64),
        "310A" => (14, 63),
        "312P" => (13, 64),
        "303P" => (14, 64),
        "306P" => (14, 64),
        "309P" => (14, 64),
        _ => (14, 64),
    };
    /// <summary>
    /// Finds where a frame was cut from the painting.
    /// </summary>
    /// <param name="frame">One frame of the lettering, ideally the first.</param>
    /// <param name="painting">The card's painting.</param>
    /// <param name="hintLeft">Where to start looking.</param>
    /// <param name="hintTop">And down.</param>
    /// <returns>The offset, or null when the frame does not belong to this painting.</returns>
    /// <remarks>
    /// <para>
    /// <b>Lettering only ever lightens.</b> The frames are the painting with white blended
    /// into them, so at the offset they were cut from, no channel of any pixel is darker
    /// than the painting under it. One pixel out and thousands are — the letters land on
    /// the wrong ground, and half of what they overlap goes the wrong way. So the score is
    /// the count of samples that came out darker, and the right offset scores nought.
    /// </para>
    /// <para>
    /// Two out of 255 is allowed per sample against a language pack that re-encoded its
    /// painting; a misplacement is off by far more than that. A sixty-fourth of the samples
    /// may fail outright for the same reason, which leaves room to spare: measured against
    /// the shipped art, one pixel out fails between a twentieth of them and a quarter,
    /// the twentieth being 202A, whose name is the shortest in the game.
    /// </para>
    /// </remarks>
    private static (int Left, int Top)? Locate(
        DecodedImage frame, DecodedImage painting, int hintLeft, int hintTop)
    {
        const int Reach = 4;
        const int Tolerance = 2;

        int samples = frame.Width * frame.Height * 3;
        int allowed = samples / 64;

        int bestScore = int.MaxValue;
        (int Left, int Top)? best = null;

        // The hint first, and outwards from it, so that the offset a tie goes to is the one
        // this engine wrote down rather than whichever the loop happened to reach first.
        foreach ((int left, int top) in Around(hintLeft, hintTop, Reach))
        {
            if (left < 0 || top < 0 ||
                left + frame.Width > painting.Width || top + frame.Height > painting.Height)
            {
                continue;
            }

            int darker = Darker(frame, painting, left, top, Tolerance, bestScore);

            if (darker < bestScore)
            {
                bestScore = darker;
                best = (left, top);

                if (darker == 0)
                {
                    break;
                }
            }
        }

        return bestScore <= allowed ? best : null;
    }

    /// <summary>Offsets to try, the hint first and then rings outwards from it.</summary>
    /// <param name="left">The hint.</param>
    /// <param name="top">And down.</param>
    /// <param name="reach">How many pixels out to go.</param>
    /// <returns>The offsets.</returns>
    private static IEnumerable<(int Left, int Top)> Around(int left, int top, int reach)
    {
        yield return (left, top);

        for (int ring = 1; ring <= reach; ring++)
        {
            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring)
                    {
                        yield return (left + dx, top + dy);
                    }
                }
            }
        }
    }

    /// <summary>How many samples of a frame are darker than the painting under it.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="painting">The painting.</param>
    /// <param name="left">Where the frame is being tried.</param>
    /// <param name="top">And down.</param>
    /// <param name="tolerance">How much darker still counts as equal.</param>
    /// <param name="giveUpAt">Stop counting once this many have failed.</param>
    /// <returns>The count, which may have stopped early.</returns>
    private static int Darker(
        DecodedImage frame,
        DecodedImage painting,
        int left,
        int top,
        int tolerance,
        int giveUpAt)
    {
        int count = 0;

        for (int y = 0; y < frame.Height; y++)
        {
            int from = y * frame.Width * 4;
            int under = (((top + y) * painting.Width) + left) * 4;

            for (int x = 0; x < frame.Width; x++, from += 4, under += 4)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    if (painting.Pixels[under + channel] - frame.Pixels[from + channel] > tolerance)
                    {
                        count++;
                    }
                }
            }

            if (count >= giveUpAt)
            {
                return count;
            }
        }

        return count;
    }

    /// <summary>
    /// Lifts the lettering off the painting it was blended into.
    /// </summary>
    /// <param name="frame">The frame, as it is in the archives.</param>
    /// <param name="painting">The card's painting.</param>
    /// <param name="left">Where the frame was cut from.</param>
    /// <param name="top">And down.</param>
    /// <returns>White pixels carrying the lettering's coverage as alpha.</returns>
    /// <remarks>
    /// <para>
    /// Solving <c>frame = painting + alpha * (255 - painting)</c> a channel at a time and
    /// pooling the three, which is a least-squares fit over however much room each channel
    /// had. Doing it per channel and averaging would weight a channel that had almost no
    /// room to move as heavily as one that had all of it, and that channel is nothing but
    /// rounding.
    /// </para>
    /// <para>
    /// Where the painting is already white there is no room at all and no answer: the
    /// letter is invisible against it either way, so the pixel is left clear.
    /// </para>
    /// </remarks>
    private static DecodedImage Lettering(
        DecodedImage frame, DecodedImage painting, int left, int top)
    {
        const int LeastRoom = 8;

        var pixels = new byte[frame.Width * frame.Height * 4];

        for (int y = 0; y < frame.Height; y++)
        {
            int from = y * frame.Width * 4;
            int under = (((top + y) * painting.Width) + left) * 4;

            for (int x = 0; x < frame.Width; x++, from += 4, under += 4)
            {
                int lit = 0;
                int room = 0;

                for (int channel = 0; channel < 3; channel++)
                {
                    int ground = painting.Pixels[under + channel];
                    int spare = 255 - ground;

                    if (spare >= LeastRoom)
                    {
                        lit += Math.Max(0, frame.Pixels[from + channel] - ground);
                        room += spare;
                    }
                }

                pixels[from] = 255;
                pixels[from + 1] = 255;
                pixels[from + 2] = 255;
                pixels[from + 3] = room > 0
                    ? (byte)Math.Clamp(((lit * 255) + (room / 2)) / room, 0, 255)
                    : (byte)0;
            }
        }

        return new DecodedImage(frame.Width, frame.Height, pixels, HasAlpha: true, "lettering");
    }
}
