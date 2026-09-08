// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Security.Cryptography;
using System.Text.Json;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// The improved room geometry that has been built, if any has.
/// </summary>
public sealed class EnhancedScenes
{
    private readonly Dictionary<string, SceneGeometryRoom> _rooms;
    private readonly Dictionary<string, ModFile?> _shapes = new(StringComparer.Ordinal);
    private readonly string _directory;
    private readonly RebarnContent? _packs;

    private EnhancedScenes(
        Dictionary<string, SceneGeometryRoom> rooms, string directory, RebarnContent? packs)
    {
        _rooms = rooms;
        _directory = directory;
        _packs = packs;
    }

    /// <summary>A library with nothing in it, which replaces nothing.</summary>
    public static EnhancedScenes Empty { get; } =
        new(new Dictionary<string, SceneGeometryRoom>(StringComparer.OrdinalIgnoreCase),
            string.Empty,
            null);

    /// <summary>How many rooms have improved geometry available.</summary>
    public int Count => _rooms.Count;

    /// <summary>Whether there is anything here to draw.</summary>
    public bool IsEmpty => _rooms.Count == 0;

    /// <summary>Whether what is here came out of a pack rather than a loose directory.</summary>
    public bool Packed => _directory.Length == 0;

    /// <summary>Indexes a directory of composed rooms, a set of packs, or both.</summary>
    /// <param name="directory">Where the loose ones are. May be empty or missing.</param>
    /// <param name="packs">Packs beside the executable, or null for none.</param>
    /// <param name="diagnostics">Receives a warning when the manifest will not read.</param>
    /// <returns>The library, empty when neither has anything.</returns>
    public static EnhancedScenes Open(
        string directory, RebarnContent? packs = null, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        string manifest = directory.Length > 0
            ? Path.Combine(directory, "scene-geometry.json")
            : string.Empty;

        bool loose = manifest.Length > 0 && File.Exists(manifest);

        if (!loose && packs?.Has(RebarnKind.Manifest, "scene-geometry") != true)
        {
            return Empty;
        }

        SceneGeometryManifest? read;

        try
        {
            byte[] bytes = loose
                ? File.ReadAllBytes(manifest)
                : packs!.Read(RebarnKind.Manifest, "scene-geometry") ?? [];

            read = bytes.Length == 0
                ? null
                : JsonSerializer.Deserialize<SceneGeometryManifest>(bytes, ManifestJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1145",
                DiagnosticSeverity.Warning,
                $"The scene-geometry manifest will not read, so every room keeps its own " +
                $"geometry: {ex.Message}",
                loose ? manifest : "<packs>"));
            return Empty;
        }
        catch (IOException ex)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1145",
                DiagnosticSeverity.Warning,
                $"The scene-geometry manifest will not open, so every room keeps its own " +
                $"geometry: {ex.Message}",
                loose ? manifest : "<packs>"));
            return Empty;
        }

        if (read is null)
        {
            return Empty;
        }

        Dictionary<string, SceneGeometryRoom> rooms = new(StringComparer.OrdinalIgnoreCase);

        foreach (SceneGeometryRoom room in read.Rooms)
        {
            rooms[room.Room] = room;
        }

        return new EnhancedScenes(rooms, loose ? directory : string.Empty, packs);
    }

    /// <summary>What is known about a room, or null when nothing is.</summary>
    /// <param name="room">The room's name.</param>
    /// <returns>Its entry in the manifest.</returns>
    public SceneGeometryRoom? Describe(string room) => _rooms.GetValueOrDefault(room);

    /// <summary>Reads the improved geometry for a room, if there is any and it fits.</summary>
    /// <param name="scene">The room as the game has it, whose surfaces the overlay indexes.</param>
    /// <param name="source">The bytes that room was parsed from, for the hash check.</param>
    /// <param name="diagnostics">Receives the reason whenever an overlay is refused.</param>
    /// <returns>The overlay, or null to draw the room exactly as it shipped.</returns>
    public SceneOverlay? Read(
        BspFile scene, ReadOnlySpan<byte> source, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        string name = Path.GetFileNameWithoutExtension(scene.Name);

        if (!_rooms.TryGetValue(name, out SceneGeometryRoom? room))
        {
            return null;
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(source));

        if (!string.Equals(hash, room.SourceSha256, StringComparison.Ordinal))
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1146",
                DiagnosticSeverity.Warning,
                $"The improved geometry for {name} was built against a different build of " +
                $"that room, so its surface numbering means something else. The room is " +
                $"drawn as it shipped. Re-run extract-scenes and compose-scenes.",
                name));

            return null;
        }

        List<SceneObjectGeometry> objects = [];
        int missing = 0;

        foreach (SceneGeometryObject placement in room.Objects)
        {
            ModFile? shape = Shape(placement.Shape, diagnostics);

            if (shape is null)
            {
                missing++;
                continue;
            }

            SceneObjectGeometry? piece = SceneObjectGlb.Place(
                shape, scene, placement.Index, placement.Surfaces);

            if (piece is null)
            {
                missing++;
                continue;
            }

            objects.Add(piece);
        }

        if (missing > 0)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1147",
                DiagnosticSeverity.Warning,
                $"{missing} of {room.Objects.Count} improved object(s) in {name} could not " +
                $"be read or did not fit the room, and are drawn as they shipped.",
                name));
        }

        return objects.Count == 0
            ? null
            : new SceneOverlay { Room = name, Objects = objects };
    }

    /// <summary>
    /// Reads one shape, once per session however many rooms and objects draw it.
    /// </summary>
    /// <param name="hash">Its content hash, as the manifest names it.</param>
    /// <param name="diagnostics">Receives a warning when it will not read.</param>
    /// <returns>The geometry, or null when there is none.</returns>
    private ModFile? Shape(string hash, DiagnosticBag? diagnostics)
    {
        if (_shapes.TryGetValue(hash, out ModFile? already))
        {
            return already;
        }

        ModFile? parsed = null;

        try
        {
            string file = _directory.Length > 0
                ? Path.Combine(_directory, hash + ".glb")
                : string.Empty;

            byte[]? bytes = file.Length > 0 && File.Exists(file)
                ? File.ReadAllBytes(file)
                : _packs?.Read(RebarnKind.SceneGeometry, hash);

            parsed = bytes is null || bytes.Length == 0
                ? null
                : GlbReader.TryParse(bytes, hash + ".glb", diagnostics);
        }
        catch (IOException ex)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1148",
                DiagnosticSeverity.Warning,
                $"The improved shape {hash} will not open, so whatever draws it is drawn " +
                $"as it shipped: {ex.Message}",
                hash));
        }

        _shapes[hash] = parsed;
        return parsed;
    }
}
