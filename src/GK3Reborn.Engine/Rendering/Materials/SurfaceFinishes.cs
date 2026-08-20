// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GK3Reborn.Content.Manifests;

namespace GK3Reborn.Rendering.Materials;

/// <summary>How rough and how reflective each texture is, looked up by name.</summary>
/// <remarks>
/// <para>
/// A <see cref="MaterialLibrary"/> read once and turned into something a renderer can ask
/// six thousand times a frame. Only two of its channels are read: how rough a surface is,
/// and how much light it throws back when looked at straight on. Those are what decide
/// which pixels are worth tracing a reflection from and how tightly to gather one.
/// </para>
/// <para>
/// A texture nobody has measured is matte, which costs nothing and reflects nothing — the
/// renderer's behaviour before any of this existed.
/// </para>
/// </remarks>
public sealed class SurfaceFinishes
{
    /// <summary>What a surface nobody has measured is assumed to be.</summary>
    public static readonly SurfaceFinish Matte = new(1f, 0.5f);

    private readonly Dictionary<string, SurfaceFinish> _finishes;

    private SurfaceFinishes(Dictionary<string, SurfaceFinish> finishes) => _finishes = finishes;

    /// <summary>Nothing measured, so everything matte.</summary>
    public static SurfaceFinishes Empty { get; } =
        new(new Dictionary<string, SurfaceFinish>(StringComparer.OrdinalIgnoreCase));

    /// <summary>How many textures there is an answer for.</summary>
    public int Count => _finishes.Count;

    /// <summary>How many of those are smooth enough to reflect anything.</summary>
    public int Reflective
    {
        get
        {
            int count = 0;

            foreach (SurfaceFinish finish in _finishes.Values)
            {
                if (finish.Reflects)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Reads the library the workspace's material pass wrote.</summary>
    /// <param name="path">Path to <c>manifests/material-library.json</c>.</param>
    /// <returns>The finishes, or empty ones if the file is missing or unreadable.</returns>
    /// <remarks>
    /// Missing is not an error. The file comes from a pass over the texture corpus that a
    /// checkout need not have run.
    /// </remarks>
    public static SurfaceFinishes Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            return Empty;
        }

        try
        {
            MaterialLibrary? library = JsonSerializer.Deserialize<MaterialLibrary>(
                File.ReadAllText(path), ManifestJson.Options);

            return library is null ? Empty : From(library);
        }
        catch (JsonException)
        {
            return Empty;
        }
        catch (IOException)
        {
            return Empty;
        }
    }

    /// <summary>Builds a lookup over a library already in hand.</summary>
    /// <param name="library">The materials.</param>
    /// <returns>The finishes.</returns>
    public static SurfaceFinishes From(MaterialLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        var finishes = new Dictionary<string, SurfaceFinish>(
            library.Materials.Count, StringComparer.OrdinalIgnoreCase);

        foreach (MaterialDefinition material in library.Materials)
        {
            finishes[material.Id] = new SurfaceFinish(
                Math.Clamp(material.Roughness, 0f, 1f),
                Math.Clamp(material.SpecularReflectance, 0f, 1f));
        }

        return new SurfaceFinishes(finishes);
    }

    /// <summary>What a texture's surface is like.</summary>
    /// <param name="texture">The texture's name, without an extension.</param>
    /// <returns>Its finish, or <see cref="Matte"/> if it is not in the library.</returns>
    public SurfaceFinish Of(string? texture) =>
        texture is not null && _finishes.TryGetValue(texture, out SurfaceFinish finish)
            ? finish
            : Matte;
}

/// <summary>How a surface responds to light beyond its colour.</summary>
/// <param name="Roughness">
/// Zero for a mirror, one for chalk. Widens the cone a reflection is gathered over.
/// </param>
/// <param name="Specular">
/// How much light the surface throws back when looked at straight on, before the grazing
/// angle raises it. Half is the usual dielectric value and the assumption here.
/// </param>
public readonly record struct SurfaceFinish(float Roughness, float Specular)
{
    /// <summary>Whether this is smooth enough for a reflection to be worth tracing.</summary>
    /// <remarks>
    /// Past this the cone a reflection would be gathered over is wide enough that what
    /// comes back is the ambient term the surface already has, arrived at far more
    /// expensively.
    /// </remarks>
    public bool Reflects => Roughness <= Roughest;

    /// <summary>The roughest surface still worth tracing a reflection from.</summary>
    public const float Roughest = 0.6f;
}
