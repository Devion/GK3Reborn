using System.Diagnostics.CodeAnalysis;

namespace GK3Reborn.Content;

/// <summary>
/// One of the languages GK3 was published in.
/// </summary>
/// <param name="Code">The ISO 639-1 code, lower case: <c>en</c>, <c>fr</c>.</param>
/// <param name="Prefix">
/// The letter the 1999 game puts in front of a spoken asset's name.
/// </param>
/// <param name="Name">What the language is called in English.</param>
/// <param name="Native">What it is called in itself, for the menu row.</param>
public sealed record GameLanguage(string Code, char Prefix, string Name, string Native)
{
    /// <summary>
    /// The code page this language's text assets are one byte a character in.
    /// </summary>
    public int CodePage { get; init; } = 1252;

    /// <summary>The language the game is read in when nobody has chosen one.</summary>
    public static GameLanguage Default { get; } =
        new("en", 'E', "English", "English") { Aliases = ["eng", "en-us", "en-gb", "us"] };

    /// <summary>
    /// Every language GK3 was published in, in the order the menu offers them.
    /// </summary>
    public static IReadOnlyList<GameLanguage> Known { get; } =
    [
        Default,

        // Not one of Sierra's own eight either, and the second release that turned out to
        // be a patch rather than a second copy of the game: one gk3_cz.brn over the English
        // archives, the same shape Polish has. Windows-1250, and the prefix is E because
        // whoever made it renamed nothing.
        new("cs", 'E', "Czech", "Čeština")
        {
            CodePage = 1250,
            Aliases = ["ces", "cze", "cz", "cesky", "cestina"],
        },

        new("de", 'G', "German", "Deutsch") { Aliases = ["deu", "ger", "deutsch"] },
        new("es", 'S', "Spanish", "Español") { Aliases = ["esp", "spa", "espanol"] },
        new("fr", 'F', "French", "Français") { Aliases = ["fra", "fre", "francais"] },
        new("it", 'I', "Italian", "Italiano") { Aliases = ["ita", "italiano"] },
        new("pl", 'E', "Polish", "Polski") { CodePage = 1250, Aliases = ["pol", "polski"] },
        new("pt", 'E', "Portuguese", "Português")
        {
            Aliases = ["por", "ptb", "pt-br", "portugues"],
        },
        new("ru", 'E', "Russian", "Русский") { CodePage = 1251, Aliases = ["rus"] },

        // Not one of Sierra's own eight. Simplified Chinese was translated by somebody else
        // and it is here because a release of it exists: the arrangement was built so that
        // adding a language is sourcing a release, and refusing one because Sierra did not
        // publish it would be the arrangement failing its own test.
        //
        // It carries E and reads ESTRINGS.TXT, like every localisation that did not rename
        // the spoken assets, and it is the one language here whose text is not one byte a
        // character — see CodePage.
        new("zh", 'E', "Chinese", "简体中文")
        {
            CodePage = 936,
            Aliases = ["chs", "zh-cn", "zh-hans", "simplified chinese"],
        },
    ];

    /// <summary>The code a pack file and a workspace directory are named for.</summary>
    public string FileCode => Code.ToUpperInvariant();

    /// <summary>The name of the string table this language reads.</summary>
    public string StringTable => Prefix + "STRINGS.TXT";

    /// <summary>
    /// Other things a person may reasonably have called this language.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Finds a language by its code.</summary>
    /// <param name="code">
    /// An ISO 639-1 code, an ISO 639-2 one, or the language's English name — in any case.
    /// </param>
    /// <returns>The language, or null when it names none.</returns>
    public static GameLanguage? Find(string? code)
    {
        if (code is not { Length: > 0 })
        {
            return null;
        }

        string trimmed = code.Trim();

        return Known.FirstOrDefault(l =>
            string.Equals(l.Code, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(l.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
            l.Aliases.Contains(trimmed, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Finds a language by its code, falling back to English.</summary>
    /// <param name="code">An ISO 639-1 code, in any case, or null.</param>
    /// <returns>The language; never null.</returns>
    public static GameLanguage Of(string? code) => Find(code) ?? Default;

    /// <summary>Whether a code names a language this build knows.</summary>
    /// <param name="code">An ISO 639-1 code, in any case, or null.</param>
    /// <returns>True when it does.</returns>
    public static bool IsKnown([NotNullWhen(true)] string? code) => Find(code) is not null;

    /// <summary>
    /// The name a spoken asset carries in this language.
    /// </summary>
    /// <param name="name">
    /// The name a script writes, without a prefix and with or without an extension.
    /// </param>
    /// <returns>The name on disk.</returns>
    public string Spoken(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Prefix + name;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Code}, {Prefix})";
}
