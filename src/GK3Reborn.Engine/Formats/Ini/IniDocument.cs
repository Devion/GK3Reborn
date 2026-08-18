using System.Globalization;
using System.Numerics;

namespace GK3Reborn.Formats.Ini;

/// <summary>One <c>key=value</c> pair, or a bare keyword whose value repeats its key.</summary>
/// <param name="Key">The key.</param>
/// <param name="Value">The value, or the key again for a bare keyword such as <c>hidden</c>.</param>
public readonly record struct IniEntry(string Key, string Value)
{
    /// <summary>Reads the value as a number.</summary>
    /// <returns>The number, or null if it is not one.</returns>
    public float? AsNumber() =>
        float.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : null;

    /// <summary>Reads the value as an integer.</summary>
    /// <returns>The integer, or null if it is not one.</returns>
    public int? AsInteger() =>
        int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    /// <summary>Reads the value as a list of numbers.</summary>
    /// <param name="components">How many are expected.</param>
    /// <returns>The numbers, or null if the value is not a list of that length.</returns>
    /// <remarks>
    /// Both brace-delimited forms (<c>pos={1, 2, 3}</c>, used in scene initialisation
    /// files) and bare comma-separated forms (<c>Position=1,2,3</c>, used in scene
    /// assets) appear in the data, so both are accepted here.
    /// </remarks>
    public float[]? AsNumbers(int components)
    {
        string trimmed = Value.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            trimmed = trimmed[1..^1];
        }

        string[] parts = trimmed.Split(',');
        if (parts.Length != components)
        {
            return null;
        }

        float[] values = new float[components];
        for (int i = 0; i < components; i++)
        {
            if (!float.TryParse(
                    parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        return values;
    }
}

/// <summary>One line of a section: its entries, in order.</summary>
/// <param name="Entries">The key/value pairs on the line.</param>
public sealed record IniLine(IReadOnlyList<IniEntry> Entries)
{
    /// <summary>The line's first entry, which names what the line is about.</summary>
    public IniEntry Head => Entries.Count > 0 ? Entries[0] : default;

    /// <summary>Finds an entry by key.</summary>
    /// <param name="key">Key to look for, matched case-insensitively.</param>
    /// <returns>The entry, or null.</returns>
    public IniEntry? Find(string key)
    {
        foreach (IniEntry entry in Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Whether a bare keyword such as <c>hidden</c> is present.</summary>
    /// <param name="keyword">The keyword.</param>
    /// <returns>True if present.</returns>
    public bool HasFlag(string keyword) => Find(keyword) is not null;

    /// <summary>Reads a value by key.</summary>
    /// <param name="key">Key to look for.</param>
    /// <returns>The value, or null.</returns>
    public string? Value(string key) => Find(key)?.Value;

    /// <summary>Reads a number by key.</summary>
    /// <param name="key">Key to look for.</param>
    /// <returns>The number, or null.</returns>
    public float? Number(string key) => Find(key)?.AsNumber();

    /// <summary>Reads a three-component vector by key.</summary>
    /// <param name="key">Key to look for.</param>
    /// <returns>The vector, or null.</returns>
    public Vector3? Vector(string key) =>
        Find(key)?.AsNumbers(3) is { } v ? new Vector3(v[0], v[1], v[2]) : null;
}

/// <summary>A section, together with the condition that gates it.</summary>
/// <param name="Name">Section name, such as <c>MODELS</c>.</param>
/// <param name="Condition">
/// The Sheep expression in the header, or null when the section is unconditional.
/// </param>
/// <param name="Lines">Its lines, in order.</param>
public sealed record IniSection(string Name, string? Condition, IReadOnlyList<IniLine> Lines);

/// <summary>
/// Reader for the INI dialect GK3's text assets use.
/// </summary>
/// <remarks>
/// <para>
/// Scene initialisation files, scene assets, cursors, fonts and several other asset types
/// share this format, so it is parsed once here rather than once per asset type.
/// </para>
/// <para>
/// It differs from ordinary INI in three ways. A line may carry several comma-separated
/// key/value pairs, and commas inside braces belong to the value rather than separating
/// pairs — <c>pos={1, 2, 3}</c> is one pair, not three. A bare token with no <c>=</c> is a
/// keyword whose value repeats its key, which is how flags like <c>hidden</c> are written.
/// And a section header may carry a condition, <c>[MODELS={IsCurrentTime("202p")}]</c>,
/// so the same section appears repeatedly with contents that depend on the story's state.
/// </para>
/// <para>
/// Conditions are kept verbatim. They are Sheep expressions, and evaluating them belongs
/// to the Sheep virtual machine rather than to a text parser.
/// </para>
/// </remarks>
public sealed class IniDocument
{
    private IniDocument(string name, IReadOnlyList<IniSection> sections)
    {
        Name = name;
        Sections = sections;
    }

    /// <summary>Name this document was read under.</summary>
    public string Name { get; }

    /// <summary>Every section, in file order, including repeats of the same name.</summary>
    public IReadOnlyList<IniSection> Sections { get; }

    /// <summary>Parses a document.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="multipleEntriesPerLine">
    /// Whether a line may hold several comma-separated key/value pairs.
    /// </param>
    /// <returns>The parsed document.</returns>
    /// <remarks>
    /// Whether commas separate pairs or belong to the value is a property of the asset
    /// type, not something that can be inferred from a line. Scene initialisation files
    /// write <c>pos={1, 2, 3}, heading=90</c> and need splitting; scene assets write
    /// <c>Position=1,2,3</c> and must not be split, or every vector in them reduces to
    /// its first component. Guessing per line gets the second case wrong silently, which
    /// is how a scene ends up with no lights at all rather than an error.
    /// </remarks>
    public static IniDocument Parse(
        string text, string name = "<memory>", bool multipleEntriesPerLine = true)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<IniSection> sections = [];
        string currentName = string.Empty;
        string? currentCondition = null;
        List<IniLine> lines = [];
        bool inBlockComment = false;

        void Flush()
        {
            if (currentName.Length > 0 || lines.Count > 0)
            {
                sections.Add(new IniSection(currentName, currentCondition, lines));
            }

            lines = [];
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = StripComments(raw, ref inBlockComment).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                Flush();

                string header = line[1..^1];
                int equals = header.IndexOf('=', StringComparison.Ordinal);

                if (equals >= 0)
                {
                    currentName = header[..equals].Trim();
                    currentCondition = header[(equals + 1)..].Trim().Trim('{', '}').Trim();
                }
                else
                {
                    currentName = header.Trim();
                    currentCondition = null;
                }

                continue;
            }

            List<IniEntry> entries = [];
            foreach (string part in multipleEntriesPerLine ? SplitOutsideBraces(line) : [line])
            {
                string pair = part.Trim();
                if (pair.Length == 0)
                {
                    continue;
                }

                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                entries.Add(equals >= 0
                    ? new IniEntry(pair[..equals].Trim(), pair[(equals + 1)..].Trim())

                    // A bare keyword. The original repeats it as the value, and code that
                    // reads these relies on the value never being empty.
                    : new IniEntry(pair, pair));
            }

            if (entries.Count > 0)
            {
                lines.Add(new IniLine(entries));
            }
        }

        Flush();
        return new IniDocument(name, sections);
    }

    /// <summary>Every line of every section with a given name.</summary>
    /// <param name="section">Section name.</param>
    /// <param name="includeConditional">Whether to include sections gated by a condition.</param>
    /// <returns>The lines.</returns>
    public IEnumerable<IniLine> LinesOf(string section, bool includeConditional = false) =>
        Sections
            .Where(s => string.Equals(s.Name, section, StringComparison.OrdinalIgnoreCase))
            .Where(s => includeConditional || s.Condition is null)
            .SelectMany(s => s.Lines);

    /// <summary>Every section whose name begins with a prefix.</summary>
    /// <param name="prefix">The prefix, matched case-insensitively.</param>
    /// <returns>The sections.</returns>
    /// <remarks>
    /// Scene assets name a section per light, <c>[Light_omni01]</c>, so the name carries
    /// data rather than only structure.
    /// </remarks>
    public IEnumerable<IniSection> SectionsStartingWith(string prefix) =>
        Sections.Where(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string StripComments(string raw, ref bool inBlockComment)
    {
        string line = raw.Replace("\r", string.Empty, StringComparison.Ordinal)
                         .Replace("\t", string.Empty, StringComparison.Ordinal);

        if (inBlockComment)
        {
            int end = line.IndexOf("*/", StringComparison.Ordinal);
            if (end < 0)
            {
                return string.Empty;
            }

            line = line[(end + 2)..];
            inBlockComment = false;
        }

        int blockStart = line.IndexOf("/*", StringComparison.Ordinal);
        if (blockStart >= 0)
        {
            int end = line.IndexOf("*/", blockStart + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                line = line[..blockStart];
                inBlockComment = true;
            }
            else
            {
                line = line[..blockStart] + line[(end + 2)..];
            }
        }

        int comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment >= 0 ? line[..comment] : line;
    }

    /// <summary>Splits a line on commas that are not inside braces.</summary>
    private static IEnumerable<string> SplitOutsideBraces(string line)
    {
        int depth = 0;
        int start = 0;

        for (int i = 0; i < line.Length; i++)
        {
            switch (line[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return line[start..i];
                    start = i + 1;
                    break;
                default:
                    break;
            }
        }

        yield return line[start..];
    }
}
