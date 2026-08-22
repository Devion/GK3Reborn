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
