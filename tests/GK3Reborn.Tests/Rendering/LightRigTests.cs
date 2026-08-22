using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for how an authored light becomes one the shader can read.
/// </summary>
public sealed class LightRigTests
{
    [Fact]
    public void A_light_that_states_no_range_at_all_reaches_the_whole_scene()
    {
        // Nothing to honour. A light with the switch off and no stored range says nothing
        // whatever about its reach, and unlimited is the only honest reading of that.
        AuthoredLight sun = Light("scenekey", usesAttenuation: false, start: 0f, end: 0f);

        Assert.Equal(GpuLight.Unlimited, GpuLight.RangeOf(sun));

        GpuLight packed = GpuLight.From(sun);

        Assert.True(
            Reach(50_000f, packed.PositionAndStart.W, packed.DirectionAndEnd.W) > 0.999f,
            "a light with no stated range fell off before it arrived");
    }

    [Fact]
    public void A_stored_range_is_honoured_even_with_the_switch_off()
    {
        // 3ds Max's far attenuation being off means the light had no decay while the scene
        // was being baked. Reproducing that at runtime is faithful and unusable: a light
        // with no falloff lights every surface it can see equally, so a rig's fill lights
        // become a flat wash with no source anywhere in the room. The hotel lobby is the
        // case — 82% of the light arriving at the middle of its floor came from lights
        // with the switch off, one of them 842 units outside the room.
        //
        // The ranges are in the file and they are the artists' own: every one of the
        // lobby's fourteen switched-off lights carries a full near and far pair, set by
        // hand and then disabled, which is a normal way to work in Max.
        AuthoredLight fill = Light("omni02", usesAttenuation: false, start: 10f, end: 77f);

        Assert.Equal(77f, GpuLight.RangeOf(fill));

        GpuLight packed = GpuLight.From(fill);
        float start = packed.PositionAndStart.W;
        float end = packed.DirectionAndEnd.W;

        // And it ramps rather than stopping dead. A light whose start equals its end is
        // full brightness to a hard edge and then nothing, and that edge is a visible
        // circle on a floor.
        Assert.Equal(1f, Reach(5f, start, end), 3);
        Assert.True(Reach(45f, start, end) is > 0.1f and < 0.9f, "no ramp between near and far");
        Assert.Equal(0f, Reach(90f, start, end), 3);
    }

    [Fact]
    public void A_light_with_attenuation_on_still_stops_at_its_stored_range()
    {
        AuthoredLight lamp = Light("omni01", usesAttenuation: true, start: 42f, end: 133f);

        Assert.Equal(133f, GpuLight.RangeOf(lamp));

        GpuLight packed = GpuLight.From(lamp);

        Assert.Equal(1f, Reach(40f, packed.PositionAndStart.W, packed.DirectionAndEnd.W), 3);
        Assert.Equal(0f, Reach(140f, packed.PositionAndStart.W, packed.DirectionAndEnd.W), 3);
    }

    [Fact]
    public void A_light_with_no_stated_range_outranks_a_bright_but_short_one()
    {
        // The order decides which lights get a shadow ray, so a light that reaches the
        // whole map has to sort ahead of one that reaches across a table. What makes a
        // light reach the whole map is stating no range, not having the switch off: one
        // with the switch off and a range of two hundred now sorts on that two hundred,
        // which is the point of honouring it.
        AuthoredLight sun = Light("scenekey", usesAttenuation: false, start: 0f, end: 0f);
        AuthoredLight lamp = Light("omni01", usesAttenuation: true, start: 0f, end: 133f) with
        {
            Intensity = 3f,
        };

        IReadOnlyList<AuthoredLight> chosen = GpuLight.Choose([lamp, sun]);

        Assert.Equal("scenekey", chosen[0].Name);

        // And the ranged one does not: a bright lamp across a table beats a dim light that
        // stops at two hundred units, which is what the numbers actually say.
        AuthoredLight ranged = Light("scenefill", usesAttenuation: false, start: 80f, end: 200f);

        Assert.Equal("omni01", GpuLight.Choose([ranged, lamp])[0].Name);
    }

    /// <summary>The shader's distance ramp, in the same form the fragment stage uses.</summary>
    private static float Reach(float distance, float start, float end) =>
        Math.Clamp((end - distance) / MathF.Max(end - start, 0.001f), 0f, 1f);


    [Fact]
    public void The_scene_key_is_a_sun_and_not_a_light_with_a_two_hundred_unit_range()
    {
        // Every one of the game's 111 scenekey lights looks like this: the attenuation
        // switch off, a stored far range of about 200, and a position tens of thousands of
        // units away. Honouring that range does not dim the sun, it deletes it — for the
        // ground as much as for the person standing on it, which is why exteriors had
        // baked building shadows and no daylight on anybody.
        AuthoredLight sun = At(
            new Vector3(13_280f, 17_988f, 16_149f),
            usesAttenuation: false,
            start: 80f,
            end: 200f);

        var town = new SceneExtent(new Vector3(-3_000f, -100f, -5_000f), new Vector3(5_000f, 2_000f, 1_000f));

        Assert.True(GpuLight.IsDistantKey(sun, town), "the scene key was not recognised as a sun");

        GpuLight packed = GpuLight.From(sun, town);

        Assert.True(packed.Cone.Z >= 1.5f, "the sun was not flagged directional for the shader");
    }

    [Fact]
    public void A_switched_off_fill_light_inside_the_room_keeps_its_range()
    {
        // The other half of the rule, and what keeps the lobby from washing out again.
        // This light also has its switch off, but it sits in the room it lights and its
        // range reaches the geometry, so the falloff is the artists' and is honoured.
        AuthoredLight fill = At(
            new Vector3(40f, 60f, 20f), usesAttenuation: false, start: 10f, end: 77f);

        var lobby = new SceneExtent(new Vector3(-400f, 0f, -400f), new Vector3(400f, 300f, 400f));

        Assert.False(GpuLight.IsDistantKey(fill, lobby), "a fill light in the room was treated as a sun");

        GpuLight packed = GpuLight.From(fill, lobby);

        Assert.True(packed.Cone.Z < 1.5f, "a fill light was flagged directional");
        Assert.Equal(77f, GpuLight.RangeOf(fill));
    }

    [Fact]
    public void Nothing_becomes_a_sun_when_the_scene_extent_is_unknown()
    {
        // The default extent has to decide nothing. An empty box would answer confidently
        // and wrongly: every light in the game is further from a point than its range, so
        // every light would become a sun and every room would be a flat wash.
        AuthoredLight fill = At(
            new Vector3(40f, 60f, 20f), usesAttenuation: false, start: 10f, end: 77f);

        Assert.False(GpuLight.IsDistantKey(fill, default));
    }

    [Fact]
    public void A_sun_outranks_the_lamps_when_the_rig_is_crowded()
    {
        // Sorted by the two hundred units left in the file, the sun is the first light
        // dropped from a full rig and the last given a shadow ray. It is the sun.
        AuthoredLight sun = At(
            new Vector3(13_280f, 17_988f, 16_149f), usesAttenuation: false, start: 80f, end: 200f);
        AuthoredLight lamp = At(
            new Vector3(10f, 60f, 10f), usesAttenuation: true, start: 100f, end: 2_000f);

        var town = new SceneExtent(new Vector3(-3_000f, -100f, -5_000f), new Vector3(5_000f, 2_000f, 1_000f));

        Assert.Equal("scenekey", GpuLight.Choose([lamp, sun], town)[0].Name);
    }

    private static AuthoredLight At(
        Vector3 position, bool usesAttenuation, float start, float end) =>
        new(
            usesAttenuation ? "omni02" : "scenekey",
            AuthoredLightKind.Point,
            position,
            -Vector3.UnitY,
            Vector3.One,
            HotSpot: 0f,
            Falloff: 0f,
            AttenuationStart: start,
            AttenuationEnd: end,
            UsesAttenuation: usesAttenuation,
            CastsShadows: true,
            Intensity: 1f,
            Radius: 2f);

    private static AuthoredLight Light(string name, bool usesAttenuation, float start, float end) =>
        new(
            name,
            AuthoredLightKind.Point,
            Vector3.Zero,
            -Vector3.UnitY,
            Vector3.One,
            HotSpot: 0f,
            Falloff: 0f,
            AttenuationStart: start,
            AttenuationEnd: end,
            UsesAttenuation: usesAttenuation,
            CastsShadows: true,
            Intensity: 1f,
            Radius: 2f);
}
