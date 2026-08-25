using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for reading geometry back out of glTF binary.
/// </summary>
/// <remarks>
/// The property that matters most is the round trip. The toolchain converts a model out to
/// glTF, somebody improves it in a DCC, and the engine reads the result: if a position or
/// a texture coordinate moves on the way through, an enhanced model lands somewhere other
/// than the original it replaces, and that shows up as a tree standing beside its stump
/// rather than as a parse error.
/// </remarks>
public sealed class GlbReaderTests
{
    private static readonly Vector3[] Triangle =
        [new(0, 0, 0), new(10, 0, 0), new(0, 20, 0)];

    private static ModFile Model(string texture = "TRUNK01", Matrix4x4? at = null) =>
        ModFile.FromMeshes("SRC",
        [
            new ModMesh
            {
                MeshToLocal = at ?? Matrix4x4.Identity,
                BoundsMin = new Vector3(0, 0, 0),
                BoundsMax = new Vector3(10, 20, 0),
                Submeshes =
                [
                    new ModSubmesh
                    {
                        TextureName = texture,
                        Color = (255, 255, 255),
                        Positions = Triangle,
                        Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                        TexCoords = [new(0, 0), new(1, 0), new(0, 1)],
                        Indices = [0, 1, 2],
                    },
                ],
            },
        ]);

    [Fact]
    public void A_model_survives_the_trip_out_to_gltf_and_back()
    {
        ModFile read = GlbReader.Parse(GlbWriter.Encode(Model()), "SRC.glb");

        Assert.Single(read.Meshes);
        Assert.Equal(1, read.TriangleCount);

        ModSubmesh submesh = read.Meshes[0].Submeshes[0];
        Assert.Equal(Triangle, submesh.Positions);
        Assert.Equal([0, 1, 2], submesh.Indices);
        Assert.Equal(new Vector2(1, 0), submesh.TexCoords[1]);
    }

    [Fact]
    public void A_material_names_the_texture_the_geometry_wants()
    {
        // Every surface in this game is addressed by a texture name that the archives or
        // the enhanced set resolve, so that name is the whole of what a material is for.
        ModFile read = GlbReader.Parse(GlbWriter.Encode(Model("GABBODY")), "SRC.glb");

        Assert.Equal("GABBODY", read.Meshes[0].Submeshes[0].TextureName);
    }

    [Fact]
    public void A_nodes_transform_becomes_the_meshs_own()
    {
        // Rather than being baked into the vertices, because that is how a MOD stores a
        // model and because the parts a scene can pose have to stay separately posable.
        var placed = Matrix4x4.CreateTranslation(3, 4, 5);
        ModFile read = GlbReader.Parse(GlbWriter.Encode(Model(at: placed)), "SRC.glb");

        Assert.Equal(placed.Translation, read.Meshes[0].MeshToLocal.Translation);
        Assert.Equal(Triangle[0], read.Meshes[0].Submeshes[0].Positions[0]);
    }

    [Fact]
    public void Bounds_are_measured_from_the_geometry_that_arrived()
    {
        ModFile read = GlbReader.Parse(GlbWriter.Encode(Model()), "SRC.glb");

        Assert.Equal(new Vector3(0, 0, 0), read.Meshes[0].BoundsMin);
        Assert.Equal(new Vector3(10, 20, 0), read.Meshes[0].BoundsMax);
    }

    [Fact]
    public void Something_that_is_not_a_glb_is_refused()
    {
        var ex = Assert.Throws<FormatParseException>(
            () => GlbReader.Parse("RIFFxxxxWAVE"u8, "X.glb"));

        Assert.Equal("GK3R1121", ex.Diagnostic.Code);
    }

    [Fact]
    public void A_document_whose_geometry_is_in_another_file_is_refused()
    {
        // Enhanced content has to be one addressable thing: a pack cannot carry half a
        // model and a path to the rest of it.
        byte[] glb = Rewrite(GlbWriter.Encode(Model()), json =>
            json["buffers"]!.AsArray()[0]!["uri"] = "geometry.bin");

        var ex = Assert.Throws<FormatParseException>(() => GlbReader.Parse(glb, "X.glb"));
        Assert.Equal("a reference to another file", ex.Diagnostic.Actual);
    }

    [Fact]
    public void A_model_that_will_not_read_costs_that_model_and_nothing_else()
    {
        // The caller loading optional content wants a warning and a null, not an
        // exception: one bad file in a set of trees should cost that tree and leave the
        // wood standing.
        var diagnostics = new DiagnosticBag();

        Assert.Null(GlbReader.TryParse("not a model at all"u8, "X.glb", diagnostics));
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1120");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostics.Items[0].Severity);
    }

    [Fact]
    public void A_primitive_that_is_not_triangles_is_left_out()
    {
        // Drawing a line strip as triangles looks like broken geometry, which is a worse
        // thing to debug than geometry that is simply absent.
        byte[] glb = Rewrite(GlbWriter.Encode(Model()), json =>
            json["meshes"]!.AsArray()[0]!["primitives"]!.AsArray()[0]!["mode"] = 3);

        Assert.Empty(GlbReader.Parse(glb, "X.glb").Meshes);
    }

    /// <summary>Reassembles a GLB after editing its JSON chunk.</summary>
    private static byte[] Rewrite(byte[] glb, Action<JsonNode> edit)
    {
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        JsonNode json = JsonNode.Parse(
            Encoding.UTF8.GetString(glb, 20, length).TrimEnd(' ', '\0'))!;

        edit(json);

        byte[] replaced = Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions()));
        int padded = (replaced.Length + 3) & ~3;

        var built = new MemoryStream();
        var writer = new BinaryWriter(built);
        writer.Write(glb.AsSpan(0, 12));      // header, patched below
        writer.Write((uint)padded);
        writer.Write(0x4E4F534Au);
        writer.Write(replaced);
        writer.Write(Enumerable.Repeat((byte)' ', padded - replaced.Length).ToArray());
        writer.Write(glb.AsSpan(20 + length)); // every chunk after the JSON, unchanged

        byte[] out_ = built.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(out_.AsSpan(8), (uint)out_.Length);
        return out_;
    }
}
