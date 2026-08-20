using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for what is under a point on the screen.
/// </summary>
/// <remarks>
/// The room here is three walls facing the camera, one behind the other along +Z, so that
/// "which one did the ray reach first" is the question every test is really asking. Their
/// meaning comes from the initialisation file rather than the geometry, which is the part
/// worth pinning: the same slab is a door, a hit test or nothing at all depending on a
/// line of text somewhere else.
/// </remarks>
public sealed class ScenePickerTests
{
    /// <summary>A camera at the origin looking down +Z, the way the fixtures are built.</summary>
    private static Camera Looking() =>
        new() { Position = Vector3.Zero, Target = new Vector3(0, 0, 1), Up = Vector3.UnitY };

    /// <summary>
    /// A room of upright slabs, each its own object, each facing back towards the camera.
    /// </summary>
    /// <param name="walls">Object name and the Z it stands at, near to far.</param>
    private static BspFile Room(params (string Name, float Z)[] walls)
    {
        List<string> names = [];
        List<BspSurface> surfaces = [];
        List<BspPolygon> polygons = [];
        List<Vector3> vertices = [];
        List<ushort> indices = [];

        foreach ((string name, float z) in walls)
        {
            int at = vertices.Count;

            // Wound so that the outward normal, cross(b - a, c - a), points back at the
            // camera: a wall the ray meets head on rather than from behind.
            vertices.Add(new Vector3(-50, -50, z));
            vertices.Add(new Vector3(-50, 50, z));
            vertices.Add(new Vector3(50, 50, z));
            vertices.Add(new Vector3(50, -50, z));

            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = indices.Count,
                VertexIndexCount = 4,
                SurfaceIndex = surfaces.Count,
            });

            indices.AddRange([(ushort)at, (ushort)(at + 1), (ushort)(at + 2), (ushort)(at + 3)]);

            surfaces.Add(new BspSurface
            {
                ObjectIndex = names.Count,
                TextureName = "wallpaper",
                LightmapUvOffset = Vector2.Zero,
                LightmapUvScale = Vector2.One,
                Flags = 0,
            });

            names.Add(name);
        }

        return BspFile.FromParts(
            "test",
            names,
            surfaces,
            polygons,
            [.. vertices],
            new Vector2[vertices.Count],
            [.. indices]);
    }

    /// <summary>A scene over that geometry, described by the given <c>[MODELS]</c> lines.</summary>
    private static LoadedScene Scene(BspFile room, string models, params PlacedModel[] placed)
    {
        SceneInitFile init = SceneInitFile.Parse($"[MODELS]\n{models}\n", "TEST.SIF");

        return new LoadedScene(
            "TEST",
            new SceneDefinition(init),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: placed.Length,
            Walkable: null,
            Geometry: room,
            Placed: placed);
    }

    /// <summary>A one-triangle model standing at a distance, facing the camera.</summary>
    private static PlacedModel Model(string name, string? noun, float z, PlacedModelKind kind)
    {
        var submesh = new ModSubmesh
        {
            TextureName = "skin",
            Color = (255, 255, 255),
            Positions = [new Vector3(-10, -10, 0), new Vector3(0, 20, 0), new Vector3(10, -10, 0)],
            Normals = [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ],
            TexCoords = new Vector2[3],
            Indices = [0, 1, 2],
        };

        var mesh = new ModMesh
        {
            MeshToLocal = Matrix4x4.Identity,
            BoundsMin = new Vector3(-10, -10, 0),
            BoundsMax = new Vector3(10, 20, 0),
            Submeshes = [submesh],
        };

        return new PlacedModel(
            name,
            noun,
            Verb: null,
            ModFile.FromMeshes(name, [mesh]),
            Matrix4x4.CreateTranslation(0, 0, z),
            kind);
    }

    /// <summary>The pick straight ahead, through the middle pixel of a 65x65 image.</summary>
    /// <remarks>
    /// Odd, so that one pixel really is the middle. An even image has no centre pixel and
    /// the nearest one looks a fraction of a degree off axis, which is right but makes
    /// every distance in these tests a hair longer than the wall is far.
    /// </remarks>
    private static ScenePick? Ahead(ScenePicker picker) => picker.Pick(Looking(), 32, 32, 65, 65);

    [Fact]
    public void A_ray_stops_at_the_nearest_thing_it_meets()
    {
        var picker = new ScenePicker(Scene(
            Room(("door", 100f), ("far_wall", 300f)),
            "model=door, noun=HOTEL_DOOR, type=scene"));

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("door", pick.Name);
        Assert.Equal("HOTEL_DOOR", pick.Noun);
        Assert.Equal(100f, pick.Distance, 3);
        Assert.Equal(PickKind.Geometry, pick.Kind);
        Assert.True(pick.IsInteractive);
    }

    [Fact]
    public void Scenery_with_no_noun_still_blocks_what_is_behind_it()
    {
        // The nearer slab is not named by the file at all. Reporting the door behind it
        // would let the player open a door through a wall.
        var picker = new ScenePicker(Scene(
            Room(("wallpaper", 100f), ("door", 300f)),
            "model=door, noun=HOTEL_DOOR, type=scene"));

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("wallpaper", pick.Name);
        Assert.Null(pick.Noun);
        Assert.False(pick.IsInteractive);
    }

    [Fact]
    public void A_hidden_object_is_not_there_at_all()
    {
        // Not merely undrawn: the ray goes through it and finds the door behind.
        var picker = new ScenePicker(Scene(
            Room(("shutter", 100f), ("door", 300f)),
            """
            model=shutter, noun=SHUTTER, type=scene, hidden
            model=door, noun=HOTEL_DOOR, type=scene
            """));

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("door", pick.Name);
        Assert.Equal(300f, pick.Distance, 3);
    }

    [Fact]
    public void A_hit_test_is_clickable_although_it_is_never_drawn()
    {
        var picker = new ScenePicker(Scene(
            Room(("doorway_ht", 100f), ("door", 300f)),
            """
            model=doorway_ht, noun=HOTEL_DOOR, type=hittest
            model=door, noun=HOTEL_DOOR, type=scene
            """));

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("doorway_ht", pick.Name);
        Assert.Equal("HOTEL_DOOR", pick.Noun);
        Assert.Equal(PickKind.HitTest, pick.Kind);
    }

    [Fact]
    public void A_noclick_object_is_solid_but_answers_to_nothing()
    {
        var picker = new ScenePicker(Scene(
            Room(("te3_floor", 100f), ("door", 300f)),
            """
            model=te3_floor, noun=FLOOR, type=noClick
            model=door, noun=HOTEL_DOOR, type=scene
            """));

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("te3_floor", pick.Name);
        Assert.Null(pick.Noun);
        Assert.False(pick.IsInteractive);
    }

    [Fact]
    public void A_prop_in_front_of_a_wall_is_what_gets_picked()
    {
        var picker = new ScenePicker(Scene(
            Room(("wall", 300f)),
            "model=lamp, noun=LAMP, type=prop",
            Model("lamp", "LAMP", 120f, PlacedModelKind.Prop)));

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("lamp", pick.Name);
        Assert.Equal("LAMP", pick.Noun);
        Assert.Equal(PickKind.Prop, pick.Kind);
        Assert.Equal(120f, pick.Distance, 3);
    }

    [Fact]
    public void An_actor_can_stand_in_front_of_a_prop()
    {
        var picker = new ScenePicker(Scene(
            Room(("wall", 300f)),
            "model=lamp, noun=LAMP, type=prop",
            Model("lamp", "LAMP", 120f, PlacedModelKind.Prop),
            Model("gab", "GABRIEL", 60f, PlacedModelKind.Actor)));

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("gab", pick.Name);
        Assert.Equal(PickKind.Actor, pick.Kind);
    }

    [Fact]
    public void A_prop_is_picked_as_its_model_and_not_twice()
    {
        // A prop line names a file to stand in the room, not an object inside the BSP. If
        // the geometry happens to carry an object of the same name it is not the prop, and
        // taking the line as describing both would give the room a second lamp.
        var picker = new ScenePicker(Scene(
            Room(("lamp", 300f)),
            "model=lamp, noun=LAMP, type=prop",
            Model("lamp", "LAMP", 120f, PlacedModelKind.Prop)));

        Assert.Equal(1, picker.TargetCount);
        Assert.Equal(PickKind.Prop, Assert.NotNull(Ahead(picker)).Kind);
    }

    [Fact]
    public void A_ray_into_nothing_finds_nothing()
    {
        var picker = new ScenePicker(Scene(
            Room(("door", 100f)),
            "model=door, noun=HOTEL_DOOR, type=scene"));

        // Behind the camera, where the wall is not.
        Assert.Null(picker.Pick(new Ray(Vector3.Zero, -Vector3.UnitZ)));
    }

    [Fact]
    public void The_back_of_a_wall_is_not_something_you_can_click()
    {
        // Standing beyond the slab and looking back at it. The room is a box seen from
        // inside, so a surface facing away is one the player is behind, and the original
        // rejects those rather than letting a click reach through from outside.
        var picker = new ScenePicker(Scene(
            Room(("door", 100f)),
            "model=door, noun=HOTEL_DOOR, type=scene"));

        Assert.Null(picker.Pick(new Ray(new Vector3(0, 0, 400), -Vector3.UnitZ)));
    }

    [Fact]
    public void A_verb_on_a_model_line_comes_back_with_the_pick()
    {
        var picker = new ScenePicker(Scene(
            Room(("stairs_ht", 100f)),
            "model=stairs_ht, noun=STAIRS, verb=EXIT_UP, type=hittest"));

        Assert.Equal("EXIT_UP", Assert.NotNull(Ahead(picker)).Verb);
    }

    [Fact]
    public void The_ray_through_a_pixel_points_where_the_pixel_looks()
    {
        Camera camera = Looking();

        // Middle of the image looks straight ahead; the right half looks to the right,
        // which under a left-handed view is +X, and the top half looks up.
        Assert.Equal(new Vector3(0, 0, 1), camera.RayThrough(32, 32, 65, 65).Direction);
        Assert.True(camera.RayThrough(60, 32, 65, 65).Direction.X > 0f);
        Assert.True(camera.RayThrough(4, 32, 65, 65).Direction.X < 0f);
        Assert.True(camera.RayThrough(32, 4, 65, 65).Direction.Y > 0f);
        Assert.True(camera.RayThrough(32, 60, 65, 65).Direction.Y < 0f);
    }
}
