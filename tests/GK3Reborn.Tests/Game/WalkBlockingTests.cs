using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Game;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for putting something in an actor's way.
/// </summary>
/// <remarks>
/// A boundary is painted once, before anybody knows where the van will park or which
/// wardrobe door will be standing open, so what occupies the floor at a given moment is
/// kept beside the bitmap rather than in it. CS3's wardrobe is the case that names itself:
/// opening it runs <c>WalkerBoundaryBlockModel("cs3_wrdb_dr_r")</c> and closing it undoes
/// exactly that.
/// </remarks>
public sealed class WalkBlockingTests
{
    /// <summary>A ten-by-ten room of open floor, each texel ten units square.</summary>
    private static WalkBoundary Room()
    {
        return new WalkBoundary(
            new IndexedImage(10, 10, new byte[100]), new Vector2(100, 100), Vector2.Zero);
    }

    [Fact]
    public void A_blocked_rectangle_is_not_somewhere_an_actor_may_stand()
    {
        WalkBoundary boundary = Room();

        Assert.Equal(100, boundary.WalkableTexels());
        Assert.True(boundary.IsWalkable(new Vector3(45, 0, 45)));

        boundary.Block("van", new Vector2(40, 40), new Vector2(60, 60));

        Assert.False(boundary.IsWalkable(new Vector3(45, 0, 45)));
        Assert.True(boundary.IsWalkable(new Vector3(5, 0, 5)));
        Assert.True(boundary.WalkableTexels() < 100);

        // The bitmap is untouched: what is standing on the floor is not what the floor is.
        Assert.Equal(0, boundary.RegionAt(new Vector3(45, 0, 45)));
    }

    [Fact]
    public void Moving_something_moves_the_hole_rather_than_making_a_second_one()
    {
        WalkBoundary boundary = Room();

        boundary.Block("van", new Vector2(0, 0), new Vector2(20, 20));
        boundary.Block("van", new Vector2(80, 80), new Vector2(100, 100));

        Assert.True(boundary.IsWalkable(new Vector3(5, 0, 5)));
        Assert.False(boundary.IsWalkable(new Vector3(95, 0, 95)));
        Assert.Equal("van", Assert.Single(boundary.Blocked).Name);
    }

    [Fact]
    public void Taking_it_away_gives_the_floor_back()
    {
        WalkBoundary boundary = Room();

        boundary.Block("van", new Vector2(40, 40), new Vector2(60, 60));
        Assert.True(boundary.Unblock("van"));

        Assert.Equal(100, boundary.WalkableTexels());
        Assert.Empty(boundary.Blocked);
        Assert.False(boundary.Unblock("van"));
    }

    [Fact]
    public void A_route_goes_around_what_is_in_the_way()
    {
        WalkBoundary boundary = Room();

        Vector3 from = boundary.ToWorld(0, 5);
        Vector3 to = boundary.ToWorld(9, 5);

        // Straight across an empty room.
        Assert.Equal(2, WalkPath.Find(boundary, from, to).Points.Count);

        // A wall across the middle with a gap at the top: the route has to bend.
        boundary.Block("crate", new Vector2(40, 0), new Vector2(60, 80));

        WalkRoute around = WalkPath.Find(boundary, from, to);

        Assert.True(around.ReachedGoal);
        Assert.True(around.Points.Count > 2, "a route past an obstacle needs a corner");
        Assert.All(around.Points, p => Assert.True(
            boundary.IsWalkable(p), $"({p.X}, {p.Z}) is inside the crate"));
    }

    [Fact]
    public void A_room_blocked_across_cannot_be_crossed_at_all()
    {
        WalkBoundary boundary = Room();
        boundary.Block("wall", new Vector2(40, -10), new Vector2(60, 110));

        WalkRoute route = WalkPath.Find(boundary, boundary.ToWorld(0, 5), boundary.ToWorld(9, 5));

        Assert.False(route.ReachedGoal);
    }

    [Fact]
    public void Anything_may_be_shut_and_only_the_named_regions_may_be_opened()
    {
        WalkBoundary boundary = Room();

        // A script that wants a stretch of open floor shut off is entitled to shut it.
        boundary.SetRegionOpen(0, open: false);
        Assert.False(boundary.IsRegionOpen(0));

        boundary.SetRegionOpen(0, open: true);
        Assert.True(boundary.IsRegionOpen(0));

        // Wall is wall whatever a script says.
        boundary.SetRegionOpen(255, open: true);
        Assert.False(boundary.IsRegionOpen(255));
    }

    /// <summary>A scene with that room's boundary and one prop standing in it.</summary>
    private static LoadedScene Scene(WalkBoundary boundary, Vector3 at)
    {
        var submesh = new ModSubmesh
        {
            TextureName = "skin",
            Color = (255, 255, 255),
            Positions = [new Vector3(-5, 0, -5), new Vector3(5, 0, -5), new Vector3(0, 20, 5)],
            Normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            TexCoords = new Vector2[3],
            Indices = [0, 1, 2],
        };

        var mesh = new ModMesh
        {
            MeshToLocal = Matrix4x4.Identity,
            BoundsMin = new Vector3(-5, 0, -5),
            BoundsMax = new Vector3(5, 20, 5),
            Submeshes = [submesh],
        };

        var placed = new PlacedModel(
            "crate",
            "CRATE",
            Verb: null,
            ModFile.FromMeshes("crate", [mesh]),
            Matrix4x4.CreateTranslation(at),
            PlacedModelKind.Prop);

        return new LoadedScene(
            "TEST",
            new SceneDefinition(general: null),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            boundary,
            Geometry: null,
            Placed: [placed]);
    }

    [Fact]
    public void A_script_puts_a_model_in_the_way_and_takes_it_out_again()
    {
        WalkBoundary boundary = Room();
        var api = new Gk3SheepApi(new GameState());
        SceneScripting.Attach(api, Scene(boundary, new Vector3(50, 0, 50)));

        Assert.True(boundary.IsWalkable(new Vector3(50, 0, 50)));

        SheepExpression.Evaluate("""WalkerBoundaryBlockModel("crate")""", api);

        // The box around everything the model is made of, flattened onto the floor.
        Assert.False(boundary.IsWalkable(new Vector3(50, 0, 50)));
        Assert.True(boundary.IsWalkable(new Vector3(10, 0, 10)));
        Assert.Equal("crate", Assert.Single(boundary.Blocked).Name);

        SheepExpression.Evaluate("""WalkerBoundaryUnblockModel("crate")""", api);

        Assert.True(boundary.IsWalkable(new Vector3(50, 0, 50)));
        Assert.Empty(boundary.Blocked);
    }

    [Fact]
    public void Blocking_something_the_scene_does_not_have_changes_nothing()
    {
        WalkBoundary boundary = Room();
        var api = new Gk3SheepApi(new GameState());
        SceneScripting.Attach(api, Scene(boundary, new Vector3(50, 0, 50)));

        SheepExpression.Evaluate("""WalkerBoundaryBlockModel("nothing_of_the_sort")""", api);

        Assert.Empty(boundary.Blocked);
        Assert.Equal(100, boundary.WalkableTexels());
    }

    [Fact]
    public void A_script_shuts_a_region_and_opens_it_again()
    {
        // Two indices, because a scriptable region is painted as an area and the border
        // around it, and moving one without the other leaves a wall a texel thick.
        byte[] indices = new byte[100];
        indices[55] = 200;
        indices[56] = 201;

        var boundary = new WalkBoundary(
            new IndexedImage(10, 10, indices), new Vector2(100, 100), Vector2.Zero);

        var api = new Gk3SheepApi(new GameState());
        SceneScripting.Attach(api, Scene(boundary, new Vector3(-500, 0, -500)));

        Assert.True(boundary.IsRegionOpen(200));

        SheepExpression.Evaluate("WalkerBoundaryBlockRegion(200, 201)", api);
        Assert.False(boundary.IsRegionOpen(200));
        Assert.False(boundary.IsRegionOpen(201));

        SheepExpression.Evaluate("WalkerBoundaryUnblockRegion(200, 201)", api);
        Assert.True(boundary.IsRegionOpen(200));
        Assert.True(boundary.IsRegionOpen(201));
    }
}
