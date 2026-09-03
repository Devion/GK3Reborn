// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// Assets the remake adds, which the game never had.
/// </summary>
/// <remarks>
/// <para>
/// The restoration table edits assets the archives already hold. This is the other half:
/// files that are not in any barn and cannot be, because the thing they describe was cut
/// before release. The temple's second room needs a scene file, an action file and a walk
/// boundary, and no version of GK3 ever shipped one.
/// </para>
/// <para>
/// <b>It is consulted last, after every barn.</b> That is the whole safety rule and it is
/// stricter than the one the prop and room libraries follow: those are asked when an
/// archive has no answer, and so is this, but this is also the layer most able to do harm
/// if it ever answered for a name the game knows — a replaced <c>.SIF</c> is a replaced
/// room. Reaching it means every archive was asked first and none had the file.
/// </para>
/// <para>
/// They live beside the geometry they belong to, in a content workspace's
/// <c>enhanced/rooms</c> or in a ReBarn volume, because that is what they are: content,
/// built by <c>tools/rooms</c> and packed like everything else. Nothing of a cut room is
/// carried in the engine — the engine carries the means to read one.
/// </para>
/// </remarks>
public sealed class AddedAssets
{
    private static readonly string[] Kinds =
        [".SIF", ".NVC", ".BMP", ".TXT", ".SHP", ".YAK", ".SCN", ".ANM", ".ACT"];

    private readonly Dictionary<string, string> _loose;
    private readonly RebarnContent? _packs;

    private AddedAssets(Dictionary<string, string> loose, RebarnContent? packs)
    {
        _loose = loose;
        _packs = packs;
    }

    /// <summary>A set with nothing in it, which adds nothing.</summary>
    public static AddedAssets Empty { get; } = new([], null);

    /// <summary>Files a player has dropped into <c>overrides/</c>, which outrank the rest.</summary>
    public ContentOverrides? Overrides { get; set; }

    /// <summary>How many loose assets are added.</summary>
    public int Count => _loose.Count;

    /// <summary>Whether there is nowhere at all to look.</summary>
    public bool IsEmpty => _loose.Count == 0 && _packs is null;

    /// <summary>Every name added, in a stable order.</summary>
    public IReadOnlyList<string> Names =>
        [.. _loose.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Indexes a directory of added assets, a set of packs, or both.</summary>
    /// <param name="directory">Where the loose ones are. May be empty or missing.</param>
    /// <param name="packs">Packs beside the executable, or null for none.</param>
    /// <param name="diagnostics">Receives a warning when the directory cannot be read.</param>
    /// <returns>The set, empty when there is nowhere to look.</returns>
    /// <remarks>
    /// Only the kinds a room is made of are taken. The same directory holds the room's
    /// geometry, which is glTF and is the room library's business; indexing it here would
    /// put a name in the archive listing that nothing can parse as a 1999 asset.
    /// </remarks>
    public static AddedAssets Open(
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
                    if (Kinds.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    {
                        loose.TryAdd(Path.GetFileName(file), file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics?.Add(new Diagnostic(
                    "GK3R1200",
                    DiagnosticSeverity.Warning,
                    $"The added-assets directory cannot be read, so nothing is added: {ex.Message}",
                    directory,
                    null,
                    "a readable directory",
                    ex.GetType().Name,
                    "Check the permissions on it, or take it away."));
            }
        }

        return loose.Count == 0 && packs is null ? Empty : new AddedAssets(loose, packs);
    }

    /// <summary>Whether an asset of this name is added.</summary>
    /// <param name="name">Asset name, with extension.</param>
    /// <returns>True when it is.</returns>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Overrides?.Has(RebarnKind.Raw, name) == true
            || _loose.ContainsKey(name)
            || _packs?.Has(RebarnKind.Raw, name) == true;
    }

    /// <summary>Reads an added asset.</summary>
    /// <param name="name">Asset name, with extension.</param>
    /// <returns>Its bytes, or null when there is no such asset.</returns>
    public byte[]? Read(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Overrides?.Read(RebarnKind.Raw, name) is { } replaced)
        {
            return replaced;
        }

        try
        {
            if (_loose.TryGetValue(name, out string? file) && File.Exists(file))
            {
                return File.ReadAllBytes(file);
            }
        }
        catch (IOException)
        {
            return null;
        }

        return _packs?.Read(RebarnKind.Raw, name);
    }

    /// <summary>A line for the startup log.</summary>
    /// <returns>What is added, or null when nothing is.</returns>
    public string? Describe() =>
        IsEmpty ? null
        : _loose.Count > 0 ? $"{_loose.Count} file(s) the game never had"
        : "the packs";
}
