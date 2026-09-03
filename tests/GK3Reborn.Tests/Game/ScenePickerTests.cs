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

    /// <summary>A grown tree standing at a distance, as one triangle facing the camera.</summary>
    /// <param name="named">The geometry object whose cards it replaces.</param>
    /// <param name="z">How far down +Z it stands.</param>
    /// <remarks>
    /// Its geometry is at the origin and the placement carries it out to <paramref name="z"/>,
    /// which is how a real one arrives: <c>Foliage.Standing</c> fits a normalised tree to
    /// the site the cards measured.
    /// </remarks>
    private static GrownStand Tree(string named, float z) =>
        new(named,
            Model("tree", null, 0f, PlacedModelKind.Prop).Model,
            Matrix4x4.CreateTranslation(0, 0, z));

    /// <summary>The same model, standing in a sink that can be told to move it.</summary>
    /// <remarks>
    /// How a model really stands in a room: the sink holds where it is and a walk writes
    /// a new transform there, so this is the shape the picker has to answer against.
    /// </remarks>
    private static PlacedModel Standing(
        HeadlessSceneSink stage, string name, string? noun, Matrix4x4 where)
    {
        PlacedModel model = Model(name, noun, 0f, PlacedModelKind.Actor);

        return model with
        {
            Transform = where,
            Placement = stage.Add(model.Model, where),
            Stage = stage,
        };
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
    public void The_card_a_grown_tree_replaced_is_not_there()
    {
        // The card is still in the BSP — nothing rewrites the geometry — and the room has
        // simply stopped drawing it. A ray that went on meeting it would name a flat tree
        // standing where a modelled one is, and would find it through the modelled one.
        var picker = new ScenePicker(Scene(
            Room(("trees", 100f), ("cliff", 300f)),
            """
            model=trees, noun=TREES, type=scene
            model=cliff, noun=CLIFF, type=scene
            """) with
        {
            ReplacedSurfaces = new HashSet<int> { 0 },
        });

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("cliff", pick.Name);
        Assert.Equal(300f, pick.Distance, 3);
    }

    [Fact]
    public void A_grown_tree_answers_to_the_object_whose_cards_it_replaced()
    {
        // What the player is pointing at is the tree the room always had. MCF's maples are
        // mcf_trs, which the scene calls TREES, and a tree grown over it is still that.
        var picker = new ScenePicker(Scene(
            Room(("trees", 100f), ("cliff", 300f)),
            """
            model=trees, noun=TREES, type=scene
            model=cliff, noun=CLIFF, type=scene
            """) with
        {
            ReplacedSurfaces = new HashSet<int> { 0 },
            Woods = [Tree("trees", 150f)],
        });

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("trees", pick.Name);
        Assert.Equal("TREES", pick.Noun);
        Assert.Equal(150f, pick.Distance, 3);
        Assert.True(pick.IsInteractive);
    }

    [Fact]
    public void A_grown_tree_stops_what_is_behind_it()
    {
        // The half this was reported for: the note at MCF stayed pickable through a canopy
        // the ray could not see, so the hotspot was there and nothing the player could aim
        // at was.
        var picker = new ScenePicker(Scene(
            Room(("cliff", 300f)),
            "model=cliff, noun=CLIFF, type=scene",
            Model("note", "CLUE_NOTE_3", 200f, PlacedModelKind.Prop)) with
        {
            Woods = [Tree("trees", 150f)],
        });

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("trees", pick.Name);
        Assert.Equal(150f, pick.Distance, 3);
    }

    /// <summary>A stand big enough that the picker sorts it into cells.</summary>
    /// <param name="named">The geometry object whose cards it replaces.</param>
    /// <param name="z">Where the nearest of its triangles stands.</param>
    /// <remarks>
    /// Six hundred triangles spread through a volume, the way a crown's leaf cards are,
    /// with one of them square in front of the camera at <paramref name="z"/>. The rest are
    /// off to the sides and behind it, so a picker that lost a cell would answer with one
    /// of those or with nothing.
    /// </remarks>
    private static GrownStand Thicket(string named, float z)
    {
        List<ModMesh> meshes = [];

        for (int i = 0; i < 600; i++)
        {
            // The first is dead ahead; the others are scattered across a hundred units and
            // set back, which is what puts them in cells of their own.
            float x = i == 0 ? 0f : ((i * 37) % 101) - 50f;
            float y = i == 0 ? 0f : ((i * 53) % 101) - 50f;
            float away = i == 0 ? 0f : ((i * 71) % 101) + 10f;

            meshes.Add(new ModMesh
            {
                MeshToLocal = Matrix4x4.CreateTranslation(x, y, away),
                BoundsMin = new Vector3(-1, -1, 0),
                BoundsMax = new Vector3(1, 1, 0),
                Submeshes =
                [
                    new ModSubmesh
                    {
                        TextureName = "leaf",
                        Color = (255, 255, 255),
                        Positions =
                        [
                            new Vector3(-4, -4, 0), new Vector3(0, 8, 0), new Vector3(4, -4, 0),
                        ],
                        Normals = [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ],
                        TexCoords = new Vector2[3],
                        Indices = [0, 1, 2],
                    },
                ],
            });
        }

        return new GrownStand(
            named, ModFile.FromMeshes(named, [.. meshes]), Matrix4x4.CreateTranslation(0, 0, z));
    }

    [Fact]
    public void A_stand_sorted_into_cells_answers_the_same_as_one_kept_whole()
    {
        // The cells exist so the box test has something to reject — a crown is ten thousand
        // leaf cards and one box round all of them is entered by nearly every ray a wood
        // sees. What they must not do is change the answer.
        var picker = new ScenePicker(Scene(
            Room(("cliff", 400f)),
            """
            model=trees, noun=TREES, type=scene
            model=cliff, noun=CLIFF, type=scene
            """) with
        {
            Woods = [Thicket("trees", 150f)],
        });

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("trees", pick.Name);
        Assert.Equal("TREES", pick.Noun);
        Assert.Equal(150f, pick.Distance, 3);
    }

    [Fact]
    public void A_grown_tree_over_an_object_the_scene_never_named_is_scenery()
    {
        // Same rule as the geometry it stands in for: an object no [MODELS] line mentions
        // is wallpaper, and growing it does not give it a noun it never had.
        var picker = new ScenePicker(Scene(
            Room(("cliff", 300f)),
            "model=cliff, noun=CLIFF, type=scene") with
        {
            Woods = [Tree("ler_treeshadowcasters", 150f)],
        });

        ScenePick pick = Assert.NotNull(Ahead(picker));

        Assert.Equal("ler_treeshadowcasters", pick.Name);
        Assert.Null(pick.Noun);
        Assert.False(pick.IsInteractive);
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
    public void A_model_that_is_not_being_drawn_is_not_there_to_be_clicked()
    {
        // A scene loads the models it means to show later and keeps them out of the
        // picture until a script says otherwise. RC1's moped waits that way for its
        // scripted ride past the hotel, and a ray that meets it picks up a noun for
        // something nobody can see.
        PlacedModel moped = Model("wmo", "WILKES", 60f, PlacedModelKind.Prop);

        var picker = new ScenePicker(Scene(
            Room(("wall", 300f)),
            "model=lamp, noun=LAMP, type=prop\nmodel=wmo, noun=WILKES, type=prop, hidden",
            Model("lamp", "LAMP", 120f, PlacedModelKind.Prop),
            moped));

        Assert.Equal("wmo", Assert.NotNull(Ahead(picker)).Name);

        moped.Visible = false;

        Assert.Equal("lamp", Assert.NotNull(Ahead(picker)).Name);

        moped.Visible = true;

        Assert.Equal("wmo", Assert.NotNull(Ahead(picker)).Name);
    }

    [Fact]
    public void Showing_every_hotspot_leaves_out_what_is_not_there()
    {
        // Reported as an item still having a hotspot after it had been picked up. Taking
        // something takes its model out of the room, so a click already stopped finding it
        // — and the list of every hotspot at once went on listing it, which is a label for
        // something that is not there.
        PlacedModel taken = Model("cap", "RED_CAP", 60f, PlacedModelKind.Prop);

        var picker = new ScenePicker(Scene(
            Room(("wall", 300f)),
            "model=lamp, noun=LAMP, type=prop" + Newline + "model=cap, noun=RED_CAP, type=prop",
            Model("lamp", "LAMP", 120f, PlacedModelKind.Prop),
            taken));

        Assert.Contains(picker.Interactive(), spot => spot.Noun == "RED_CAP");

        taken.Visible = false;

        Assert.DoesNotContain(picker.Interactive(), spot => spot.Noun == "RED_CAP");
        Assert.Contains(picker.Interactive(), spot => spot.Noun == "LAMP");
    }

    /// <summary>A line break, spelled out so no editor can eat it.</summary>
    private const string Newline = "\n";

    [Fact]
    public void An_actor_who_walks_takes_their_noun_with_them()
    {
        // The bug this exists for: the picker gathers a model's triangles once, and an
        // actor is moved by handing the sink a new transform. Baking the triangles into
        // the room at load leaves Gabriel's noun on the spot he set off from — the pointer
        // finds him where he used to be, and finds nothing where he is now.
        var stage = new HeadlessSceneSink();

        PlacedModel gabriel =
            Standing(stage, "gab", "GABRIEL", Matrix4x4.CreateTranslation(0, 0, 120));

        var picker = new ScenePicker(Scene(
            Room(("wall", 300f)),
            "model=gab, noun=GABRIEL, type=prop",
            gabriel));

        Assert.Equal("gab", Assert.NotNull(Ahead(picker)).Name);

        // Two hundred units to his right, still the same distance away.
        stage.MoveModel(gabriel.Placement, Matrix4x4.CreateTranslation(200, 0, 120));

        // Where he was, there is now the wall behind him.
        Assert.Equal("wall", Assert.NotNull(Ahead(picker)).Name);

        ScenePick? found = picker.Pick(
            new Ray(Vector3.Zero, Vector3.Normalize(new Vector3(200, 0, 120))));

        Assert.Equal("gab", Assert.NotNull(found).Name);
        Assert.Equal(PickKind.Actor, Assert.NotNull(found).Kind);

        // And the hit comes back in the room's units rather than the model's.
        Assert.Equal(
            new Vector3(200, 0, 120).Length(), Assert.NotNull(found).Distance, 2);
    }

    [Fact]
    public void A_scaled_model_is_still_as_far_away_as_it_looks()
    {
        // The ray is sent into the model's own space to meet it, which scales the ray
        // along with everything else. What must not scale is the answer: an actor at half
        // size standing 120 units away is 120 units away, or the nearest of two things is
        // decided by whichever happens to be modelled bigger.
        var stage = new HeadlessSceneSink();

        PlacedModel small = Standing(
            stage,
            "gab",
            "GABRIEL",
            Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(0, 0, 120));

        var picker = new ScenePicker(Scene(
            Room(("wall", 300f)), "model=gab, noun=GABRIEL, type=prop", small));

        ScenePick? found = Ahead(picker);

        Assert.Equal("gab", Assert.NotNull(found).Name);
        Assert.Equal(120f, Assert.NotNull(found).Distance, 2);
    }

    [Fact]
    public void A_model_scaled_to_nothing_is_something_the_ray_goes_past()
    {
        // A transform with no inverse cannot be asked where a ray goes in the model's own
        // space. There is nothing there to click either way, so the ray carries on to the
        // wall rather than the pick being abandoned.
        var stage = new HeadlessSceneSink();

        PlacedModel gabriel =
            Standing(stage, "gab", "GABRIEL", Matrix4x4.CreateTranslation(0, 0, 120));

        var picker = new ScenePicker(Scene(
            Room(("wall", 300f)), "model=gab, noun=GABRIEL, type=prop", gabriel));

        stage.MoveModel(gabriel.Placement, Matrix4x4.CreateScale(0f));

        Assert.Equal("wall", Assert.NotNull(Ahead(picker)).Name);
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
