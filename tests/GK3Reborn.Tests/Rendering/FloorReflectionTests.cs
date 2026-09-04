using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests the plane a polished floor is reflected about.
/// </summary>
/// <remarks>
/// Reported as "the tile floor in the hotel, the tile floor in the church don't reflect
/// much at all". They reflected nothing at all, and could not: the screen-space march can
/// only return what is already in the frame, and what a floor shows is mostly what is above
/// the camera. The answer is the pass the mirrors already use — the room drawn a second
/// time from under the floor — and the whole of what makes it cheap to sample is that a
/// point on the plane lands on the same pixel in both renders.
/// </remarks>
public sealed class FloorReflectionTests
{
    /// <summary>A flat square of floor at a height, in world units.</summary>
    private static MeshVertex[] Slab(float height, float across, int steps = 8)
    {
        var vertices = new List<MeshVertex>();

        for (int x = 0; x <= steps; x++)
        {
            for (int z = 0; z <= steps; z++)
            {
                vertices.Add(new MeshVertex
                {
                    Position = new Vector3(
                        (x / (float)steps * 2f - 1f) * across,
                        height,
                        (z / (float)steps * 2f - 1f) * across),
                    Normal = Vector3.UnitY,
                });
            }
        }

        return [.. vertices];
    }

    [Fact]
    public void A_flat_floor_is_reflected_about_its_own_level()
    {
        MirrorSurface? plane = MirrorSurfaces.Ground([(Slab(12.5f, 200f), Matrix4x4.Identity)]);

        Assert.NotNull(plane);
        Assert.Equal(new Vector3(0f, 1f, 0f), new Vector3(plane.Value.Plane.X, plane.Value.Plane.Y, plane.Value.Plane.Z));
        Assert.Equal(-12.5f, plane.Value.Plane.W, 2);
        Assert.True(plane.Value.Radius >= MirrorSurfaces.LeastFloor);
    }

    [Fact]
    public void A_floor_too_small_to_be_worth_a_second_pass_gets_none()
    {
        // A polished tabletop is smooth and flat and is not what this is for: the cost is
        // a whole extra draw of the room.
        Assert.Null(MirrorSurfaces.Ground([(Slab(0f, 20f), Matrix4x4.Identity)]));
    }

    [Fact]
    public void A_floor_with_a_step_in_it_is_reflected_about_the_level_most_of_it_is_at()
    {
        // The church's nave and the step up to its altar. The step is simply not on the
        // plane, and the pass tests each pixel against the plane — so it reflects on the
        // lower level and not on the upper, which is what a floor with a step in it does.
        MeshVertex[] nave = Slab(0f, 300f, steps: 12);
        MeshVertex[] altar = Slab(30f, 60f, steps: 4);

        MirrorSurface? plane = MirrorSurfaces.Ground(
            [(nave, Matrix4x4.Identity), (altar, Matrix4x4.Identity)]);

        Assert.NotNull(plane);
        Assert.Equal(0f, plane.Value.Plane.W, 1);
    }

    [Fact]
    public void A_ramp_is_not_a_floor()
    {
        var sloping = new List<MeshVertex>();

        for (int i = 0; i <= 200; i++)
        {
            sloping.Add(new MeshVertex
            {
                Position = new Vector3(i * 4f, i * 2f, 0f),
                Normal = Vector3.UnitY,
            });
        }

        Assert.Null(MirrorSurfaces.Ground([(sloping.ToArray(), Matrix4x4.Identity)]));
    }

    [Fact]
    public void The_whole_floor_decides_the_plane_and_not_its_largest_piece()
    {
        // Fitted a piece at a time, the church chose the plane of its tiled runner — the
        // largest single piece — and the grey tiles either side of it sat a little lower
        // and were not on it, so the reflection appeared on a strip up the middle of the
        // nave and nowhere else.
        MeshVertex[] runner = Slab(1.0f, 200f, steps: 6);
        MeshVertex[] tilesLeft = Slab(0.4f, 200f, steps: 10);
        MeshVertex[] tilesRight = Slab(0.4f, 200f, steps: 10);

        MirrorSurface? plane = MirrorSurfaces.Ground(
        [
            (runner, Matrix4x4.Identity),
            (tilesLeft, Matrix4x4.Identity),
            (tilesRight, Matrix4x4.Identity),
        ]);

        Assert.NotNull(plane);

        // All three are inside one band of levelling, so the plane is their common mean and
        // every one of them is on it. What must not happen is the plane landing on the
        // runner alone.
        Assert.InRange(-plane.Value.Plane.W, 0.4f, 1.0f);
    }

    [Fact]
    public void A_floor_is_placed_where_the_room_puts_it()
    {
        // The vertices are in the piece's own space and the batch says where that space is.
        // A floor reflected about its untransformed height is a floor reflecting the room
        // from somewhere else entirely.
        Matrix4x4 lifted = Matrix4x4.CreateTranslation(0f, 40f, 0f);

        MirrorSurface? plane = MirrorSurfaces.Ground([(Slab(0f, 200f), lifted)]);

        Assert.NotNull(plane);
        Assert.Equal(-40f, plane.Value.Plane.W, 2);
    }

    [Fact]
    public void The_reflection_plan_is_kept_inside_its_range()
    {
        Assert.Equal(1f, new ReflectionPlan(float.NaN).Sane().Strength);
        Assert.Equal(ReflectionPlan.Strongest, new ReflectionPlan(99f).Sane().Strength);
        Assert.Equal(0f, new ReflectionPlan(-1f).Sane().Strength);

        // Nought is the switch as well as the amount.
        Assert.Equal(0f, ReflectionPlan.None.Strength);
        Assert.False(ReflectionPlan.None.PlanarFloors);
    }
}

/// <summary>
/// Tests that the artists' baking scaffolding can be switched off outright.
/// </summary>
/// <remarks>
/// Asked for as "an option to disable most of the fake lights and try to go realism by only
/// allowing daylight and/or lamp sources to light the environment". The classification
/// already existed for turning them <em>down</em>; this is the same rule taken to nought.
/// </remarks>
public sealed class RealisticLightingTests
{
    /// <summary>A light with a name and a brightness, which is all this rule reads.</summary>
    private static AuthoredLight Light(string name) => new(
        name,
        AuthoredLightKind.Point,
        Vector3.Zero,
        -Vector3.UnitY,
        Vector3.One,
        HotSpot: 0f,
        Falloff: 0f,
        AttenuationStart: 0f,
        AttenuationEnd: 500f,
        UsesAttenuation: true,
        CastsShadows: true,
        Intensity: 1f,
        Radius: 1f);

    [Fact]
    public void Only_real_sources_survive_when_the_player_asks_for_only_real_sources()
    {
        List<AuthoredLight> rig =
        [
            Light("cs3_key"),
            Light("chandelier_omni"),
            Light("back_room_fill"),
            Light("cs3_ambient"),
            Light("sky_bounce01"),
            Light("cs3_turret_window_floor_warmer04"),
        ];

        IReadOnlyList<AuthoredLight> lit = RigBalance.For(
            rig, RayTracingQuality.High, out int dimmed, realistic: true);

        Assert.Equal(4, dimmed);

        // The lights stay in the rig at nought rather than being taken out of it: a light
        // is addressed by its place in the list, and renumbering a room's lights because a
        // preference changed is a way to make a scene look different depending on what the
        // player did last.
        Assert.Equal(rig.Count, lit.Count);

        Assert.Equal(1f, lit[0].Intensity);
        Assert.Equal(1f, lit[1].Intensity);
        Assert.Equal(0f, lit[2].Intensity);
        Assert.Equal(0f, lit[3].Intensity);
        Assert.Equal(0f, lit[4].Intensity);
        Assert.Equal(0f, lit[5].Intensity);
    }

    [Fact]
    public void With_no_rays_the_fills_are_left_alone_whatever_the_player_asked()
    {
        // The bake *is* the room's lighting there and the rig only reaches the people
        // standing in it, so switching off the fills would darken the characters and leave
        // the room they stand in exactly as bright. That is not realism, it is a bug with a
        // switch on it.
        Assert.Equal(1f, RigBalance.Keep(RayTracingQuality.None, realistic: true));

        List<AuthoredLight> rig = [Light("cs3_key"), Light("back_room_fill")];

        Assert.Same(
            rig,
            RigBalance.For(rig, RayTracingQuality.None, out int dimmed, realistic: true));

        Assert.Equal(0, dimmed);
    }

    [Fact]
    public void Without_the_setting_the_fills_are_only_turned_down()
    {
        Assert.Equal(0.15f, RigBalance.Keep(RayTracingQuality.High, realistic: false));
        Assert.Equal(0.5f, RigBalance.Keep(RayTracingQuality.Medium, realistic: false));
        Assert.Equal(0f, RigBalance.Keep(RayTracingQuality.Medium, realistic: true));
    }
}
