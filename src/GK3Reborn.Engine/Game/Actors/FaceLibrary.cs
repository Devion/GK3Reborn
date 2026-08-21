using GK3Reborn.Content;
using GK3Reborn.Formats.Ini;

namespace GK3Reborn.Game.Actors;

/// <summary>A spot on a face bitmap, in pixels from its top left corner.</summary>
/// <param name="X">Pixels from the left.</param>
/// <param name="Y">Pixels from the top.</param>
public readonly record struct FaceSpot(int X, int Y);

/// <summary>One of the blink animations a character uses, and how often it is chosen.</summary>
/// <param name="Animation">The <c>.ANM</c>, which is nothing but eyelid textures.</param>
/// <param name="Weight">Its share of the draw, out of the total of all of them.</param>
public readonly record struct BlinkChoice(string Animation, int Weight);

/// <summary>
/// How one character's face is put together.
/// </summary>
/// <param name="Identifier">The three-letter code the file lists them under.</param>
/// <param name="FaceTexture">The bitmap the head is painted with.</param>
/// <param name="MouthOffset">Where the mouth region starts on that bitmap.</param>
/// <param name="MouthSize">How big it is.</param>
/// <param name="EyelidsOffset">Where the eyelids go.</param>
/// <param name="EyelidsAlpha">A bitmap saying how much of the resting eyelids to show.</param>
/// <param name="ForeheadOffset">Where the forehead goes.</param>
/// <param name="Blinks">The blink animations, with the odds of each.</param>
/// <param name="BlinkFrom">Shortest gap between blinks, in seconds.</param>
/// <param name="BlinkTo">Longest gap between blinks, in seconds.</param>
public sealed record FaceConfig(
    string Identifier,
    string FaceTexture,
    FaceSpot MouthOffset,
    FaceSpot MouthSize,
    FaceSpot EyelidsOffset,
    string? EyelidsAlpha,
    FaceSpot ForeheadOffset,
    IReadOnlyList<BlinkChoice> Blinks,
    double BlinkFrom,
    double BlinkTo)
{
    /// <summary>The bitmap a face part rests at when nothing is painted over it.</summary>
    /// <param name="part">Which part.</param>
    /// <returns>Its texture name.</returns>
    /// <remarks>
    /// The naming convention the file documents at the top: <c>xxx_face</c>,
    /// <c>xxx_eyelids</c>, <c>xxx_forehead</c>, and a mouth per shape. It is a convention
    /// and not a list, which is why there is nothing to read here — only the four
    /// characters whose face bitmap does not follow it say so, with a <c>Face Name</c>.
    /// </remarks>
    public string RestingTexture(Formats.Animation.FacePart part) => part switch
    {
        Formats.Animation.FacePart.Eyelids => $"{Identifier}_EYELIDS",
        Formats.Animation.FacePart.Forehead => $"{Identifier}_FOREHEAD",
        _ => MouthTexture("MOUTH00"),
    };

    /// <summary>The bitmap for a mouth shape.</summary>
    /// <param name="shape">The shape as an animation names it, such as <c>MOUTH03</c>.</param>
    /// <returns>Its texture name.</returns>
    /// <remarks>
    /// A <c>LIPSYNCH</c> node names the shape and not the bitmap, because the same eight
    /// shapes belong to all forty-odd characters. The code in front of it is what says
    /// whose mouth it is.
    /// </remarks>
    public string MouthTexture(string shape) => $"{Identifier}_{shape}";
}

/// <summary>
/// <c>FACES.TXT</c> — how each character's face is assembled.
/// </summary>
/// <remarks>
/// <para>
/// GK3's people have no facial geometry at all. A head is one mesh painted with one
/// bitmap, and everything a face does — talking, blinking, raising an eyebrow — is done by
/// patching regions of that bitmap while the game runs. This file is the only place the
/// regions are written down: where the mouth sits on the texture and how big it is, where
/// the eyelids and forehead go, which blink animations a character uses and how often.
/// </para>
/// <para>
/// Thirty-two characters have an entry. Two of them — <c>CON-XXX</c> and <c>EM2-xxx</c> —
/// carry the suffix the file's own header says to remove once the art exists, so they are
/// not art that shipped and are skipped rather than half-read.
/// </para>
/// </remarks>
public sealed class FaceLibrary
{
    private readonly Dictionary<string, FaceConfig> _faces =
        new(StringComparer.OrdinalIgnoreCase);

    private FaceLibrary()
    {
    }

    /// <summary>How many characters the file described.</summary>
    public int Count => _faces.Count;

    /// <summary>Reads the file out of the archives.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <returns>The set, empty when there is no such file.</returns>
    public static FaceLibrary Open(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        return archives.ReadText("FACES.TXT") is { } text ? Parse(text) : new FaceLibrary();
    }

    /// <summary>Reads the file's text.</summary>
    /// <param name="text">The file's contents.</param>
    /// <returns>The set.</returns>
    public static FaceLibrary Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var library = new FaceLibrary();

        // One entry a line, and a key with spaces in it: "Mouth Offset = 90,132". Splitting
        // on commas as well would make the offset two entries and the key would lose its
        // value.
        IniDocument document = IniDocument.Parse(text, "FACES.TXT", multipleEntriesPerLine: false);

        IniSection? defaults = document.Sections.FirstOrDefault(
            s => s.Name.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase));

        foreach (IniSection section in document.Sections)
        {
            // [Eyes] is a table of eye bitmaps rather than a character, and [DEFAULT] is
            // what the others fall back to. The -XXX suffix marks art that never arrived.
            if (section.Name.Length != 3 ||
                section.Name.Equals("Eyes", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string code = section.Name.ToUpperInvariant();

            if (Spot(section, "Mouth Offset") is not { } mouth ||
                Spot(section, "Mouth Size", 'x') is not { } size)
            {
                continue;
            }

            (double from, double to) = Frequency(section, defaults);

            library._faces[code] = new FaceConfig(
                code,
                Value(section, "Face Name")?.ToUpperInvariant() ?? $"{code}_FACE",
                mouth,
                size,
                Spot(section, "Eyelids Offset") ?? default,
                Value(section, "Eyelids Alpha Channel")?.ToUpperInvariant(),
                Spot(section, "Forehead Offset") ?? default,
                Blinks(section),
                from,
                to);
        }

        return library;
    }

    /// <summary>Finds a character by the name a model or a scene uses.</summary>
    /// <param name="name">A model name, which may carry more than the character's code.</param>
    /// <returns>Their face, or null.</returns>
    /// <remarks>
    /// The same rule as <see cref="CharacterLibrary.Of"/>: a scene places <c>gab</c> and the
    /// file lists <c>GAB</c>, and the clothing variants — <c>gabclothes110a</c> and the rest
    /// — are the same person wearing the same face.
    /// </remarks>
    public FaceConfig? Of(string? name)
    {
        if (name is not { Length: > 0 })
        {
            return null;
        }

        if (_faces.TryGetValue(name, out FaceConfig? exact))
        {
            return exact;
        }

        return name.Length >= 3 && _faces.TryGetValue(name[..3], out FaceConfig? code)
            ? code
            : null;
    }

    /// <summary>Reads a value from a section, ignoring case and surrounding space.</summary>
    private static string? Value(IniSection section, string key) =>
        section.Lines
            .FirstOrDefault(l => l.Head.Key.Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            ?.Head.Value.Trim() is { Length: > 0 } found
            ? found
            : null;

    /// <summary>Reads a pair of numbers, however the file happens to separate them.</summary>
    /// <remarks>
    /// Offsets are written <c>90,132</c> and sizes <c>78x82</c>. The same shape, two
    /// separators, and both mean the same thing.
    /// </remarks>
    private static FaceSpot? Spot(IniSection section, string key, char separator = ',')
    {
        if (Value(section, key) is not { } value)
        {
            return null;
        }

        string[] parts = value.Split(separator);

        return parts.Length == 2 &&
               int.TryParse(parts[0].Trim(), out int x) &&
               int.TryParse(parts[1].Trim(), out int y)
            ? new FaceSpot(x, y)
            : null;
    }

    /// <summary>Reads the blink animations and the odds of each.</summary>
    /// <remarks>
    /// <c>gabblink,90,gabblink2,10</c> — pairs of a name and a weight. Every character in
    /// the file uses the same ninety/ten split between an ordinary blink and a double one,
    /// which is what keeps blinking from looking metronomic.
    /// </remarks>
    private static List<BlinkChoice> Blinks(IniSection section)
    {
        if (Value(section, "Blink Anims") is not { } value)
        {
            return [];
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        List<BlinkChoice> choices = [];

        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (parts[i].Length > 0 && int.TryParse(parts[i + 1], out int weight) && weight > 0)
            {
                choices.Add(new BlinkChoice(parts[i], weight));
            }
        }

        return choices;
    }

    /// <summary>How long between blinks, in seconds, falling back to the file's default.</summary>
    /// <remarks>
    /// Written in milliseconds — <c>5000,12000</c> — and the only entry the file says is
    /// worth copying from <c>[DEFAULT]</c>. A character with neither blinks every five to
    /// twelve seconds like everybody else.
    /// </remarks>
    private static (double From, double To) Frequency(IniSection section, IniSection? defaults)
    {
        FaceSpot? range = Spot(section, "Blink Frequency") ??
                          (defaults is not null ? Spot(defaults, "Blink Frequency") : null);

        return range is { } found && found.X > 0 && found.Y >= found.X
            ? (found.X / 1000.0, found.Y / 1000.0)
            : (5.0, 12.0);
    }
}
