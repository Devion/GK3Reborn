// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game;

/// <summary>
/// Reads the saves the 1999 game wrote, and turns them into saves this engine can open.
/// </summary>
public static class OriginalSaves
{
    /// <summary>
    /// Imports every original save in a directory that has not been imported yet.
    /// </summary>
    /// <param name="directory">Where the original game kept them, usually its install root.</param>
    /// <param name="store">Where the imports go.</param>
    /// <param name="scores">The score table, for what each event is worth.</param>
    /// <param name="introductions">
    /// The introductions table, for who a past timeblock has already been met in.
    /// </param>
    /// <returns>How many were imported this time.</returns>
    public static int Import(
        string directory,
        SaveStore store,
        ScoreEvents scores,
        Story.Introductions introductions)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(introductions);

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        int imported = 0;

        foreach (string path in Directory.EnumerateFiles(directory, "*.gk3"))
        {
            string slot = "gk3-" + Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

            if (!SaveStore.IsSlotName(slot) || Brought(store, slot))
            {
                continue;
            }

            if (Read(path, scores) is not { } read)
            {
                continue;
            }

            (OriginalSaveHeader header, OriginalSaveState? state) = read;

            store.Write(slot, Recovered(header, state, scores, introductions));

            // And the picture the original took when it saved, decoded and kept beside the
            // import like any other slot's. A picture that does not decode costs the slot
            // its thumbnail and nothing else.
            if (header.Picture is { Length: > 0 } picture)
            {
                try
                {
                    store.Illustrate(
                        slot, Formats.Bitmaps.PngReader.Decode(picture, slot));
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException)
                {
                }
            }

            imported++;
        }

        return imported;
    }

    /// <summary>
    /// Which reader an import was made with.
    /// </summary>
    private const string Reader = "original:2";

    /// <summary>Whether a slot already holds this save, read by this reader.</summary>
    /// <param name="store">Where the imports go.</param>
    /// <param name="slot">The slot the file would be imported to.</param>
    /// <returns>True when there is nothing to do.</returns>
    private static bool Brought(SaveStore store, string slot) =>
        store.Read(slot, out SaveFault fault) is { } already &&
        fault == SaveFault.None &&
        string.Equals(already.Imported, Reader, StringComparison.Ordinal);

    /// <summary>What one original save says about itself.</summary>
    /// <param name="path">The <c>.gk3</c> file.</param>
    /// <returns>The summary, or null when the file is not an original save.</returns>
    public static (string Title, string Location, Timeblock When, int Score, byte[]? Picture)?
        Summary(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (Read(path) is not { } read)
        {
            return null;
        }

        OriginalSaveHeader header = read.Header;

        return (header.Title, header.Location, header.When, header.Score, header.Picture);
    }

    /// <summary>Reads a save, header and state both.</summary>
    /// <param name="path">The <c>.gk3</c> file.</param>
    /// <param name="events">
    /// The score table, for finding the save's own. Without it only the header is read,
    /// which is all a list of saves needs and all the summary above asks for.
    /// </param>
    /// <returns>
    /// The header and, when the state could be read, everything it holds; null when the file
    /// is not an original save at all. A header with no state is the honest answer for a file
    /// this engine can summarise and not unpack.
    /// </returns>
    public static (OriginalSaveHeader Header, OriginalSaveState? State)? Read(
        string path, ScoreEvents? events = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (OriginalSaveFile.ReadHeader(bytes) is not { } header)
        {
            return null;
        }

        if (events is null || OriginalSaveFile.ReadBody(bytes, header) is not { } body)
        {
            return (header, null);
        }

        return (header, OriginalSaveFile.ReadState(body, events.Names));
    }

    /// <summary>A save this engine can load, built from what the original one holds.</summary>
    /// <param name="header">What the save says about itself.</param>
    /// <param name="original">Its state, or null when that could not be read.</param>
    /// <param name="scores">The score table.</param>
    /// <param name="introductions">The introductions table.</param>
    /// <returns>The save.</returns>
    private static SaveGame Recovered(
        OriginalSaveHeader header,
        OriginalSaveState? original,
        ScoreEvents scores,
        Story.Introductions introductions)
    {
        var story = new GameState
        {
            Timeblock = header.When,
            Ego = Whose(header.When),
        };

        story.Location = header.Location;

        if (original is null)
        {
            return Assumed(header, story, scores, introductions);
        }

        // The score, replayed rather than restated. Awarding each event its own worth adds
        // up to the number the header carries — checked against three real saves, at 99, 140
        // and 213 points — which is the strongest single check that the state has been read
        // correctly, because the two numbers come from opposite ends of the file.
        foreach (string earned in original.Scored)
        {
            story.AwardScore(earned, scores.Worth(earned));
        }

        foreach (string flag in original.Flags)
        {
            story.SetFlag(flag);
        }

        foreach ((string name, int value) in original.Variables)
        {
            story.SetVariable(name, value);
        }

        Counted(story, original);

        foreach ((string noun, int count) in original.ChatCounts)
        {
            story.SetChatCount(noun, count);
        }

        foreach ((string actor, string where) in original.ActorLocations)
        {
            story.SetActorLocation(actor, where);
        }

        // Each room's visits belong to whoever was playing at that point in the story: the
        // original counts them for the ego alone, and which of the two that was is not
        // something the save has to record because the timeblock already says.
        foreach ((string where, string when, int been) in original.Visits)
        {
            if (Timeblock.TryParse(when, out Timeblock block))
            {
                story.SetLocationCount(Whose(block), where, block, been);
            }
        }

        Carried(story, original);
        Scanned(story, original);
        Met(story, original, introductions, header.When);

        return Written(story, header);
    }

    /// <summary>Puts the noun, verb and topic counts back.</summary>
    /// <param name="story">The game being built.</param>
    /// <param name="original">What the save holds.</param>
    private static void Counted(GameState story, OriginalSaveState original)
    {
        foreach (OriginalCount count in original.Counts)
        {
            if (count.Count <= 0)
            {
                continue;
            }

            story.SetNounVerbCount(
                count.Gabriel ? "GABRIEL" : "GRACE", count.Noun, count.Verb, count.Count);

            if (!count.Verb.StartsWith("T_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Whichever of the two raised it. A topic count in this engine is not per
            // character, and taking the larger keeps a topic both of them have asked about
            // from reading as though only the second had.
            story.SetTopicCount(
                count.Noun,
                count.Verb,
                Math.Max(story.GetTopicCount(count.Noun, count.Verb), count.Count));
        }
    }

    /// <summary>Fills the two of them back up.</summary>
    /// <param name="story">The game being built.</param>
    /// <param name="original">What the save holds.</param>
    private static void Carried(GameState story, OriginalSaveState original)
    {
        foreach ((string item, string status) in original.Items)
        {
            if (status.Equals("GabeHas", StringComparison.Ordinal) ||
                status.Equals("BothHave", StringComparison.Ordinal))
            {
                story.Inventory.Add("GABRIEL", item);
            }

            if (status.Equals("GraceHas", StringComparison.Ordinal) ||
                status.Equals("BothHave", StringComparison.Ordinal))
            {
                story.Inventory.Add("GRACE", item);
            }
        }

        if (original.GabrielItem.Length > 0)
        {
            story.Inventory.SetActive("GABRIEL", original.GabrielItem);
        }

        if (original.GraceItem.Length > 0)
        {
            story.Inventory.SetActive("GRACE", original.GraceItem);
        }
    }

    /// <summary>Puts back what has been through Sidney's scanner.</summary>
    /// <param name="story">The game being built.</param>
    /// <param name="original">What the save holds.</param>
    private static void Scanned(GameState story, OriginalSaveState original)
    {
        foreach (OriginalCount count in original.Counts)
        {
            if (count.Count <= 0 ||
                !count.Verb.Equals("SCANNER", StringComparison.OrdinalIgnoreCase) ||
                Sidney.SidneyFiles.For(count.Noun) is not { } file)
            {
                continue;
            }

            story.AddSidneyFile(file.Id);
            story.RecordSidneyScan(file.Item);
        }
    }

    /// <summary>Says who the player is to be treated as having met.</summary>
    /// <param name="story">The game being built.</param>
    /// <param name="original">What the save holds.</param>
    /// <param name="introductions">The introductions table.</param>
    /// <param name="when">Where the story has got to.</param>
    private static void Met(
        GameState story,
        OriginalSaveState original,
        Story.Introductions introductions,
        Timeblock when)
    {
        foreach (string noun in introductions.MetBy(when))
        {
            story.Introduce(noun);
        }

        foreach (OriginalCount count in original.Counts)
        {
            if (count.Count > 0 &&
                count.Verb.Equals("T_INTRODUCE", StringComparison.OrdinalIgnoreCase))
            {
                story.Introduce(count.Noun);
            }
        }
    }

    /// <summary>
    /// A save built from the header alone, for a file whose state could not be read.
    /// </summary>
    /// <param name="header">What the save says about itself.</param>
    /// <param name="story">The game being built.</param>
    /// <param name="scores">The score table.</param>
    /// <param name="introductions">The introductions table.</param>
    /// <returns>The save.</returns>
    private static SaveGame Assumed(
        OriginalSaveHeader header,
        GameState story,
        ScoreEvents scores,
        Story.Introductions introductions)
    {
        foreach (string name in scores.Names)
        {
            if (ScoreEvents.TimeblockOf(name) is { } when && when < header.When)
            {
                story.AwardScore(name, scores.Worth(name));
            }
        }

        foreach (string noun in introductions.MetBy(header.When))
        {
            story.Introduce(noun);
        }

        // At least what a new game starts with. Restoring with the pockets empty would lose
        // Prince James's card, which is the one item the story cannot move without.
        foreach ((string owner, string item) in StartingItems.Open())
        {
            story.Inventory.Add(owner, item);
        }

        return Written(story, header) with { Score = header.Score };
    }

    /// <summary>Writes a built game down as the save the import stores.</summary>
    /// <param name="story">The game.</param>
    /// <param name="header">What the original said about itself.</param>
    /// <returns>The save, dated as the original was rather than as of now.</returns>
    private static SaveGame Written(GameState story, OriginalSaveHeader header) =>
        story.Capture(header.Title.Length > 0 ? header.Title : "From the original game")
            with { Written = header.Written, Imported = Reader };

    /// <summary>Which of the two is playing at a point in the story.</summary>
    /// <param name="when">The point.</param>
    /// <returns>The ego's noun.</returns>
    private static string Whose(Timeblock when) =>
        GraceLeads.Contains(when) ? "GRACE" : "GABRIEL";

    /// <summary>The points in the story Grace plays, out of the walkthrough's own headings.</summary>
    private static readonly Timeblock[] GraceLeads =
    [
        new(2, 7, false), new(2, 12, true), new(2, 5, true),
        new(3, 7, false), new(3, 12, true), new(3, 6, true),
    ];
}
