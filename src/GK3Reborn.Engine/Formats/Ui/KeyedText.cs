// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Text;

namespace GK3Reborn.Formats.Ui;

/// <summary>
/// The game's prose files: sections of <c>key = the rest of the line</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ESIDNEY.TXT</c>, <c>ESIDNEYEMAIL.TXT</c> and their neighbours look like the scene
/// initialisation files and are not the same thing. A <c>.SIF</c> line is a list of
/// comma-separated settings and <see cref="Ini.IniDocument"/> splits it as one; these hold
/// a single value that runs to the end of the line, and most of them are English sentences
/// full of commas. Parsing one with the other reader turns a paragraph into forty settings.
/// </para>
/// <para>
/// So: sections in brackets, one key to a line, everything after the first <c>=</c> is the
/// value. Comments start with <c>;</c> or <c>//</c> — both appear, sometimes in the same
/// file — and a key may repeat, which is how <c>Body1</c>, <c>Body2</c> would work if the
/// game had not numbered them; order is kept either way.
/// </para>
/// <para>
/// Two escapes are decoded, because the files are written for a renderer that understood
/// them: <c>\n</c> and <c>\t</c> as the characters they name, and <c>&lt;space&gt;</c> as a
/// paragraph break on a line of its own. Nothing else is interpreted — a <c>%s</c> in the
/// Sidney text is a placeholder its caller fills, and this is not its caller.
/// </para>
/// </remarks>
public sealed class KeyedText
{
    private readonly Dictionary<string, List<(string Key, string Value)>> _sections;

    private KeyedText(
        string name, Dictionary<string, List<(string, string)>> sections, IReadOnlyList<string> order)
    {
        Name = name;
        _sections = sections;
        SectionNames = order;
    }

    /// <summary>Name used in diagnostics.</summary>
    public string Name { get; }

    /// <summary>Every section, in the order the file declares them.</summary>
    public IReadOnlyList<string> SectionNames { get; }

    /// <summary>Reads a file.</summary>
    /// <param name="text">Its contents.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>The parsed file.</returns>
    public static KeyedText Parse(string text, string name = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(text);

        Dictionary<string, List<(string, string)>> sections =
            new(StringComparer.OrdinalIgnoreCase);

        List<string> order = [];

        // Everything before the first bracket belongs to a section with no name, which is
        // where a file that never declares one puts its keys.
        string current = string.Empty;
        List<(string, string)> lines = [];

        sections[current] = lines;
        order.Add(current);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');

            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');

                current = close > 1 ? line[1..close].Trim() : line[1..].Trim();

                if (!sections.TryGetValue(current, out lines!))
                {
                    lines = [];
                    sections[current] = lines;
                    order.Add(current);
                }

                continue;
            }

            int equals = line.IndexOf('=');

            if (equals <= 0)
            {
                continue;
            }

            lines.Add((line[..equals].Trim(), Decode(line[(equals + 1)..].Trim())));
        }

        return new KeyedText(name, sections, order);
    }

    /// <summary>Every key and value of a section, in order.</summary>
    /// <param name="section">The section's name.</param>
    /// <returns>Its lines, which is empty when there is no such section.</returns>
    public IReadOnlyList<(string Key, string Value)> Section(string section)
    {
        ArgumentNullException.ThrowIfNull(section);

        return _sections.TryGetValue(section, out List<(string, string)>? lines) ? lines : [];
    }

    /// <summary>Whether a section exists.</summary>
    /// <param name="section">Its name.</param>
    /// <returns>True when the file declares it.</returns>
    public bool Has(string section) => _sections.ContainsKey(section);

    /// <summary>One value of a section.</summary>
    /// <param name="section">The section's name.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null when either is missing.</returns>
    public string? Value(string section, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        foreach ((string name, string value) in Section(section))
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// The values of a numbered run — <c>Body1</c>, <c>Body2</c>, and so on.
    /// </summary>
    /// <param name="section">The section's name.</param>
    /// <param name="prefix">What the keys are called before their number.</param>
    /// <returns>The values in order, stopping at the first number that is missing.</returns>
    /// <remarks>
    /// Stopping at the first gap rather than gathering every match, because the files
    /// number from one and a gap is a mistake rather than a signal. Gathering past one
    /// would silently reorder somebody's paragraphs.
    /// </remarks>
    public IReadOnlyList<string> Run(string section, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        List<string> found = [];

        for (int i = 1; ; i++)
        {
            if (Value(section, prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                is not { } value)
            {
                return found;
            }

            found.Add(value);
        }
    }

    /// <summary>Turns the file's escapes into the characters they name.</summary>
    private static string Decode(string value)
    {
        if (string.Equals(value, "<space>", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var built = new StringBuilder(value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                built.Append(value[i]);
                continue;
            }

            switch (value[i + 1])
            {
                case 'n':
                    built.Append('\n');
                    i++;
                    break;

                case 't':
                    built.Append('\t');
                    i++;
                    break;

                case '\\':
                    built.Append('\\');
                    i++;
                    break;

                default:
                    built.Append(value[i]);
                    break;
            }
        }

        return built.ToString();
    }
}
