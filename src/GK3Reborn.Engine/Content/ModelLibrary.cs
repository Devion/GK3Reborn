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
    public int Count => _loose.Count;

    /// <summary>Whether there is nowhere at all to look.</summary>
    public bool IsEmpty =>
        _loose.Count == 0 && _packs is null && (Overrides?.CountOf(RebarnKind.Model) ?? 0) == 0;

    /// <summary>Indexes a directory of models, a set of packs, or both.</summary>
    /// <param name="directory">Where the loose ones are. May be empty or missing.</param>
    /// <param name="packs">Packs beside the executable, or null for none.</param>
    /// <param name="diagnostics">Receives a warning when the directory cannot be read.</param>
    /// <returns>The library, empty when neither has anything.</returns>
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
