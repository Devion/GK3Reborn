// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GK3Reborn.Content.Authoring;
using GK3Reborn.Content;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Rendering.Materials;

/// <summary>How rough and how reflective each texture is, looked up by name.</summary>
public sealed class SurfaceFinishes
{
    /// <summary>What a surface nobody has measured is assumed to be.</summary>
    public static readonly SurfaceFinish Matte =
        new(1f, 0.5f, 0f, 1f, 0f, false, false, false, 0, 0f, 0f, false, 0f);

    /// <summary>The deepest relief a height field may claim, in world units.</summary>
    public const float MaximumRelief = 8f;

    /// <summary>The most shells a coat may ask for.</summary>
    public const int MaximumShells = 24;

    /// <summary>How far fur may stand off the surface it grows on, in world units.</summary>
    public const float MaximumFur = 4f;

    private readonly Dictionary<string, SurfaceFinish> _finishes;

    private SurfaceFinishes(Dictionary<string, SurfaceFinish> finishes) => _finishes = finishes;

    /// <summary>Nothing measured, so everything matte.</summary>
    public static SurfaceFinishes Empty { get; } =
        new(new Dictionary<string, SurfaceFinish>(StringComparer.OrdinalIgnoreCase));

    /// <summary>How many textures there is an answer for.</summary>
    public int Count => _finishes.Count;

    /// <summary>How many of those are metals.</summary>
    public int Metallic
    {
        get
        {
            int count = 0;

            foreach (SurfaceFinish finish in _finishes.Values)
            {
                if (finish.Metallic > 0.5f)
                {
                    count++;
                }
            }

            return count;
        }
    }

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

    /// <summary>How many of those are mirrors.</summary>
    public int Mirrors
    {
        get
        {
            int count = 0;

            foreach (SurfaceFinish finish in _finishes.Values)
            {
                if (finish.Mirror)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many of those are lit CRT screens.</summary>
    public int Screens
    {
        get
        {
            int count = 0;

            foreach (SurfaceFinish finish in _finishes.Values)
            {
                if (finish.Lit)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many of the finishes a person corrected by hand.</summary>
    public int Corrected { get; private set; }

    /// <summary>Reads the library the workspace's material pass wrote, and its corrections.</summary>
    /// <param name="path">Path to <c>manifests/material-library.json</c>.</param>
    /// <param name="packs">The shipped volumes, consulted where the loose file is absent.</param>
    /// <param name="diagnostics">Receives warnings about corrections that no longer apply.</param>
    /// <returns>The finishes, or empty ones if the file is missing or unreadable.</returns>
    public static SurfaceFinishes Load(
        string path, RebarnContent? packs = null, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            byte[] library = Read(path, packs, LibraryKey);

            if (library.Length == 0)
            {
                return Empty;
            }

            MaterialLibrary? read = JsonSerializer.Deserialize<MaterialLibrary>(
                library, ManifestJson.Options);

            if (read is null)
            {
                return Empty;
            }

            var bag = diagnostics ?? new DiagnosticBag();
            int before = Authored(read);

            read = read.WithEdits(Corrections(path, packs), bag);

            SurfaceFinishes finishes = From(read);
            finishes.Corrected = Authored(read) - before;

            return finishes;
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

    /// <summary>What the library is called inside a pack.</summary>
    private const string LibraryKey = "material-library.json";

    /// <summary>And what the corrections beside it are called there.</summary>
    private const string EditsKey = "material-library.materials.edits.json";

    /// <summary>Reads one of the two files, from the workspace if it is there and the pack if not.</summary>
    private static byte[] Read(string path, RebarnContent? packs, string key)
    {
        string loose = key == LibraryKey ? path : Beside(path);

        if (File.Exists(loose))
        {
            return File.ReadAllBytes(loose);
        }

        return packs?.Read(RebarnKind.Manifest, key) ?? [];
    }

    /// <summary>How many of a library's materials a person has had a hand in.</summary>
    private static int Authored(MaterialLibrary library) =>
        library.Materials.Count(m => m.Provenance != AuthoringProvenance.Derived);

    /// <summary>The corrections filed beside a library, if anybody has made any.</summary>
    private static MaterialEdits? Corrections(string path, RebarnContent? packs)
    {
        try
        {
            byte[] bytes = Read(path, packs, EditsKey);

            return bytes.Length == 0
                ? null
                : JsonSerializer.Deserialize<MaterialEdits>(bytes, ManifestJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Where the corrections sit beside a library on disk.</summary>
    private static string Beside(string path) => Path.Combine(
        Path.GetDirectoryName(path) ?? string.Empty,
        Path.GetFileNameWithoutExtension(path) + ".materials.edits.json");

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
                Math.Clamp(material.SpecularReflectance, 0f, 1f),
                Math.Clamp(material.Metallic, 0f, 1f),
                Math.Clamp(material.NormalStrength, 0f, 4f),
                Math.Clamp(material.HeightDepth, 0f, MaximumRelief),
                material.Emissive != System.Numerics.Vector3.Zero,
                material.Provenance != AuthoringProvenance.Derived,
                material.Displaced,
                Math.Clamp(material.Shells, 0, MaximumShells),
                Math.Clamp(material.ShellDepth, 0f, MaximumFur),
                Math.Clamp(material.ShellDensity, 1f, 4096f),
                material.Mirror,
                Math.Clamp(material.MirrorInset, 0f, 0.45f))
            {
                Emission = System.Numerics.Vector3.Clamp(
                    material.Emissive,
                    System.Numerics.Vector3.Zero,
                    new System.Numerics.Vector3(8f)),
                Screen = material.Screen,
                Glass = System.Numerics.Vector4.Clamp(
                    material.ScreenGlass,
                    System.Numerics.Vector4.Zero,
                    System.Numerics.Vector4.One),
            };
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
/// <param name="Metallic">
/// Zero for a dielectric, one for a conductor. A metal has no diffuse term at all and
/// tints its own reflection with its base colour, which is why this is not a slider between
/// two shading models but a switch between them — and why a classifier that guesses it
/// wrong on a stone wall is unmistakable rather than subtle.
/// </param>
/// <param name="NormalStrength">
/// How much of the normal map to believe. One is as generated; everything in a generated
/// map is invented, so this is a per-material decision rather than a constant.
/// </param>
/// <param name="HeightDepth">
/// How deep the height map goes, in world units from its floor to its ceiling. Clamped to
/// <see cref="SurfaceFinishes.MaximumRelief"/>, which is well past anything a generated
/// field has any business claiming about a surface.
/// </param>
/// <param name="Emits">
/// Whether the surface is its own light source — a lit bulb, a lamp shade with a bulb
/// inside it, the painted view through a window.
/// </param>
/// <param name="Authored">
/// Whether a person had a hand in these numbers, rather than a classifier alone.
/// </param>
/// <param name="Displaced">
/// Whether the height map is cut into the geometry as well as marched by the shader. Only a
/// paved, tiled or boarded surface wants this; see
/// <see cref="MaterialDefinition.Displaced"/>.
/// </param>
/// <param name="Shells">
/// How many fur shells stand over the surface, and zero for everything that is not an
/// animal. See <see cref="MaterialDefinition.Shells"/>.
/// </param>
/// <param name="ShellDepth">How far the outermost shell stands off, in world units.</param>
/// <param name="ShellDensity">How many strands stand across one turn of the texture.</param>
/// <param name="Mirror">
/// Whether this surface's reflection is rendered rather than painted on it. See
/// <see cref="MaterialDefinition.Mirror"/>: it is set by hand, it is not a synonym for
/// smooth, and it is what takes a surface away from the screen-space pass — which cannot
/// answer a mirror facing the player, because what such a mirror shows is behind the camera
/// and therefore not in the frame it marches.
/// </param>
/// <param name="MirrorInset">
/// How much of each edge of the texture is frame rather than glass, as a share of the edge.
/// GK3's mirrors carry their ornate frames in the texture, so a reflection that covers the
/// whole card paints over the frame. See <see cref="MaterialDefinition.MirrorInset"/>.
/// </param>
public readonly record struct SurfaceFinish(
    float Roughness,
    float Specular,
    float Metallic = 0f,
    float NormalStrength = 1f,
    float HeightDepth = 0f,
    bool Emits = false,
    bool Authored = false,
    bool Displaced = false,
    int Shells = 0,
    float ShellDepth = 0f,
    float ShellDensity = 0f,
    bool Mirror = false,
    float MirrorInset = 0f)
{
    /// <summary>
    /// What colour this surface gives off, and how much of it.
    /// </summary>
    public System.Numerics.Vector3 Emission { get; init; }

    /// <summary>Whether this texture is a lit CRT screen. See <see cref="MaterialDefinition.Screen"/>.</summary>
    public bool Screen { get; init; }

    /// <summary>
    /// Where the lit glass is inside it: u and v of one corner, then the other.
    /// </summary>
    public System.Numerics.Vector4 Glass { get; init; }

    /// <summary>Whether a phosphor raster is drawn over this surface.</summary>
    public bool Lit => Screen && Glass.Z > Glass.X && Glass.W > Glass.Y;

    /// <summary>Whether anything grows on this surface.</summary>
    public bool Furred => Shells > 0 && ShellDepth > 0f;

    /// <summary>Whether this surface should stop a ray.</summary>
    public bool Occludes => !Emits;

    /// <summary>Whether this is smooth enough for a reflection to be worth tracing.</summary>
    public bool Reflects => Roughness <= Roughest && !Mirror;

    /// <summary>The roughest surface still worth tracing a reflection from.</summary>
    public const float Roughest = 0.6f;
}
