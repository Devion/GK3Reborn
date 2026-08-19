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
    public void A_light_with_attenuation_switched_off_reaches_the_whole_scene()
    {
        // R25's afternoon key light: the sun, fifty thousand units out, with a stored
        // range of two hundred that means nothing because the switch is off. Honouring
        // that range put the sun behind its own falloff and no daylight entered the room.
        AuthoredLight sun = Light("scenekey", usesAttenuation: false, start: 80f, end: 200f);

        Assert.Equal(GpuLight.Unlimited, GpuLight.RangeOf(sun));

        GpuLight packed = GpuLight.From(sun);
        float far = packed.DirectionAndEnd.W;
        float start = packed.PositionAndStart.W;

        // Fifty thousand units away, the shader's ramp between start and end still has to
        // come out at full brightness.
        Assert.True(Reach(50_000f, start, far) > 0.999f, "the sun fell off before it arrived");
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
    public void An_unattenuated_light_outranks_a_bright_but_short_one()
    {
        // The order decides which lights get a shadow ray, so a light that reaches the
        // whole map has to sort ahead of one that reaches across a table.
        AuthoredLight sun = Light("scenekey", usesAttenuation: false, start: 80f, end: 200f);
        AuthoredLight lamp = Light("omni01", usesAttenuation: true, start: 0f, end: 133f) with
        {
            Intensity = 3f,
        };

        IReadOnlyList<AuthoredLight> chosen = GpuLight.Choose([lamp, sun]);

        Assert.Equal("scenekey", chosen[0].Name);
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
