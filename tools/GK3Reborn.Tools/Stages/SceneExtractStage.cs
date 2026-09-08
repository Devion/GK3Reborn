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
using GK3Reborn.Rendering.Materials;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Cuts every room into one glTF file per object, so the geometry can be improved.
/// </summary>
public sealed class SceneExtractStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public SceneExtractStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Where a room's objects are written, under the workspace.</summary>
    public const string OutputDirectory = "enhanced/scenes";

    /// <summary>Where the extracted originals sit inside a room's directory.</summary>
    public const string SourceSubdirectory = "original";

    /// <summary>Extracts every room.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="workspaceDirectory">Content workspace root.</param>
    /// <param name="only">Rooms to extract, or empty for all of them.</param>
    /// <param name="crease">Angle beyond which a shared edge shades as a crease.</param>
    /// <param name="dryRun">Report the plan and write nothing.</param>
    /// <param name="diagnostics">Receives what went wrong.</param>
    /// <returns>The manifest, which is also written to the workspace.</returns>
    public SceneObjectManifest Run(
        string sourceDirectory,
        string workspaceDirectory,
        IReadOnlyCollection<string> only,
        float crease,
        bool dryRun,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(workspaceDirectory);
        ArgumentNullException.ThrowIfNull(only);
        ArgumentNullException.ThrowIfNull(diagnostics);

        using GameArchives archives = GameArchives.Open(sourceDirectory);

        MaterialClasses classes = MaterialClasses.Load(
            Path.Combine(workspaceDirectory, "manifests", "material-library.json"));

        _log($"materials: {classes.Count} textures carry a class");

        SceneRoles roles = SceneRoles.Read(archives, _log);

        List<SceneObjectRoom> rooms = [];
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        int written = 0;

        foreach (string room in Rooms(archives, only))
        {
            if (archives.Read(room + ".BSP") is not { } bytes)
            {
                continue;
            }

            BspFile scene;

            try
            {
                scene = BspFile.Parse(bytes, room + ".BSP");
            }
            catch (FormatParseException ex)
            {
                diagnostics.Add(ex.Diagnostic);
                continue;
            }

            string directory = $"{OutputDirectory}/{room}";
            string full = Path.Combine(
                workspaceDirectory, OutputDirectory.Replace('/', Path.DirectorySeparatorChar), room);

            List<SceneObjectRole> objects = [];
            HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < scene.ObjectNames.Count; index++)
            {
                ObjectFacts facts = ObjectFacts.Of(scene, index);

                if (facts.TriangleCount == 0)
                {
                    continue;
                }

                string file = FileNameFor(scene.ObjectNames[index], index, used);

                (SceneObjectDisposition disposition, string reason) =
                    Classifier.Decide(scene, index, facts, classes, roles.For(room));

                objects.Add(new SceneObjectRole
                {
                    Index = index,
                    Name = scene.ObjectNames[index],
                    File = file,
                    Surfaces = facts.Surfaces,
                    Textures = facts.Textures,
                    Materials = [.. facts.Textures.Select(t => classes.Of(t) ?? "?")],
                    TriangleCount = facts.TriangleCount,
                    PlaneCount = facts.PlaneCount,
                    Size = facts.Size,
                    Flags = facts.Flags,
                    Roles = roles.For(room).Of(scene.ObjectNames[index]),
                    Disposition = disposition,
                    Reason = reason,
                });

                counts[disposition.ToString()] = counts.GetValueOrDefault(disposition.ToString()) + 1;

                if (dryRun)
                {
                    continue;
                }

                if (SceneObjectGlb.Encode(scene, index, crease: crease) is { } glb)
                {
                    string into = Path.Combine(full, SourceSubdirectory);
                    Directory.CreateDirectory(into);
                    File.WriteAllBytes(Path.Combine(into, file), glb);
                    written++;
                }
            }

            rooms.Add(new SceneObjectRoom
            {
                Room = room,
                Directory = directory,
                SourceSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                SurfaceCount = scene.Surfaces.Count,
                TriangleCount = scene.TriangleCount,
                Objects = objects,
            });

            _log(string.Create(
                CultureInfo.InvariantCulture,
                $"{room,-12} {objects.Count,4} objects  {scene.Surfaces.Count,5} surfaces  " +
                $"{scene.TriangleCount,7} triangles  " +
                $"{objects.Count(o => Worth(o.Disposition)),4} worth improving"));
        }

        var manifest = new SceneObjectManifest
        {
            SchemaVersion = 1,
            Stage = "C5.scene-objects",
            SourceRoot = sourceDirectory.Replace('\\', '/'),
            Crease = crease,
            DispositionCounts = counts.OrderByDescending(kv => kv.Value).ToDictionary(StringComparer.Ordinal),
            Rooms = rooms,
        };

        if (!dryRun)
        {
            string path = Path.Combine(workspaceDirectory, "manifests", "scene-objects.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, ManifestJson.Options));
            _log($"wrote {path}");
        }

        _log(string.Empty);
        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"{rooms.Count} rooms, {rooms.Sum(r => r.Objects.Count)} objects, {written} files written"));

        foreach ((string name, int count) in manifest.DispositionCounts)
        {
            _log(string.Create(CultureInfo.InvariantCulture, $"  {name,-14} {count,5}"));
        }

        return manifest;
    }

    /// <summary>Whether a disposition is one the modelling pass does anything for.</summary>
    /// <param name="disposition">The disposition.</param>
    /// <returns>True when the object is a candidate for improvement.</returns>
    public static bool Worth(SceneObjectDisposition disposition) =>
        disposition is SceneObjectDisposition.Ornament
            or SceneObjectDisposition.Furniture
            or SceneObjectDisposition.Vehicle
            or SceneObjectDisposition.Rock
            or SceneObjectDisposition.Architecture;

    /// <summary>Every room the archives hold, filtered by what was asked for.</summary>
    private static IEnumerable<string> Rooms(GameArchives archives, IReadOnlyCollection<string> only) =>
        archives.Names(".BSP")
            .Select(n => Path.GetFileNameWithoutExtension(n) ?? string.Empty)
            .Where(n => n.Length > 0)
            .Where(n => only.Count == 0 || only.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    /// <summary>A file name for an object, unique within its room.</summary>
    private static string FileNameFor(string name, int index, HashSet<string> used)
    {
        Span<char> scratch = stackalloc char[name.Length];

        for (int i = 0; i < name.Length; i++)
        {
            char c = char.ToLowerInvariant(name[i]);
            scratch[i] = char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_';
        }

        string bare = scratch.TrimEnd('_').ToString();

        if (bare.Length == 0)
        {
            bare = "object";
        }

        string candidate = bare;

        if (!used.Add(candidate))
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{bare}~{index}");
            used.Add(candidate);
        }

        return candidate + ".glb";
    }
}

/// <summary>What one of a room's objects is made of, measured rather than assumed.</summary>
internal sealed record ObjectFacts
{
    public required IReadOnlyList<int> Surfaces { get; init; }

    public required IReadOnlyList<string> Textures { get; init; }

    public required int TriangleCount { get; init; }

    public required int PlaneCount { get; init; }

    public required float Size { get; init; }

    public required Vector3 Extent { get; init; }

    public required uint Flags { get; init; }

    /// <summary>Whether every one of its surfaces is a translucent shadow decal.</summary>
    public required bool AllShadow { get; init; }

    /// <summary>Measures one object.</summary>
    public static ObjectFacts Of(BspFile scene, int objectIndex)
    {
        List<Vector3> planes = [];
        List<string> textures = [];
        HashSet<int> surfaces = [];
        uint flags = 0;
        int shadows = 0;
        int triangles = 0;

        Vector3 least = new(float.PositiveInfinity);
        Vector3 most = new(float.NegativeInfinity);

        foreach ((int _, ushort a, ushort b, ushort c, int surface) in
                 SceneObjectNormals.Faces(scene, objectIndex))
        {
            triangles++;

            if (surfaces.Add(surface))
            {
                flags |= scene.Surfaces[surface].Flags;

                if ((scene.Surfaces[surface].Flags & BspSurface.ShadowTextureFlag) != 0)
                {
                    shadows++;
                }

                if (!textures.Contains(scene.Surfaces[surface].TextureName, StringComparer.OrdinalIgnoreCase))
                {
                    textures.Add(scene.Surfaces[surface].TextureName);
                }
            }

            Vector3 pa = scene.Vertices[a];
            Vector3 pb = scene.Vertices[b];
            Vector3 pc = scene.Vertices[c];

            least = Vector3.Min(least, Vector3.Min(pa, Vector3.Min(pb, pc)));
            most = Vector3.Max(most, Vector3.Max(pa, Vector3.Max(pb, pc)));

            Vector3 cross = Vector3.Cross(pb - pa, pc - pa);

            if (cross.LengthSquared() <= 1e-12f)
            {
                continue;
            }

            Vector3 unit = Vector3.Normalize(cross);

            // Distinct to within about six degrees. Anything finer counts the tessellation
            // of a curve rather than the number of faces it turns through, which is the
            // thing that says whether an object is worth subdividing.
            if (!planes.Any(p => Vector3.Dot(p, unit) > 0.995f))
            {
                planes.Add(unit);
            }
        }

        Vector3 extent = triangles > 0 ? most - least : Vector3.Zero;

        return new ObjectFacts
        {
            Surfaces = [.. surfaces.Order()],
            Textures = textures,
            TriangleCount = triangles,
            PlaneCount = planes.Count,
            Size = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)),
            Extent = extent,
            Flags = flags,
            AllShadow = surfaces.Count > 0 && shadows == surfaces.Count,
        };
    }
}

/// <summary>The material class each texture was sorted into, where one was.</summary>
internal sealed class MaterialClasses
{
    private readonly Dictionary<string, string> _classes;

    private MaterialClasses(Dictionary<string, string> classes) => _classes = classes;

    public int Count => _classes.Count;

    /// <summary>Reads the classes out of the material library.</summary>
    public static MaterialClasses Load(string path)
    {
        Dictionary<string, string> classes = new(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            return new MaterialClasses(classes);
        }

        try
        {
            MaterialLibrary? library = JsonSerializer.Deserialize<MaterialLibrary>(
                File.ReadAllText(path), ManifestJson.Options);

            foreach (MaterialDefinition material in library?.Materials ?? [])
            {
                if (material.ReviewNote is not { Length: > 0 } note)
                {
                    continue;
                }

                int colon = note.IndexOf(':', StringComparison.Ordinal);
                string name = colon > 0 ? note[..colon] : note;

                if (name.Length is > 0 and < 24 && !name.Contains(' ', StringComparison.Ordinal))
                {
                    classes[material.Id] = name;
                }
            }
        }
        catch (JsonException)
        {
            return new MaterialClasses(classes);
        }
        catch (IOException)
        {
            return new MaterialClasses(classes);
        }

        return new MaterialClasses(classes);
    }

    /// <summary>The class of a texture, or null when nothing classified it.</summary>
    public string? Of(string texture) => _classes.GetValueOrDefault(texture);
}
