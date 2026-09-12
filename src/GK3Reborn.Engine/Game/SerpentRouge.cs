// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Game;

/// <summary>One verse of Le Serpent Rouge, and where it sits on its page.</summary>
/// <param name="Noun">The noun the inventory's action files answer for it.</param>
/// <param name="Flag">The flag that says Sidney has worked this verse out.</param>
/// <param name="Page">Which of the six pages it is on, from one.</param>
/// <param name="Stem">The start of its two pictures' names — <c>LSR_PG1_AQU</c> — which end <c>_LIT</c> and <c>_FIN</c>.</param>
/// <param name="X">Its left edge, in the page's own pixels.</param>
/// <param name="Y">Its bottom edge measured up from the foot of the page, as the retail engine anchors it.</param>
/// <param name="Width">How wide its picture is.</param>
/// <param name="Height">How tall.</param>
public sealed record SerpentRougeVerse(
    string Noun, string Flag, int Page, string Stem, int X, int Y, int Width, int Height)
{
    /// <summary>Where the verse is on the page, top-left down, in the page's own pixels.</summary>
    public Vector4 Bounds => new(X, SerpentRouge.PageHeight - Y - Height, Width, Height);
}

/// <summary>A verse as it is shown: which one, and the picture over it, if any.</summary>
/// <param name="Verse">Which verse.</param>
/// <param name="Picture">
/// The file drawn over it — lit while it is the one to work on, struck through once it is
/// done — or null for a verse the story has not reached, which is drawn as the page has it
/// and can still be clicked.
/// </param>
public sealed record SerpentRougeRegion(SerpentRougeVerse Verse, string? Picture);

/// <summary>A page of the poem, ready to draw.</summary>
/// <param name="Number">Which page, from one.</param>
/// <param name="Picture">The page's own file.</param>
/// <param name="Regions">The verses on it.</param>
public sealed record SerpentRougePage(int Number, string Picture, IReadOnlyList<SerpentRougeRegion> Regions);

/// <summary>
/// Le Serpent Rouge, the poem in Grace's bag, read a page at a time.
/// </summary>
public static class SerpentRouge
{
    /// <summary>The poem, as the inventory names it.</summary>
    public const string Item = "LSR";

    /// <summary>The variable the open page is kept in while the poem is open.</summary>
    public const string PageVariable = "LsrPage";

    /// <summary>How many pages there are.</summary>
    public const int Pages = 6;

    /// <summary>The width of a page picture.</summary>
    public const int PageWidth = 640;

    /// <summary>The height of a page picture.</summary>
    public const int PageHeight = 400;

    /// <summary>The thirteen verses, in the order the poem — and Sidney — takes them.</summary>
    public static IReadOnlyList<SerpentRougeVerse> Verses { get; } =
    [
        new("LSR_AQUARIUS", "Aquarius", 1, "LSR_PG1_AQU", 150, 188, 305, 142),
        new("LSR_PISCES", "Pisces", 1, "LSR_PG1_PIS", 150, 12, 307, 178),
        new("LSR_ARIES", "Aries", 2, "LSR_PG2_ARI", 165, 217, 294, 172),
        new("LSR_TAURUS", "Taurus", 2, "LSR_PG2_TAU", 156, 23, 308, 183),
        new("LSR_GEMINI", "Gemini", 3, "LSR_PG3_GEM", 170, 162, 299, 232),
        new("LSR_CANCER", "Cancer", 3, "LSR_PG3_CAN", 170, 163, 299, 97),
        new("LSR_LEO", "Leo", 3, "LSR_PG3_LEO", 154, 14, 311, 149),
        new("LSR_VIRGO", "Virgo", 4, "LSR_PG4_VIR", 170, 219, 292, 176),
        new("LSR_LIBRA", "Libra", 4, "LSR_PG4_LIB", 155, 10, 309, 209),
        new("LSR_SCORPIO", "Scorpio", 5, "LSR_PG5_SCO", 172, 204, 294, 191),
        new("LSR_OPHIUCHUS", "Ophiuchus", 5, "LSR_PG5_OPH", 155, 12, 312, 190),
        new("LSR_SAGITTARIUS", "Sagittarius", 6, "LSR_PG6_SAG", 149, 232, 323, 166),
        new("LSR_CAPRICORN", "Capricorn", 6, "LSR_PG6_CAP", 135, 12, 340, 221),
    ];

    /// <summary>
    /// Whether a close-up's subject is the poem — the poem itself, or one of its verses.
    /// </summary>
    /// <param name="subject">What the close-up is of.</param>
    /// <returns>True for the poem or a verse.</returns>
    public static bool IsReader(string? subject) =>
        string.Equals(subject, Item, StringComparison.OrdinalIgnoreCase) ||
        Verses.Any(v => string.Equals(v.Noun, subject, StringComparison.OrdinalIgnoreCase));

    /// <summary>The verse a subject names, or null for the poem itself.</summary>
    /// <param name="subject">What the close-up is of.</param>
    /// <returns>The verse, or null.</returns>
    public static SerpentRougeVerse? VerseOf(string? subject) =>
        Verses.FirstOrDefault(v => string.Equals(v.Noun, subject, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The page the poem opens at: the page of the verse in hand.
    /// </summary>
    /// <param name="story">The game.</param>
    /// <returns>A page number from one.</returns>
    public static int Opens(GameState story)
    {
        ArgumentNullException.ThrowIfNull(story);

        return story.GetFlag("Ophiuchus") ? 6
            : story.GetFlag("Libra") ? 5
            : story.GetFlag("Leo") ? 4
            : story.GetFlag("Taurus") ? 3
            : story.GetFlag("Pisces") ? 2
            : 1;
    }

    /// <summary>The page open now.</summary>
    /// <param name="story">The game.</param>
    /// <returns>A page number from one.</returns>
    public static int Page(GameState story)
    {
        ArgumentNullException.ThrowIfNull(story);

        int kept = story.GetVariable(PageVariable);

        return kept is >= 1 and <= Pages ? kept : Opens(story);
    }

    /// <summary>Turns the page.</summary>
    /// <param name="story">The game.</param>
    /// <param name="by">How many pages, forward for positive; held within the book.</param>
    /// <returns>The page now open.</returns>
    public static int Turn(GameState story, int by)
    {
        ArgumentNullException.ThrowIfNull(story);

        int page = Math.Clamp(Page(story) + by, 1, Pages);

        story.SetVariable(PageVariable, page);

        return page;
    }

    /// <summary>
    /// Forgets the open page, so that the poem opens at the verse in hand next time — which
    /// is what the retail engine does, keeping the page only while the close-up is up.
    /// </summary>
    /// <param name="story">The game.</param>
    public static void Close(GameState story)
    {
        ArgumentNullException.ThrowIfNull(story);

        if (story.GetVariable(PageVariable) != 0)
        {
            story.SetVariable(PageVariable, 0);
        }
    }

    /// <summary>
    /// A page as it stands in the story.
    /// </summary>
    /// <param name="story">The game.</param>
    /// <param name="page">Which page, from one.</param>
    /// <returns>The page's picture and its verses, each with the picture over it.</returns>
    public static SerpentRougePage Show(GameState story, int page)
    {
        ArgumentNullException.ThrowIfNull(story);

        List<SerpentRougeRegion> regions = [];

        for (int i = 0; i < Verses.Count; i++)
        {
            SerpentRougeVerse verse = Verses[i];

            if (verse.Page != page)
            {
                continue;
            }

            // Lit once the verse before it is done — the first is lit from the start —
            // and struck through once it is done itself. Before its turn it is the page as
            // printed, and still answers a click.
            bool done = story.GetFlag(verse.Flag);
            bool reached = i == 0 || story.GetFlag(Verses[i - 1].Flag);

            regions.Add(new SerpentRougeRegion(
                verse,
                done ? verse.Stem + "_FIN.BMP" : reached ? verse.Stem + "_LIT.BMP" : null));
        }

        return new SerpentRougePage(page, $"LSR_PG{page}_BASE.BMP", regions);
    }
}
