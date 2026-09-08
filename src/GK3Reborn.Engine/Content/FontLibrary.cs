using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ui;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// The game's 137 bitmap fonts, read on demand.
/// </summary>
public sealed class FontLibrary
{
    private readonly GameArchives _archives;
    private readonly Dictionary<string, FontFile?> _read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets every font it has read, for a language change.</summary>
    public void Forget() => _read.Clear();

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

        // ReadText, not Read: it is the one place that knows which code page the open
        // language's assets are one byte a character in.
        if (_archives.ReadText(bare + ".FON") is { } text)
        {
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
    /// <summary>
    /// Reads a ladder of fonts and returns the one whose letters are nearest a wanted size.
    /// </summary>
    /// <param name="wantedHeight">How tall a capital should be, in pixels.</param>
    /// <param name="names">The ladder, in any order.</param>
    /// <returns>The nearest one that loads, or null when none of them does.</returns>
    public FontFile? Nearest(int wantedHeight, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        FontFile? best = null;

        foreach (string name in names)
        {
            if (Read(name) is not { Count: > 0 } font)
            {
                continue;
            }

            if (best is null ||
                Math.Abs(font.Height - wantedHeight) < Math.Abs(best.Height - wantedHeight))
            {
                best = font;
            }
        }

        return best;
    }

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
