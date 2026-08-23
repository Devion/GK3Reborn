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
    string Slot, string Title, string Summary, DateTimeOffset Written, int Schema);

/// <summary>
/// Where saved games live, and the only thing that writes them.
/// </summary>
/// <remarks>
/// <para>
/// In the user's own profile beside the settings — <c>%AppData%\GK3Reborn\saves</c> on
/// Windows, <c>~/.config/GK3Reborn/saves</c> on Linux — for the same reasons the settings
/// are: a game directory may be read-only, shared between accounts, or replaced wholesale
/// by an update, and none of those should cost somebody their progress.
/// </para>
/// <para>
/// <b>Every write is atomic.</b> A save is written to a temporary file, flushed, and moved
/// into place; a process that dies halfway through leaves the previous save untouched
/// rather than a half-written one. This is the single most important property here — the
/// plan's own words are that "failures cannot corrupt the last good save" — and it is why
/// <see cref="AtomicFile"/> exists.
/// </para>
/// <para>
/// <b>A save is never overwritten from a game that failed to start.</b> Nothing here
/// decides that; the caller does, by saving after the state is real rather than before.
/// The autosave slot is written on arriving somewhere, which is the point at which the
/// story is at rest.
/// </para>
/// <para>
/// Slots are files, and the name is the slot: <c>autosave</c>, <c>quicksave</c>,
/// <c>slot-01</c>. A name is checked before it becomes a path, because a slot name reaches
/// this from a console command and a save called <c>..\..\settings</c> must not be a way
/// to write one.
/// </para>
/// </remarks>
public sealed class SaveStore
{
    /// <summary>The slot a new room writes.</summary>
    public const string AutoSlot = "autosave";

    /// <summary>The slot the quick-save key writes.</summary>
    public const string QuickSlot = "quicksave";

    /// <summary>How many numbered slots the interface offers.</summary>
    public const int NumberedSlots = 12;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;

    /// <summary>Opens the store.</summary>
    /// <param name="directory">Where saves live, or null for this user's own.</param>
    public SaveStore(string? directory = null) =>
        _directory = directory ?? DefaultDirectory;

    /// <summary>Where saves live for this user.</summary>
    public static string DefaultDirectory => Path.Combine(
        Path.GetDirectoryName(Settings.DefaultPath) ?? ".", "saves");

    /// <summary>Where this store keeps its files.</summary>
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
    /// <remarks>
    /// Letters, digits and hyphens, and nothing else. Slot names arrive from a console
    /// command, and a store that joined one onto a path without asking would let
    /// <c>..\..\settings</c> be a save.
    /// </remarks>
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
    /// <remarks>
    /// Failure is reported rather than thrown, and the previous save survives it. A player
    /// whose disk is full should be told, not crashed, and should still have the game they
    /// saved an hour ago.
    /// </remarks>
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
        fault = SaveFault.Missing;

        if (!IsSlotName(slot))
        {
            fault = SaveFault.Unreadable;
            return null;
        }

        string path = PathOf(slot);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            SaveGame? save = JsonSerializer.Deserialize<SaveGame>(File.ReadAllText(path), Json);

            if (save is null)
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
                                          or NotSupportedException)
        {
            fault = SaveFault.Unreadable;
            return null;
        }
    }

    /// <summary>What is in every slot, newest first.</summary>
    /// <returns>The slots, which is empty when nothing has been saved.</returns>
    /// <remarks>
    /// A file that cannot be read is left out rather than reported here. The list is what
    /// the player may load, and offering something that will fail is worse than not
    /// offering it; <see cref="Read"/> is where a fault gets a name.
    /// </remarks>
    public IReadOnlyList<SaveSlot> List()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return [];
        }

        List<SaveSlot> slots = [];

        foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            string slot = Path.GetFileNameWithoutExtension(path);

            if (Read(slot, out SaveFault fault) is { } save && fault == SaveFault.None)
            {
                slots.Add(new SaveSlot(
                    slot, save.Title, save.Summary, save.Written, save.SchemaVersion));
            }
        }

        return [.. slots.OrderByDescending(s => s.Written)];
    }

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
    /// <remarks>
    /// One schema exists, so this does nothing yet and is here anyway: the alternative is
    /// discovering at the first schema change that every save in the wild is unreadable,
    /// and the shape of the fix is much harder to add then than now. Each future step reads
    /// a version and returns the next one.
    /// </remarks>
    private static SaveGame Migrate(SaveGame save) => save;

    private string PathOf(string slot) => Path.Combine(_directory, slot + ".json");
}
