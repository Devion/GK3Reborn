// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Text.Json.Serialization;

namespace GK3Reborn.Game;

/// <summary>What an actor is carrying, and which of it is in hand.</summary>
/// <param name="Owner">Whose pockets.</param>
/// <param name="Items">What is in them.</param>
/// <param name="Active">The one in hand, or null.</param>
public sealed record SavedInventory(string Owner, IReadOnlyList<string> Items, string? Active);

/// <summary>A timer a script set and the story is still waiting on.</summary>
/// <param name="Noun">The noun its case is about.</param>
/// <param name="Verb">The verb.</param>
/// <param name="Seconds">How much longer.</param>
public sealed record SavedTimer(string Noun, string Verb, double Seconds);

/// <summary>
/// A game, written down.
/// </summary>
/// <remarks>
/// <para>
/// The contents are not a design decision so much as a reading of
/// <see cref="GameState.ComputeHash"/>: that method already enumerates everything
/// observable about a run, in a fixed order, because a state hash that missed something
/// would be useless for the comparison it exists for. A save that stores less than the hash
/// covers is a save that can be loaded into a different game than the one that was saved,
/// so the two lists are kept deliberately identical and a test compares the hash across a
/// round trip rather than comparing fields.
/// </para>
/// <para>
/// <b>Presentation is not in here.</b> Which screen was open, where the camera was
/// gliding, what was half-said — none of it is state the story reads, and restoring it
/// would mean restoring a moment rather than a position. A loaded game puts the player in
/// the room, at the scene's own camera, with nothing in front of it.
/// </para>
/// <para>
/// <b>Schema version is checked, not assumed.</b> A save from a future build is refused by
/// name rather than half-read; a save from a past one goes through
/// <see cref="SaveStore"/>'s migration, which is a real place to put work rather than a
/// promise. The one migration that exists today is from nothing.
/// </para>
/// </remarks>
public sealed record SaveGame
{
    /// <summary>The schema this build writes.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Which schema this save was written with.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Which build wrote it, for a diagnostic that can name a version.</summary>
    public string? Engine { get; init; }

    /// <summary>When, in UTC.</summary>
    public required DateTimeOffset Written { get; init; }

    /// <summary>What the player called it, or what the slot is for.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Day of the story.</summary>
    public required int Day { get; init; }

    /// <summary>Hour of the day, on the twelve-hour clock the timeblocks use.</summary>
    public required int Hour { get; init; }

    /// <summary>Whether that hour is after noon.</summary>
    public required bool Afternoon { get; init; }

    /// <summary>Which room.</summary>
    public required string Location { get; init; }

    /// <summary>The room before it.</summary>
    public string LastLocation { get; init; } = string.Empty;

    /// <summary>Which camera of that room.</summary>
    public string CameraAngle { get; init; } = string.Empty;

    /// <summary>Who the player is.</summary>
    public required string Ego { get; init; }

    /// <summary>The score so far.</summary>
    public int Score { get; init; }

    /// <summary>
    /// How many numbers the story has drawn from the generator.
    /// </summary>
    /// <remarks>
    /// Saved so a reloaded game draws the same sequence a continued one would. Without it,
    /// reloading is a way to re-roll anything the story left to chance, which is not what
    /// a save is for. See <see cref="Foundation.DeterministicRandom"/>.
    /// </remarks>
    public int RandomDraws { get; init; }

    /// <summary>
    /// The generator's own four words, so a reloaded game draws what a continued one would.
    /// </summary>
    /// <remarks>
    /// The count alone cannot restore it — the generator is a state machine, not a
    /// position in a stream — and replaying the draws to catch up would be both slow and a
    /// lie, since nothing records how many of them a script asked for rather than took.
    /// </remarks>
    public IReadOnlyList<ulong> RandomState { get; init; } = [];

    /// <summary>Flags that are set. The rest are not.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>Named integers the scripts keep.</summary>
    public IReadOnlyDictionary<string, int> Variables { get; init; } =
        new Dictionary<string, int>();

    /// <summary>How often each actor has done each verb to each noun.</summary>
    public IReadOnlyDictionary<string, int> NounVerbCounts { get; init; } =
        new Dictionary<string, int>();

    /// <summary>How often each topic has been raised with each noun.</summary>
    public IReadOnlyDictionary<string, int> TopicCounts { get; init; } =
        new Dictionary<string, int>();

    /// <summary>Which individual lines of a topic have been said.</summary>
    public IReadOnlyList<string> SaidTopics { get; init; } = [];

    /// <summary>How often each noun has been chatted to.</summary>
    public IReadOnlyDictionary<string, int> ChatCounts { get; init; } =
        new Dictionary<string, int>();

    /// <summary>How often each actor has been in each location.</summary>
    public IReadOnlyDictionary<string, int> LocationCounts { get; init; } =
        new Dictionary<string, int>();

    /// <summary>Where each actor is.</summary>
    public IReadOnlyDictionary<string, string> ActorLocations { get; init; } =
        new Dictionary<string, string>();

    /// <summary>What has been scanned into Sidney.</summary>
    public IReadOnlyList<string> SidneyFiles { get; init; } = [];

    /// <summary>Which inventory items have been through Sidney's scanner.</summary>
    public IReadOnlyList<string> SidneyScans { get; init; } = [];

    /// <summary>What everyone is carrying.</summary>
    public IReadOnlyList<SavedInventory> Inventories { get; init; } = [];

    /// <summary>Timers the story is still waiting on.</summary>
    public IReadOnlyList<SavedTimer> Timers { get; init; } = [];

    /// <summary>Hit tests a script has closed.</summary>
    public IReadOnlyList<string> BlockedHitTests { get; init; } = [];

    /// <summary>A one-line description for a list of saves.</summary>
    [JsonIgnore]
    public string Summary =>
        $"Day {Day}, {Hour}{(Afternoon ? "pm" : "am")} — {Location}";
}
