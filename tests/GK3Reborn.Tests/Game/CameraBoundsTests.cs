using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Game.Navigation;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the shell that keeps the camera inside the room.
/// </summary>
/// <remarks>
/// The fixture is a closed box two hundred units across with its faces turned inward, which
/// is what a camera-bounds model is: a room-shaped bag the camera lives in. The questions
/// worth asking of it are the ones that decide whether a player can get the view outside —
/// walking into a wall, walking into the seam between two of its triangles, and covering
/// more ground in one frame than the wall is thick.
/// </remarks>
public sealed class CameraBoundsTests
{
    /// <summary>Half the width of the fixture box, in world units.</summary>
    private const float Half = 100f;

    /// <summary>A closed box centred on the origin, every face turned inward.</summary>
    /// <param name="half">Half its width, on every axis.</param>
    /// <remarks>
    /// Turned inward because that is the side the camera is on, and it is the side the
    /// solver refuses to let it leave. Each face is wound and then checked against the
    /// centre rather than being written out by hand, because a box with one face wound the
    /// wrong way is a box with one wall missing and nothing about it looks wrong.
    /// </remarks>
    private static CameraBounds Box(float half = Half) =>
        new([Shell(Corners(half))]);

    /// <summary>A model of loose triangles, the way a bounds model arrives.</summary>
    private static ModFile Shell(Vector3[] positions, Matrix4x4? meshToLocal = null)
    {
        ushort[] indices = new ushort[positions.Length];

        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (ushort)i;
        }

        var submesh = new ModSubmesh
        {
            TextureName = "invisible",
            Color = (255, 255, 255),
            Positions = positions,
            Normals = new Vector3[positions.Length],
            TexCoords = new Vector2[positions.Length],
            Indices = indices,
        };

        var mesh = new ModMesh
        {
            MeshToLocal = meshToLocal ?? Matrix4x4.Identity,
            BoundsMin = new Vector3(-Half, -Half, -Half),
            BoundsMax = new Vector3(Half, Half, Half),
            Submeshes = [submesh],
        };

        return ModFile.FromMeshes("bounds", [mesh]);
    }

    [Fact]
    public void A_step_that_stays_inside_is_the_step_that_was_asked_for()
    {
        Vector3 where = Box().Resolve(Vector3.Zero, new Vector3(10, 0, 0));

        Assert.Equal(10f, where.X, 0.01f);
        Assert.Equal(0f, where.Y, 0.01f);
        Assert.Equal(0f, where.Z, 0.01f);
    }

    [Fact]
    public void A_step_into_a_wall_stops_a_camera_radius_short_of_it()
    {
        // Straight at the +X wall from the middle, asking for far more than the room holds.
        Vector3 where = Box().Resolve(Vector3.Zero, new Vector3(500, 0, 0));

        Assert.Equal(Half - CameraBounds.Radius, where.X, 0.5f);
    }

    [Fact]
    public void A_step_along_a_wall_slides_rather_than_sticking()
    {
        // Into the +X wall and towards +Z at once. The wall takes the X and the Z survives,
        // which is the difference between a camera that runs along a wall and one that is
        // glued to the first one it touches.
        Vector3 where = Box().Resolve(new Vector3(50, 0, 0), new Vector3(100, 0, 40));

        Assert.Equal(Half - CameraBounds.Radius, where.X, 1f);
        Assert.True(where.Z > 30f, $"expected to slide along the wall, got z={where.Z}");
    }

    [Fact]
    public void A_step_at_the_seam_between_two_triangles_is_still_stopped()
    {
        // The box's faces are two triangles each and their shared edge runs corner to
        // corner. Aimed exactly along it, a solver that only tests triangle interiors finds
        // the point of arrival outside both halves and lets the camera through.
        Vector3 where = Box().Resolve(new Vector3(0, 0, 0), new Vector3(0, 0, 500));

        Assert.True(where.Z <= Half, $"the camera left the box, ending at z={where.Z}");
        Assert.Equal(Half - CameraBounds.Radius, where.Z, 1f);
    }

    [Fact]
    public void A_step_that_clips_the_rim_of_a_surface_is_stopped_by_it()
    {
        // One triangle, and a camera aimed past its edge — through where its plane is but
        // outside its outline. The camera has width, so its side still catches the rim, and
        // a solver that only asked whether the centre line landed within the outline would
        // let it through. That is the shape of every gap a shell leaks through.
        Vector3[] triangle =
        [
            new Vector3(0, 0, 0),
            new Vector3(100, 0, 0),
            new Vector3(0, 100, 0),
        ];

        // Wound so the normal faces +Z, which is the side the camera is on.
        var edge = new CameraBounds([Shell(triangle)]);

        // Five units beyond the edge that runs up the Y axis: outside the triangle, well
        // within a camera's sixteen-unit radius of it.
        Vector3 where = edge.Resolve(new Vector3(-5, 50, 60), new Vector3(0, 0, -120));

        Assert.True(where.Z > 0f, $"the camera passed through the surface, ending at z={where.Z}");
    }

    [Fact]
    public void A_step_longer_than_the_room_does_not_pass_through_it()
    {
        // A single frame's worth of movement at an absurd speed. Nothing may tunnel: the
        // sweep is against the whole step rather than against where it ends.
        Vector3 where = Box().Resolve(new Vector3(-90, 0, 0), new Vector3(100_000, 0, 0));

        Assert.True(where.X < Half, $"the camera tunnelled out, ending at x={where.X}");
    }

    [Fact]
    public void A_camera_already_outside_can_come_back_in()
    {
        // Scenes place their own viewpoints and one of them may sit outside the shell. A
        // solver that refused every crossing would leave that room unusable, so only the
        // way out is barred.
        Vector3 where = Box().Resolve(new Vector3(300, 0, 0), new Vector3(-100, 0, 0));

        Assert.Equal(200f, where.X, 1f);
    }

    [Fact]
    public void A_scene_with_no_shell_lets_the_camera_go_anywhere()
    {
        var nothing = new CameraBounds([]);

        Assert.True(nothing.IsEmpty);
        Assert.Equal(
            new Vector3(9_000, 0, 0),
            nothing.Resolve(Vector3.Zero, new Vector3(9_000, 0, 0)));
    }

    [Fact]
    public void The_mesh_transform_is_where_the_shell_stands()
    {
        // A bounds model sits at the world origin, but its meshes carry their own place
        // within it. Ignoring that puts a room's shell somewhere the room is not, which
        // shows as a camera stopped by nothing in mid-air.
        var moved = new CameraBounds([
            Shell(Corners(), Matrix4x4.CreateTranslation(1_000, 0, 0)),
        ]);

        // Nothing near the origin any more.
        Assert.Equal(
            new Vector3(500, 0, 0),
            moved.Resolve(Vector3.Zero, new Vector3(500, 0, 0)));

        // And the wall is where the transform put it.
        Assert.Equal(1_100f - CameraBounds.Radius, moved.Resolve(
            new Vector3(1_000, 0, 0), new Vector3(500, 0, 0)).X, 1f);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, true)]
    [InlineData(99f, -99f, 99f, true)]
    [InlineData(101f, 0f, 0f, false)]
    [InlineData(0f, 0f, -400f, false)]
    [InlineData(0f, 5_000f, 0f, false)]
    public void A_point_is_inside_the_shell_or_it_is_not(float x, float y, float z, bool inside) =>
        Assert.Equal(inside, Box().Contains(new Vector3(x, y, z)));

    [Fact]
    public void Nothing_is_inside_a_scene_with_no_shell() =>
        Assert.False(new CameraBounds([]).Contains(Vector3.Zero));

    [Fact]
    public void The_triangles_of_every_shell_a_scene_names_are_counted_together()
    {
        var both = new CameraBounds([Shell(Corners()), Shell(Corners())]);

        Assert.Equal(24, both.TriangleCount);
    }

    /// <summary>The box fixture's triangles, as loose positions.</summary>
    private static Vector3[] Corners(float half = Half)
    {
        List<Vector3> positions = [];

        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Add(a, b, c);
            Add(a, c, d);
        }

        void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), -(a + b + c) / 3f) < 0f)
            {
                (b, c) = (c, b);
            }

            positions.Add(a);
            positions.Add(b);
            positions.Add(c);
        }

        Vector3 Corner(int x, int y, int z) => new(x * half, y * half, z * half);

        Face(Corner(-1, -1, -1), Corner(-1, 1, -1), Corner(1, 1, -1), Corner(1, -1, -1));
        Face(Corner(-1, -1, 1), Corner(-1, 1, 1), Corner(1, 1, 1), Corner(1, -1, 1));
        Face(Corner(-1, -1, -1), Corner(-1, 1, -1), Corner(-1, 1, 1), Corner(-1, -1, 1));
        Face(Corner(1, -1, -1), Corner(1, 1, -1), Corner(1, 1, 1), Corner(1, -1, 1));
        Face(Corner(-1, -1, -1), Corner(1, -1, -1), Corner(1, -1, 1), Corner(-1, -1, 1));
        Face(Corner(-1, 1, -1), Corner(1, 1, -1), Corner(1, 1, 1), Corner(-1, 1, 1));

        return [.. positions];
    }
}
