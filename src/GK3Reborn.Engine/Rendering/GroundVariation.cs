// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering;

/// <summary>
/// How far an outdoor room's ground may depart from the picture painted on it.
/// </summary>
public static class GroundVariation
{
    /// <summary>A surface left exactly as it was drawn.</summary>
    public const float None = 0f;

    /// <summary>What the floor map calls a lawn, a verge, a meadow.</summary>
    private const float Grass = 1f;

    /// <summary>What it calls bare earth, mud, a track, a tyre rut.</summary>
    private const float Dirt = 0.85f;

    /// <summary>
    /// What it calls concrete, which is every rock face and cliff in the game as well as
    /// the roads. Told apart below, since the two want opposite amounts.
    /// </summary>
    private const float Stone = 0.5f;

    /// <summary>Rock, which is stone that has weathered and should read as though it has.</summary>
    private const float Rock = 0.8f;

    /// <summary>Ground the floor map has nothing to say about.</summary>
    private const float Unknown = 0.5f;

    /// <summary>The names <c>FLOORMAP.TXT</c> gives the ground that is out of doors.</summary>
    private static readonly Dictionary<string, float> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grass"] = Grass,
        ["Dirt"] = Dirt,
        ["Concrete"] = Stone,

        // Indoors, and named here so that a room with a skybox through its window — the
        // museum, every hotel bedroom — cannot have its parquet treated as a hillside.
        ["Carpet"] = None,
        ["Tile"] = None,
        ["Wood"] = None,
    };

    /// <summary>
    /// Words in a texture's name that say it is rock rather than the road or the pavement
    /// the floor map files it beside.
    /// </summary>
    private static readonly string[] Weathered =
        ["rock", "rck", "stone", "cliff", "pbly", "boulder", "scree"];

    /// <summary>How far this ground may vary.</summary>
    /// <param name="texture">The colour texture's name, with or without an extension.</param>
    /// <param name="groundOf">
    /// What <c>FLOORMAP.TXT</c> says a texture is underfoot, or null where it says nothing.
    /// </param>
    /// <returns>Nought to leave the surface alone, up to one for the full treatment.</returns>
    public static float Of(string texture, Func<string?, string?> groundOf)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(groundOf);

        string plain = Path.GetFileNameWithoutExtension(texture);

        if (plain.Length == 0)
        {
            return None;
        }

        // The same data quirk Grass.Cover has to step over: the floor map files a
        // transparent checker under grass, and it is a keying texture rather than ground.
        if (plain.Contains("trans", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        if (groundOf(plain) is not { Length: > 0 } type)
        {
            return Unknown;
        }

        if (!Types.TryGetValue(type, out float varies))
        {
            return Unknown;
        }

        // A rock face filed as concrete. The bucket holds `cdbwhtrck`, `Rock_Blend`,
        // `mcfstriatrockL` and `roqstone` beside `Ploasph` and `GRIdrivewycncrt`, and one
        // number cannot serve both: a cliff wants weathering and a driveway does not.
        return string.Equals(type, "Concrete", StringComparison.OrdinalIgnoreCase) &&
               Weathers(plain)
            ? Rock
            : varies;
    }

    /// <summary>Whether a name says rock rather than made ground.</summary>
    /// <param name="plain">The texture's name without its extension.</param>
    /// <returns>True for a rock face, a cliff, a pebble bed.</returns>
    private static bool Weathers(string plain)
    {
        foreach (string word in Weathered)
        {
            if (plain.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
