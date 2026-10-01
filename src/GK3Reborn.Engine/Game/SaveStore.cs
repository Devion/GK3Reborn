// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GK3Reborn.Foundation;

namespace GK3Reborn.Game;

/// <summary>Why a save could not be read.</summary>
public enum SaveFault
{
    /// <summary>It was read.</summary>
    None,

    /// <summary>There is no such save.</summary>
    Missing,

    /// <summary>The file is there and is not a save, or is truncated.</summary>
    Unreadable,

    /// <summary>It was written by a later build than this one.</summary>
    FromTheFuture,
}

/// <summary>What a slot holds, without reading the whole of it.</summary>
/// <param name="Slot">The slot's name, as <see cref="SaveStore"/> addresses it.</param>
/// <param name="Title">What the player called it.</param>
/// <param name="Summary">Day, hour and room.</param>
/// <param name="Written">When it was written, in UTC.</param>
/// <param name="Schema">Which schema version it carries.</param>
public sealed record SaveSlot(
    string Slot, string Title, string Summary, DateTimeOffset Written, int Schema)
{
    /// <summary>The original filename without its extension.</summary>
    public string Name { get; init; } = Slot;

    /// <summary>Why this entry cannot be restored.</summary>
    public SaveFault Fault { get; init; }
}

/// <summary>
/// Where saved games live, and the only thing that writes them.
/// </summary>
public sealed class SaveStore
{
    /// <summary>The slot a new room writes.</summary>
    public const string AutoSlot = "autosave";

    /// <summary>The slot the quick-save key writes.</summary>
    public const string QuickSlot = "quicksave";

    /// <summary>How many numbered slots the interface offers.</summary>
    public const int NumberedSlots = 20;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;

    private readonly string[] _directories;
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Opens the writable store and the other locations to index.</summary>
    /// <param name="directory">Where to write, or null for the game's usual location.</param>
    /// <param name="additionalDirectories">Other save folders, or null for platform defaults.</param>
    public SaveStore(string? directory = null, IEnumerable<string>? additionalDirectories = null)
    {
        _directory = Path.GetFullPath(directory ?? DefaultDirectory);
        IEnumerable<string> others = additionalDirectories ?? (directory is null
            ? [Path.Combine(AppContext.BaseDirectory, "saves"), Path.Combine(InstallPaths.UserData, "saves")]
            : []);
        _directories = [.. new[] { _directory }.Concat(others)
            .Select(Path.GetFullPath).Distinct(PathComparer)];
    }

    /// <summary>Where saves live.</summary>
    public static string DefaultDirectory => InstallPaths.WritableDirectory("saves");

    /// <summary>All folders indexed for saves, independently of write access.</summary>
    public IReadOnlyList<string> SearchDirectories => _directories;

    /// <summary>Where this store writes new saves.</summary>
    public string Directory => _directory;

    /// <summary>The name of a numbered slot.</summary>
    /// <param name="number">One upwards.</param>
    /// <returns>The slot name.</returns>
    public static string Numbered(int number) =>
        string.Create(CultureInfo.InvariantCulture, $"slot-{number:00}");

    /// <summary>
    /// Whether a slot name may become a file name.
    /// </summary>
    /// <param name="slot">The name.</param>
    /// <returns>True when it is safe.</returns>
    public static bool IsSlotName(string? slot)
    {
        if (slot is not { Length: > 0 and <= 64 })
        {
            return false;
        }

        foreach (char c in slot)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes a game to a slot.</summary>
    /// <param name="slot">Which slot.</param>
    /// <param name="save">The game.</param>
    /// <returns>True when it was written.</returns>
    public bool Write(string slot, SaveGame save)
    {
        ArgumentNullException.ThrowIfNull(save);

        if (!IsSlotName(slot))
        {
            return false;
        }

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            AtomicFile.WriteAllText(PathOf(slot), JsonSerializer.Serialize(save, Json));

            return true;
        }
        catch (Exception error) when (error is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Reads a slot.</summary>
    /// <param name="slot">Which slot.</param>
    /// <param name="fault">Why it could not be read, when it could not.</param>
    /// <returns>The game, or null.</returns>
    public SaveGame? Read(string slot, out SaveFault fault)
    {
        string? path = ReadPath(slot);
        if (path is null)
        {
            fault = SaveFault.Unreadable;
            return null;
        }
        return ReadFile(path, out fault);
    }

    /// <summary>Reads the newest compatible copy of a named slot across all save folders.</summary>
    /// <param name="slot">The unqualified slot name, such as quicksave.</param>
    /// <param name="fault">Why it could not be read.</param>
    /// <returns>The most recent saved game, or null.</returns>
    public SaveGame? ReadNewest(string slot, out SaveFault fault)
    {
        SaveSlot? newest = List().FirstOrDefault(entry =>
            string.Equals(entry.Name, slot, StringComparison.OrdinalIgnoreCase) && entry.Fault == SaveFault.None);
        return Read(newest?.Slot ?? slot, out fault);
    }

    private static SaveGame? ReadFile(string path, out SaveFault fault)
    {
        fault = SaveFault.Missing;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            SaveGame? save = JsonSerializer.Deserialize<SaveGame>(File.ReadAllText(path), Json);

            if (save is null || !HasState(save))
            {
                fault = SaveFault.Unreadable;
                return null;
            }

            // A later build may have written fields this one would silently drop, and
            // dropping a field of a save is losing a game. Refusing by name is the only
            // honest answer.
            if (save.SchemaVersion > SaveGame.CurrentSchema)
            {
                fault = SaveFault.FromTheFuture;
                return null;
            }

            fault = SaveFault.None;

            return Migrate(save);
        }
        catch (Exception error) when (error is IOException
                                          or JsonException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or ArgumentException)
        {
            fault = SaveFault.Unreadable;
            return null;
        }
    }

    /// <summary>What is in every slot, newest first.</summary>
    /// <returns>The slots, which is empty when nothing has been saved.</returns>
    public IReadOnlyList<SaveSlot> List()
    {
        List<SaveSlot> slots = [];
        for (int source = 0; source < _directories.Length; source++)
        {
            string[] paths;
            try
            {
                paths = System.IO.Directory.GetFiles(_directories[source])
                    .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, PathComparer).ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A missing or inaccessible folder must not hide the other folder's saves.
                continue;
            }

            foreach (string path in paths)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                // Qualified keys address the exact file, including its extension's casing.
                string key = source == 0 && IsSlotName(name) && Path.GetExtension(path) == ".json"
                    ? name : $"@{source}:{Path.GetFileName(path)}";
                SaveGame? save = ReadFile(path, out SaveFault fault);
                slots.Add(new SaveSlot(key, save?.Title ?? name, save?.Summary ?? string.Empty,
                    save?.Written ?? DateTimeOffset.MinValue, save?.SchemaVersion ?? 0)
                {
                    Name = name,
                    Fault = fault,
                });
            }
        }
        return [.. slots.OrderByDescending(s => s.Written)];
    }

    private string? ReadPath(string slot)
    {
        if (string.IsNullOrEmpty(slot))
        {
            return null;
        }
        if (slot.StartsWith('@') && slot.IndexOf(':') is int colon && colon > 1 &&
            int.TryParse(slot.AsSpan(1, colon - 1), out int source) && source >= 0 && source < _directories.Length)
        {
            string file = slot[(colon + 1)..];
            if (file.Length > 0 && file.IndexOfAny(['/', '\\', ':', '\0']) < 0 &&
                string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(_directories[source], file);
            }
            return null;
        }
        if (!IsSlotName(slot))
        {
            return null;
        }
        return _directories.Select(directory => Path.Combine(directory, slot + ".json"))
            .FirstOrDefault(File.Exists) ?? PathOf(slot);
    }

    private static bool HasState(SaveGame save) =>
        !string.IsNullOrWhiteSpace(save.Location) && !string.IsNullOrWhiteSpace(save.Ego) &&
        save.Title is not null && save.LastLocation is not null && save.CameraAngle is not null &&
        save.Flags is not null && save.Flags.All(x => x is not null) &&
        save.Variables is not null && save.NounVerbCounts is not null && save.TopicCounts is not null &&
        save.SaidTopics is not null && save.SaidTopics.All(x => x is not null) &&
        save.ChatCounts is not null && save.LocationCounts is not null && save.ActorLocations is not null &&
        save.ActorLocations.Values.All(x => x is not null) &&
        save.Scored is not null && save.Scored.All(x => x is not null) &&
        save.Introduced is not null && save.Introduced.All(x => x is not null) && save.Hints is not null &&
        save.RandomState is not null &&
        save.SidneyFiles is not null && save.SidneyFiles.All(x => x is not null) &&
        save.SidneyScans is not null && save.SidneyScans.All(x => x is not null) &&
        save.SidneyMarks is not null && save.SidneyMarks.All(x => x is not null) && save.SidneyFigures is not null &&
        save.SidneyFigures.All(x => x is not null && x.Shape is not null && x.Points is not null && x.Points.All(point => point is not null)) &&
        save.BlockedHitTests is not null && save.BlockedHitTests.All(x => x is not null) && save.Inventories is not null &&
        save.Inventories.All(x => x is not null && x.Owner is not null && x.Items is not null && x.Items.All(i => i is not null)) &&
        save.Timers is not null && save.Timers.All(x => x is not null && x.Noun is not null && x.Verb is not null);

    /// <summary>Deletes a slot.</summary>
    /// <param name="slot">Which slot.</param>
    /// <returns>True when there is now nothing in it.</returns>
    public bool Delete(string slot)
    {
        if (!IsSlotName(slot))
        {
            return false;
        }

        try
        {
            File.Delete(PathOf(slot));

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Brings an older save up to the current schema.
    /// </summary>
    /// <param name="save">The save as read.</param>
    /// <returns>The save this build understands.</returns>
    internal static SaveGame Migrate(SaveGame save)
    {
        if (save.SchemaVersion < 2)
        {
            save = ToSchema2(save);
        }

        if (save.SchemaVersion < 3)
        {
            save = ToSchema3(save);
        }

        if (save.SchemaVersion < 4)
        {
            // Before actor ownership was recorded, day-one history was Gabriel's.
            // Later saves cannot disambiguate shared topics. Keep those with the saved
            // protagonist; do not invent completed conversations for the other one.
            string actor = save.Day == 1 || (save.Day == 2 && save.Hour == 7 && !save.Afternoon)
                ? "GABRIEL" : save.Ego.ToUpperInvariant();
            save = save with
            {
                SchemaVersion = 4,
                TopicCounts = save.TopicCounts.ToDictionary(
                    pair => actor + "|" + pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                SaidTopics = [.. save.SaidTopics.Select(line => actor + "\u0001" + line)],
            };

            if (save.Day == 2 && save.Hour == 7 && !save.Afternoon)
            {
                var topics = new Dictionary<string, int>(save.TopicCounts, StringComparer.OrdinalIgnoreCase);
                foreach ((string score, string topic) in new[]
                {
                    ("e_207a_ma3_talk_abbe_introduce", "GRACE|ABBE|T_INTRODUCE"),
                    ("e_207a_din_talk_buthan_tour_group", "GRACE|BUTHANE|T_TOUR_GROUP"),
                })
                {
                    if (save.Scored.Contains(score, StringComparer.OrdinalIgnoreCase))
                    {
                        topics[topic] = 1;
                    }
                }
                save = save with { TopicCounts = topics };
            }
        }

        return save;
    }

    /// <summary>
    /// Works out what an older save can honestly be said to have achieved.
    /// </summary>
    /// <param name="save">A save written before score events were recorded.</param>
    /// <returns>The same save, with what is recoverable recovered.</returns>
    private static SaveGame ToSchema2(SaveGame save)
    {
        var reached = new Timeblock(save.Day, save.Hour, save.Afternoon);

        List<string> earned =
        [
            .. ScoreEvents.Open().Names
                .Where(name => ScoreEvents.TimeblockOf(name) is { } when && when < reached),
        ];

        return save with
        {
            SchemaVersion = 2,
            Scored = earned,
        };
    }

    /// <summary>
    /// Works out who an older save's player cannot still be a stranger to.
    /// </summary>
    /// <param name="save">A save written before introductions were recorded.</param>
    /// <returns>The same save, with what is recoverable recovered.</returns>
    private static SaveGame ToSchema3(SaveGame save)
    {
        var reached = new Timeblock(save.Day, save.Hour, save.Afternoon);

        bool imported = save.TopicCounts.Count == 0 &&
                        save.Introduced.Count == 0 &&
                        reached > new Timeblock(1, 10, IsAfternoon: false);

        return save with
        {
            SchemaVersion = 3,
            Introduced = imported
                ? [.. Story.Introductions.Open().MetBy(reached)]
                : save.Introduced,
        };
    }

    private string PathOf(string slot) => Path.Combine(_directory, slot + ".json");

    /// <summary>Where a slot's picture of the room lives.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The path, whether or not anything is there.</returns>
    public string PictureOf(string slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        return Path.ChangeExtension(ReadPath(slot) ?? throw new ArgumentException("Invalid save slot.", nameof(slot)), ".png");
    }

    /// <summary>
    /// Keeps a picture of the room beside a save.
    /// </summary>
    /// <param name="slot">The slot it belongs to.</param>
    /// <param name="picture">The frame, already reduced to a thumbnail.</param>
    /// <returns>True when it was written.</returns>
    public bool Illustrate(string slot, Formats.Bitmaps.DecodedImage picture)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!IsSlotName(slot))
        {
            return false;
        }

        try
        {
            AtomicFile.WriteAllBytes(
                Path.Combine(_directory, slot + ".png"), Formats.Bitmaps.PngWriter.Encode(picture));

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The picture beside a save, if there is one.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The image, or null when the slot has none or it cannot be read.</returns>
    public Formats.Bitmaps.DecodedImage? Picture(string slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        try
        {
            string path = PictureOf(slot);

            return File.Exists(path)
                ? Formats.Bitmaps.PngReader.Decode(File.ReadAllBytes(path), path)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                                      InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
