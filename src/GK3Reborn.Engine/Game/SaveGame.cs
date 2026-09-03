// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Text.Json.Serialization;

namespace GK3Reborn.Game;

/// <summary>What is on Sidney's map, as the story keeps it.</summary>
/// <param name="Marks">The places marked, as "x,y" in the map's own 1,368 pixels.</param>
/// <param name="Figures">The figures laid over them, in the order they were laid.</param>
/// <param name="Grid">How many cells the ruling is divided into, or nought for none.</param>
public sealed record SavedMap(
    IReadOnlyList<string> Marks, IReadOnlyList<SavedFigure> Figures, int Grid);

/// <summary>One figure laid over Sidney's map.</summary>
/// <param name="Shape">Which figure, as <c>MapShape</c> names it.</param>
/// <param name="X">Where its middle is across the map, in map pixels.</param>
/// <param name="Y">And down.</param>
/// <param name="Size">The radius of the circle it is drawn inside.</param>
/// <param name="Turn">How far it has been turned, in degrees.</param>
/// <param name="Points">
/// The places it was fitted to, as "x,y" in map pixels — its own, and not the map's.
/// </param>
public sealed record SavedFigure(
    string Shape,
    float X,
    float Y,
    float Size,
    float Turn,
    IReadOnlyList<string> Points)
{
    /// <summary>A figure saved before figures kept their own places.</summary>
    public SavedFigure(string shape, float x, float y, float size, float turn)
        : this(shape, x, y, size, turn, [])
    {
    }
}

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
/// promise. Two steps exist, and both recover what a point in the story implies rather than
/// inventing anything: see <see cref="Scored"/> and <see cref="Introduced"/>.
/// </para>
/// </remarks>
public sealed record SaveGame
{
    /// <summary>The schema this build writes.</summary>
    /// <remarks>
    /// <para>
    /// Two adds the score events earned and the journal's hints. The first of those was
    /// always missing rather than newly needed: a save has always carried the player's total
    /// and never which events made it up, so loading one and doing the same thing again
    /// scored it twice. The journal made that visible, because it reads those events to know
    /// what has been done.
    /// </para>
    /// <para>
    /// Three adds who the player has been introduced to, for the saves that cannot say it
    /// any other way. See <see cref="Introduced"/>.
    /// </para>
    /// </remarks>
    public const int CurrentSchema = 3;

    /// <summary>Which schema this save was written with.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Every score event earned.</summary>
    /// <remarks>
    /// Empty in a schema-1 save, which never wrote them. <see cref="SaveStore"/>'s migration puts back
    /// what can honestly be put back and invents nothing.
    /// </remarks>
    public IReadOnlyList<string> Scored { get; init; } = [];

    /// <summary>
    /// Who the player is to be treated as having met, whatever else the save says.
    /// </summary>
    /// <remarks>
    /// Empty in a game played through in this engine, and correctly so: the labels ask the
    /// game's own conditions — <c>MET_BUTHANE</c>, <c>INTRODUCED_EMILIO</c> — and a save
    /// carries the topic counts those conditions are about. It is filled for a game brought
    /// across from the original, whose file has a timeblock and a score in it and not one
    /// topic count, so the question has to be answered from the point in the story instead.
    /// See <see cref="Story.Introductions.MetBy"/>.
    /// </remarks>
    public IReadOnlyList<string> Introduced { get; init; } = [];

    /// <summary>How many hints the player has asked for, per objective.</summary>
    public IReadOnlyDictionary<string, int> Hints { get; init; } =
        new Dictionary<string, int>();

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

    /// <summary>
    /// The places marked on Sidney's map, as "x,y" in the map's own 1,368 pixels.
    /// </summary>
    /// <remarks>
    /// The map puzzle is several sittings long — mark a village, go and read a painting's
    /// geometry, come back and lay the figure it saved over the marks — so what is on the
    /// map has to survive a save like everything else the story remembers.
    /// </remarks>
    public IReadOnlyList<string> SidneyMarks { get; init; } = [];

    /// <summary>The figures laid over it, in the order they were laid.</summary>
    public IReadOnlyList<SavedFigure> SidneyFigures { get; init; } = [];

    /// <summary>How many cells the map's grid is ruled into, or nought for none.</summary>
    public int SidneyGrid { get; init; }

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
