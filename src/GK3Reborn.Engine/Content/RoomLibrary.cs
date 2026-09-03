// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// Rooms the game never had, as glTF.
/// </summary>
/// <remarks>
/// <para>
/// A room is a <c>.BSP</c> read out of the archives by name, and a scene whose <c>.BSP</c>
/// is missing fails outright — which is where the temple's cut second room stops, with its
/// object list, its light rig, its textures and sixty-two lines of dialogue all still on the
/// disc and no geometry. This is the way a room can exist without one: the model is read as
/// glTF and turned into the same shape a parsed <c>.BSP</c> has, so everything downstream —
/// drawing, picking, the floor, hidden objects, the light rig — works unchanged.
/// See <see cref="SceneFromModel"/>.
/// </para>
/// <para>
/// <b>It answers only for names the archives have no <c>.BSP</c> for.</b> The same boundary
/// the prop library has, for the same reason and with more at stake: a library that could
/// stand in front of the game's own rooms would replace one the moment a workspace happened
/// to hold a mesh of the same name, and a room is not a chair — its floor, its walk
/// boundary, its cameras and its bake all belong to the original. Replacing a room that
/// shipped is what <see cref="EnhancedScenes"/> does, carefully, as an overlay that keeps
/// all of those; this cannot do it at all.
/// </para>
/// <para>
/// Nothing here reaches the 1999 archives. A room comes from <c>overrides/</c>, from a
/// content workspace, or from a ReBarn volume, and never from a barn.
/// </para>
/// <para>
/// It shares <see cref="RebarnKind.SceneGeometry"/> with the improved-geometry overlays and
/// cannot collide with them: those are addressed inside a pack by the hash of the shape they
/// carry, these by the room's own name, and the two are consulted in mutually exclusive
/// cases — an overlay only where a <c>.BSP</c> exists, a room only where none does.
/// </para>
/// </remarks>
public sealed class RoomLibrary
{
    private readonly Dictionary<string, BspFile?> _built = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _loose;
    private readonly RebarnContent? _packs;

    private RoomLibrary(Dictionary<string, string> loose, RebarnContent? packs)
    {
        _loose = loose;
        _packs = packs;
    }

    /// <summary>A library with nothing in it, which builds no rooms.</summary>
    public static RoomLibrary Empty { get; } = new([], null);

    /// <summary>Files a player has dropped into <c>overrides/</c>, which outrank the rest.</summary>
    public ContentOverrides? Overrides { get; set; }

    /// <summary>How many loose rooms are available.</summary>
    public int Count => _loose.Count;

    /// <summary>Whether there is nowhere at all to look.</summary>
    public bool IsEmpty =>
        _loose.Count == 0 && _packs is null &&
        (Overrides?.CountOf(RebarnKind.SceneGeometry) ?? 0) == 0;

    /// <summary>Indexes a directory of rooms, a set of packs, or both.</summary>
    /// <param name="directory">Where the loose ones are. May be empty or missing.</param>
    /// <param name="packs">Packs beside the executable, or null for none.</param>
    /// <param name="diagnostics">Receives a warning when the directory cannot be read.</param>
    /// <returns>The library, empty when there is nowhere to look.</returns>
    public static RoomLibrary Open(
        string directory, RebarnContent? packs = null, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        Dictionary<string, string> loose = new(StringComparer.OrdinalIgnoreCase);

        if (directory.Length > 0 && Directory.Exists(directory))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory)
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string extension = Path.GetExtension(file);

                    if (extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
                    {
                        loose.TryAdd(Path.GetFileNameWithoutExtension(file), file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics?.Add(new Diagnostic(
                    "GK3R1198",
                    DiagnosticSeverity.Warning,
                    $"The rooms directory cannot be read, so no room is built from it: {ex.Message}",
                    directory,
                    null,
                    "a readable directory",
                    ex.GetType().Name,
                    "Check the permissions on it, or take it away."));
            }
        }

        return loose.Count == 0 && packs is null ? Empty : new RoomLibrary(loose, packs);
    }

    /// <summary>Whether a room of this name is available.</summary>
    /// <param name="name">The room's name, without extension.</param>
    /// <returns>True when one of the layers has it.</returns>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Overrides?.Has(RebarnKind.SceneGeometry, name) == true
            || _loose.ContainsKey(name)
            || _packs?.Has(RebarnKind.SceneGeometry, name) == true;
    }

    /// <summary>Builds a room, if one of the layers has it and it can be one.</summary>
    /// <param name="name">The room's name, without extension.</param>
    /// <param name="diagnostics">Receives the reason whenever one is refused.</param>
    /// <returns>The room, or null when there is none to build.</returns>
    /// <remarks>Built once and kept, like every other room the loader reads.</remarks>
    public BspFile? Read(string name, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_built.TryGetValue(name, out BspFile? already))
        {
            return already;
        }

        BspFile? room = null;

        try
        {
            if (Bytes(name, diagnostics) is { Length: > 0 } bytes &&
                GlbReader.TryParse(bytes, name + ".glb", diagnostics) is { } model)
            {
                room = SceneFromModel.Build(model, name, diagnostics);
            }
        }
        catch (IOException ex)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1199",
                DiagnosticSeverity.Warning,
                $"The room {name} will not open, so it cannot be built: {ex.Message}",
                name));
        }

        _built[name] = room;

        return room;
    }

    /// <summary>A line for the startup log.</summary>
    /// <returns>What is available, or null when nothing is.</returns>
    public string? Describe()
    {
        int overridden = Overrides?.CountOf(RebarnKind.SceneGeometry) ?? 0;

        if (IsEmpty)
        {
            return null;
        }

        List<string> parts = [];

        if (_loose.Count > 0)
        {
            parts.Add($"{_loose.Count} loose");
        }

        if (_packs is not null)
        {
            parts.Add("the packs");
        }

        if (overridden > 0)
        {
            parts.Add($"{overridden} overridden");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private byte[]? Bytes(string name, DiagnosticBag? diagnostics)
    {
        if (Overrides?.Read(RebarnKind.SceneGeometry, name, diagnostics) is { } replaced)
        {
            return replaced;
        }

        if (_loose.TryGetValue(name, out string? file) && File.Exists(file))
        {
            return File.ReadAllBytes(file);
        }

        return _packs?.Read(RebarnKind.SceneGeometry, name);
    }
}
