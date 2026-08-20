using System.Text;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ui;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// The game's 137 bitmap fonts, read on demand.
/// </summary>
/// <remarks>
/// <para>
/// A font is two files that have to be found together: the <c>.FON</c> that lists its
/// characters and the bitmap they are cut from. Most name their bitmap outright; the nine
/// that do not are called the same thing as the definition.
/// </para>
/// <para>
/// The definition is read as Latin-1 rather than UTF-8. The <c>Font=</c> line is a run of
/// characters in the sheet's own order, and a third of them are above 127 — read as UTF-8
/// they become replacement characters, and every accented letter in the game maps to the
/// wrong picture.
/// </para>
/// </remarks>
public sealed class FontLibrary
{
    private readonly GameArchives _archives;
    private readonly Dictionary<string, FontFile?> _read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a library over a set of archives.</summary>
    /// <param name="archives">Where the fonts are.</param>
    public FontLibrary(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);
        _archives = archives;
    }

    /// <summary>Diagnostics raised while reading.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>Reads a font, or returns what was read before.</summary>
    /// <param name="name">Its name, with or without the extension.</param>
    /// <returns>The font, or null when it or its bitmap is missing.</returns>
    public FontFile? Read(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_read.TryGetValue(name, out FontFile? cached))
        {
            return cached;
        }

        string bare = Path.GetFileNameWithoutExtension(name);
        FontFile? font = null;

        if (_archives.Read(bare + ".FON") is { } definition)
        {
            string text = Encoding.Latin1.GetString(definition);

            if (Sheet(text, bare) is { } sheet)
            {
                font = FontFile.Parse(text, sheet, bare, Diagnostics);
            }
        }

        _read[name] = font;
        return font;
    }

    /// <summary>Reads the first of several fonts that is there.</summary>
    /// <param name="names">Names to try, in order of preference.</param>
    /// <returns>The first one found, or null.</returns>
    /// <remarks>
    /// The interface asks for a size it would like and takes what the installation has,
    /// rather than failing because one particular font is missing from one particular
    /// release.
    /// </remarks>
    public FontFile? Any(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        foreach (string name in names)
        {
            if (Read(name) is { Count: > 0 } font)
            {
                return font;
            }
        }

        return null;
    }

    /// <summary>Finds and decodes the bitmap a font is cut from.</summary>
    private DecodedImage? Sheet(string definition, string bare)
    {
        string? named = null;

        foreach (string line in definition.Split('\n'))
        {
            if (line.StartsWith("Bitmap Name", StringComparison.OrdinalIgnoreCase) &&
                line.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals)
            {
                named = line[(equals + 1)..].Trim();
                break;
            }
        }

        foreach (string candidate in Candidates(named, bare))
        {
            if (_archives.Read(candidate) is { } bytes && BitmapDecoder.CanDecode(bytes))
            {
                return BitmapDecoder.Decode(bytes, candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string? named, string bare)
    {
        if (named is { Length: > 0 })
        {
            yield return named;
            yield return Path.GetFileNameWithoutExtension(named) + ".BMP";
        }

        yield return bare + ".BMP";
    }
}
