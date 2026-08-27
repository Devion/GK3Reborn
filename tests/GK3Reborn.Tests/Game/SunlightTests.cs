using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the synthesized sun.
/// </summary>
/// <remarks>
/// The sun is the one light no artist authored, so these pin what was decided rather than
/// what was measured: daylight hours get one, dusk and the dig do not, the arc runs east
/// to west and peaks near noon, and the recogniser that swaps out an authored scenekey
/// keeps its hands off the street lamps that share the scenekey's switched-off attenuation.
/// </remarks>
public sealed class SunlightTests
{
    private static readonly Vector3 Centre = new(2400f, 200f, -2400f);

    [Fact]
    public void A_morning_sun_stands_east_and_low_and_a_noon_sun_high()
    {
        AuthoredLight morning = Sunlight.For(new Timeblock(3, 7, IsAfternoon: false), Centre)!;
        AuthoredLight noon = Sunlight.For(new Timeblock(1, 12, IsAfternoon: true), Centre)!;

        Assert.True(ElevationOf(morning) < 20f, $"7am stands at {ElevationOf(morning):0}°");
        Assert.True(ElevationOf(noon) > 55f, $"noon stands at {ElevationOf(noon):0}°");

        // East of the map in the morning: the azimuth's sine is positive.
        Assert.True(morning.Position.X > Centre.X);
    }

    [Fact]
    public void Afternoon_swings_the_sun_west_of_where_morning_had_it()
    {
        AuthoredLight morning = Sunlight.For(new Timeblock(1, 10, IsAfternoon: false), Centre)!;
        AuthoredLight late = Sunlight.For(new Timeblock(1, 4, IsAfternoon: true), Centre)!;

        Assert.True(late.Position.X < morning.Position.X);
    }

    [Fact]
    public void Night_and_dusk_have_no_sun()
    {
        // The 2am dig, and the evening blocks whose art is painted as dusk.
        Assert.Null(Sunlight.For(new Timeblock(3, 2, IsAfternoon: false), Centre));
        Assert.Null(Sunlight.For(new Timeblock(1, 6, IsAfternoon: true), Centre));
        Assert.Null(Sunlight.For(new Timeblock(2, 10, IsAfternoon: true), Centre));
    }

    [Fact]
    public void The_sun_is_a_distant_unattenuated_shadow_caster()
    {
        AuthoredLight sun = Sunlight.For(new Timeblock(1, 10, IsAfternoon: false), Centre)!;

        Assert.False(sun.UsesAttenuation);
        Assert.True(sun.CastsShadows);
        Assert.True(Vector3.Distance(sun.Position, Centre) > 10_000f);

        // Pointed back at the scene it lights.
        Assert.True(Vector3.Dot(sun.Direction, Centre - sun.Position) > 0f);
    }

    [Fact]
    public void A_low_sun_is_warmer_than_a_high_one()
    {
        AuthoredLight morning = Sunlight.For(new Timeblock(3, 7, IsAfternoon: false), Centre)!;
        AuthoredLight noon = Sunlight.For(new Timeblock(1, 12, IsAfternoon: true), Centre)!;

        float Warmth(AuthoredLight sun) => sun.Color.X - sun.Color.Z;

        Assert.True(Warmth(morning) > Warmth(noon));
    }

    /// <summary>A scenekey standing in a given direction, at a scenekey's distance.</summary>
    private static AuthoredLight Scenekey(float azimuth, float elevation, float intensity = 1f)
    {
        float radians = MathF.PI / 180f;
        var toward = new Vector3(
            MathF.Cos(elevation * radians) * MathF.Sin(azimuth * radians),
            MathF.Sin(elevation * radians),
            MathF.Cos(elevation * radians) * MathF.Cos(azimuth * radians));

        return new AuthoredLight(
            "scenekey", AuthoredLightKind.Point, Centre + (toward * 54_000f), -toward,
            new Vector3(0.53f, 0.48f, 0.40f),
            HotSpot: 0, Falloff: 0, AttenuationStart: 0, AttenuationEnd: 200f,
            UsesAttenuation: false, CastsShadows: true, Intensity: intensity, Radius: 0f);
    }

    private static float ElevationOf(AuthoredLight sun) =>
        MathF.Asin(-sun.Direction.Y) * 180f / MathF.PI;

    private static float AzimuthOf(AuthoredLight sun) =>
        MathF.Atan2(-sun.Direction.X, -sun.Direction.Z) * 180f / MathF.PI;

    /// <summary>
    /// The replacement takes the artists' aim, and only their aim.
    /// </summary>
    /// <remarks>
    /// The hour picked the scene asset, and the asset carries the key its room was baked
    /// under. Aiming by the hour instead put the light a median 42 degrees from that key
    /// across the corpus and 107 at worst, which is a room lit from somewhere the bake and
    /// the painted sky both disagree with. What the hour still decides is everything else:
    /// whether there is a sun at all, and — through the elevation the key turns out to
    /// stand at — how warm it is.
    /// </remarks>
    [Fact]
    public void An_authored_scenekey_aims_the_sun_that_replaces_it()
    {
        var timeblock = new Timeblock(1, 10, IsAfternoon: false);

        AuthoredLight aimed = Sunlight.For(timeblock, Centre, Scenekey(31f, 40f))!;

        Assert.Equal(40f, ElevationOf(aimed), 0.5f);
        Assert.Equal(31f, AzimuthOf(aimed), 0.5f);

        // And nothing else of it. The scenekey's two hundred unit range is exactly what
        // made it unusable; the replacement stands far enough out to be directional and
        // carries no falloff at all.
        Assert.False(aimed.UsesAttenuation);
        Assert.True(Vector3.Distance(aimed.Position, Centre) > 10_000f);
        Assert.True(aimed.Radius > 0f);
        Assert.True(aimed.Intensity > 1f);
    }

    [Fact]
    public void Without_a_scenekey_the_sun_still_follows_the_hour()
    {
        var timeblock = new Timeblock(1, 10, IsAfternoon: false);

        AuthoredLight arc = Sunlight.For(timeblock, Centre)!;
        AuthoredLight aimed = Sunlight.For(timeblock, Centre, Scenekey(31f, 40f))!;

        Assert.True(
            MathF.Abs(AzimuthOf(arc) - AzimuthOf(aimed)) > 45f,
            $"the two paths agreed by accident: {AzimuthOf(arc):0}° and {AzimuthOf(aimed):0}°");

        Assert.Equal(51f, ElevationOf(arc), 1f);
    }

    [Fact]
    public void The_brightest_of_two_distant_keys_is_the_one_that_aims_the_sun()
    {
        Vector3 minimum = Centre - new Vector3(1500f, 300f, 1000f);
        Vector3 maximum = Centre + new Vector3(1500f, 300f, 1000f);

        AuthoredLight[] rig = [Scenekey(200f, 25f, 0.4f), Scenekey(31f, 40f, 1f)];

        AuthoredLight? found = Sunlight.AuthoredSun(rig, minimum, maximum);

        Assert.Same(rig[1], found);
        Assert.Equal(31f, AzimuthOf(Sunlight.For(
            new Timeblock(1, 10, IsAfternoon: false), Centre, found)!), 0.5f);
    }

    /// <summary>
    /// A key underground is not an answer about where the sun is.
    /// </summary>
    /// <remarks>
    /// No scenekey in the corpus stands below the horizon — all 749 are between 24 and 62
    /// degrees up. A rig is a text file anybody may edit, though, and a scene lit from
    /// underneath is worse than one lit by the clock.
    /// </remarks>
    [Fact]
    public void A_key_below_the_horizon_falls_back_to_the_hour()
    {
        var timeblock = new Timeblock(1, 10, IsAfternoon: false);

        AuthoredLight below = Sunlight.For(timeblock, Centre, Scenekey(31f, -20f))!;
        AuthoredLight arc = Sunlight.For(timeblock, Centre)!;

        Assert.Equal(ElevationOf(arc), ElevationOf(below), 0.5f);
        Assert.Equal(AzimuthOf(arc), AzimuthOf(below), 0.5f);
    }

    [Fact]
    public void A_scenekey_does_not_put_a_sun_over_a_night_sky()
    {
        Assert.Null(Sunlight.For(
            new Timeblock(1, 6, IsAfternoon: true), Centre, Scenekey(31f, 40f)));
    }

    /// <summary>RC1's own rig, reduced to the shapes that matter to the recogniser.</summary>
    [Fact]
    public void The_recogniser_takes_the_scenekey_and_leaves_the_street_lamps()
    {
        Vector3 minimum = new(900f, -100f, -3300f);
        Vector3 maximum = new(3900f, 500f, -1300f);

        static AuthoredLight Light(
            Vector3 position, bool attenuated, bool shadows, float end) => new(
            "light", AuthoredLightKind.Point, position, -Vector3.UnitY, Vector3.One,
            HotSpot: 0.4f, Falloff: 0.45f, AttenuationStart: end * 0.4f, AttenuationEnd: end,
            UsesAttenuation: attenuated, CastsShadows: shadows, Intensity: 1f, Radius: 0f);

        // The scenekey: far outside the town, switch off, two hundred units of range.
        Assert.True(Sunlight.IsAuthoredSun(
            Light(new Vector3(13280f, 17988f, 16149f), false, true, 200f),
            minimum, maximum));

        // A street lamp inside the square with the same switch and a similar range.
        Assert.False(Sunlight.IsAuthoredSun(
            Light(new Vector3(2658f, 120f, -2518f), false, true, 240f),
            minimum, maximum));

        // The sky bounce: the scenekey's shape without its shadows. It stays.
        Assert.False(Sunlight.IsAuthoredSun(
            Light(new Vector3(-140f, 17255f, -7615f), false, false, 200f),
            minimum, maximum));

        // The ground bounce: distant, but its attenuation is real and reaches.
        Assert.False(Sunlight.IsAuthoredSun(
            Light(new Vector3(2998f, -4241f, -2037f), true, false, 5396f),
            minimum, maximum));
    }
}
