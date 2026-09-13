using System.Numerics;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Scenes;

/// <summary>A drawable surface: one texture, belonging to one named object.</summary>
public sealed record BspSurface
{
    /// <summary>Index into the file's object-name table.</summary>
    public required int ObjectIndex { get; init; }

    /// <summary>Texture drawn on this surface, without an extension.</summary>
    public required string TextureName { get; init; }

    /// <summary>Offset applied to this surface's lightmap coordinates.</summary>
    public required Vector2 LightmapUvOffset { get; init; }

    /// <summary>Scale applied to this surface's lightmap coordinates.</summary>
    public required Vector2 LightmapUvScale { get; init; }

    /// <summary>Surface flags. Meanings are only partly known.</summary>
    public required uint Flags { get; init; }

    /// <summary>Bit 8: the surface is not lit by the bake at all.</summary>
    public const uint IgnoreLightmapFlag = 8;

    /// <summary>Bit 16: the surface is part of a light fitting — a shade, a globe, a sconce.</summary>
    public const uint LightFixtureFlag = 16;

    /// <summary>Bit 64: the surface is a translucent shadow decal rather than solid geometry.</summary>
    public const uint ShadowTextureFlag = 64;

    /// <summary>Whether the bake lit this surface, or it carries its own brightness.</summary>
    public bool IsSelfLit =>
        (Flags & IgnoreLightmapFlag) != 0 || (Flags & ShadowTextureFlag) != 0;

    /// <summary>
    /// Whether this surface is a stain on whatever it lies over rather than a thing in its
    /// own right: the shadow a parked moped drops on the gravel, the blood on ARM's floor,
    /// the tyre tracks, the darkened glass in LHE's windows, TE5's shadow bridge. Ninety of
    /// them across twenty-six rooms, each a white picture with the mark painted dark on it,
    /// and each meant to be multiplied into what is behind it rather than drawn over it.
    /// </summary>
    public bool IsDecal => (Flags & ShadowTextureFlag) != 0;

    /// <summary>
    /// Whether this surface should block a ray-traced shadow.
    /// </summary>
    public bool CastsShadows =>
        (Flags & (IgnoreLightmapFlag | LightFixtureFlag | ShadowTextureFlag)) == 0;
}

/// <summary>A convex polygon, indexing into the shared vertex-index array.</summary>
public sealed record BspPolygon
{
    /// <summary>Start of this polygon's run within the vertex-index array.</summary>
    public required int VertexIndexOffset { get; init; }

    /// <summary>How many indices this polygon uses.</summary>
    public required int VertexIndexCount { get; init; }

    /// <summary>Which surface, and therefore which texture, this polygon belongs to.</summary>
    public required int SurfaceIndex { get; init; }
}

/// <summary>
/// Reader for GK3's scene geometry: the rooms themselves.
/// </summary>
public sealed class BspFile
{
    private BspFile(
        string name,
        IReadOnlyList<string> objectNames,
        IReadOnlyList<BspSurface> surfaces,
        IReadOnlyList<BspPolygon> polygons,
        Vector3[] vertices,
        Vector2[] texCoords,
        ushort[] vertexIndices)
    {
        Name = name;
        ObjectNames = objectNames;
        Surfaces = surfaces;
        Polygons = polygons;
        Vertices = vertices;
        TexCoords = texCoords;
        VertexIndices = vertexIndices;
    }

    /// <summary>Name this scene was read under.</summary>
    public string Name { get; }

    /// <summary>
    /// Names that group surfaces logically — several surfaces might make up a "door".
    /// </summary>
    public IReadOnlyList<string> ObjectNames { get; }

    /// <summary>The surfaces.</summary>
    public IReadOnlyList<BspSurface> Surfaces { get; }

    /// <summary>The polygons.</summary>
    public IReadOnlyList<BspPolygon> Polygons { get; }

    /// <summary>Shared vertex positions.</summary>
    public Vector3[] Vertices { get; }

    /// <summary>Shared texture coordinates, indexed identically to positions.</summary>
    public Vector2[] TexCoords { get; }

    /// <summary>Shared index array that polygons slice into.</summary>
    public ushort[] VertexIndices { get; }

    /// <summary>Total triangles once polygons are fanned.</summary>
    public int TriangleCount => Polygons.Sum(p => Math.Max(0, p.VertexIndexCount - 2));

    /// <summary>Builds a scene from parts already in memory.</summary>
    /// <param name="name">Name for the produced scene.</param>
    /// <param name="objectNames">Object names surfaces group under.</param>
    /// <param name="surfaces">The surfaces.</param>
    /// <param name="polygons">The polygons.</param>
    /// <param name="vertices">Shared vertex positions.</param>
    /// <param name="texCoords">Shared texture coordinates.</param>
    /// <param name="vertexIndices">Shared index array the polygons slice into.</param>
    /// <returns>The scene.</returns>
    public static BspFile FromParts(
        string name,
        IReadOnlyList<string> objectNames,
        IReadOnlyList<BspSurface> surfaces,
        IReadOnlyList<BspPolygon> polygons,
        Vector3[] vertices,
        Vector2[] texCoords,
        ushort[] vertexIndices) =>
        new(name, objectNames, surfaces, polygons, vertices, texCoords, vertexIndices);

    /// <summary>Parses a scene.</summary>
    /// <param name="data">The asset's bytes.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>The parsed scene.</returns>
    /// <exception cref="FormatParseException">The data is not a valid scene.</exception>
    public static BspFile Parse(ReadOnlySpan<byte> data, string name = "<memory>")
    {
        var reader = new SpanReader(data, name);

        reader.ExpectMagic("NECS"u8, "Scene header");
        reader.Skip(4);  // version
        reader.Skip(4);  // content size
        reader.Skip(4);  // root node index

        int nameCount = ReadCount(ref reader, name, "names");
        int vertexCount = ReadCount(ref reader, name, "vertices");
        int uvCount = ReadCount(ref reader, name, "texture coordinates");
        int vertexIndexCount = ReadCount(ref reader, name, "vertex indices");
        int otherIndexCount = ReadCount(ref reader, name, "secondary indices");
        int surfaceCount = ReadCount(ref reader, name, "surfaces");
        int planeCount = ReadCount(ref reader, name, "planes");
        int nodeCount = ReadCount(ref reader, name, "nodes");
        int polygonCount = ReadCount(ref reader, name, "polygons");

        string[] objectNames = new string[nameCount];
        for (int i = 0; i < nameCount; i++)
        {
            objectNames[i] = reader.ReadFixedString(32);
        }

        BspSurface[] surfaces = new BspSurface[surfaceCount];
        for (int i = 0; i < surfaceCount; i++)
        {
            surfaces[i] = new BspSurface
            {
                ObjectIndex = (int)reader.ReadUInt32(),
                TextureName = reader.ReadFixedString(32),
                LightmapUvOffset = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                LightmapUvScale = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                Flags = SkipUnknownFloatThenFlags(ref reader),
            };
        }

        // The BSP tree is not retained: eight 16-bit fields per node describing children,
        // plane and polygon ranges, none of which a modern renderer traverses.
        reader.Skip(nodeCount * 16);

        BspPolygon[] polygons = new BspPolygon[polygonCount];
        for (int i = 0; i < polygonCount; i++)
        {
            int offset = reader.ReadUInt16();
            reader.Skip(2); // unknown, almost always 1073
            int count = reader.ReadUInt16();
            int surfaceIndex = reader.ReadUInt16();

            polygons[i] = new BspPolygon
            {
                VertexIndexOffset = offset,
                VertexIndexCount = count,
                SurfaceIndex = surfaceIndex,
            };
        }

        reader.Skip(planeCount * 16); // plane normal and distance

        Vector3[] vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        Vector2[] texCoords = new Vector2[uvCount];
        for (int i = 0; i < uvCount; i++)
        {
            texCoords[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }

        ushort[] vertexIndices = new ushort[vertexIndexCount];
        for (int i = 0; i < vertexIndexCount; i++)
        {
            vertexIndices[i] = reader.ReadUInt16();
        }

        // A second index array follows that, across every retail scene, matches the first
        // exactly. Skipped rather than stored.
        reader.Skip(otherIndexCount * 2);

        Validate(name, surfaces, polygons, vertices, texCoords, vertexIndices, objectNames.Length);

        return new BspFile(name, objectNames, surfaces, polygons, vertices, texCoords, vertexIndices);
    }

    /// <summary>
    /// Reads a vertex's texture coordinate, or the origin when the file has none for it.
    /// </summary>
    /// <param name="index">Vertex index.</param>
    /// <returns>The coordinate.</returns>
    public Vector2 TexCoordFor(ushort index) =>
        index < TexCoords.Length ? TexCoords[index] : Vector2.Zero;

    /// <summary>Expands one polygon into triangles as a fan.</summary>
    /// <param name="polygon">The polygon.</param>
    /// <returns>Triples of indices into <see cref="Vertices"/>.</returns>
    public IEnumerable<(ushort A, ushort B, ushort C)> Triangulate(BspPolygon polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);

        // Polygons are convex, so a fan from the first vertex is exact rather than an
        // approximation. This matches how the reference implementation walks them.
        for (int i = 1; i < polygon.VertexIndexCount - 1; i++)
        {
            yield return (
                VertexIndices[polygon.VertexIndexOffset],
                VertexIndices[polygon.VertexIndexOffset + i],
                VertexIndices[polygon.VertexIndexOffset + i + 1]);
        }
    }

    private static uint SkipUnknownFloatThenFlags(ref SpanReader reader)
    {
        reader.Skip(4); // unknown; assumed to be a scale at one point, but unconfirmed
        return reader.ReadUInt32();
    }

    private static int ReadCount(ref SpanReader reader, string name, string what)
    {
        uint value = reader.ReadUInt32();
        if (value > 4_000_000)
        {
            throw Corrupt(name, reader.Position, $"a plausible {what} count", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return (int)value;
    }

    /// <summary>
    /// Checks that the arrays actually refer to one another consistently.
    /// </summary>
    private static void Validate(
        string name,
        BspSurface[] surfaces,
        BspPolygon[] polygons,
        Vector3[] vertices,
        Vector2[] texCoords,
        ushort[] vertexIndices,
        int nameCount)
    {
        foreach (BspSurface surface in surfaces)
        {
            if ((uint)surface.ObjectIndex >= (uint)nameCount)
            {
                throw Corrupt(name, 0, $"an object index below {nameCount}", surface.ObjectIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        foreach (BspPolygon polygon in polygons)
        {
            if ((uint)polygon.SurfaceIndex >= (uint)surfaces.Length)
            {
                throw Corrupt(name, 0, $"a surface index below {surfaces.Length}", polygon.SurfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            long end = (long)polygon.VertexIndexOffset + polygon.VertexIndexCount;
            if (end > vertexIndices.Length)
            {
                throw Corrupt(name, 0, $"an index run within {vertexIndices.Length} indices", end.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // Indices must address a real vertex. They are deliberately not checked against
        // the texture-coordinate array: DEFAULT.BSP, a placeholder cube, has eight
        // vertices and only four coordinates, so requiring both would reject valid data.
        // The reference implementation reads out of bounds there; TexCoordFor does not.
        foreach (ushort index in vertexIndices)
        {
            if (index >= vertices.Length)
            {
                throw Corrupt(
                    name, 0,
                    $"indices below {vertices.Length}",
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        _ = texCoords;
    }

    private static FormatParseException Corrupt(string file, int offset, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1050",
            DiagnosticSeverity.Error,
            "Scene geometry is corrupt or is not a supported variant.",
            file,
            offset,
            expected,
            actual,
            "Re-extract the asset and report the scene name."));
}
