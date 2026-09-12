using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the spray a fountain throws where its water lands.
/// </summary>
public sealed class FountainSprayTests
{
    /// <summary>A disc of water: wide, thin, and centred where it is put.</summary>
    private static ModMesh Water(string texture, Vector3 centre, float radius, float thickness)
    {
        var submesh = new ModSubmesh
        {
            TextureName = texture,
            Color = (255, 255, 255),
            Positions = [new Vector3(-radius, 0, 0), new Vector3(radius, 0, 0), new Vector3(0, 0, radius)],
            Normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            TexCoords = new Vector2[3],
            Indices = [0, 1, 2],
        };

        return new ModMesh
        {
            MeshToLocal = Matrix4x4.CreateTranslation(centre),
            BoundsMin = new Vector3(-radius, -thickness, -radius),
            BoundsMax = new Vector3(radius, thickness, radius),
            Submeshes = [submesh],
        };
    }

    /// <summary>The game's fountain in miniature: two pools, a jet and a curtain.</summary>
    private static PlacedModel Fountain(Matrix4x4 standing)
    {
        ModFile model = ModFile.FromMeshes(
            "fountain",
            [
                // The sheets running off the top basin into the pool at the foot.
                Water("WATERRUNOFF", new Vector3(0, 48, 0), 47f, 36f),

                // The pool at the foot, which is the widest thing here.
                Water("WATER", new Vector3(0, 27, 0), 79f, 5f),

                // The jet standing in the top basin.
                Water("WATERFTN", new Vector3(0, 99, 0), 36f, 29f),

                // And the top basin itself.
                Water("WATER", new Vector3(0, 80, 0), 44f, 4f),
            ]);

        return new PlacedModel(
            "fountain", "FOUNTAIN", Verb: null, model, standing, PlacedModelKind.Prop);
    }

    [Fact]
    public void A_fountain_throws_spray_where_each_sheet_meets_a_pool()
    {
        IReadOnlyList<FountainSource> found = Fountains.In([Fountain(Matrix4x4.Identity)]);

        Assert.Equal(2, found.Count);

        Fountain[] rings = [.. found.Select(Fountains.Ring)];

        // The top basin, fed by the jet standing in it rather than by the sheets falling
        // past it: the jet is the narrower of the two and the basin is what it lands in.
        Fountain top = rings.Single(r => r.Centre.Y > 60);

        Assert.Equal(84f, top.Centre.Y, 1);
        Assert.Equal(36f, top.Radius, 1);

        // The pool at the foot, fed by the sheets. They reach higher than the jet does and
        // so throw further.
        Fountain foot = rings.Single(r => r.Centre.Y < 60);

        Assert.Equal(32f, foot.Centre.Y, 1);
        Assert.Equal(47f, foot.Radius, 1);
        Assert.True(foot.Fall > top.Fall, "the sheets fall further than the jet");
    }

    [Fact]
    public void The_ring_is_where_the_water_is_now_and_not_where_it_was_modelled()
    {
        // Every fountain in the game is carried into its room by an absolute clip: RC1's is
        // authored near the origin and played four thousand units away. Reading the rest
        // pose puts the spray in a field outside the village.
        PlacedModel fountain = Fountain(Matrix4x4.Identity);

        FountainSource source = Assert.Single(Fountains.In([fountain]), s => s.Pool == 1);

        Assert.Equal(0f, Fountains.Ring(source).Centre.X, 1);

        // What the clip does: it poses the mesh groups, one at a time, somewhere else.
        for (int mesh = 0; mesh < fountain.Model.Meshes.Count; mesh++)
        {
            fountain.Pose(
                mesh,
                fountain.Model.Meshes[mesh].MeshToLocal *
                    Matrix4x4.CreateTranslation(new Vector3(3115f, 0f, -2336f)));
        }

        Fountain moved = Fountains.Ring(source);

        Assert.Equal(3115f, moved.Centre.X, 1);
        Assert.Equal(-2336f, moved.Centre.Z, 1);
    }

    [Fact]
    public void A_room_with_no_water_in_it_has_no_spray()
    {
        var submesh = new ModSubmesh
        {
            TextureName = "stone",
            Color = (255, 255, 255),
            Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ],
            Normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            TexCoords = new Vector2[3],
            Indices = [0, 1, 2],
        };

        ModFile model = ModFile.FromMeshes(
            "bench",
            [
                new ModMesh
                {
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = Vector3.Zero,
                    BoundsMax = Vector3.One,
                    Submeshes = [submesh],
                },
            ]);

        var bench = new PlacedModel(
            "bench", "BENCH", Verb: null, model, Matrix4x4.Identity, PlacedModelKind.Prop);

        Assert.Empty(Fountains.In([bench]));
        Assert.False(new FountainSpray(Fountains.In([bench])).Any);
    }

    [Fact]
    public void Droplets_are_thrown_up_and_fall_back_of_their_own_accord()
    {
        var spray = new FountainSpray(Fountains.In([Fountain(Matrix4x4.Identity)]));

        Assert.True(spray.Any);
        Assert.Equal(2, spray.Rings);

        spray.Advance(1f / 60f, Vector3.Zero);

        IReadOnlyList<Particle> thrown = spray.Facing(Vector3.Zero);

        Assert.NotEmpty(thrown);

        // Nothing is below the water it came off, and nothing is thrown higher than the
        // fountain is tall: a droplet on the cobbles or over the rooftops is the ring
        // having been worked out in the wrong space.
        Assert.All(thrown, p => Assert.InRange(p.Position.Y, 25f, 140f));

        // And the same room twice is the same room: the drift is seeded, not random.
        var again = new FountainSpray(Fountains.In([Fountain(Matrix4x4.Identity)]));

        again.Advance(1f / 60f, Vector3.Zero);

        Assert.Equal(thrown.Count, again.Facing(Vector3.Zero).Count);
        Assert.Equal(thrown[0].Position, again.Facing(Vector3.Zero)[0].Position);
    }

    [Fact]
    public void A_fountain_across_the_map_costs_nothing()
    {
        var spray = new FountainSpray(Fountains.In([Fountain(Matrix4x4.Identity)]));

        spray.Advance(1f / 60f, new Vector3(50_000f, 0f, 0f));

        Assert.Equal(0, spray.Count);
        Assert.Empty(spray.Facing(new Vector3(50_000f, 0f, 0f)));
    }
}
