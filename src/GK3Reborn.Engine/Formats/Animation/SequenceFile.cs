// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Ini;

namespace GK3Reborn.Formats.Animation;

/// <summary>
/// A <c>.SEQ</c>: a list of pictures to show one after another.
/// </summary>
public sealed class SequenceFile
{
    private SequenceFile(string name, string? type, IReadOnlyList<string> sprites)
    {
        Name = name;
        Type = type;
        Sprites = sprites;
    }

    /// <summary>What the file was called, for reports.</summary>
    public string Name { get; }

    /// <summary>The <c>Sequence Type</c> the file declares, or null when it declares none.</summary>
    public string? Type { get; }

    /// <summary>
    /// The pictures, in the order they are shown, without extensions.
    /// </summary>
    public IReadOnlyList<string> Sprites { get; }

    /// <summary>
    /// How many pictures a second a series runs at.
    /// </summary>
    public const double FramesPerSecond = 15.0;

    /// <summary>Reads one.</summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="name">What it was called, for reports.</param>
    /// <returns>The sequence, with no sprites when the file lists none.</returns>
    public static SequenceFile Parse(string text, string name = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(text);

        IniDocument document = IniDocument.Parse(text, name, multipleEntriesPerLine: false);

        string? type = null;
        List<string> sprites = [];

        foreach (IniLine line in document.LinesOf(string.Empty))
        {
            IniEntry entry = line.Head;

            // A bare line in one of the animation-name files parses as a keyword whose
            // value is itself. Neither key below matches one, so those files come back
            // empty, which is what a caller after pictures should see.
            if (string.Equals(entry.Key, "Sequence Type", StringComparison.OrdinalIgnoreCase))
            {
                type = entry.Value;
            }
            else if (string.Equals(entry.Key, "Sprite List", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string part in entry.Value.Split(','))
                {
                    if (part.Trim() is { Length: > 0 } sprite)
                    {
                        sprites.Add(sprite);
                    }
                }
            }
        }

        return new SequenceFile(name, type, sprites);
    }
}
