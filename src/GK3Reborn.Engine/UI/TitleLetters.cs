// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.UI;

/// <summary>One letter of the game's name, as painted on the title sheet.</summary>
/// <param name="Character">Which letter it is, in lower case.</param>
/// <param name="Box">
/// Where it is painted, as a part of the whole sheet: left, top, width and height, each
/// from nought to one.
/// </param>
public readonly record struct TitleLetter(char Character, Vector4 Box)
{
    /// <summary>Where this letter lands on the screen once the sheet is laid out.</summary>
    /// <param name="sheet">Where the whole sheet would be, in pixels: left, top, width, height.</param>
    /// <returns>The letter's rectangle, in pixels.</returns>
    public Vector4 On(Vector4 sheet) => new(
        sheet.X + (Box.X * sheet.Z),
        sheet.Y + (Box.Y * sheet.W),
        Box.Z * sheet.Z,
        Box.W * sheet.W);
}

/// <summary>
/// Where every letter of the title is painted on <c>titlename.png</c>, and the word the
/// player can spell out of them.
/// </summary>
public static class TitleLetters
{
    /// <summary>What the player spells to start the party.</summary>
    public const string Word = "dance";

    /// <summary>The sheet's width, in pixels, that the boxes below were measured on.</summary>
    private const float SheetWidth = 1448f;

    /// <summary>Its height.</summary>
    private const float SheetHeight = 1086f;

    /// <summary>
    /// How far outside its paint a letter still answers a click, as a part of its height.
    /// </summary>
    private const float Margin = 0.25f;

    /// <summary>Every letter, top line first, left to right.</summary>
    public static IReadOnlyList<TitleLetter> All { get; } =
    [
        // blood of the sacred
        Letter('b', 224, 355, 267, 419),
        Letter('l', 289, 354, 310, 418),
        Letter('o', 331, 377, 372, 419),
        Letter('o', 395, 377, 436, 419),
        Letter('d', 458, 354, 501, 419),
        Letter('o', 549, 377, 591, 419),
        Letter('f', 614, 354, 645, 418),
        Letter('t', 688, 368, 714, 418),
        Letter('h', 735, 354, 780, 418),
        Letter('e', 801, 377, 839, 419),
        Letter('s', 891, 377, 923, 419),
        Letter('a', 946, 377, 989, 419),
        Letter('c', 1010, 377, 1048, 419),
        Letter('r', 1071, 377, 1104, 418),
        Letter('e', 1125, 377, 1163, 419),
        Letter('d', 1185, 354, 1227, 419),

        // GABRIEL KNIGHT
        Letter('g', 54, 483, 169, 653),
        Letter('a', 162, 484, 272, 651),
        Letter('b', 272, 484, 357, 651),
        Letter('r', 370, 486, 474, 650),
        Letter('i', 474, 486, 518, 650),
        Letter('e', 536, 484, 608, 651),
        Letter('l', 625, 486, 700, 650),
        Letter('k', 743, 485, 850, 652),
        Letter('n', 850, 485, 975, 652),
        Letter('i', 987, 486, 1029, 650),
        Letter('g', 1044, 483, 1160, 653),
        Letter('h', 1170, 486, 1290, 650),
        Letter('t', 1293, 484, 1391, 650),

        // blood of the damned
        Letter('b', 225, 696, 269, 759),
        Letter('l', 288, 696, 309, 758),
        Letter('o', 328, 718, 369, 758),
        Letter('o', 388, 718, 429, 758),
        Letter('d', 449, 696, 491, 759),
        Letter('o', 534, 718, 575, 758),
        Letter('f', 593, 695, 625, 758),
        Letter('t', 658, 708, 685, 758),
        Letter('h', 702, 696, 746, 758),
        Letter('e', 764, 717, 801, 758),
        Letter('d', 843, 718, 887, 758),
        Letter('a', 906, 718, 955, 758),
        Letter('m', 971, 718, 1042, 758),
        Letter('n', 1062, 718, 1107, 758),
        Letter('e', 1126, 718, 1163, 758),
        Letter('d', 1183, 695, 1224, 759),
    ];

    /// <summary>Which letter a point on the screen is on, if any.</summary>
    /// <param name="point">The point, in pixels.</param>
    /// <param name="sheet">Where the whole sheet is laid out, in pixels.</param>
    /// <returns>Its index in <see cref="All"/>, or -1 when the point is on none of them.</returns>
    public static int At(Vector2 point, Vector4 sheet)
    {
        int found = -1;
        float nearest = float.MaxValue;

        for (int i = 0; i < All.Count; i++)
        {
            Vector4 box = All[i].On(sheet);
            float grow = box.W * Margin;

            if (point.X < box.X - grow || point.X > box.X + box.Z + grow ||
                point.Y < box.Y - grow || point.Y > box.Y + box.W + grow)
            {
                continue;
            }

            // The nearest centre wins where two margins overlap, so a click between an
            // 'a' and an 'm' goes to whichever it is closer to rather than to whichever is
            // listed first.
            var centre = new Vector2(box.X + (box.Z / 2f), box.Y + (box.W / 2f));
            float distance = Vector2.DistanceSquared(point, centre);

            if (distance < nearest)
            {
                nearest = distance;
                found = i;
            }
        }

        return found;
    }

    private static TitleLetter Letter(char c, int left, int top, int right, int bottom) =>
        new(c, new Vector4(
            left / SheetWidth,
            top / SheetHeight,
            (right - left) / SheetWidth,
            (bottom - top) / SheetHeight));
}

/// <summary>
/// The word being spelled on the title, one click at a time.
/// </summary>
public sealed class DanceCode
{
    private readonly List<int> _lit = [];

    /// <summary>Which letters are lit, as indices into <see cref="TitleLetters.All"/>.</summary>
    public IReadOnlyList<int> Lit => _lit;

    /// <summary>How many letters of the word have been spelled.</summary>
    public int Spelled => _lit.Count;

    /// <summary>Whether the whole word has been spelled.</summary>
    public bool Complete => _lit.Count >= TitleLetters.Word.Length;

    /// <summary>Takes a click on a letter.</summary>
    /// <param name="letter">Which letter, as an index into <see cref="TitleLetters.All"/>.</param>
    /// <returns>
    /// Whether the letter was the next one of the word. False also for a click on nothing.
    /// </returns>
    public bool Click(int letter)
    {
        if (letter < 0 || letter >= TitleLetters.All.Count || Complete)
        {
            return false;
        }

        char wanted = TitleLetters.Word[_lit.Count];

        if (TitleLetters.All[letter].Character == wanted)
        {
            _lit.Add(letter);
            return true;
        }

        // Wrong letter. The first letter of the word again, if that is what was clicked,
        // rather than nothing: d-d-a-n-c-e is somebody who mis-clicked, not somebody who
        // gave up.
        _lit.Clear();

        if (TitleLetters.All[letter].Character == TitleLetters.Word[0])
        {
            _lit.Add(letter);
        }

        return false;
    }

    /// <summary>Puts the word back to nothing.</summary>
    public void Reset() => _lit.Clear();
}
