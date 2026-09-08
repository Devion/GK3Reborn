// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Content;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// What a step sounds like: which floor, which shoes.
/// </summary>
public sealed class Footsteps
{
    private readonly Dictionary<string, string> _ground = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Shoe, string Ground), string[]> _steps = [];
    private readonly Dictionary<(string Shoe, string Ground), string[]> _scuffs = [];

    private Footsteps()
    {
    }

    /// <summary>An empty set, for a run with no archives.</summary>
    public static Footsteps None { get; } = new();

    /// <summary>How many floor textures are classified.</summary>
    public int SurfaceCount => _ground.Count;

    /// <summary>How many shoe-and-ground pairs have sounds.</summary>
    public int SoundCount => _steps.Count + _scuffs.Count;

    /// <summary>Reads the three files out of the archives.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <returns>The set, empty where a file is missing.</returns>
    public static Footsteps Open(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        var steps = new Footsteps();

        if (archives.ReadText("FLOORMAP.TXT") is { } map)
        {
            steps.ReadFloors(map);
        }

        if (archives.ReadText("FOOTSTEPS.TXT") is { } walking)
        {
            ReadSounds(walking, steps._steps);
        }

        if (archives.ReadText("FOOTSCUFFS.TXT") is { } scuffing)
        {
            ReadSounds(scuffing, steps._scuffs);
        }

        return steps;
    }

    /// <summary>Reads the three files' text.</summary>
    /// <param name="floors">The floor map.</param>
    /// <param name="steps">The footstep sounds.</param>
    /// <param name="scuffs">The scuff sounds.</param>
    /// <returns>The set.</returns>
    public static Footsteps Parse(string floors, string steps, string scuffs)
    {
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(scuffs);

        var read = new Footsteps();

        read.ReadFloors(floors);
        ReadSounds(steps, read._steps);
        ReadSounds(scuffs, read._scuffs);

        return read;
    }

    /// <summary>What kind of ground a floor texture is.</summary>
    /// <param name="texture">The texture's name, with or without an extension.</param>
    /// <returns>The ground type, or null when nothing classifies it.</returns>
    public string? GroundOf(string? texture) =>
        texture is { Length: > 0 } named &&
        _ground.TryGetValue(Path.GetFileNameWithoutExtension(named), out string? ground)
            ? ground
            : null;

    /// <summary>
    /// The sounds a step makes.
    /// </summary>
    /// <param name="shoe">The shoe type, out of <c>CHARACTERS.TXT</c>.</param>
    /// <param name="texture">The floor texture underfoot.</param>
    /// <param name="scuff">Whether it is a scuff rather than a step.</param>
    /// <returns>The candidates, empty when nothing matches.</returns>
    public IReadOnlyList<string> Sounds(string? shoe, string? texture, bool scuff = false)
    {
        if (shoe is not { Length: > 0 } worn || GroundOf(texture) is not { } ground)
        {
            return [];
        }

        Dictionary<(string, string), string[]> table = scuff ? _scuffs : _steps;

        return table.TryGetValue((worn, ground), out string[]? sounds) ? sounds : [];
    }

    /// <summary>Reads the texture-to-ground map.</summary>
    private void ReadFloors(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 ||
                line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith('[') ||
                line.IndexOf('=') is not (> 0 and { } equals))
            {
                continue;
            }

            string ground = line[..equals].Trim();

            foreach (string texture in line[(equals + 1)..].Split(','))
            {
                if (texture.Trim() is { Length: > 0 } named)
                {
                    _ground[Path.GetFileNameWithoutExtension(named)] = ground;
                }
            }
        }
    }

    /// <summary>Reads one of the two sound tables.</summary>
    private static void ReadSounds(string text, Dictionary<(string, string), string[]> into)
    {
        string shoe = string.Empty;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                shoe = close > 1 ? line[1..close].Trim() : line[1..].Trim();
                continue;
            }

            if (shoe.Length == 0 || line.IndexOf('=') is not (> 0 and { } equals))
            {
                continue;
            }

            string[] sounds = [.. line[(equals + 1)..]
                .Split(',')
                .Select(n => n.Trim())
                .Where(n => n.Length > 0)];

            if (sounds.Length > 0)
            {
                into[(shoe, line[..equals].Trim())] = sounds;
            }
        }
    }
}
