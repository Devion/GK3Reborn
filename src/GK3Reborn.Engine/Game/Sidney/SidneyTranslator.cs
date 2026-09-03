// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game.Sidney;

/// <summary>Something Sidney can turn into English, and what it turns into.</summary>
/// <param name="Key">What the game's text calls it, before the line numbers.</param>
/// <param name="Language">What it is written in, as the screen names that language.</param>
/// <param name="Original">Its lines, as they were recorded.</param>
/// <param name="English">The same lines in English.</param>
/// <param name="Incomplete">
/// Whether the machine should say the sentence is unfinished and offer to add to it, which
/// is the whole of the Arcadia puzzle.
/// </param>
public sealed record SidneyTranslation(
    string Key,
    string Language,
    IReadOnlyList<string> Original,
    IReadOnlyList<string> English,
    bool Incomplete = false);

/// <summary>
/// Sidney's translate screen: what may be translated, out of what, and into what.
/// </summary>
/// <remarks>
/// <para>
/// <b>None of this is invented.</b> <c>ESIDNEY.TXT</c>'s translate section carries the four
/// languages the screen offers, the refusals it gives, and both halves of every piece of
/// text the story needs turned into English — the Abbé's telephone call in French, Buchelli's
/// in Italian, and the Latin off the tomb. The screen said "Not implemented yet" only
/// because nothing had read them.
/// </para>
/// <para>
/// <b>The from-language is a real choice and a real refusal.</b> The screen asks what the
/// text is written in before it will translate it, and answering wrongly gets
/// <c>WrongFrom</c> — which is the game's own text, and the reason the screen has a menu of
/// languages rather than a single button.
/// </para>
/// <para>
/// <b>Et in Arcadia Ego is the one that is not a translation.</b> Turning it into English
/// gives an unfinished sentence, and the machine offers to add to it; the word that finishes
/// it is <c>Sum</c>, which the player has to have found, and the completed line is the point
/// of the puzzle. That exchange is written into the same section as <c>Incomplete</c>,
/// <c>Input</c> and <c>BadInput1</c>.
/// </para>
/// </remarks>
public sealed class SidneyTranslator
{
    /// <summary>What the screen calls the section these strings live in.</summary>
    public const string Section = "Translate Screen";

    /// <summary>The word that finishes the Arcadia inscription.</summary>
    private const string Completion = "Sum";

    /// <summary>The languages the screen offers, as the game's text keys them.</summary>
    private static readonly string[] Named = ["English", "Latin", "French", "Italian"];

    private static readonly (string Item, string Key, string Language, bool Incomplete)[] Table =
    [
        ("ABBE_TAPE", "AbbeTape", "French", false),
        ("BUCHELLI_TAPE", "BuchTape", "Italian", false),
        ("I_AM_WORDS", "SUMScript", "Latin", false),
        ("POUSSIN_POSTCARD", "ArcadiaText", "Latin", true),
    ];

    private readonly SidneyLibrary _library;

    /// <summary>Creates the translator.</summary>
    /// <param name="library">The game's own Sidney text.</param>
    public SidneyTranslator(SidneyLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        _library = library;
    }

    /// <summary>The languages the screen offers to translate out of.</summary>
    /// <remarks>
    /// English is one of them, and translating English into English is a refusal the game
    /// wrote — so it stays on the menu.
    /// </remarks>
    public IReadOnlyList<string> Languages =>
        [.. Named.Select(name => _library.Say(name, Section)).Where(name => name.Length > 0)];

    /// <summary>Whether a file has anything in it to translate.</summary>
    /// <param name="file">The file.</param>
    /// <returns>True when it does.</returns>
    public bool CanTranslate(SidneyFile? file) => Find(file) is not null;

    /// <summary>What a file says, and in what.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The text, or null when the file is not one of the four.</returns>
    public SidneyTranslation? Find(SidneyFile? file)
    {
        if (file is null)
        {
            return null;
        }

        foreach ((string item, string key, string language, bool incomplete) in Table)
        {
            if (!string.Equals(file.Item, item, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IReadOnlyList<string> original = _library.Lines(Section, key);

            if (original.Count == 0)
            {
                return null;
            }

            return new SidneyTranslation(
                key,
                _library.Say(language, Section) is { Length: > 0 } named ? named : language,
                original,
                _library.Lines(Section, key + "T"),
                incomplete);
        }

        return null;
    }

    /// <summary>
    /// Translates a file out of a language.
    /// </summary>
    /// <param name="file">What to translate.</param>
    /// <param name="from">What the player says it is written in.</param>
    /// <returns>What the machine says back.</returns>
    public SidneyResult Translate(SidneyFile? file, string? from)
    {
        if (Find(file) is not { } text)
        {
            return new SidneyResult(_library.Say("NotTranslatable", Section));
        }

        if (!string.Equals(from, text.Language, StringComparison.OrdinalIgnoreCase))
        {
            return new SidneyResult(_library.Say("WrongFrom", Section));
        }

        string done = string.Join("\n", text.English);

        if (!text.Incomplete)
        {
            return new SidneyResult(done);
        }

        return new SidneyResult(
            done + "\n\n" + _library.Say("Subject", Section),
            _library.Say("Question", Section),
            [_library.Say("Yes", Section), _library.Say("No", Section)]);
    }

    /// <summary>
    /// Adds a string to an unfinished sentence.
    /// </summary>
    /// <param name="file">The file being translated.</param>
    /// <param name="typed">What the player typed.</param>
    /// <returns>What the machine says back, and what it produced when the word was right.</returns>
    /// <remarks>
    /// Matched against the one word that completes it and nothing cleverer, ignoring case
    /// and surrounding space. The player has to have read <c>Sum</c> somewhere — that is the
    /// puzzle — and accepting anything that merely looks Latin would hand it over.
    /// </remarks>
    public SidneyResult Append(SidneyFile? file, string? typed)
    {
        if (Find(file) is not { Incomplete: true })
        {
            return new SidneyResult(_library.Say("NoFurther", Section));
        }

        if (!string.Equals(typed?.Trim(), Completion, StringComparison.OrdinalIgnoreCase))
        {
            return new SidneyResult(
                _library.Say("BadInput1", Section) + " " + _library.Say("BadInput2", Section));
        }

        IReadOnlyList<string> whole = _library.Lines(Section, "ArcSUMTextT");

        return new SidneyResult(
            whole.Count > 0 ? string.Join("\n", whole) : _library.Say("Updating", Section),
            Produced: "ArcSUMText");
    }
}
