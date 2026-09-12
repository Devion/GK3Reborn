using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for what the weather is allowed to take off a room's ground.
/// </summary>
public sealed class GroundErosionTests
{
    private const string Ground = "GRASS_DIRT0";

    /// <summary>How far the test slabs run either way from the origin, in world units.</summary>
    private const float Reach = 600f;

    /// <summary>A lattice of triangles over the square, at whatever height is asked for.</summary>
    /// <param name="height">How high the ground is at a point on X and Z.</param>
    /// <param name="steps">How many quads the square is cut into, each way.</param>
    /// <returns>The triangles, and a room made of exactly them.</returns>
    private static (List<(Vector3 A, Vector3 B, Vector3 C, string Texture)> Ground, BspFile Room)
        Slope(
            Func<float, float, float> height,
            int steps = 12,
            IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)>? standing = null)
    {
        List<(Vector3, Vector3, Vector3, string)> triangles = [];
        List<Vector3> vertices = [];
        List<Vector2> uvs = [];
        List<ushort> indices = [];
        List<BspPolygon> polygons = [];

        float step = (2f * Reach) / steps;

        for (int i = 0; i < steps; i++)
        {
            for (int j = 0; j < steps; j++)
            {
                float x = -Reach + (i * step);
                float z = -Reach + (j * step);

                Vector3 At(float ax, float az) => new(ax, height(ax, az), az);

                Vector3 a = At(x, z);
                Vector3 b = At(x + step, z);
                Vector3 c = At(x + step, z + step);
                Vector3 d = At(x, z + step);

                Add(a, b, c);
                Add(a, c, d);
            }
        }

        // Anything else the room draws: put into the geometry, and deliberately not into
        // the list of ground, which is what the plan hands the field.
        foreach ((Vector3 a, Vector3 b, Vector3 c) in standing ?? [])
        {
            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = indices.Count,
                VertexIndexCount = 3,
                SurfaceIndex = 1,
            });

            foreach (Vector3 corner in (Span<Vector3>)[a, b, c])
            {
                indices.Add((ushort)vertices.Count);
                vertices.Add(corner);
                uvs.Add(new Vector2(corner.X / 100f, corner.Y / 100f));
            }
        }

        BspFile room = BspFile.FromParts(
            "test",
            ["the_ground", "the_wall"],
            [
                new BspSurface
                {
                    ObjectIndex = 0,
                    TextureName = Ground,
                    Flags = 0,
                    LightmapUvOffset = Vector2.Zero,
                    LightmapUvScale = Vector2.One,
                },
                new BspSurface
                {
                    ObjectIndex = 1,
                    TextureName = "STONE_WALL",
                    Flags = 0,
                    LightmapUvOffset = Vector2.Zero,
                    LightmapUvScale = Vector2.One,
                },
            ],
            polygons,
            [.. vertices],
            [.. uvs],
            [.. indices]);

        return (triangles, room);

        void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            triangles.Add((a, b, c, Ground));

            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = indices.Count,
                VertexIndexCount = 3,
                SurfaceIndex = 0,
            });

            foreach (Vector3 corner in (Span<Vector3>)[a, b, c])
            {
                indices.Add((ushort)vertices.Count);
                vertices.Add(corner);
                uvs.Add(new Vector2(corner.X / 100f, corner.Z / 100f));
            }
        }
    }

    /// <summary>The room's ground, weathered, with everything defaulted to "nothing holds it".</summary>
    private static GroundErosion? Weathered(
        Func<float, float, float> height,
        IReadOnlyList<GroundAnchor>? anchors = null,
        Func<float, float, bool>? walkable = null,
        float erodes = 1f,
        float depth = 24f,
        int seed = 17,
        IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)>? standing = null)
    {
        (List<(Vector3, Vector3, Vector3, string)> ground, BspFile room) =
            Slope(height, standing: standing);

        return GroundErosion.For(ground, room, _ => erodes, anchors ?? [], walkable, depth, seed);
    }

    /// <summary>A gentle hillside: a fifth of a unit up for every unit across.</summary>
    private static float Hillside(float x, float z) => (x * 0.2f) + (z * 0.05f);

    [Fact]
    public void NothingIsEverRaised()
    {
        GroundErosion? weather = Weathered(Hillside);

        Assert.NotNull(weather);

        for (float x = -Reach; x <= Reach; x += 17f)
        {
            for (float z = -Reach; z <= Reach; z += 17f)
            {
                Assert.True(
                    weather.At(x, z) <= 0f,
                    $"the ground rose by {-weather.At(x, z)} units at ({x}, {z}).");
            }
        }
    }

    [Fact]
    public void AHillsideIsWeathered()
    {
        GroundErosion? weather = Weathered(Hillside);

        Assert.NotNull(weather);
        Assert.True(weather.Deepest > 1f, $"nothing was taken off: {weather.Deepest}.");
        Assert.True(weather.Deepest <= 24f, $"more than the depth was taken: {weather.Deepest}.");
    }

    /// <summary>
    /// The decision in <c>Nowhere</c>: a level courtyard is not weathered, because a field
    /// with a floor under it would sink a room's whole ground and stand everything holding
    /// it on a pedestal.
    /// </summary>
    [Fact]
    public void LevelGroundIsLeftAlone()
    {
        GroundErosion? weather = Weathered((_, _) => 0f);

        Assert.True(
            weather is null || weather.Deepest < 0.01f,
            $"level ground was weathered by {weather?.Deepest}.");
    }

    [Fact]
    public void TheRimOfTheGroundIsHeld()
    {
        GroundErosion? weather = Weathered(Hillside);

        Assert.NotNull(weather);

        for (float z = -Reach; z <= Reach; z += 23f)
        {
            Assert.Equal(0f, weather.At(-Reach, z));
            Assert.Equal(0f, weather.At(Reach, z));
            Assert.Equal(0f, weather.At(z, -Reach));
            Assert.Equal(0f, weather.At(z, Reach));
        }
    }

    /// <summary>
    /// Where an actor may stand, because their feet are put on the ground the 1999 files
    /// describe and know nothing about any of this.
    /// </summary>
    [Fact]
    public void WhereAnActorMayWalkIsHeld()
    {
        GroundErosion? weather = Weathered(
            Hillside, walkable: (x, z) => MathF.Abs(x) < 200f && MathF.Abs(z) < 200f);

        Assert.NotNull(weather);

        for (float x = -180f; x <= 180f; x += 20f)
        {
            for (float z = -180f; z <= 180f; z += 20f)
            {
                Assert.Equal(0f, weather.At(x, z));
            }
        }
    }

    [Fact]
    public void WhatTheSceneStandsOnItHoldsIt()
    {
        GroundErosion? weather = Weathered(
            Hillside, anchors: [new GroundAnchor(new Vector2(150f, -100f), 120f)]);

        Assert.NotNull(weather);
        Assert.Equal(0f, weather.At(150f, -100f));
        Assert.Equal(0f, weather.At(190f, -100f));
    }

    /// <summary>
    /// A made road is filed under the same rule as a lawn and weathers at half the depth or
    /// less; something the classifier calls firm should not move at all.
    /// </summary>
    [Fact]
    public void GroundThatDoesNotErodeIsLeftAlone()
    {
        GroundErosion? weather = Weathered(Hillside, erodes: 0f);

        Assert.True(
            weather is null || weather.Deepest < 0.01f,
            $"ground that cannot erode lost {weather?.Deepest} units.");
    }

    [Fact]
    public void FirmerGroundKeepsMoreOfItself()
    {
        GroundErosion? soft = Weathered(Hillside, erodes: 1f);
        GroundErosion? firm = Weathered(Hillside, erodes: 0.4f);

        Assert.NotNull(soft);
        Assert.NotNull(firm);
        Assert.True(
            firm.Deepest < soft.Deepest,
            $"firm ground lost {firm.Deepest} and soft {soft.Deepest}.");
    }

    [Fact]
    public void NoDepthIsNoWeather() => Assert.Null(Weathered(Hillside, depth: 0f));

    [Fact]
    public void TheSameRoomWeathersTheSameWayTwice()
    {
        GroundErosion? once = Weathered(Hillside);
        GroundErosion? again = Weathered(Hillside);

        Assert.NotNull(once);
        Assert.NotNull(again);

        for (float x = -Reach; x <= Reach; x += 31f)
        {
            for (float z = -Reach; z <= Reach; z += 31f)
            {
                Assert.Equal(once.At(x, z), again.At(x, z));
            }
        }
    }

    [Fact]
    public void DifferentRoomsWeatherDifferently()
    {
        GroundErosion? here = Weathered(Hillside, seed: 1);
        GroundErosion? there = Weathered(Hillside, seed: 2);

        Assert.NotNull(here);
        Assert.NotNull(there);

        bool differs = false;

        for (float x = -Reach; x <= Reach && !differs; x += 13f)
        {
            for (float z = -Reach; z <= Reach && !differs; z += 13f)
            {
                differs = MathF.Abs(here.At(x, z) - there.At(x, z)) > 0.01f;
            }
        }

        Assert.True(differs, "two rooms weathered identically.");
    }

    /// <summary>
    /// The one learnt on screen: LHM's meadow slid out from under a rock face that leans
    /// over it, and what the camera saw was the underside of the rock.
    /// </summary>
    [Fact]
    public void TheFootOfSomethingSteepIsHeld()
    {
        // Level to the west, a wall of ground climbing steeply to the east.
        GroundErosion? weather = Weathered((x, _) => x < 0f ? 0f : x * 4f);

        Assert.NotNull(weather);

        for (float z = -300f; z <= 300f; z += 37f)
        {
            // Nothing at all where the two meet, and on the steep side of it.
            Assert.Equal(0f, weather.At(0f, z));
            Assert.Equal(0f, weather.At(40f, z));

            // And next to nothing for the cell after that, where the fade has only begun.
            Assert.InRange(weather.At(-10f, z), -0.1f, 0f);
        }
    }

    /// <summary>
    /// A wall standing on the ground holds it, although it has no footprint at all.
    /// </summary>
    [Fact]
    public void AWallStandingOnTheGroundHoldsIt()
    {
        // A sheet of stone on end along the line x = 0, from below the ground it stands on
        // to well above it, in the two triangles a quad is.
        Vector3 Foot(float z) => new(0f, -60f, z);
        Vector3 Head(float z) => new(0f, 150f, z);

        GroundErosion? weather = Weathered(
            Hillside,
            standing:
            [
                (Foot(-Reach), Foot(Reach), Head(Reach)),
                (Foot(-Reach), Head(Reach), Head(-Reach)),
            ]);

        Assert.NotNull(weather);

        for (float z = -300f; z <= 300f; z += 37f)
        {
            // Nothing at all at the foot of it, which is where the tear was, and next to
            // nothing for the cell after that, where the fade has only begun.
            Assert.Equal(0f, weather.At(0f, z));
            Assert.InRange(weather.At(-11f, z), -1.5f, 0f);
            Assert.InRange(weather.At(11f, z), -1.5f, 0f);
        }

        // And the hillside away from it is still weathered, or the hold has eaten the room.
        Assert.True(
            weather.At(300f, 0f) < -0.5f,
            $"the wall held the whole hillside: {weather.At(300f, 0f)}.");
    }

    [Fact]
    public void TheFieldCoversTheGroundItWasBuiltFrom()
    {
        GroundErosion? weather = Weathered(Hillside);

        Assert.NotNull(weather);
        Assert.True(weather.Cells > 0);
        Assert.True(weather.Free > 0, "no cell of the field was ground at all.");
        Assert.True(weather.Cell >= 12f, $"the field was cut finer than a cell: {weather.Cell}.");
    }
}
