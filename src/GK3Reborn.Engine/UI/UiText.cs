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
public sealed class UiText
{
    /// <summary>What the file is called, inside a pack and inside the assembly.</summary>
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
    public string Say(string key, string english, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Say(key, english), values ?? []);

    /// <summary>Every key this holds, for the tests that check the files agree.</summary>
    public IReadOnlyCollection<string> Keys => _said.Keys;

    /// <summary>Reads a table out of JSON, forgiving anything that is not one.</summary>
    /// <param name="bytes">The file.</param>
    /// <returns>The table, empty when the file will not read.</returns>
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
