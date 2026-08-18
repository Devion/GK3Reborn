using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Models;
using Xunit;

namespace GK3Reborn.Tests.Formats;

public sealed class ModelTests
{
    /// <summary>Builds a minimal but structurally complete model.</summary>
    private sealed class ModBuilder : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;
        private int _meshes;

        public ModBuilder(uint flags = 0)
        {
            _writer = new BinaryWriter(_stream, Encoding.ASCII);
            _writer.Write("LDOM"u8);
            _writer.Write((byte)1);   // minor
            _writer.Write((byte)1);   // major
            _writer.Write((ushort)0); // unknown
            _writer.Write(0u);        // mesh count, patched in Build
            _writer.Write(0u);        // data size
            _writer.Write(0u);        // unknown
            _writer.Write(0u);        // unknown
            _writer.Write(flags);
            _writer.Write(new byte[16]);
            _writer.Write(8u);
        }

        public ModBuilder AddMesh(
            Vector3 position,
            string texture,
            Vector3[] vertices,
            ushort[] indices,
            uint lodBlocks = 0)
        {
            _writer.Write("HSEM"u8);
            WriteVector(Vector3.UnitX);
            WriteVector(Vector3.UnitY);
            WriteVector(Vector3.UnitZ);
            WriteVector(position);
            _writer.Write(1u); // one submesh
            WriteVector(new Vector3(-1, -1, -1));
            WriteVector(new Vector3(1, 1, 1));

            _writer.Write("PRGM"u8);
            byte[] name = new byte[32];
            Encoding.ASCII.GetBytes(texture).CopyTo(name, 0);
            _writer.Write(name);
            _writer.Write(0x00FF8040u); // 0xAABBGGRR
            _writer.Write(1u);
            _writer.Write((uint)vertices.Length);
            _writer.Write((uint)(indices.Length / 3));
            _writer.Write(lodBlocks);
            _writer.Write(0u);

            foreach (Vector3 v in vertices)
            {
                WriteVector(v);
            }

            foreach (Vector3 _ in vertices)
            {
                WriteVector(Vector3.UnitY);
            }

            foreach (Vector3 _ in vertices)
            {
                _writer.Write(0.25f);
                _writer.Write(0.75f);
            }

            for (int i = 0; i < indices.Length; i += 3)
            {
                _writer.Write(indices[i]);
                _writer.Write(indices[i + 1]);
                _writer.Write(indices[i + 2]);
                _writer.Write((ushort)0xF100); // the fourth value of unknown meaning
            }

            for (uint i = 0; i < lodBlocks; i++)
            {
                _writer.Write("KDOL"u8);
                _writer.Write(2u); // two entries of 8 bytes
                _writer.Write(3u); // three entries of 4 bytes
                _writer.Write(1u); // one entry of 2 bytes
                _writer.Write(new byte[(2 * 8) + (3 * 4) + (1 * 2)]);
            }

            _meshes++;
            return this;
        }

        public byte[] Build(bool includeTrailer = true)
        {
            if (includeTrailer)
            {
                _writer.Write("XDOM"u8);
            }

            _writer.Flush();
            byte[] bytes = _stream.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)_meshes);
            return bytes;
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }

        private void WriteVector(Vector3 v)
        {
            _writer.Write(v.X);
            _writer.Write(v.Y);
            _writer.Write(v.Z);
        }
    }

    private static readonly Vector3[] Triangle =
        [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];

    private static readonly ushort[] TriangleIndices = [0, 1, 2];

    [Fact]
    public void A_model_round_trips_through_the_parser()
    {
        using var builder = new ModBuilder();
        byte[] data = builder
            .AddMesh(new Vector3(10, 20, 30), "GABBODY", Triangle, TriangleIndices)
            .Build();

        ModFile model = ModFile.Parse(data, "GAB.MOD");

        ModMesh mesh = Assert.Single(model.Meshes);
        ModSubmesh submesh = Assert.Single(mesh.Submeshes);

        Assert.Equal("GABBODY", submesh.TextureName);
        Assert.Equal(3, submesh.Positions.Length);
        Assert.Equal(new Vector3(1, 0, 0), submesh.Positions[1]);
        Assert.Equal([0, 1, 2], submesh.Indices);
        Assert.Equal(3, model.VertexCount);
        Assert.Equal(1, model.TriangleCount);
    }

    [Fact]
    public void The_mesh_transform_carries_its_position()
    {
        using var builder = new ModBuilder();
        byte[] data = builder
            .AddMesh(new Vector3(10, 20, 30), "T", Triangle, TriangleIndices)
            .Build();

        ModMesh mesh = Assert.Single(ModFile.Parse(data).Meshes);

        Assert.Equal(new Vector3(10, 20, 30), mesh.MeshToLocal.Translation);
    }

    [Fact]
    public void Submesh_colour_is_read_as_bgr_with_the_stored_alpha_ignored()
    {
        // Stored 0xAABBGGRR = 0x00FF8040, so red 0x40, green 0x80, blue 0xFF. Taking the
        // stored alpha literally would make every model invisible.
        using var builder = new ModBuilder();
        byte[] data = builder.AddMesh(Vector3.Zero, "T", Triangle, TriangleIndices).Build();

        ModSubmesh submesh = ModFile.Parse(data).Meshes[0].Submeshes[0];

        Assert.Equal((0x40, 0x80, 0xFF), submesh.Color);
    }

    [Fact]
    public void Level_of_detail_blocks_are_skipped_by_size()
    {
        // The contents are unidentified, but their sizes are known, so the parser has to
        // step over them exactly or everything after drifts.
        using var builder = new ModBuilder();
        byte[] data = builder
            .AddMesh(Vector3.Zero, "A", Triangle, TriangleIndices, lodBlocks: 2)
            .AddMesh(Vector3.One, "B", Triangle, TriangleIndices)
            .Build();

        ModFile model = ModFile.Parse(data);

        Assert.Equal(2, model.Meshes.Count);
        Assert.Equal("B", model.Meshes[1].Submeshes[0].TextureName);
    }

    [Fact]
    public void A_missing_trailer_is_reported_as_truncation()
    {
        // The trailer is the cheap integrity check: if any field above was miscounted,
        // the read lands somewhere else and this tag is not there. Running off the end
        // of the buffer is truncation rather than a signature mismatch.
        using var builder = new ModBuilder();
        byte[] data = builder
            .AddMesh(Vector3.Zero, "T", Triangle, TriangleIndices)
            .Build(includeTrailer: false);

        var ex = Assert.Throws<FormatParseException>(() => ModFile.Parse(data, "BROKEN.MOD"));

        Assert.Equal("GK3R1001", ex.Diagnostic.Code);
        Assert.Equal("BROKEN.MOD", ex.Diagnostic.File);
    }

    [Fact]
    public void A_trailer_that_is_not_MODX_is_reported_as_a_mismatch()
    {
        // Here the bytes exist but are wrong, which is what a drifted parse actually
        // looks like: it lands on data rather than past the end.
        using var builder = new ModBuilder();
        byte[] data = builder.AddMesh(Vector3.Zero, "T", Triangle, TriangleIndices).Build();
        "NOPE"u8.CopyTo(data.AsSpan(data.Length - 4));

        var ex = Assert.Throws<FormatParseException>(() => ModFile.Parse(data, "DRIFTED.MOD"));

        Assert.Equal("GK3R1002", ex.Diagnostic.Code);
        Assert.Equal("XDOM", ex.Diagnostic.Expected);
        Assert.Equal("NOPE", ex.Diagnostic.Actual);
    }

    [Fact]
    public void Something_that_is_not_a_model_is_refused()
    {
        var ex = Assert.Throws<FormatParseException>(() => ModFile.Parse("RIFFxxxx"u8, "X.MOD"));
        Assert.Equal("GK3R1002", ex.Diagnostic.Code);
    }

    [Fact]
    public void The_billboard_flag_is_read()
    {
        using var billboard = new ModBuilder(flags: 2);
        using var plain = new ModBuilder(flags: 0);

        Assert.True(ModFile.Parse(
            billboard.AddMesh(Vector3.Zero, "T", Triangle, TriangleIndices).Build()).IsBillboard);
        Assert.False(ModFile.Parse(
            plain.AddMesh(Vector3.Zero, "T", Triangle, TriangleIndices).Build()).IsBillboard);
    }

    [Fact]
    public void Exported_glb_is_structurally_valid()
    {
        using var builder = new ModBuilder();
        ModFile model = ModFile.Parse(builder
            .AddMesh(new Vector3(5, 0, 0), "GABBODY", Triangle, TriangleIndices).Build());

        byte[] glb = GlbWriter.Encode(model);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(glb);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8));

        Assert.Equal(0x46546C67u, magic); // "glTF"
        Assert.Equal(2u, version);
        Assert.Equal((uint)glb.Length, length);
    }

    [Fact]
    public void Exported_glb_describes_the_geometry_it_was_given()
    {
        using var builder = new ModBuilder();
        ModFile model = ModFile.Parse(builder
            .AddMesh(new Vector3(5, 0, 0), "GABBODY", Triangle, TriangleIndices).Build());

        JsonElement gltf = ReadJsonChunk(GlbWriter.Encode(model));

        JsonElement primitive = gltf.GetProperty("meshes")[0].GetProperty("primitives")[0];
        JsonElement accessors = gltf.GetProperty("accessors");

        int position = primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
        int indices = primitive.GetProperty("indices").GetInt32();

        Assert.Equal(3, accessors[position].GetProperty("count").GetInt32());
        Assert.Equal(3, accessors[indices].GetProperty("count").GetInt32());

        // The specification requires bounds on POSITION accessors.
        Assert.Equal([0.0, 0.0, 0.0], accessors[position].GetProperty("min").EnumerateArray().Select(v => v.GetDouble()));
        Assert.Equal([1.0, 1.0, 0.0], accessors[position].GetProperty("max").EnumerateArray().Select(v => v.GetDouble()));
    }

    [Fact]
    public void Materials_reference_the_converted_texture()
    {
        using var builder = new ModBuilder();
        ModFile model = ModFile.Parse(builder
            .AddMesh(Vector3.Zero, "GABBODY", Triangle, TriangleIndices).Build());

        JsonElement gltf = ReadJsonChunk(GlbWriter.Encode(model));

        Assert.Equal("../textures/GABBODY.PNG", gltf.GetProperty("images")[0].GetProperty("uri").GetString());
        Assert.True(gltf.GetProperty("materials")[0].GetProperty("doubleSided").GetBoolean());
    }

    [Fact]
    public void A_model_without_a_texture_produces_no_image()
    {
        using var builder = new ModBuilder();
        ModFile model = ModFile.Parse(builder
            .AddMesh(Vector3.Zero, string.Empty, Triangle, TriangleIndices).Build());

        JsonElement gltf = ReadJsonChunk(GlbWriter.Encode(model));

        Assert.False(gltf.TryGetProperty("images", out _));
        Assert.Equal(1, gltf.GetProperty("materials").GetArrayLength());
    }

    [Fact]
    public void Buffer_views_stay_four_byte_aligned()
    {
        // Misaligned views are invalid glTF and some viewers reject the file outright.
        using var builder = new ModBuilder();
        ModFile model = ModFile.Parse(builder
            .AddMesh(Vector3.Zero, "A", Triangle, TriangleIndices)
            .AddMesh(Vector3.One, "B", Triangle, TriangleIndices).Build());

        JsonElement gltf = ReadJsonChunk(GlbWriter.Encode(model));

        foreach (JsonElement view in gltf.GetProperty("bufferViews").EnumerateArray())
        {
            Assert.Equal(0, view.GetProperty("byteOffset").GetInt32() % 4);
        }
    }

    private static JsonElement ReadJsonChunk(byte[] glb)
    {
        int offset = 12;
        while (offset < glb.Length)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(offset));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(offset + 4));

            if (type == 0x4E4F534A)
            {
                return JsonDocument.Parse(glb.AsSpan(offset + 8, length).ToArray()).RootElement;
            }

            offset += 8 + length;
        }

        throw new InvalidOperationException("no JSON chunk");
    }
}
