using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for clicking the ground to go there.
/// </summary>
/// <remarks>
/// The room is a floor slab at y=0 with a second, nameless slab lying on part of it — a
/// rug, in effect — and a camera overhead looking straight down. Everything these tests
/// ask is which of the two the ray reached and whether the boundary was consulted before
/// an answer came back, because those are the two ways this goes wrong: a click that
/// walks the player through the furniture, and a click on the furniture that walks them
/// at all.
/// </remarks>
public sealed class FloorClickTests
{
    /// <summary>How wide the fixture's floor is, in world units, on both axes.</summary>
    private const float Extent = 400f;

    /// <summary>A camera high above the middle of the floor, looking straight down.</summary>
    private static Camera Overhead() =>
        new()
        {
            Position = new Vector3(Extent / 2f, 500f, Extent / 2f),
            Target = new Vector3(Extent / 2f, 0f, Extent / 2f),

            // Any horizontal direction will do for a camera looking down; +Z keeps the
            // picture's rows running along world Z, which makes the pixels below readable.
            Up = Vector3.UnitZ,
        };

    /// <summary>Flat slabs, each its own named object, in the order given.</summary>
    /// <param name="slabs">Object name, the height it lies at, and the ground it covers.</param>
    /// <remarks>
    /// Wound so the upward normal faces the camera. The picker refuses back faces on room
    /// geometry, so a floor wound the other way is invisible to a click — which is a
    /// mistake worth not writing into the fixture by accident.
    /// </remarks>
    private static BspFile Ground(
        params (string Name, float Y, float MinX, float MinZ, float MaxX, float MaxZ)[] slabs)
    {
        List<string> names = [];
        List<BspSurface> surfaces = [];
        List<BspPolygon> polygons = [];
        List<Vector3> vertices = [];
        List<ushort> indices = [];

        foreach ((string name, float y, float minX, float minZ, float maxX, float maxZ) in slabs)
        {
            int at = vertices.Count;

            vertices.Add(new Vector3(minX, y, minZ));
            vertices.Add(new Vector3(minX, y, maxZ));
            vertices.Add(new Vector3(maxX, y, maxZ));
            vertices.Add(new Vector3(maxX, y, minZ));

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
                TextureName = "boards",
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

    /// <summary>
    /// A boundary over the whole floor, open on the near half and walled on the far half.
    /// </summary>
    /// <remarks>
    /// The image's top row is the far end of the room, so the walls go at the top. Four
    /// texels square over four hundred units means each is a hundred across, and the
    /// middle of the open row nearest the wall is at z=150.
    /// </remarks>
    private static WalkBoundary HalfOpen()
    {
        byte[] indices =
        [
            255, 255, 255, 255,
            255, 255, 255, 255,
            0, 0, 0, 0,
            0, 0, 0, 0,
        ];

        return new WalkBoundary(
            new IndexedImage(4, 4, indices), new Vector2(Extent, Extent), Vector2.Zero);
    }

    /// <summary>The interaction over a room, described by the given initialisation file.</summary>
    private static SceneInteraction Room(BspFile room, string ini, WalkBoundary? boundary = null)
    {
        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse(ini, "TEST.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 0,
            boundary,
            room);

        return new SceneInteraction(scene, new Gk3SheepApi(new GameState()));
    }

    /// <summary>What a click on a pixel of a 100x100 picture would do.</summary>
    private static Hover At(SceneInteraction interaction, int x, int y) =>
        interaction.At(Overhead(), x, y, 100, 100);

    [Fact]
    public void A_click_on_the_floor_is_somewhere_to_walk()
    {
        SceneInteraction interaction = Room(
            Ground(("test_floor", 0f, 0, 0, Extent, Extent)),
            "[GENERAL]\nfloor=test_floor\n");

        Vector3? target = interaction.FloorTarget(At(interaction, 50, 50));

        Assert.NotNull(target);
        Assert.Equal(Extent / 2f, target!.Value.X, 5f);
        Assert.Equal(Extent / 2f, target.Value.Z, 5f);
    }

    [Fact]
    public void A_click_on_something_standing_on_the_floor_is_not()
    {
        // The rug is second, so it lies above the floor and the ray reaches it first.
        SceneInteraction interaction = Room(
            Ground(("test_floor", 0f, 0, 0, Extent, Extent), ("test_rug", 4f, 150, 150, 250, 250)),
            "[GENERAL]\nfloor=test_floor\n");

        Hover hover = At(interaction, 50, 50);

        Assert.Equal("test_rug", hover.Pick?.Name);
        Assert.Null(interaction.FloorTarget(hover));
    }

    [Fact]
    public void A_click_on_the_floor_of_a_scene_that_names_none_is_not()
    {
        SceneInteraction interaction = Room(
            Ground(("test_floor", 0f, 0, 0, Extent, Extent)),
            "[GENERAL]\n");

        Assert.Null(interaction.FloorTarget(At(interaction, 50, 50)));
    }

    [Fact]
    public void A_click_on_a_floor_the_scene_also_names_belongs_to_the_noun()
    {
        // TE3 declares its floor as an object with a noun. A click there is a click on the
        // thing, not on the ground under it, and the verb has to win.
        SceneInteraction interaction = Room(
            Ground(("test_floor", 0f, 0, 0, Extent, Extent)),
            "[GENERAL]\nfloor=test_floor\n\n[MODELS]\nmodel=test_floor,noun=FLOOR,verb=LOOK\n");

        Hover hover = At(interaction, 50, 50);

        Assert.Equal("FLOOR", hover.Noun);
        Assert.Null(interaction.FloorTarget(hover));
    }

    [Fact]
    public void A_click_beyond_the_boundary_walks_as_near_as_it_allows()
    {
        SceneInteraction interaction = Room(
            Ground(("test_floor", 0f, 0, 0, Extent, Extent)),
            "[GENERAL]\nfloor=test_floor\n",
            HalfOpen());

        // Top of the picture is the far end of the room, which the boundary walls off.
        Hover hover = At(interaction, 50, 24);

        Assert.Equal("test_floor", hover.Pick?.Name);
        Assert.True(hover.Pick!.Value.Point.Z > 300f, "the fixture should have aimed at the far end");

        Vector3? target = interaction.FloorTarget(hover);

        Assert.NotNull(target);
        Assert.Equal(150f, target!.Value.Z, 1f);
    }

    [Fact]
    public void A_click_inside_the_boundary_is_left_where_it_landed()
    {
        SceneInteraction interaction = Room(
            Ground(("test_floor", 0f, 0, 0, Extent, Extent)),
            "[GENERAL]\nfloor=test_floor\n",
            HalfOpen());

        Hover hover = At(interaction, 50, 80);
        Vector3? target = interaction.FloorTarget(hover);

        Assert.NotNull(target);
        Assert.Equal(hover.Pick!.Value.Point.X, target!.Value.X, 0.01f);
        Assert.Equal(hover.Pick.Value.Point.Z, target.Value.Z, 0.01f);
    }

    [Fact]
    public void The_height_that_comes_back_is_the_height_that_was_clicked()
    {
        // The boundary is a plan and has no storeys: what it snaps to is a point at y=0,
        // and losing the clicked height there is how a click on a gallery walks somebody
        // along the floor below it. So the floor here is a gallery, well above zero.
        const float Gallery = 120f;

        SceneInteraction interaction = Room(
            Ground(("test_floor", Gallery, 0, 0, Extent, Extent)),
            "[GENERAL]\nfloor=test_floor\n",
            HalfOpen());

        Hover hover = At(interaction, 50, 24);

        Assert.Equal(Gallery, hover.Pick!.Value.Point.Y, 0.01f);
        Assert.Equal(Gallery, interaction.FloorTarget(hover)!.Value.Y, 0.01f);
    }
}
