// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Buffers.Binary;
using System.Text;

namespace GK3Reborn.Game;

/// <summary>
/// Reads the saves the 1999 game wrote, as far as they can honestly be read.
/// </summary>
/// <remarks>
/// <para>
/// A retail <c>.gk3</c> save is two things. The front is a summary — who saved, when, where
/// they were in the story, what they had scored, and a picture — in a fixed layout that
/// G-Engine documents field by field. The rest is the original engine serialising nearly
/// every live class in itself through RTTI, which no reimplementation reads; G-Engine
/// writes its own data there too.
/// </para>
/// <para>
/// So the import takes what the summary states and recovers what follows from it: the
/// timeblock, the location, the score, and the save's own name. Everything a point in the
/// story implies — every score event belonging to a timeblock already behind the player —
/// is marked earned by the same reasoning the schema-1 migration uses: the story cannot
/// leave a timeblock until its rules are met, and marking those events is also what stops
/// them paying out twice. What the player was carrying beyond their starting items, and the
/// flags of the current block, are not in the summary and are not invented.
/// </para>
/// </remarks>
public static class OriginalSaves
{
    /// <summary>
    /// Imports every original save in a directory that has not been imported yet.
    /// </summary>
    /// <param name="directory">Where the original game kept them, usually its install root.</param>
    /// <param name="store">Where the imports go.</param>
    /// <param name="scores">The score table, for what a past timeblock is worth.</param>
    /// <returns>How many were imported this time.</returns>
    /// <remarks>
    /// Each import is filed under the original file's own name — <c>gk3-save0004</c> — so
    /// importing is idempotent: a slot that exists is a save already brought across, and
    /// deleting the import is how somebody asks for it to be brought across again.
    /// </remarks>
    public static int Import(string directory, SaveStore store, ScoreEvents scores)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scores);

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        int imported = 0;

        foreach (string path in Directory.EnumerateFiles(directory, "*.gk3"))
        {
            string slot = "gk3-" + Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

            if (!SaveStore.IsSlotName(slot) || store.Read(slot, out _) is not null)
            {
                continue;
            }

            if (Summary(path) is not { } summary)
            {
                continue;
            }

            store.Write(slot, Recovered(summary, scores));

            // And the picture the original took when it saved, decoded and kept beside the
            // import like any other slot's. A picture that does not decode costs the slot
            // its thumbnail and nothing else.
            if (summary.Picture is { Length: > 0 } picture)
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

    /// <summary>What one original save says about itself.</summary>
    /// <param name="path">The <c>.gk3</c> file.</param>
    /// <returns>The summary, or null when the file is not an original save.</returns>
    public static (string Title, string Location, Timeblock When, int Score, byte[]? Picture)?
        Summary(string path)
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

        // The fixed front: "GK3!Save", a version, and then the size of the rest of the
        // header — 232 in every save the retail game wrote — so the summary that follows
        // can be reached without understanding a byte in between. The last four letters of
        // the magic are compared without case: the reference writes SAVE and the retail
        // game wrote Save, and three real saves are how the difference was found.
        if (bytes.Length < 16 ||
            !"GK3!"u8.SequenceEqual(bytes.AsSpan(0, 4)) ||
            !Encoding.ASCII.GetString(bytes, 4, 4).Equals(
                "Save", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        int at = 16 + headerSize;

        // The summary: the save's name, the location, the timeblock, then the score. Each
        // string is a 32-bit length, its bytes, and a terminating nul the length does not
        // count. Measured off real saves rather than off the reference, whose own writer
        // puts a version number first that the retail files do not have.
        if (!TryString(bytes, ref at, out string title) ||
            !TryString(bytes, ref at, out string location) ||
            !TryString(bytes, ref at, out string when) ||
            !TryInt(bytes, ref at, out int score) ||
            !Timeblock.TryParse(when, out Timeblock timeblock))
        {
            return null;
        }

        // Past the maximum score and the CD number sits the picture the original took when
        // it saved, as a plain PNG. The one part of a retail save this engine can carry
        // over pixel for pixel.
        byte[]? picture = null;

        if (TryInt(bytes, ref at, out _) &&
            TryInt(bytes, ref at, out _) &&
            TryInt(bytes, ref at, out int thumbnail) &&
            thumbnail > 0 &&
            at + thumbnail <= bytes.Length)
        {
            picture = bytes.AsSpan(at, thumbnail).ToArray();
        }

        return (title, location.ToUpperInvariant(), timeblock, score, picture);
    }

    /// <summary>A save this engine can load, built from what the summary states.</summary>
    private static SaveGame Recovered(
        (string Title, string Location, Timeblock When, int Score, byte[]? Picture) summary,
        ScoreEvents scores)
    {
        // Everything a point in the story implies. The same reasoning as the schema-1
        // migration: a save standing in day two has been through the whole of day one, and
        // marking those events earned is also what stops them scoring twice.
        List<string> earned =
        [
            .. scores.Names.Where(name =>
                ScoreEvents.TimeblockOf(name) is { } when && when < summary.When),
        ];

        return new SaveGame
        {
            SchemaVersion = SaveGame.CurrentSchema,
            Written = DateTimeOffset.UtcNow,
            Title = summary.Title.Length > 0 ? summary.Title : "From the original game",
            Day = summary.When.Day,
            Hour = summary.When.Hour,
            Afternoon = summary.When.IsAfternoon,
            Location = summary.Location,
            Ego = GraceLeads.Contains(summary.When) ? "GRACE" : "GABRIEL",
            Score = summary.Score,
            Scored = earned,

            // At least what a new game starts with. The summary says nothing about the
            // pockets, and restoring an import with them empty would lose Prince James's
            // card — the one item the story cannot move without. What was picked up along
            // the way is not recoverable and is not invented; a player may have to pick a
            // thing or two up again.
            Inventories =
            [
                .. StartingItems.Open()
                    .GroupBy(given => given.Owner, StringComparer.OrdinalIgnoreCase)
                    .Select(owner => new SavedInventory(
                        owner.Key, [.. owner.Select(given => given.Item)], null)),
            ],
        };
    }

    /// <summary>The points in the story Grace plays, out of the walkthrough's own headings.</summary>
    private static readonly Timeblock[] GraceLeads =
    [
        new(2, 7, false), new(2, 12, true), new(2, 5, true),
        new(3, 7, false), new(3, 12, true), new(3, 6, true),
    ];

    private static bool TryInt(byte[] bytes, ref int at, out int value)
    {
        value = 0;

        if (at + 4 > bytes.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
        at += 4;
        return true;
    }

    private static bool TryString(byte[] bytes, ref int at, out string value)
    {
        value = string.Empty;

        if (!TryInt(bytes, ref at, out int length) ||
            length is < 0 or > 4096 ||
            at + length > bytes.Length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(bytes, at, length);
        at += length;

        // The terminating nul the length does not count.
        if (at < bytes.Length && bytes[at] == 0)
        {
            at++;
        }

        return true;
    }
}
