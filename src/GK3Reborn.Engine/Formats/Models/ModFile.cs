using System.Numerics;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Models;

/// <summary>One drawable group within a mesh: a single texture and its triangles.</summary>
public sealed record ModSubmesh
{
    /// <summary>Texture this group is drawn with, without an extension.</summary>
    public required string TextureName { get; init; }

    /// <summary>Tint colour. The stored alpha is always zero and is ignored.</summary>
    public required (byte R, byte G, byte B) Color { get; init; }

    /// <summary>Vertex positions, in mesh space.</summary>
    public required Vector3[] Positions { get; init; }

    /// <summary>Vertex normals, as stored.</summary>
    public required Vector3[] Normals { get; init; }

    /// <summary>Texture coordinates.</summary>
    public required Vector2[] TexCoords { get; init; }

    /// <summary>Triangle indices, three per face.</summary>
    public required ushort[] Indices { get; init; }
}

/// <summary>A mesh: a transform, bounds, and one or more submeshes.</summary>
public sealed record ModMesh
{
    /// <summary>
    /// Mesh-space to local-space transform, built from the stored basis vectors and
    /// position. For a character this is what places an arm relative to its torso.
    /// </summary>
    public required Matrix4x4 MeshToLocal { get; init; }

    /// <summary>Lower corner of the stored bounding box.</summary>
    public required Vector3 BoundsMin { get; init; }

    /// <summary>Upper corner of the stored bounding box.</summary>
    public required Vector3 BoundsMax { get; init; }

    /// <summary>The drawable groups.</summary>
    public required IReadOnlyList<ModSubmesh> Submeshes { get; init; }
}

/// <summary>
/// Reader for GK3's model format.
/// </summary>
/// <remarks>
/// <para>
/// Documented from G-Engine's <c>Model::ParseFromData</c>. Tags are stored
/// little-endian, so they read reversed on disk: <c>LDOM</c> for the file,
/// <c>HSEM</c> per mesh, <c>PRGM</c> per submesh — the game called those "mesh
/// groups" — <c>KDOL</c> for level-of-detail blocks and <c>XDOM</c> for the trailing
/// section.
/// </para>
/// <para>
/// Two oddities are handled rather than explained, because the reference
/// implementation does not explain them either: each triangle carries a fourth
/// 16-bit value of unknown meaning after its three indices, and each LODK block holds
/// three counted arrays whose contents are unidentified. Both are skipped by size.
/// </para>
/// </remarks>
public sealed class ModFile
{
    private ModFile(string name, bool billboard, IReadOnlyList<ModMesh> meshes)
    {
        Name = name;
        IsBillboard = billboard;
        Meshes = meshes;
    }

    /// <summary>
    /// Builds a model from meshes that did not come from a MOD file.
    /// </summary>
    /// <remarks>
    /// Lets the scene exporter present a room as meshes and reuse the glTF writer,
    /// rather than duplicating accessor and buffer handling for a second format.
    /// </remarks>
    /// <param name="name">Name for the produced model.</param>
    /// <param name="meshes">The meshes.</param>
    /// <returns>A model wrapping those meshes.</returns>
    public static ModFile FromMeshes(string name, IReadOnlyList<ModMesh> meshes) =>
        new(name, billboard: false, meshes);

    /// <summary>Name this model was read under.</summary>
    public string Name { get; }

    /// <summary>Whether the model is flagged to render as a billboard.</summary>
    public bool IsBillboard { get; }

    /// <summary>The meshes.</summary>
    public IReadOnlyList<ModMesh> Meshes { get; }

    /// <summary>Total vertices across every submesh.</summary>
    public int VertexCount => Meshes.Sum(m => m.Submeshes.Sum(s => s.Positions.Length));

    /// <summary>Total triangles across every submesh.</summary>
    public int TriangleCount => Meshes.Sum(m => m.Submeshes.Sum(s => s.Indices.Length / 3));

    /// <summary>Parses a model.</summary>
    /// <param name="data">The asset's bytes.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>The parsed model.</returns>
    /// <exception cref="FormatParseException">The data is not a valid model.</exception>
    public static ModFile Parse(ReadOnlySpan<byte> data, string name = "<memory>")
    {
        var reader = new SpanReader(data, name);

        reader.ExpectMagic("LDOM"u8, "Model header");
        reader.Skip(2);                    // minor and major version
        reader.Skip(2);                    // unknown, always zero so far
        uint meshCount = reader.ReadUInt32();
        reader.Skip(4);                    // data size, excluding the 48-byte header
        reader.Skip(8);                    // unknown
        uint flags = reader.ReadUInt32();
        reader.Skip(16);                   // unknown, likely more flags
        reader.Skip(4);                    // unknown, always 8

        if (meshCount > 4096)
        {
            throw Corrupt(name, reader.Position, "a plausible mesh count", meshCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        List<ModMesh> meshes = new((int)meshCount);
        for (uint i = 0; i < meshCount; i++)
        {
            meshes.Add(ReadMesh(ref reader, name));
        }

        // Every mesh is followed by a trailing MODX section. Checking for it is the
        // cheapest possible proof that the parse stayed aligned: any miscounted field
        // anywhere above lands somewhere else entirely and this tag will not be here.
        reader.ExpectMagic("XDOM"u8, "Model trailer");

        return new ModFile(name, (flags & 2) != 0, meshes);
    }

    private static ModMesh ReadMesh(ref SpanReader reader, string name)
    {
        reader.ExpectMagic("HSEM"u8, "Mesh block");

        Vector3 iBasis = ReadVector3(ref reader);
        Vector3 jBasis = ReadVector3(ref reader);
        Vector3 kBasis = ReadVector3(ref reader);
        Vector3 position = ReadVector3(ref reader);

        // The basis vectors are the columns of the transform. GK3 is Y-up while models
        // are authored Z-up, and that rotation is already baked in here.
        var meshToLocal = new Matrix4x4(
            iBasis.X, iBasis.Y, iBasis.Z, 0,
            jBasis.X, jBasis.Y, jBasis.Z, 0,
            kBasis.X, kBasis.Y, kBasis.Z, 0,
            position.X, position.Y, position.Z, 1);

        uint submeshCount = reader.ReadUInt32();
        Vector3 min = ReadVector3(ref reader);
        Vector3 max = ReadVector3(ref reader);

        if (submeshCount > 4096)
        {
            throw Corrupt(name, reader.Position, "a plausible submesh count", submeshCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        List<ModSubmesh> submeshes = new((int)submeshCount);
        for (uint j = 0; j < submeshCount; j++)
        {
            submeshes.Add(ReadSubmesh(ref reader, name));
        }

        return new ModMesh
        {
            MeshToLocal = meshToLocal,
            BoundsMin = min,
            BoundsMax = max,
            Submeshes = submeshes,
        };
    }

    private static ModSubmesh ReadSubmesh(ref SpanReader reader, string name)
    {
        reader.ExpectMagic("PRGM"u8, "Submesh block");

        string textureName = reader.ReadFixedString(32);

        // Stored 0xAABBGGRR. The alpha byte is always zero, which would make every
        // model invisible if taken literally, so it is ignored.
        uint packed = reader.ReadUInt32();
        (byte R, byte G, byte B) color = ((byte)(packed & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)((packed >> 16) & 0xFF));

        reader.Skip(4);                    // unknown, usually 1
        uint vertexCount = reader.ReadUInt32();
        uint faceCount = reader.ReadUInt32();
        uint lodBlockCount = reader.ReadUInt32();
        reader.Skip(4);                    // unknown, usually zero

        if (vertexCount > 1_000_000 || faceCount > 1_000_000)
        {
            throw Corrupt(name, reader.Position, "plausible vertex and face counts", $"{vertexCount} vertices, {faceCount} faces");
        }

        Vector3[] positions = new Vector3[vertexCount];
        for (int k = 0; k < vertexCount; k++)
        {
            positions[k] = ReadVector3(ref reader);
        }

        Vector3[] normals = new Vector3[vertexCount];
        for (int k = 0; k < vertexCount; k++)
        {
            normals[k] = ReadVector3(ref reader);
        }

        Vector2[] texCoords = new Vector2[vertexCount];
        for (int k = 0; k < vertexCount; k++)
        {
            texCoords[k] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }

        ushort[] indices = new ushort[faceCount * 3];
        for (int k = 0; k < faceCount; k++)
        {
            indices[(k * 3) + 0] = reader.ReadUInt16();
            indices[(k * 3) + 1] = reader.ReadUInt16();
            indices[(k * 3) + 2] = reader.ReadUInt16();
            reader.Skip(2); // a fourth value per face, meaning unknown
        }

        for (uint k = 0; k < lodBlockCount; k++)
        {
            reader.ExpectMagic("KDOL"u8, "Level-of-detail block");
            uint first = reader.ReadUInt32();
            uint second = reader.ReadUInt32();
            uint third = reader.ReadUInt32();
            reader.Skip(checked((int)((first * 8) + (second * 4) + (third * 2))));
        }

        return new ModSubmesh
        {
            TextureName = textureName,
            Color = color,
            Positions = positions,
            Normals = normals,
            TexCoords = texCoords,
            Indices = indices,
        };
    }

    private static Vector3 ReadVector3(ref SpanReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static FormatParseException Corrupt(string file, int offset, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1040",
            DiagnosticSeverity.Error,
            "Model is corrupt or is not a supported variant.",
            file,
            offset,
            expected,
            actual,
            "Re-extract the asset and report the model name and offset."));
}
