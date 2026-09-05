// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Text.Json;
using GK3Reborn.Content;

namespace GK3Reborn.UI;

/// <summary>
/// The port's own interface, in the language the game is being played in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is a translation rather than an extraction.</b> GK3 localised its own
/// strings — the string table, Sidney's documents, the 293 names of the things the player
/// carries — and every one of those is read out of the archives through the language pack,
/// which is why a French game already read "Chambre de Gabriel" in the corner. What it
/// never had is a main menu with a graphics API on it, a settings screen with five
/// sections, a journal, a toolbar or a verb under the cursor. Those are the port's, so
/// there is nothing to compare and the words have to be written.
/// </para>
/// <para>
/// <b>One JSON a language, and the key is the same in all of them.</b> That is the shape
/// GK3's own <c>ESIDNEY.TXT</c> uses and the reason its files survived being translated
/// eight times: the identifier is stable and only the value moves. Adding a language is a
/// file, and it is the only piece of this that a translator has to touch.
/// </para>
/// <para>
/// <b>Three places to look, in this order.</b> The language pack beside the game, so a
/// translation can be corrected or a new one added without rebuilding anything; the copy
/// carried inside the assembly, so a player with no packs at all still has every language
/// the port ships; and finally the English the call site passes, so a key nobody has
/// translated yet draws a word rather than an identifier. A missing translation costs that
/// row and not the screen — the same rule the packs, the enhanced textures and the
/// overrides all follow.
/// </para>
/// <para>
/// <b>The English stays in the source</b> beside the key it belongs to, because a line
/// reading <c>Say("settings.detail.trees")</c> tells a reader nothing about what the row
/// says, and this codebase is meant to be read. It is duplicated in
/// <c>interface-en.json</c> and <c>UiTextTests</c> checks the two agree, so the duplication
/// is a checked one rather than a hopeful one.
/// </para>
/// </remarks>
public sealed class UiText
{
    /// <summary>What the file is called, inside a pack and inside the assembly.</summary>
    /// <remarks>
    /// The same name in every volume: each language's pack is opened on its own, so
    /// <c>interface.json</c> inside <c>Reborn_DE.rebarn</c> cannot be confused with the one
    /// inside <c>Reborn_FR.rebarn</c>. The copies inside the assembly need the code in
    /// their name because they are all in one assembly.
    /// </remarks>
    public const string FileName = "interface.json";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    private readonly Dictionary<string, string> _said;

    private UiText(string language, Dictionary<string, string> said)
    {
        Language = language;
        _said = said;
    }

    /// <summary>The words for a game nobody has told anything, which is English.</summary>
    /// <remarks>
    /// Empty rather than loaded: every call site carries its own English, so an empty table
    /// answers every one of them correctly and costs nothing. It is also what the tests get
    /// by default, which is why they read in English.
    /// </remarks>
    public static UiText English { get; } = new(GameLanguage.Default.Code, []);

    /// <summary>Which language this is, as an ISO 639-1 code.</summary>
    public string Language { get; }

    /// <summary>How many phrases it holds, for the startup line.</summary>
    public int Count => _said.Count;

    /// <summary>Where they were read from, for the startup line.</summary>
    public string Source { get; private init; } = "the source";

    /// <summary>
    /// The words for a language, from its pack where there is one and from the assembly
    /// otherwise.
    /// </summary>
    /// <param name="language">Which language.</param>
    /// <param name="pack">The language pack, or null when none is open.</param>
    /// <returns>The words, which are never null and may be empty.</returns>
    public static UiText Of(GameLanguage? language, LocalizedContent? pack)
    {
        string code = (language ?? GameLanguage.Default).Code;

        if (pack?.ReadManifest(FileName) is { Length: > 0 } packed &&
            Parse(packed) is { Count: > 0 } fromPack)
        {
            return new UiText(code, fromPack)
            {
                Source = $"{System.IO.Path.GetFileName(pack.Path)}:{FileName}",
            };
        }

        return Carried(code);
    }

    /// <summary>
    /// The copy of a language's words that ships inside the assembly.
    /// </summary>
    /// <param name="code">The ISO 639-1 code.</param>
    /// <returns>The words, empty for a language the port carries none for.</returns>
    /// <remarks>
    /// Public because <c>pack-content</c> reads it: the packs carry what the assembly
    /// carries, so that there is one place the words are written and two places they can be
    /// read from.
    /// </remarks>
    public static UiText Carried(string? code)
    {
        string language = code is { Length: > 0 } named
            ? named.ToLowerInvariant()
            : GameLanguage.Default.Code;

        if (CarriedBytes(language) is not { Length: > 0 } bytes)
        {
            return new UiText(language, []) { Source = "the source" };
        }

        return new UiText(language, Parse(bytes)) { Source = $"interface-{language}.json" };
    }

    /// <summary>
    /// A language's words as the assembly carries them, for whoever is packing them.
    /// </summary>
    /// <param name="code">The ISO 639-1 code.</param>
    /// <returns>The file's bytes, or null for a language the port carries none for.</returns>
    public static byte[]? CarriedBytes(string? code)
    {
        if (code is not { Length: > 0 } named)
        {
            return null;
        }

        using Stream? file = typeof(UiText).Assembly.GetManifestResourceStream(
            $"GK3Reborn.Assets.Ui.interface-{named.ToLowerInvariant()}.json");

        if (file is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        file.CopyTo(memory);

        return memory.ToArray();
    }

    /// <summary>
    /// One phrase.
    /// </summary>
    /// <param name="key">Its identifier, which is the same in every language.</param>
    /// <param name="english">What it says in English, which is the last resort.</param>
    /// <returns>The phrase.</returns>
    public string Say(string key, string english)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _said.TryGetValue(key, out string? said) && said.Length > 0 ? said : english;
    }

    /// <summary>
    /// One phrase with something filled into it.
    /// </summary>
    /// <param name="key">Its identifier.</param>
    /// <param name="english">What it says in English, with the same placeholders.</param>
    /// <param name="values">What goes in them.</param>
    /// <returns>The phrase.</returns>
    /// <remarks>
    /// <b>Placeholders are numbered, not positional prose.</b> A translation reorders a
    /// sentence, so "Not installed: copy {0} into the game's libs folder" has to be able to
    /// put the file name anywhere — which is also why the English is a format string rather
    /// than three pieces of concatenation.
    /// </remarks>
    public string Say(string key, string english, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Say(key, english), values ?? []);

    /// <summary>Every key this holds, for the tests that check the six agree.</summary>
    public IReadOnlyCollection<string> Keys => _said.Keys;

    /// <summary>Reads a table out of JSON, forgiving anything that is not one.</summary>
    /// <param name="bytes">The file.</param>
    /// <returns>The table, empty when the file will not read.</returns>
    /// <remarks>
    /// A pack somebody made by hand is the ordinary case here, so a malformed file falls
    /// back to the assembly's own copy rather than stopping the game: the whole point of
    /// the layering is that a bad translation costs the translation.
    /// </remarks>
    private static Dictionary<string, string> Parse(byte[] bytes)
    {
        try
        {
            Dictionary<string, string>? read =
                JsonSerializer.Deserialize<Dictionary<string, string>>(bytes, Json);

            return read is null
                ? []
                : new Dictionary<string, string>(read, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
