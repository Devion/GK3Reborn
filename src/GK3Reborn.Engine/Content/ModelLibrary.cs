// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// Prop geometry that did not ship with the game, as glTF binary.
/// </summary>
/// <remarks>
/// <para>
/// A prop is a <c>.MOD</c> read out of the archives by name. That is the whole of it: there
/// is no way for a room to place a thing the 1999 archives do not contain, which is why the
/// objects in <c>docs/cut-content.md</c> that have rules and recordings but were never
/// modelled could not be put back. This is the way — a model named in a scene file is
/// looked for here first, and comes from an override, a content workspace or a ReBarn pack.
/// </para>
/// <para>
/// <b>It answers only for names the archives do not have.</b> That boundary is the whole
/// safety argument and it is deliberately narrow. A library that could stand in front of
/// the game's own props would replace them wholesale the moment a workspace happened to
/// contain a mesh of the same name — every chair and lamp in the game quietly swapped for a
/// generated one, with nothing on screen to say so. A model the archives have never heard
/// of cannot do that: the only reason a scene names one is that a restoration put it there,
/// and every restoration is already behind its own switch. Replacing the meshes that
/// <em>did</em> ship is a separate feature and wants a separate setting.
/// </para>
/// <para>
/// Every layer is optional and each falls back on its own. No directory, no pack, no entry,
/// a file that will not open or one that will not parse: each of those leaves the prop
/// unplaced and the room otherwise exactly as it was, with a diagnostic saying which.
/// </para>
/// </remarks>
public sealed class ModelLibrary
{
    private readonly Dictionary<string, ModFile?> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _loose;
    private readonly RebarnContent? _packs;

    private ModelLibrary(Dictionary<string, string> loose, RebarnContent? packs)
    {
        _loose = loose;
        _packs = packs;
    }

    /// <summary>A library with nothing in it, which places nothing.</summary>
    public static ModelLibrary Empty { get; } = new([], null);

    /// <summary>Files a player has dropped into <c>overrides/</c>, which outrank the rest.</summary>
    public ContentOverrides? Overrides { get; set; }

    /// <summary>How many loose models are available.</summary>
    /// <remarks>
    /// Loose only, and deliberately. The packs store a prop and a grown tree under the same
    /// kind — <see cref="RebarnKind.Model"/> is "geometry, as glTF binary" and the tree
    /// library reads it too — so a count taken from a pack would report the forest as
    /// props. What is in a pack is answered for by name, which is the only question that
    /// matters here.
    /// </remarks>
    public int Count => _loose.Count;

    /// <summary>Whether there is nowhere at all to look.</summary>
    public bool IsEmpty =>
        _loose.Count == 0 && _packs is null && (Overrides?.CountOf(RebarnKind.Model) ?? 0) == 0;

    /// <summary>Indexes a directory of models, a set of packs, or both.</summary>
    /// <param name="directory">Where the loose ones are. May be empty or missing.</param>
    /// <param name="packs">Packs beside the executable, or null for none.</param>
    /// <param name="diagnostics">Receives a warning when the directory cannot be read.</param>
    /// <returns>The library, empty when neither has anything.</returns>
    /// <remarks>
    /// No manifest. Unlike the improved room geometry, which indexes a room's surfaces and
    /// has to be refused when it was built against a different build of that room, a prop
    /// is a whole object with nothing to line up against — so the file being there is all
    /// there is to know, and a directory listing is the index.
    /// </remarks>
    public static ModelLibrary Open(
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

                    if (!extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // First wins, so the ordering above is what makes two files claiming
                    // one name resolve the same way on every machine.
                    loose.TryAdd(Path.GetFileNameWithoutExtension(file), file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics?.Add(new Diagnostic(
                    "GK3R1194",
                    DiagnosticSeverity.Warning,
                    $"The model directory cannot be read, so nothing is placed from it: {ex.Message}",
                    directory,
                    null,
                    "a readable directory",
                    ex.GetType().Name,
                    "Check the permissions on it, or take it away."));
            }
        }

        // Always an instance when there is anywhere at all to look, because overrides are
        // attached afterwards and Empty is shared: setting them on it would leak one
        // player's overrides into every other library in the process.
        return loose.Count == 0 && packs is null ? Empty : new ModelLibrary(loose, packs);
    }

    /// <summary>Whether a model of this name is available.</summary>
    /// <param name="name">The model's name, without extension.</param>
    /// <returns>True when one of the layers has it.</returns>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Overrides?.Has(RebarnKind.Model, name) == true
            || _loose.ContainsKey(name)
            || _packs?.Has(RebarnKind.Model, name) == true;
    }

    /// <summary>Reads a model, if one of the layers has it and it parses.</summary>
    /// <param name="name">The model's name, without extension.</param>
    /// <param name="diagnostics">Receives the reason whenever one is refused.</param>
    /// <returns>The mesh, or null to place nothing.</returns>
    /// <remarks>
    /// Parsed once and kept. A prop is placed every time its room is built, and a room is
    /// built every time the player walks into it.
    /// </remarks>
    public ModFile? Read(string name, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_parsed.TryGetValue(name, out ModFile? already))
        {
            return already;
        }

        ModFile? parsed = null;

        try
        {
            byte[]? bytes = Bytes(name, diagnostics);

            parsed = bytes is null || bytes.Length == 0
                ? null
                : GlbReader.TryParse(bytes, name + ".glb", diagnostics);
        }
        catch (IOException ex)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1195",
                DiagnosticSeverity.Warning,
                $"The model {name} will not open, so whatever places it places nothing: " +
                $"{ex.Message}",
                name));
        }

        _parsed[name] = parsed;

        return parsed;
    }

    /// <summary>A line for the startup log.</summary>
    /// <returns>What is available, or null when nothing is.</returns>
    public string? Describe()
    {
        int overridden = Overrides?.CountOf(RebarnKind.Model) ?? 0;

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

        return string.Join(", ", parts);
    }

    private byte[]? Bytes(string name, DiagnosticBag? diagnostics)
    {
        if (Overrides?.Read(RebarnKind.Model, name, diagnostics) is { } replaced)
        {
            return replaced;
        }

        if (_loose.TryGetValue(name, out string? file) && File.Exists(file))
        {
            return File.ReadAllBytes(file);
        }

        return _packs?.Read(RebarnKind.Model, name);
    }
}
