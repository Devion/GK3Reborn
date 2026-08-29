// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using GK3Reborn.Content;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Gathers a room's improved objects back into the one file the game reads.
/// </summary>
/// <remarks>
/// <para>
/// The directory of per-object files is the shape the work is done in; it is not the
/// shape the work ships in. A pack key carries no directory, and the corpus repeats
/// itself — a location has a geometry file per timeblock, holding the same furniture at
/// the same coordinates under a different surface numbering — so the composed form is a
/// flat pool of glTF files addressed by the hash of their own geometry, plus a manifest
/// saying which rooms draw which of them and what each room calls its surfaces.
/// </para>
/// <para>
/// Measured over the corpus that is 2,054 shapes for 2,721 improved objects: a fifth of
/// the set was being shipped more than once. Hashing what is actually in the file, rather
/// than trusting two rooms to agree, also keeps the sharing honest by itself — an object
/// somebody edits in one room stops matching and quietly gets a shape of its own.
/// </para>
/// <para>
/// <b>Everything this refuses, it refuses loudly.</b> A composed room whose surface
/// indices belong to a different build of the geometry is not a slightly wrong room: it
/// is every lightmap on the wrong surface. So the source is hashed, the triangles are
/// checked against the surfaces they claim, and an object that has wandered outside the
/// box the original occupied is dropped with its numbers reported rather than shipped.
/// </para>
/// </remarks>
public sealed class SceneComposeStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public SceneComposeStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Where the composed rooms are written, under the workspace.</summary>
    public const string OutputDirectory = "enhanced/scene-geometry";

    /// <summary>The manifest written beside them, and packed with them.</summary>
    public const string ManifestName = "scene-geometry.json";

    /// <summary>How much of a shape's hash names its file.</summary>
    /// <remarks>
    /// Sixteen hex digits is sixty-four bits, which over a pool of a few thousand shapes
    /// is a collision every few hundred million corpora. The rest of the digits would buy
    /// nothing but a wider directory listing.
    /// </remarks>
    public const int ShapeNameLength = 16;

    /// <summary>
    /// How far outside the original object's box a replacement may reach, as a fraction of
    /// that box's longest edge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refining a curve does reach outside the authored hull, and is supposed to: an
    /// interpolating scheme leaves every authored vertex alone and bows the surface
    /// between them out to the curve their normals describe, which on the coarsest thing
    /// in the corpus that is meant to read as round — an eight-sided lantern, turning 45°
    /// at each of its own sides — is about eight per cent of that curve's radius. A
    /// bevel then only ever cuts inward. A quarter is comfortably above the one and
    /// nowhere near the other.
    /// </para>
    /// <para>
    /// What this is really for is the mistake that produces no error anywhere else: a
    /// modelling tool exporting with a different up axis or unit scale. That is not off
    /// by a fraction, it is off by a whole multiple, and it comes back as a room-sized
    /// chair standing on its side.
    /// </para>
    /// </remarks>
    public const float Drift = 0.25f;

    /// <summary>Composes every room that has anything to compose.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="workspaceDirectory">Content workspace root.</param>
    /// <param name="only">Rooms to compose, or empty for all of them.</param>
    /// <param name="dryRun">Report the plan and write nothing.</param>
    /// <param name="diagnostics">Receives what was refused.</param>
    /// <returns>True when at least one room composed.</returns>
    public bool Run(
        string sourceDirectory,
        string workspaceDirectory,
        IReadOnlyCollection<string> only,
        bool dryRun,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(workspaceDirectory);
        ArgumentNullException.ThrowIfNull(only);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string manifestPath = Path.Combine(workspaceDirectory, "manifests", "scene-objects.json");

        if (!File.Exists(manifestPath))
        {
            _log($"no {manifestPath}: run extract-scenes first");
            return false;
        }

        SceneObjectManifest? extracted = JsonSerializer.Deserialize<SceneObjectManifest>(
            File.ReadAllText(manifestPath), ManifestJson.Options);

        if (extracted is null)
        {
            _log($"{manifestPath} will not read");
            return false;
        }

        using GameArchives archives = GameArchives.Open(sourceDirectory);

        string outputRoot = Path.Combine(
            workspaceDirectory, OutputDirectory.Replace('/', Path.DirectorySeparatorChar));

        List<SceneGeometryRoom> composed = [];
        HashSet<string> shapes = new(StringComparer.Ordinal);
        int refused = 0;

        foreach (SceneObjectRoom room in extracted.Rooms)
        {
            if (only.Count > 0 && !only.Contains(room.Room, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string directory = Path.Combine(
                workspaceDirectory, room.Directory.Replace('/', Path.DirectorySeparatorChar));

            // Only the objects a modelling pass actually wrote. Everything else is drawn
            // from the original geometry, which is the whole of what makes this optional.
            List<SceneObjectRole> improved =
                [.. room.Objects.Where(o => File.Exists(Path.Combine(directory, o.File)))];

            if (improved.Count == 0)
            {
                continue;
            }

            if (archives.Read(room.Room + ".BSP") is not { } bytes)
            {
                continue;
            }

            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

            if (!string.Equals(hash, room.SourceSha256, StringComparison.Ordinal))
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R1141",
                    DiagnosticSeverity.Error,
                    $"{room.Room} was extracted from different geometry than is in the " +
                    "archives now, so its surface indices mean something else. Re-run " +
                    "extract-scenes before composing.",
                    room.Room + ".BSP"));
                refused++;
                continue;
            }

            BspFile scene;

            try
            {
                scene = BspFile.Parse(bytes, room.Room + ".BSP");
            }
            catch (FormatParseException ex)
            {
                diagnostics.Add(ex.Diagnostic);
                continue;
            }

            List<SceneObjectGeometry> objects = [];
            int before = 0;

            foreach (SceneObjectRole role in improved)
            {
                string file = Path.Combine(directory, role.File);
                SceneOverlay overlay;

                try
                {
                    overlay = SceneObjectGlb.Read(
                        File.ReadAllBytes(file), scene, $"{room.Room}/{role.File}", diagnostics);
                }
                catch (IOException ex)
                {
                    diagnostics.Add(new Diagnostic(
                        "GK3R1142", DiagnosticSeverity.Warning,
                        $"{file} will not open: {ex.Message}", file));
                    refused++;
                    continue;
                }

                foreach (SceneObjectGeometry piece in overlay.Objects)
                {
                    if (piece.ObjectIndex != role.Index)
                    {
                        diagnostics.Add(new Diagnostic(
                            "GK3R1143", DiagnosticSeverity.Warning,
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"{room.Room}/{role.File} carries surfaces of object " +
                                $"{piece.ObjectIndex} ({piece.Name}) as well as {role.Index} " +
                                $"({role.Name}). Each file must hold one object; the extra " +
                                $"was dropped."),
                            file));
                        refused++;
                        continue;
                    }

                    if (!Stayed(scene, piece, out float drifted))
                    {
                        diagnostics.Add(new Diagnostic(
                            "GK3R1144", DiagnosticSeverity.Warning,
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"{room.Room}/{role.File} reaches {drifted:P0} outside the box " +
                                $"{role.Name} occupied, past the {Drift:P0} a refinement can. " +
                                $"Check the exporter's up axis and unit scale."),
                            file));
                        refused++;
                        continue;
                    }

                    before += role.TriangleCount;
                    objects.Add(piece);
                }
            }

            if (objects.Count == 0)
            {
                continue;
            }

            int after = objects.Sum(o => o.Triangles.Count);
            List<SceneGeometryObject> placed = [];

            foreach (SceneObjectGeometry piece in objects)
            {
                // Addressed by what is in it, so the same chair in nine variants of one
                // dining room is one file. See SceneObjectGlb.ShapeOf for why the hash is
                // over a canonical form rather than over the encoded bytes.
                string shape = SceneObjectGlb.ShapeOf(piece)[..ShapeNameLength];

                IReadOnlyList<int> slots = [.. piece.Surfaces.Order()];

                if (shapes.Add(shape))
                {
                    byte[] glb = SceneObjectGlb.EncodeShape(piece.Name, piece, out slots);

                    if (!dryRun)
                    {
                        Directory.CreateDirectory(outputRoot);
                        File.WriteAllBytes(Path.Combine(outputRoot, shape + ".glb"), glb);
                    }
                }

                placed.Add(new SceneGeometryObject
                {
                    Index = piece.ObjectIndex,
                    Name = piece.Name,
                    Shape = shape,
                    Surfaces = slots,
                    TriangleCount = piece.Triangles.Count,
                });
            }

            _log(string.Create(
                CultureInfo.InvariantCulture,
                $"{room.Room,-12} {objects.Count,4} objects  {before,7} -> {after,8} triangles  " +
                $"({(before > 0 ? (double)after / before : 0):F1}x)"));

            composed.Add(new SceneGeometryRoom
            {
                Room = room.Room,
                SourceSha256 = hash,
                OriginalTriangles = before,
                Objects = placed,
            });
        }

        var manifest = new SceneGeometryManifest
        {
            SchemaVersion = 1,
            Stage = "C5.scene-geometry",
            ShapeCount = shapes.Count,
            Rooms = composed,
        };

        if (!dryRun && composed.Count > 0)
        {
            Directory.CreateDirectory(outputRoot);
            File.WriteAllText(
                Path.Combine(outputRoot, ManifestName),
                JsonSerializer.Serialize(manifest, ManifestJson.Options));
        }

        _log(string.Empty);
        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"{composed.Count} room(s) composed, {composed.Sum(r => r.ObjectCount)} objects, " +
            $"{composed.Sum(r => r.TriangleCount)} triangles, {refused} refused"));
        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"{shapes.Count} distinct shape(s) shipped for {composed.Sum(r => r.ObjectCount)} " +
            $"placement(s)"));

        return composed.Count > 0;
    }

    /// <summary>Whether a replacement stayed where the object it replaces was.</summary>
    private static bool Stayed(BspFile scene, SceneObjectGeometry piece, out float drifted)
    {
        Vector3 least = new(float.PositiveInfinity);
        Vector3 most = new(float.NegativeInfinity);

        foreach ((int _, ushort a, ushort b, ushort c, int _) in
                 SceneObjectNormals.Faces(scene, piece.ObjectIndex))
        {
            foreach (ushort at in (ReadOnlySpan<ushort>)[a, b, c])
            {
                least = Vector3.Min(least, scene.Vertices[at]);
                most = Vector3.Max(most, scene.Vertices[at]);
            }
        }

        drifted = 0f;

        if (least.X > most.X)
        {
            return true;
        }

        Vector3 extent = most - least;
        float scale = MathF.Max(1e-3f, MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)));

        foreach (SceneTriangle triangle in piece.Triangles)
        {
            foreach (SceneVertex corner in
                     (ReadOnlySpan<SceneVertex>)[triangle.A, triangle.B, triangle.C])
            {
                Vector3 outside = Vector3.Max(
                    Vector3.Zero,
                    Vector3.Max(least - corner.Position, corner.Position - most));

                drifted = MathF.Max(
                    drifted,
                    MathF.Max(outside.X, MathF.Max(outside.Y, outside.Z)) / scale);
            }
        }

        return drifted <= Drift;
    }
}
