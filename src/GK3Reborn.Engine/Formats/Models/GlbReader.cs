using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Models;

/// <summary>
/// Reads models written as glTF 2.0 binary (<c>.glb</c>) back into the engine's own
/// model type.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="GlbWriter"/>, and the reason enhanced geometry can reach
/// the screen at all. Until this existed <c>enhanced/models</c> was an output and never an
/// input: the toolchain could convert a model out to glTF, improve it in a DCC and write
/// it back, and the engine had no way to read the result. Everything drawn came from a
/// <c>.MOD</c>.
/// </para>
/// <para>
/// A deliberately small subset, because the documents this reads are ones the project's
/// own tools wrote: a static mesh hierarchy with positions, normals and one set of texture
/// coordinates. Skins, morph targets, animation, sparse accessors and external buffers are
/// refused rather than half-supported — a silently ignored skin is a model that draws in
/// its bind pose forever and looks like a different bug.
/// </para>
/// <para>
/// A material names a texture and nothing else. GK3 addresses every surface by a texture
/// name that the archives or the enhanced set resolve, so what the engine needs from a
/// glTF material is that name; the PBR factors alongside it belong to a material system
/// this renderer does not have. The name is taken from the material, falling back to the
/// image, because Blender's exporter keeps material names exactly and rewrites image ones.
/// </para>
/// <para>
/// Coordinates pass straight through, as they do on the way out. The GLB files the
/// toolchain writes are the game's own axes wearing a glTF label, so a model that goes to
/// Blender and comes back lands exactly where it started — which is the property the tree
/// work depends on, since a generated tree has to stand where the card it replaces stood.
/// </para>
/// </remarks>
public static class GlbReader
{
    private const uint GlbMagic = 0x46546C67;    // "glTF"
    private const uint ChunkJson = 0x4E4F534A;   // "JSON"
    private const uint ChunkBinary = 0x004E4942; // "BIN"

    /// <summary>How many vertices one submesh may hold, because indices are 16-bit.</summary>
    private const int VertexLimit = 65536;

    /// <summary>Reads a GLB file.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="name">Name used in diagnostics and given to the model.</param>
    /// <returns>The model.</returns>
    /// <exception cref="FormatParseException">The data is not a GLB this can read.</exception>
    public static ModFile Parse(ReadOnlySpan<byte> data, string name = "<memory>")
    {
        (JsonElement root, byte[] binary) = Split(data, name);

        JsonElement[] nodes = Array(root, "nodes");
        JsonElement[] meshes = Array(root, "meshes");
        JsonElement[] accessors = Array(root, "accessors");
        JsonElement[] views = Array(root, "bufferViews");
        string[] materials = MaterialTextures(root);

        List<ModMesh> built = [];

        foreach (int rootNode in RootNodes(root, nodes))
        {
            Walk(rootNode, Matrix4x4.Identity);
        }

        return ModFile.FromMeshes(Path.GetFileNameWithoutExtension(name), built);

        void Walk(int index, Matrix4x4 parent)
        {
            if (index < 0 || index >= nodes.Length)
            {
                throw Corrupt(name, "a node index within the document", index.ToString(CultureInfo.InvariantCulture));
            }

            JsonElement node = nodes[index];
            Matrix4x4 here = LocalTransform(node, name) * parent;

            if (node.TryGetProperty("mesh", out JsonElement meshIndex))
            {
                int at = meshIndex.GetInt32();

                if (at < 0 || at >= meshes.Length)
                {
                    throw Corrupt(name, "a mesh index within the document", at.ToString(CultureInfo.InvariantCulture));
                }

                List<ModSubmesh> submeshes = [];

                foreach (JsonElement primitive in Array(meshes[at], "primitives"))
                {
                    submeshes.AddRange(
                        ReadPrimitive(primitive, accessors, views, binary, materials, name));
                }

                if (submeshes.Count > 0)
                {
                    // The node's transform becomes the mesh's own, rather than being baked
                    // into the vertices. That is how a MOD stores a model, and keeping the
                    // shape means the parts a scene can pose stay separately posable.
                    built.Add(new ModMesh
                    {
                        MeshToLocal = here,
                        BoundsMin = Least(submeshes),
                        BoundsMax = Most(submeshes),
                        Submeshes = submeshes,

                        // Kept because a room built from glTF groups its surfaces by it:
                        // the node names are what a scene file binds nouns to, so losing
                        // them would leave every object in such a room called nothing.
                        Name = node.TryGetProperty("name", out JsonElement named) &&
                               named.ValueKind == JsonValueKind.String
                            ? named.GetString() ?? string.Empty
                            : string.Empty,
                    });
                }
            }

            if (node.TryGetProperty("children", out JsonElement children))
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    Walk(child.GetInt32(), here);
                }
            }
        }
    }

    /// <summary>Reads a GLB file, returning null rather than throwing.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="name">Name used in diagnostics and given to the model.</param>
    /// <param name="diagnostics">Receives a warning when the file will not read.</param>
    /// <returns>The model, or null.</returns>
    /// <remarks>
    /// For callers loading optional content. A generated tree that will not parse should
    /// cost that tree and leave the scene standing, in the same way an enhanced texture
    /// that will not decode falls back to the original.
    /// </remarks>
    public static ModFile? TryParse(
        ReadOnlySpan<byte> data, string name, DiagnosticBag? diagnostics)
    {
        try
        {
            return Parse(data, name);
        }
        catch (Exception ex) when (ex is FormatParseException or JsonException or
                                       IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1120",
                DiagnosticSeverity.Warning,
                $"The model {name} will not read, so nothing is put in its place: {ex.Message}",
                name,
                null,
                "a readable glTF binary",
                ex.GetType().Name,
                "Export the model again, or take it out of the enhanced set."));

            return null;
        }
    }

    private static (JsonElement Root, byte[] Binary) Split(ReadOnlySpan<byte> data, string name)
    {
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(data) != GlbMagic)
        {
            throw Corrupt(name, "a glTF binary header", "something else");
        }

        uint total = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        int end = Math.Min(data.Length, (int)Math.Min(total, int.MaxValue));
        int at = 12;

        JsonDocument? json = null;
        byte[] binary = [];

        while (at + 8 <= end)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            at += 8;

            if (length < 0 || at + length > end)
            {
                throw Corrupt(name, "a chunk inside the file", "one that runs past its end");
            }

            ReadOnlySpan<byte> chunk = data.Slice(at, length);
            at += length;

            if (kind == ChunkJson && json is null)
            {
                // Chunks are padded to four bytes. The specification says to pad the JSON
                // one with spaces and some writers pad it with zeroes instead, and a
                // trailing zero is not whitespace as far as the parser is concerned.
                while (chunk.Length > 0 &&
                       (chunk[^1] == 0x00 || chunk[^1] == 0x20))
                {
                    chunk = chunk[..^1];
                }

                json = JsonDocument.Parse(chunk.ToArray());
            }
            else if (kind == ChunkBinary && binary.Length == 0)
            {
                binary = chunk.ToArray();
            }
        }

        if (json is null)
        {
            throw Corrupt(name, "a JSON chunk", "none");
        }

        JsonElement root = json.RootElement;

        // An external buffer would mean reading a file beside this one, which enhanced
        // content is not allowed to do: a pack has to be one addressable thing.
        if (root.TryGetProperty("buffers", out JsonElement buffers))
        {
            foreach (JsonElement buffer in buffers.EnumerateArray())
            {
                if (buffer.TryGetProperty("uri", out _))
                {
                    throw Corrupt(name, "geometry inside the file", "a reference to another file");
                }
            }
        }

        return (root, binary);
    }

    private static IEnumerable<int> RootNodes(JsonElement root, JsonElement[] nodes)
    {
        int wanted = root.TryGetProperty("scene", out JsonElement scene) ? scene.GetInt32() : 0;
        JsonElement[] scenes = Array(root, "scenes");

        if (wanted >= 0 && wanted < scenes.Length &&
            scenes[wanted].TryGetProperty("nodes", out JsonElement listed))
        {
            foreach (JsonElement node in listed.EnumerateArray())
            {
                yield return node.GetInt32();
            }

            yield break;
        }

        // A document with no scene is still a document. Every node that nothing else
        // claims as a child is a root, which is the same walk by another route.
        HashSet<int> claimed = [];

        foreach (JsonElement node in nodes)
        {
            if (node.TryGetProperty("children", out JsonElement children))
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    claimed.Add(child.GetInt32());
                }
            }
        }

        for (int index = 0; index < nodes.Length; index++)
        {
            if (!claimed.Contains(index))
            {
                yield return index;
            }
        }
    }

    private static Matrix4x4 LocalTransform(JsonElement node, string name)
    {
        if (node.TryGetProperty("matrix", out JsonElement matrix))
        {
            float[] cells = Floats(matrix, 16, name, "a node matrix");

            // glTF stores a matrix in column-major order; this one is row-major.
            return new Matrix4x4(
                cells[0], cells[1], cells[2], cells[3],
                cells[4], cells[5], cells[6], cells[7],
                cells[8], cells[9], cells[10], cells[11],
                cells[12], cells[13], cells[14], cells[15]);
        }

        Matrix4x4 built = Matrix4x4.Identity;

        if (node.TryGetProperty("scale", out JsonElement scale))
        {
            float[] cells = Floats(scale, 3, name, "a node scale");
            built *= Matrix4x4.CreateScale(cells[0], cells[1], cells[2]);
        }

        if (node.TryGetProperty("rotation", out JsonElement rotation))
        {
            float[] cells = Floats(rotation, 4, name, "a node rotation");
            built *= Matrix4x4.CreateFromQuaternion(
                new Quaternion(cells[0], cells[1], cells[2], cells[3]));
        }

        if (node.TryGetProperty("translation", out JsonElement translation))
        {
            float[] cells = Floats(translation, 3, name, "a node translation");
            built *= Matrix4x4.CreateTranslation(cells[0], cells[1], cells[2]);
        }

        return built;
    }

    private static IEnumerable<ModSubmesh> ReadPrimitive(
        JsonElement primitive,
        JsonElement[] accessors,
        JsonElement[] views,
        byte[] binary,
        string[] materials,
        string name)
    {
        if (primitive.TryGetProperty("mode", out JsonElement mode) && mode.GetInt32() != 4)
        {
            // Only triangles. A line or point primitive drawn as triangles is worse than
            // a primitive that was left out, because it looks like broken geometry.
            yield break;
        }

        if (!primitive.TryGetProperty("attributes", out JsonElement attributes) ||
            !attributes.TryGetProperty("POSITION", out JsonElement positionAt))
        {
            yield break;
        }

        Vector3[] positions = Vector3s(accessors, views, binary, positionAt.GetInt32(), name);

        Vector3[] normals = attributes.TryGetProperty("NORMAL", out JsonElement normalAt)
            ? Vector3s(accessors, views, binary, normalAt.GetInt32(), name)
            : [];

        Vector2[] texCoords = attributes.TryGetProperty("TEXCOORD_0", out JsonElement uvAt)
            ? Vector2s(accessors, views, binary, uvAt.GetInt32(), name)
            : [];

        int[] indices = primitive.TryGetProperty("indices", out JsonElement indexAt)
            ? Indices(accessors, views, binary, indexAt.GetInt32(), name)
            : [.. Enumerable.Range(0, positions.Length)];

        string texture = primitive.TryGetProperty("material", out JsonElement materialAt) &&
                         materialAt.GetInt32() >= 0 && materialAt.GetInt32() < materials.Length
            ? materials[materialAt.GetInt32()]
            : string.Empty;

        foreach (ModSubmesh submesh in Chop(positions, normals, texCoords, indices, texture))
        {
            yield return submesh;
        }
    }

    /// <summary>Cuts a primitive into pieces small enough to index with 16 bits.</summary>
    /// <remarks>
    /// A MOD submesh indexes with <c>ushort</c>, which is the format's own limit and not
    /// one worth relaxing: the whole point of producing <see cref="ModFile"/> is that
    /// enhanced geometry travels the same path as original geometry and is drawn by the
    /// same code. A generated tree is a few thousand triangles and never needs this, but a
    /// merged stand of them does, and silently dropping the overflow would take the far
    /// half of a wood away.
    /// </remarks>
    private static IEnumerable<ModSubmesh> Chop(
        Vector3[] positions,
        Vector3[] normals,
        Vector2[] texCoords,
        int[] indices,
        string texture)
    {
        int triangles = indices.Length / 3;

        if (positions.Length <= VertexLimit)
        {
            yield return new ModSubmesh
            {
                TextureName = texture,
                Color = (255, 255, 255),
                Positions = positions,
                Normals = normals.Length == positions.Length ? normals : Facing(positions),
                TexCoords = texCoords.Length == positions.Length
                    ? texCoords
                    : new Vector2[positions.Length],
                Indices = [.. indices.Select(i => (ushort)i)],
            };

            yield break;
        }

        var remap = new Dictionary<int, ushort>();
        List<Vector3> keptPositions = [];
        List<Vector3> keptNormals = [];
        List<Vector2> keptTexCoords = [];
        List<ushort> keptIndices = [];

        for (int triangle = 0; triangle < triangles; triangle++)
        {
            if (keptPositions.Count + 3 > VertexLimit)
            {
                yield return Pack();
                remap.Clear();
                keptPositions.Clear();
                keptNormals.Clear();
                keptTexCoords.Clear();
                keptIndices.Clear();
            }

            for (int corner = 0; corner < 3; corner++)
            {
                int from = indices[(triangle * 3) + corner];

                if (!remap.TryGetValue(from, out ushort to))
                {
                    to = (ushort)keptPositions.Count;
                    remap[from] = to;
                    keptPositions.Add(positions[from]);
                    keptNormals.Add(from < normals.Length ? normals[from] : Vector3.UnitY);
                    keptTexCoords.Add(from < texCoords.Length ? texCoords[from] : Vector2.Zero);
                }

                keptIndices.Add(to);
            }
        }

        if (keptIndices.Count > 0)
        {
            yield return Pack();
        }

        ModSubmesh Pack() => new()
        {
            TextureName = texture,
            Color = (255, 255, 255),
            Positions = [.. keptPositions],
            Normals = [.. keptNormals],
            TexCoords = [.. keptTexCoords],
            Indices = [.. keptIndices],
        };
    }

    /// <summary>Normals for geometry that arrived without any.</summary>
    private static Vector3[] Facing(Vector3[] positions) =>
        [.. Enumerable.Repeat(Vector3.UnitY, positions.Length)];

    private static string[] MaterialTextures(JsonElement root)
    {
        JsonElement[] materials = Array(root, "materials");
        JsonElement[] textures = Array(root, "textures");
        JsonElement[] images = Array(root, "images");
        string[] named = new string[materials.Length];

        for (int index = 0; index < materials.Length; index++)
        {
            JsonElement material = materials[index];

            if (material.TryGetProperty("name", out JsonElement label) &&
                label.GetString() is { Length: > 0 } text)
            {
                named[index] = Bare(text);
                continue;
            }

            named[index] = ImageName(material, textures, images);
        }

        return named;
    }

    private static string ImageName(
        JsonElement material, JsonElement[] textures, JsonElement[] images)
    {
        if (!material.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr) ||
            !pbr.TryGetProperty("baseColorTexture", out JsonElement slot) ||
            !slot.TryGetProperty("index", out JsonElement at))
        {
            return string.Empty;
        }

        int texture = at.GetInt32();

        if (texture < 0 || texture >= textures.Length ||
            !textures[texture].TryGetProperty("source", out JsonElement source))
        {
            return string.Empty;
        }

        int image = source.GetInt32();

        if (image < 0 || image >= images.Length)
        {
            return string.Empty;
        }

        if (images[image].TryGetProperty("name", out JsonElement label) &&
            label.GetString() is { Length: > 0 } named)
        {
            return Bare(named);
        }

        return images[image].TryGetProperty("uri", out JsonElement uri) &&
               uri.GetString() is { Length: > 0 } path
            ? Bare(path)
            : string.Empty;
    }

    /// <summary>A texture name without its directory or its extension.</summary>
    private static string Bare(string path) =>
        Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));

    private static Vector3[] Vector3s(
        JsonElement[] accessors, JsonElement[] views, byte[] binary, int index, string name)
    {
        float[] cells = Read(accessors, views, binary, index, "VEC3", 3, name);
        Vector3[] out_ = new Vector3[cells.Length / 3];

        for (int i = 0; i < out_.Length; i++)
        {
            out_[i] = new Vector3(cells[i * 3], cells[(i * 3) + 1], cells[(i * 3) + 2]);
        }

        return out_;
    }

    private static Vector2[] Vector2s(
        JsonElement[] accessors, JsonElement[] views, byte[] binary, int index, string name)
    {
        float[] cells = Read(accessors, views, binary, index, "VEC2", 2, name);
        Vector2[] out_ = new Vector2[cells.Length / 2];

        for (int i = 0; i < out_.Length; i++)
        {
            out_[i] = new Vector2(cells[i * 2], cells[(i * 2) + 1]);
        }

        return out_;
    }

    private static float[] Read(
        JsonElement[] accessors,
        JsonElement[] views,
        byte[] binary,
        int index,
        string type,
        int components,
        string name)
    {
        JsonElement accessor = Accessor(accessors, index, name);

        if (accessor.GetProperty("type").GetString() != type)
        {
            throw Corrupt(name, type, accessor.GetProperty("type").GetString() ?? "nothing");
        }

        if (accessor.GetProperty("componentType").GetInt32() != 5126)
        {
            throw Corrupt(name, "floating-point vertex data", "an integer accessor");
        }

        int count = accessor.GetProperty("count").GetInt32();
        float[] out_ = new float[count * components];
        ReadOnlySpan<byte> bytes = Slice(accessor, views, binary, name, out int stride, components * 4);

        for (int element = 0; element < count; element++)
        {
            for (int component = 0; component < components; component++)
            {
                out_[(element * components) + component] = BinaryPrimitives.ReadSingleLittleEndian(
                    bytes[((element * stride) + (component * 4))..]);
            }
        }

        return out_;
    }

    private static int[] Indices(
        JsonElement[] accessors, JsonElement[] views, byte[] binary, int index, string name)
    {
        JsonElement accessor = Accessor(accessors, index, name);
        int component = accessor.GetProperty("componentType").GetInt32();

        int width = component switch
        {
            5121 => 1,
            5123 => 2,
            5125 => 4,
            _ => throw Corrupt(name, "an index accessor", component.ToString(CultureInfo.InvariantCulture)),
        };

        int count = accessor.GetProperty("count").GetInt32();
        int[] out_ = new int[count];
        ReadOnlySpan<byte> bytes = Slice(accessor, views, binary, name, out int stride, width);

        for (int at = 0; at < count; at++)
        {
            ReadOnlySpan<byte> cell = bytes[(at * stride)..];

            out_[at] = width switch
            {
                1 => cell[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(cell),
                _ => (int)BinaryPrimitives.ReadUInt32LittleEndian(cell),
            };
        }

        return out_;
    }

    private static JsonElement Accessor(JsonElement[] accessors, int index, string name)
    {
        if (index < 0 || index >= accessors.Length)
        {
            throw Corrupt(name, "an accessor index within the document",
                index.ToString(CultureInfo.InvariantCulture));
        }

        JsonElement accessor = accessors[index];

        if (accessor.TryGetProperty("sparse", out _))
        {
            throw Corrupt(name, "plain vertex data", "a sparse accessor");
        }

        return accessor;
    }

    private static ReadOnlySpan<byte> Slice(
        JsonElement accessor,
        JsonElement[] views,
        byte[] binary,
        string name,
        out int stride,
        int packed)
    {
        if (!accessor.TryGetProperty("bufferView", out JsonElement viewAt))
        {
            throw Corrupt(name, "vertex data", "an accessor with no buffer behind it");
        }

        int index = viewAt.GetInt32();

        if (index < 0 || index >= views.Length)
        {
            throw Corrupt(name, "a buffer view within the document",
                index.ToString(CultureInfo.InvariantCulture));
        }

        JsonElement view = views[index];
        int start = Offset(view, "byteOffset") + Offset(accessor, "byteOffset");
        int length = view.GetProperty("byteLength").GetInt32() - Offset(accessor, "byteOffset");
        stride = view.TryGetProperty("byteStride", out JsonElement strided)
            ? strided.GetInt32()
            : packed;

        if (start < 0 || length < 0 || start + length > binary.Length)
        {
            throw Corrupt(name, "a buffer view inside the file", "one that runs past its end");
        }

        return binary.AsSpan(start, length);
    }

    private static int Offset(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) ? value.GetInt32() : 0;

    private static JsonElement[] Array(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()]
            : [];

    private static float[] Floats(JsonElement element, int count, string name, string what)
    {
        float[] cells = [.. element.EnumerateArray().Select(v => v.GetSingle())];

        return cells.Length == count
            ? cells
            : throw Corrupt(name, what, cells.Length.ToString(CultureInfo.InvariantCulture) + " numbers");
    }

    private static Vector3 Least(IReadOnlyList<ModSubmesh> submeshes)
    {
        var least = new Vector3(float.MaxValue);

        foreach (ModSubmesh submesh in submeshes)
        {
            foreach (Vector3 position in submesh.Positions)
            {
                least = Vector3.Min(least, position);
            }
        }

        return least.X == float.MaxValue ? Vector3.Zero : least;
    }

    private static Vector3 Most(IReadOnlyList<ModSubmesh> submeshes)
    {
        var most = new Vector3(float.MinValue);

        foreach (ModSubmesh submesh in submeshes)
        {
            foreach (Vector3 position in submesh.Positions)
            {
                most = Vector3.Max(most, position);
            }
        }

        return most.X == float.MinValue ? Vector3.Zero : most;
    }

    private static FormatParseException Corrupt(string name, string expected, string found) =>
        new(new Diagnostic(
            "GK3R1121",
            DiagnosticSeverity.Error,
            "The glTF binary is corrupt, or uses a feature this reader does not.",
            name,
            null,
            expected,
            found,
            "Export the model again as a plain static mesh with one UV set."));
}
