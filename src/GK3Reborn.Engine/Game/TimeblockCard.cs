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
    public int At(double seconds) => Math.Clamp(
        (int)(Math.Max(0, seconds) * SequenceFile.FramesPerSecond), 0, Frames.Count - 1);

    /// <summary>
    /// Reads the lettering for a part of the day out of the archives.
    /// </summary>
    /// <param name="archives">The game's own barns.</param>
    /// <param name="timeblock">Which one, as <c>102P</c>.</param>
    /// <returns>The lettering, or null when the archives cannot supply it.</returns>
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
