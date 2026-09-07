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
/// <remarks>
/// <para>
/// There are two shapes of file under this extension and only one of them is a sequence of
/// pictures. Seventeen are <c>Sequence Type=Series</c> followed by <c>Sprite List=</c> and a
/// comma-separated run of bitmap names; the rest are a bare list of animation names, one to
/// a line, with no keys at all. This reader is for the first sort and answers the second
/// with an empty <see cref="Sprites"/> rather than mistaking a name for a key.
/// </para>
/// <para>
/// <b>One key to a line.</b> The sprite list is commas all the way along and is a single
/// value, not a row of settings — reading it the way a <c>.SIF</c> line is read turns one
/// sequence into fifteen keyless entries. That is what <c>multipleEntriesPerLine</c> is for.
/// </para>
/// <para>
/// Format understanding from G-Engine's <c>Sequence</c> by Clark Kromenaker
/// (https://github.com/kromenak/gengine), GNU General Public License version 3. See NOTICE.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// Every one in the archives says <c>Series</c>. It is carried rather than checked so
    /// that a file saying something else is visible in a report instead of being silently
    /// played as if it had not.
    /// </remarks>
    public string? Type { get; }

    /// <summary>
    /// The pictures, in the order they are shown, without extensions.
    /// </summary>
    /// <remarks>
    /// A name may repeat: <c>D312P.SEQ</c> ends on its last frame twice, which holds the
    /// finished lettering for an extra frame. Kept as written, because the repeat is the
    /// timing.
    /// </remarks>
    public IReadOnlyList<string> Sprites { get; }

    /// <summary>
    /// How many pictures a second a series runs at.
    /// </summary>
    /// <remarks>
    /// Fifteen. No file in the archives carries a rate, and the original's own sequencer
    /// has this as its default, so it is the rate every one of them was authored against.
    /// </remarks>
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
