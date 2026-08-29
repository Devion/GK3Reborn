// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for cutting a room into objects and putting them back.
/// </summary>
/// <remarks>
/// The property everything here rests on is that a triangle can be handed to a modelling
/// tool and come back still knowing which surface of which room it belongs to. Lose that
/// and the geometry is fine and the lighting is somebody else's: every surface carries its
/// own lightmap placement and its own flags, and there is nothing in a returned triangle's
/// position to say which.
/// </remarks>
public sealed class SceneObjectGlbTests
{
    /// <summary>A room of quads, each its own surface, grouped into objects as asked.</summary>
    private static BspFile Room(params (int Object, Vector3 Corner, Vector3 Across, Vector3 Up)[] quads)
    {
        List<Vector3> vertices = [];
        List<Vector2> texCoords = [];
        List<ushort> indices = [];
        List<BspSurface> surfaces = [];
        List<BspPolygon> polygons = [];

        foreach ((int owner, Vector3 corner, Vector3 across, Vector3 up) in quads)
        {
            int at = vertices.Count;
            int offset = indices.Count;

            vertices.AddRange([corner, corner + up, corner + across + up, corner + across]);
            texCoords.AddRange([new(0, 0), new(0, 1), new(1, 1), new(1, 0)]);
            indices.AddRange([(ushort)at, (ushort)(at + 1), (ushort)(at + 2), (ushort)(at + 3)]);

            surfaces.Add(new BspSurface
            {
                ObjectIndex = owner,
                TextureName = $"TEX{owner}",

                // Deliberately not the identity, so a surface that ends up with somebody
                // else's mapping produces different numbers rather than the same ones.
                LightmapUvOffset = new Vector2(surfaces.Count, 0),
                LightmapUvScale = new Vector2(0.5f, 0.25f),
                Flags = 0,
            });

            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = offset,
                VertexIndexCount = 4,
                SurfaceIndex = surfaces.Count - 1,
            });
        }

        int objects = quads.Length == 0 ? 0 : quads.Max(q => q.Object) + 1;

        return BspFile.FromParts(
            "TESTROOM",
            [.. Enumerable.Range(0, objects).Select(i => $"object{i}")],
            surfaces,
            polygons,
            [.. vertices],
            [.. texCoords],
            [.. indices]);
    }

    /// <summary>A box: six quads round a unit cube, all one object.</summary>
    private static BspFile Box(int owner = 0) =>
        Room(
            (owner, new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0)),
            (owner, new Vector3(10, 0, 0), new Vector3(0, 0, 10), new Vector3(0, 10, 0)),
            (owner, new Vector3(10, 0, 10), new Vector3(-10, 0, 0), new Vector3(0, 10, 0)),
            (owner, new Vector3(0, 0, 10), new Vector3(0, 0, -10), new Vector3(0, 10, 0)),
            (owner, new Vector3(0, 10, 0), new Vector3(10, 0, 0), new Vector3(0, 0, 10)),
            (owner, new Vector3(0, 0, 10), new Vector3(10, 0, 0), new Vector3(0, 0, -10)));

    [Fact]
    public void An_object_comes_back_with_every_triangle_still_naming_its_surface()
    {
        BspFile room = Box();
        byte[]? glb = SceneObjectGlb.Encode(room, 0);

        Assert.NotNull(glb);

        SceneOverlay read = SceneObjectGlb.Read(glb, room, "box.glb");

        SceneObjectGeometry piece = Assert.Single(read.Objects);
        Assert.Equal(0, piece.ObjectIndex);
        Assert.Equal("object0", piece.Name);

        // Six quads fanned is twelve triangles, and each names one of the six surfaces.
        Assert.Equal(12, piece.Triangles.Count);
        Assert.Equal([0, 1, 2, 3, 4, 5], [.. piece.Surfaces.Order()]);

        foreach (SceneTriangle triangle in piece.Triangles)
        {
            Assert.InRange(triangle.Surface, 0, 5);
        }
    }

    [Fact]
    public void The_objects_are_recovered_from_the_surfaces_rather_than_from_the_nodes()
    {
        // Two objects in one file, which is what a modelling tool produces when somebody
        // joins two meshes. Nothing names the objects; the surfaces do.
        BspFile room = Room(
            (0, new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0)),
            (1, new Vector3(0, 0, 20), new Vector3(10, 0, 0), new Vector3(0, 10, 0)));

        byte[] glb = SceneObjectGlb.EncodeRoom(room);
        SceneOverlay read = SceneObjectGlb.Read(glb, room, "room.glb");

        Assert.Equal(2, read.Objects.Count);
        Assert.Equal(["object0", "object1"], [.. read.Objects.Select(o => o.Name)]);
        Assert.All(read.Objects, o => Assert.Single(o.Surfaces));
    }

    [Fact]
    public void Composing_what_was_extracted_changes_nothing()
    {
        BspFile room = Box();

        SceneOverlay once = SceneObjectGlb.Read(SceneObjectGlb.EncodeRoom(room), room, "a.glb");
        SceneOverlay twice = SceneObjectGlb.Read(
            SceneObjectGlb.EncodeOverlay("TESTROOM", room, once.Objects), room, "b.glb");

        Assert.Equal(once.TriangleCount, twice.TriangleCount);
        Assert.Equal(
            [.. once.Objects.SelectMany(o => o.Triangles).Select(t => t.Surface).Order()],
            [.. twice.Objects.SelectMany(o => o.Triangles).Select(t => t.Surface).Order()]);
    }

    [Fact]
    public void A_triangle_that_names_no_surface_is_dropped_and_reported()
    {
        BspFile room = Box();

        // What a modelling tool produces when somebody adds geometry by hand and gives it
        // a material of their own. It cannot be placed, because nothing says which
        // lightmap or which flags it should carry.
        var stray = ModFile.FromMeshes("stray", [new ModMesh
        {
            MeshToLocal = Matrix4x4.Identity,
            BoundsMin = Vector3.Zero,
            BoundsMax = Vector3.Zero,
            Submeshes = [new ModSubmesh
            {
                TextureName = "MYOWNMATERIAL",
                Color = (255, 255, 255),
                Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                TexCoords = [Vector2.Zero, Vector2.Zero, Vector2.Zero],
                Indices = [0, 1, 2],
            }],
        }]);

        var diagnostics = new DiagnosticBag();
        SceneOverlay read = SceneObjectGlb.From(stray, room, "stray.glb", diagnostics);

        Assert.True(read.IsEmpty);
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1140");
    }

    [Theory]
    [InlineData("RC1WOOD#00042", true, 42)]
    [InlineData("RC1WOOD#42", true, 42)]

    // A tool that had to rename a duplicated datablock appends its own suffix, and the
    // name has to survive that: the alternative is a whole object silently unplaceable.
    [InlineData("RC1WOOD#00042.001", true, 42)]
    [InlineData("RC1WOOD", false, -1)]
    [InlineData("RC1WOOD#", false, -1)]
    [InlineData("RC1WOOD#nonsense", false, -1)]
    [InlineData("RC1WOOD#42.tail", false, -1)]
    [InlineData(null, false, -1)]
    public void A_surface_index_is_read_out_of_a_material_name(
        string? material, bool expected, int surface)
    {
        Assert.Equal(expected, SceneObjectGlb.TrySurfaceOf(material, out int read));
        Assert.Equal(surface, read);
    }

    [Fact]
    public void A_box_keeps_its_corners_and_a_curve_does_not()
    {
        // Every face of a box meets its neighbours at ninety degrees, so no vertex may
        // carry a normal that has been averaged across two of them: a rounded corner in
        // the shading with a square corner in the silhouette is what soft plastic looks
        // like.
        BspFile box = Box();
        SceneOverlay read = SceneObjectGlb.Read(SceneObjectGlb.EncodeRoom(box), box, "box.glb");

        foreach (SceneTriangle triangle in read.Objects.SelectMany(o => o.Triangles))
        {
            Vector3 face = Vector3.Normalize(Vector3.Cross(
                triangle.B.Position - triangle.A.Position,
                triangle.C.Position - triangle.A.Position));

            foreach (SceneVertex corner in
                     (ReadOnlySpan<SceneVertex>)[triangle.A, triangle.B, triangle.C])
            {
                Assert.True(
                    Vector3.Dot(face, corner.Normal) > 0.999f,
                    $"a box corner shaded {Vector3.Dot(face, corner.Normal)} away from its face");
            }
        }

        // A fan of quads turning ten degrees at a time is a tessellated curve, and its
        // shared edges must smooth: that is the difference between a lamp shade and a
        // twelve-sided prism.
        List<(int, Vector3, Vector3, Vector3)> ring = [];

        for (int i = 0; i < 12; i++)
        {
            float a = i * MathF.Tau / 12f;
            float b = (i + 1) * MathF.Tau / 12f;

            Vector3 from = new(MathF.Cos(a) * 10f, 0, MathF.Sin(a) * 10f);
            Vector3 to = new(MathF.Cos(b) * 10f, 0, MathF.Sin(b) * 10f);

            ring.Add((0, from, to - from, new Vector3(0, 10, 0)));
        }

        BspFile drum = Room([.. ring]);
        SceneOverlay curved = SceneObjectGlb.Read(
            SceneObjectGlb.EncodeRoom(drum), drum, "drum.glb");

        int smoothed = 0;

        foreach (SceneTriangle triangle in curved.Objects.SelectMany(o => o.Triangles))
        {
            Vector3 face = Vector3.Normalize(Vector3.Cross(
                triangle.B.Position - triangle.A.Position,
                triangle.C.Position - triangle.A.Position));

            foreach (SceneVertex corner in
                     (ReadOnlySpan<SceneVertex>)[triangle.A, triangle.B, triangle.C])
            {
                if (Vector3.Dot(face, corner.Normal) < 0.9995f)
                {
                    smoothed++;
                }
            }
        }

        Assert.True(smoothed > 0, "a twelve-sided drum came back shaded as flat panels");
    }

    [Fact]
    public void One_shape_serves_two_rooms_that_number_their_surfaces_differently()
    {
        // The case the whole shipped set is deduplicated on: a location has a geometry
        // file per timeblock, holding the same furniture at the same coordinates with a
        // different surface numbering, and 604 of the corpus's 2,710 improved objects are
        // a second copy of one of the others.
        BspFile morning = Box();

        // The same box, but as object 1 with its surfaces starting at 1 rather than 0.
        BspFile evening = Room(
            [
                (0, new Vector3(-40, 0, 0), new Vector3(5, 0, 0), new Vector3(0, 5, 0)),
                (1, new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0)),
                (1, new Vector3(10, 0, 0), new Vector3(0, 0, 10), new Vector3(0, 10, 0)),
                (1, new Vector3(10, 0, 10), new Vector3(-10, 0, 0), new Vector3(0, 10, 0)),
                (1, new Vector3(0, 0, 10), new Vector3(0, 0, -10), new Vector3(0, 10, 0)),
                (1, new Vector3(0, 10, 0), new Vector3(10, 0, 0), new Vector3(0, 0, 10)),
                (1, new Vector3(0, 0, 10), new Vector3(10, 0, 0), new Vector3(0, 0, -10)),
            ]);

        SceneObjectGeometry first = Assert.Single(
            SceneObjectGlb.Read(SceneObjectGlb.EncodeRoom(morning), morning, "a.glb").Objects);

        SceneObjectGeometry second = SceneObjectGlb
            .Read(SceneObjectGlb.EncodeRoom(evening), evening, "b.glb").Objects
            .Single(o => o.ObjectIndex == 1);

        // Different surface numbers, and the same shape: the numbering lives in the
        // placement, not in the geometry.
        Assert.Equal([0, 1, 2, 3, 4, 5], [.. first.Surfaces.Order()]);
        Assert.Equal([1, 2, 3, 4, 5, 6], [.. second.Surfaces.Order()]);
        Assert.Equal(SceneObjectGlb.ShapeOf(first), SceneObjectGlb.ShapeOf(second));

        // And the one shipped file draws correctly in the room it was not cut from.
        byte[] shape = SceneObjectGlb.EncodeShape("box", first, out IReadOnlyList<int> slots);

        Assert.Equal([0, 1, 2, 3, 4, 5], [.. slots]);

        SceneObjectGeometry? placed = SceneObjectGlb.Place(
            GlbReader.Parse(shape, "box.glb"), evening, 1, [1, 2, 3, 4, 5, 6]);

        Assert.NotNull(placed);
        Assert.Equal(second.Triangles.Count, placed.Triangles.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6], [.. placed.Surfaces.Order()]);
    }

    [Fact]
    public void A_shape_is_refused_where_its_slots_do_not_belong_to_the_object()
    {
        // The failure that would otherwise be silent and catastrophic: a manifest that
        // has drifted from the room puts every one of a shape's slots on somebody else's
        // surface, and the room draws perfectly under somebody else's lighting.
        BspFile room = Box();

        SceneObjectGeometry piece = Assert.Single(
            SceneObjectGlb.Read(SceneObjectGlb.EncodeRoom(room), room, "a.glb").Objects);

        byte[] shape = SceneObjectGlb.EncodeShape("box", piece, out _);
        ModFile read = GlbReader.Parse(shape, "box.glb");

        Assert.Null(SceneObjectGlb.Place(read, room, 0, [0, 1, 2]));
        Assert.Null(SceneObjectGlb.Place(read, room, 0, [0, 1, 2, 3, 4, 99]));
        Assert.NotNull(SceneObjectGlb.Place(read, room, 0, [0, 1, 2, 3, 4, 5]));
    }

    [Fact]
    public void A_shape_is_named_by_its_geometry_and_not_by_the_bytes_it_was_written_as()
    {
        // Blender writes two meshes for byte-identical input that agree on every position
        // and differ in the last bit of an interpolated texture coordinate. Reordering
        // the triangles and nudging a coordinate below the quantum must not rename the
        // shape, and moving a vertex a visible distance must.
        BspFile room = Box();

        SceneObjectGeometry piece = Assert.Single(
            SceneObjectGlb.Read(SceneObjectGlb.EncodeRoom(room), room, "a.glb").Objects);

        SceneTriangle[] shuffled = [.. piece.Triangles.Reverse()];

        var reordered = piece with { Triangles = shuffled };
        Assert.Equal(SceneObjectGlb.ShapeOf(piece), SceneObjectGlb.ShapeOf(reordered));

        SceneTriangle first = piece.Triangles[0];

        var nudged = piece with
        {
            Triangles = [
                first with { A = first.A with { TexCoord = first.A.TexCoord + new Vector2(1e-7f, 0) } },
                .. piece.Triangles.Skip(1)],
        };

        Assert.Equal(SceneObjectGlb.ShapeOf(piece), SceneObjectGlb.ShapeOf(nudged));

        var moved = piece with
        {
            Triangles = [
                first with { A = first.A with { Position = first.A.Position + new Vector3(1f, 0, 0) } },
                .. piece.Triangles.Skip(1)],
        };

        Assert.NotEqual(SceneObjectGlb.ShapeOf(piece), SceneObjectGlb.ShapeOf(moved));
    }

    [Fact]
    public void A_material_name_carries_the_surface_and_the_picture_separately()
    {
        // Forty surfaces sharing one panelling texture have to be forty materials, or
        // there is nowhere to write which lightmap each of them uses. They must still
        // share one image between them, or a room's glTF holds two thousand copies of a
        // reference to the same file.
        BspFile room = Room(
            (0, new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0)),
            (0, new Vector3(0, 0, 20), new Vector3(10, 0, 0), new Vector3(0, 10, 0)));

        string text = System.Text.Encoding.UTF8.GetString(SceneObjectGlb.EncodeRoom(room));

        Assert.Contains("TEX0#00000", text, StringComparison.Ordinal);
        Assert.Contains("TEX0#00001", text, StringComparison.Ordinal);

        // One image for the two of them.
        Assert.Equal(1, text.Split("TEX0.PNG", StringSplitOptions.None).Length - 1);
    }
}
