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
/// <remarks>
/// <para>
/// <b>The prefix is not derivable from the code and is not unique.</b> Sierra renamed the
/// spoken assets for four localisations only — French is <c>F</c>, German <c>G</c>, Italian
/// <c>I</c>, Spanish <c>S</c> — and shipped Portuguese, Russian and Polish with the English
/// spellings left alone, so those three carry <c>E</c> and are told apart by the content of
/// the file rather than by its name. That is why a language is a record here rather than a
/// letter: two languages can want the same name and mean different bytes, which is exactly
/// what a per-language pack is for.
/// </para>
/// <para>
/// It reaches three places. <see cref="AnimationLibrary.Language"/> puts it in front of a
/// line of dialogue's <c>.YAK</c>; <see cref="Game.GameStrings"/> puts it in front of
/// <c>STRINGS.TXT</c>; and the moments — <c>.MOM</c> — take it for the same reason the
/// YAKs do. Nothing else in the corpus is spelled per language.
/// </para>
/// </remarks>
public sealed record GameLanguage(string Code, char Prefix, string Name, string Native)
{
    /// <summary>
    /// The code page this language's text assets are one byte a character in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 1252 for the Western European localisations, 1250 for Polish, 1251 for Russian and
    /// 936 for Simplified Chinese. It is a property of the language rather than of the file
    /// because nothing in a GK3 text asset says what it is encoded in — there is no
    /// byte-order mark, no declaration and no header, only bytes — so the only thing that
    /// can know is whoever chose the language.
    /// </para>
    /// <para>
    /// Sierra's own eight are all one byte a character. 936 is not: it is GBK, where a byte
    /// above 0x80 begins a pair. See <see cref="Foundation.Gk3Encoding"/> for why that is
    /// the one page the engine does not carry a table for.
    /// </para>
    /// </remarks>
    public int CodePage { get; init; } = 1252;

    /// <summary>The language the game is read in when nobody has chosen one.</summary>
    /// <remarks>
    /// English, and stated rather than inferred from the installation. A player whose
    /// install is French and who has no French pack should still start in a language the
    /// game can be finished in, and the archives under a French install answer to the
    /// English spellings anyway — <c>E</c> is the prefix Sierra shipped there too.
    /// </remarks>
    public static GameLanguage Default { get; } =
        new("en", 'E', "English", "English") { Aliases = ["eng", "en-us", "en-gb", "us"] };

    /// <summary>
    /// Every language GK3 was published in, in the order the menu offers them.
    /// </summary>
    /// <remarks>
    /// English first because it is the default and the one every installation can fall back
    /// to; the rest alphabetically by their own name, which is the order somebody scanning
    /// a language list reads in. The list is what the game <em>knows about</em>, not what it
    /// can play: <see cref="LocalizedContent.Available"/> answers that, and it is a fact
    /// about which packs are on disk.
    /// </remarks>
    public static IReadOnlyList<GameLanguage> Known { get; } =
    [
        Default,
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
    /// <remarks>
    /// Upper case, so <c>Reborn_FR.rebarn</c> and <c>enhanced/localized/FR</c>. The code
    /// itself stays lower case because that is how ISO 639 writes it and how
    /// <see cref="Game.Settings.Language"/> stores it.
    /// </remarks>
    public string FileCode => Code.ToUpperInvariant();

    /// <summary>The name of the string table this language reads.</summary>
    /// <remarks>
    /// <c>ESTRINGS.TXT</c> in English and Portuguese, <c>FSTRINGS.TXT</c> in French. It is
    /// the file G-Engine's <c>DataHelper</c> uses to tell one installation from another,
    /// and the only asset whose <em>name</em> says what language a Data directory is.
    /// </remarks>
    public string StringTable => Prefix + "STRINGS.TXT";

    /// <summary>
    /// Other things a person may reasonably have called this language.
    /// </summary>
    /// <remarks>
    /// Only ever used to work out which language a <em>directory somebody named</em> holds.
    /// A release is unpacked by hand and the directory is called whatever the person
    /// unpacking it typed: <c>ESP</c>, <c>GER</c>, <c>Deutsch</c>. Refusing all of those and
    /// insisting on <c>es</c> would be a rule nobody can see written down, enforced by
    /// silence — the language simply would not appear.
    /// <para>
    /// The ISO 639-2 codes are here because they are what the discs themselves were labelled
    /// with. Nothing anywhere else in the game reads these.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// A settings file is a text file somebody may edit, and a language nobody has heard of
    /// is not a reason to fail to start — for the same reason every other value in
    /// <see cref="Game.Settings"/> is clamped rather than rejected.
    /// </remarks>
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
    /// <remarks>
    /// Scripts never write the prefix — <c>StartVoiceOver("1LLJ644QR1")</c> — so the engine
    /// adds it, which is why a plate taken straight from an action file matches nothing.
    /// </remarks>
    public string Spoken(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Prefix + name;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Code}, {Prefix})";
}
