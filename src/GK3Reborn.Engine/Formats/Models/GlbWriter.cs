using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GK3Reborn.Formats.Models;

/// <summary>
/// Writes models as glTF 2.0 binary (<c>.glb</c>).
/// </summary>
/// <remarks>
/// <para>
/// glTF is the plan's interchange format for geometry, and a single <c>.glb</c> opens
/// in Blender, any web viewer and most DCC tools without a plugin — which is the whole
/// point of converting: 1,878 models currently exist in a format nothing but the game
/// can read.
/// </para>
/// <para>
/// Materials reference the converted PNGs by relative URI, so a model opens textured
/// as long as the normalized tree is intact. That keeps the geometry and its textures
/// linked without embedding several megabytes of image into every model.
/// </para>
/// <para>
/// Normals are transformed into mesh space on the way out. glTF requires positions and
/// normals to share a space, and in the original data they do not: positions are mesh
/// space, normals appear to be local space. G-Engine applies the same correction, but
/// only to models whose name is three characters — its own comment calls that a hack
/// and says untransformed normals look wrong on characters while transformed ones look
/// wrong on props. Exporting a self-consistent document is the right call for an
/// interchange format; if the distinction turns out to be real, it belongs in the
/// material pipeline rather than here.
/// </para>
/// </remarks>
public static class GlbWriter
{
    private const uint GlbMagic = 0x46546C67;  // "glTF"
    private const uint ChunkJson = 0x4E4F534A; // "JSON"
    private const uint ChunkBinary = 0x004E4942; // "BIN"

    /// <summary>Encodes a model as a GLB file.</summary>
    /// <param name="model">The model to write.</param>
    /// <param name="texturePathPrefix">
    /// Relative path prepended to texture file names, for example <c>../textures/</c>.
    /// Pass an empty string to omit textures.
    /// </param>
    /// <returns>The complete GLB file.</returns>
    public static byte[] Encode(ModFile model, string texturePathPrefix = "../textures/")
    {
        ArgumentNullException.ThrowIfNull(model);

        var binary = new MemoryStream();
        var accessors = new JsonArray();
        var bufferViews = new JsonArray();
        var meshes = new JsonArray();
        var nodes = new JsonArray();
        var rootNodes = new JsonArray();

        // One material per distinct texture, so a model that reuses a texture across
        // submeshes does not produce duplicates.
        Dictionary<string, int> materialIndices = new(StringComparer.OrdinalIgnoreCase);
        var materials = new JsonArray();
        var textures = new JsonArray();
        var images = new JsonArray();

        foreach (ModMesh mesh in model.Meshes)
        {
            var primitives = new JsonArray();

            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                if (submesh.Positions.Length == 0 || submesh.Indices.Length == 0)
                {
                    continue;
                }

                Vector3[] normals = TransformNormals(submesh.Normals, mesh.MeshToLocal);

                int position = AddVector3Accessor(binary, bufferViews, accessors, submesh.Positions, bounds: true);
                int normal = AddVector3Accessor(binary, bufferViews, accessors, normals, bounds: false);
                int texCoord = AddVector2Accessor(binary, bufferViews, accessors, submesh.TexCoords);
                int index = AddIndexAccessor(binary, bufferViews, accessors, submesh.Indices);

                primitives.Add(new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["POSITION"] = position,
                        ["NORMAL"] = normal,
                        ["TEXCOORD_0"] = texCoord,
                    },
                    ["indices"] = index,
                    ["material"] = MaterialFor(submesh, texturePathPrefix, materialIndices, materials, textures, images),
                    ["mode"] = 4, // triangles
                });
            }

            if (primitives.Count == 0)
            {
                continue;
            }

            meshes.Add(new JsonObject { ["primitives"] = primitives });

            rootNodes.Add(nodes.Count);
            nodes.Add(new JsonObject
            {
                ["mesh"] = meshes.Count - 1,
                ["matrix"] = ToJsonArray(mesh.MeshToLocal),
            });
        }

        var root = new JsonObject
        {
            ["asset"] = new JsonObject
            {
                ["version"] = "2.0",
                ["generator"] = "GK3Reborn importer",
            },
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["nodes"] = rootNodes } },
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray
            {
                new JsonObject { ["byteLength"] = binary.Length },
            },
        };

        if (materials.Count > 0)
        {
            root["materials"] = materials;
        }

        if (textures.Count > 0)
        {
            root["textures"] = textures;
            root["images"] = images;
            root["samplers"] = new JsonArray { new JsonObject { ["wrapS"] = 10497, ["wrapT"] = 10497 } };
        }

        return Assemble(root, binary.ToArray());
    }

    private static Vector3[] TransformNormals(Vector3[] normals, Matrix4x4 meshToLocal)
    {
        // Transposing the mesh-to-local matrix turns it into a local-to-mesh transform
        // for direction vectors, which avoids computing an inverse.
        Matrix4x4 transposed = Matrix4x4.Transpose(meshToLocal);
        Vector3[] result = new Vector3[normals.Length];

        for (int i = 0; i < normals.Length; i++)
        {
            Vector3 transformed = Vector3.TransformNormal(normals[i], transposed);
            result[i] = transformed.LengthSquared() > 0 ? Vector3.Normalize(transformed) : new Vector3(0, 1, 0);
        }

        return result;
    }

    private static int MaterialFor(
        ModSubmesh submesh,
        string texturePathPrefix,
        Dictionary<string, int> indices,
        JsonArray materials,
        JsonArray textures,
        JsonArray images)
    {
        string key = submesh.TextureName.Length == 0 ? "(none)" : submesh.TextureName;
        if (indices.TryGetValue(key, out int existing))
        {
            return existing;
        }

        var pbr = new JsonObject
        {
            ["baseColorFactor"] = new JsonArray
            {
                submesh.Color.R / 255.0,
                submesh.Color.G / 255.0,
                submesh.Color.B / 255.0,
                1.0,
            },

            // The originals carry no PBR channels at all; these are neutral starting
            // values that the material inference pass replaces. See ADR 0006.
            ["metallicFactor"] = 0.0,
            ["roughnessFactor"] = 1.0,
        };

        if (submesh.TextureName.Length > 0 && texturePathPrefix.Length > 0)
        {
            images.Add(new JsonObject
            {
                ["uri"] = texturePathPrefix + Path.ChangeExtension(submesh.TextureName, ".png").ToUpperInvariant(),
            });

            textures.Add(new JsonObject
            {
                ["source"] = images.Count - 1,
                ["sampler"] = 0,
            });

            pbr["baseColorTexture"] = new JsonObject { ["index"] = textures.Count - 1 };
        }

        materials.Add(new JsonObject
        {
            ["name"] = key,
            ["pbrMetallicRoughness"] = pbr,

            // GK3's winding is not consistently counter-clockwise, and single-sided
            // materials would make parts of models invisible in a viewer.
            ["doubleSided"] = true,
        });

        indices[key] = materials.Count - 1;
        return materials.Count - 1;
    }

    private static int AddVector3Accessor(
        MemoryStream binary, JsonArray bufferViews, JsonArray accessors, Vector3[] values, bool bounds)
    {
        int offset = Align(binary);
        Span<byte> scratch = stackalloc byte[12];

        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);

        foreach (Vector3 v in values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(scratch, v.X);
            BinaryPrimitives.WriteSingleLittleEndian(scratch[4..], v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(scratch[8..], v.Z);
            binary.Write(scratch);

            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        bufferViews.Add(BufferView(offset, values.Length * 12, target: 34962));

        var accessor = new JsonObject
        {
            ["bufferView"] = bufferViews.Count - 1,
            ["componentType"] = 5126, // float
            ["count"] = values.Length,
            ["type"] = "VEC3",
        };

        if (bounds && values.Length > 0)
        {
            // The specification requires min and max on POSITION accessors.
            accessor["min"] = new JsonArray { min.X, min.Y, min.Z };
            accessor["max"] = new JsonArray { max.X, max.Y, max.Z };
        }

        accessors.Add(accessor);
        return accessors.Count - 1;
    }

    private static int AddVector2Accessor(
        MemoryStream binary, JsonArray bufferViews, JsonArray accessors, Vector2[] values)
    {
        int offset = Align(binary);
        Span<byte> scratch = stackalloc byte[8];

        foreach (Vector2 v in values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(scratch, v.X);
            BinaryPrimitives.WriteSingleLittleEndian(scratch[4..], v.Y);
            binary.Write(scratch);
        }

        bufferViews.Add(BufferView(offset, values.Length * 8, target: 34962));
        accessors.Add(new JsonObject
        {
            ["bufferView"] = bufferViews.Count - 1,
            ["componentType"] = 5126,
            ["count"] = values.Length,
            ["type"] = "VEC2",
        });

        return accessors.Count - 1;
    }

    private static int AddIndexAccessor(
        MemoryStream binary, JsonArray bufferViews, JsonArray accessors, ushort[] indices)
    {
        int offset = Align(binary);
        Span<byte> scratch = stackalloc byte[2];

        foreach (ushort index in indices)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(scratch, index);
            binary.Write(scratch);
        }

        bufferViews.Add(BufferView(offset, indices.Length * 2, target: 34963));
        accessors.Add(new JsonObject
        {
            ["bufferView"] = bufferViews.Count - 1,
            ["componentType"] = 5123, // unsigned short
            ["count"] = indices.Length,
            ["type"] = "SCALAR",
        });

        return accessors.Count - 1;
    }

    private static JsonObject BufferView(int offset, int length, int target) => new()
    {
        ["buffer"] = 0,
        ["byteOffset"] = offset,
        ["byteLength"] = length,
        ["target"] = target,
    };

    /// <summary>Pads the buffer so the next view starts on a four-byte boundary.</summary>
    private static int Align(MemoryStream binary)
    {
        while ((binary.Length % 4) != 0)
        {
            binary.WriteByte(0);
        }

        return (int)binary.Length;
    }

    private static JsonArray ToJsonArray(Matrix4x4 m) =>
        [
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44,
        ];

    private static byte[] Assemble(JsonObject root, byte[] binary)
    {
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
        }));

        // Both chunks are padded to four bytes: JSON with spaces, binary with zeros.
        int jsonPadding = (4 - (json.Length % 4)) % 4;
        int binaryPadding = (4 - (binary.Length % 4)) % 4;

        int total = 12 + 8 + json.Length + jsonPadding + 8 + binary.Length + binaryPadding;

        var output = new MemoryStream(total);
        var writer = new BinaryWriter(output);

        writer.Write(GlbMagic);
        writer.Write(2u);
        writer.Write((uint)total);

        writer.Write((uint)(json.Length + jsonPadding));
        writer.Write(ChunkJson);
        writer.Write(json);
        for (int i = 0; i < jsonPadding; i++)
        {
            writer.Write((byte)' ');
        }

        writer.Write((uint)(binary.Length + binaryPadding));
        writer.Write(ChunkBinary);
        writer.Write(binary);
        for (int i = 0; i < binaryPadding; i++)
        {
            writer.Write((byte)0);
        }

        writer.Flush();
        return output.ToArray();
    }
}
