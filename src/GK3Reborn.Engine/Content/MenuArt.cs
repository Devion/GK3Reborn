// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// The layers the title screen is built out of, wherever they are to be had.
/// </summary>
public sealed class MenuArt
{
    /// <summary>The statue, cut out, which stands on the left.</summary>
    public const string Statue = "Angel";

    /// <summary>The wall behind it, which scrolls and is horizontally seamless.</summary>
    public const string Wall = "RedWOffset";

    /// <summary>The game's name, which stands to the right of the statue.</summary>
    public const string Name = "titlename";

    /// <summary>The three sigils, one of which surfaces in the wall at a time.</summary>
    public static IReadOnlyList<string> Sigils { get; } = ["SA", "Schat", "Penta"];

    /// <summary>Every layer the screen needs, in the order they are drawn.</summary>
    public static IReadOnlyList<string> Layers { get; } =
        [Statue, Wall, Name, .. Sigils];

    private readonly Dictionary<string, DecodedImage> _layers =
        new(StringComparer.OrdinalIgnoreCase);

    private MenuArt(string from) => From = from;

    /// <summary>Where the layers came from, for the startup line.</summary>
    public string From { get; }

    /// <summary>How many of the six were found and decoded.</summary>
    public int Count => _layers.Count;

    /// <summary>Whether the whole screen is there.</summary>
    public bool Complete => _layers.Count == Layers.Count;

    /// <summary>Which of the six are missing, for the line that says why.</summary>
    public IReadOnlyList<string> Missing =>
        [.. Layers.Where(layer => !_layers.ContainsKey(layer))];

    /// <summary>A set with nothing in it, which is what a game with no packs has.</summary>
    public static MenuArt None { get; } = new("nowhere");

    /// <summary>One layer.</summary>
    /// <param name="layer">Its name, as <see cref="Layers"/> spells it.</param>
    /// <returns>The picture, or null when this set has not got it.</returns>
    public DecodedImage? this[string layer]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(layer);

            return _layers.TryGetValue(layer, out DecodedImage found) ? found : null;
        }
    }

    /// <summary>Finds the title screen's layers.</summary>
    /// <param name="packs">The volumes beside the executable, or null for none.</param>
    /// <param name="directory">
    /// The workspace's <c>enhanced/menu</c>, or empty when there is no workspace. What a
    /// development machine reads; a shipped game has only the pack.
    /// </param>
    /// <param name="overrides">What the player dropped into <c>overrides/</c>, or null.</param>
    /// <param name="diagnostics">Where a layer that will not decode is reported.</param>
    /// <returns>
    /// The set. <see cref="Complete"/> says whether the modern screen can be drawn at all.
    /// </returns>
    public static MenuArt Open(
        RebarnContent? packs,
        string directory,
        ContentOverrides? overrides,
        DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        // The player's own file first, then a loose workspace PNG, then the pack. The same
        // order every other set in this project uses, and for the same reason: there is one
        // answer per name, and the nearer it was authored to the player the more it wins.
        IReadOnlyDictionary<string, string> dropped =
            overrides?.Images(RebarnKind.Menu) ?? new Dictionary<string, string>();

        var art = new MenuArt(
            dropped.Count > 0 ? $"overrides/, then {Where(packs, directory)}"
                : Where(packs, directory));

        foreach (string layer in Layers)
        {
            if (Read(layer, dropped, directory, packs, diagnostics) is { } picture)
            {
                art._layers[layer] = picture;
            }
        }

        return art;
    }

    private static string Where(RebarnContent? packs, string directory) =>
        directory.Length > 0 && System.IO.Directory.Exists(directory)
            ? directory
            : packs is not null ? "a pack" : "nowhere";

    private static DecodedImage? Read(
        string layer,
        IReadOnlyDictionary<string, string> dropped,
        string directory,
        RebarnContent? packs,
        DiagnosticBag? diagnostics)
    {
        if (dropped.TryGetValue(layer, out string? own) && Decode(own, diagnostics) is { } mine)
        {
            return mine;
        }

        if (directory.Length > 0)
        {
            string loose = Path.Combine(directory, layer + ".png");

            if (File.Exists(loose) && Decode(loose, diagnostics) is { } fromFile)
            {
                return fromFile;
            }
        }

        if (packs?.Read(RebarnKind.Menu, layer) is not { Length: > 0 } bytes)
        {
            return null;
        }

        return Decode(bytes, $"{layer} (packed)", diagnostics);
    }

    private static DecodedImage? Decode(string file, DiagnosticBag? diagnostics)
    {
        try
        {
            return Decode(File.ReadAllBytes(file), file, diagnostics);
        }
        catch (IOException error)
        {
            Complain(file, error, diagnostics);

            return null;
        }
    }

    private static DecodedImage? Decode(byte[] bytes, string what, DiagnosticBag? diagnostics)
    {
        try
        {
            // Straight alpha and eight bits a channel, which is what the interface's own
            // picture list takes. The layers are painted as PNGs and packed as PNGs, so
            // this is the only decode there is.
            return BitmapDecoder.CanDecode(bytes)
                ? BitmapDecoder.Decode(bytes, what)
                : PngReader.Decode(bytes, what);
        }
        catch (Exception error) when (error is FormatParseException or FormatException)
        {
            Complain(what, error, diagnostics);

            return null;
        }
    }

    private static void Complain(string what, Exception error, DiagnosticBag? diagnostics) =>
        diagnostics?.Add(new Diagnostic(
            "GK3R1096",
            DiagnosticSeverity.Warning,
            $"The title screen's {what} will not decode, so the game opens on the "
            + $"original title art instead: {error.Message}",
            what,
            null,
            "a readable PNG",
            error.GetType().Name,
            "Produce the layer again, or take it out of enhanced/menu and repack."));
}
