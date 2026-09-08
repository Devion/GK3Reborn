using GK3Reborn.Game;
using GK3Reborn.Rendering.Upscaling;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// What the neural rendering settings mean by the time they reach the network.
/// </summary>
public sealed class NeuralUpliftTests
{
    /// <summary>Nothing is on until somebody turns it on.</summary>
    [Fact]
    public void The_network_is_off_until_it_is_asked_for()
    {
        Assert.False(NeuralUplift.None.Enabled);
        Assert.False(new Settings().NeuralUplift);
        Assert.False(new Settings().Upscaling.Neural.Enabled);
    }

    /// <summary>Every strength is clamped, including the ones that are not numbers.</summary>
    [Theory]
    [InlineData(-3f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1f, 1f)]
    [InlineData(4f, 1f)]
    public void A_strength_is_kept_between_nothing_and_all_of_it(float set, float expected)
    {
        NeuralUplift uplift = new NeuralUplift
        {
            Intensity = set,
            LocalTone = set,
            GlobalTone = set,
            LocalStructure = set,
            SkinStructure = set,
        }.Sane();

        Assert.Equal(expected, uplift.Intensity);
        Assert.Equal(expected, uplift.LocalTone);
        Assert.Equal(expected, uplift.GlobalTone);
        Assert.Equal(expected, uplift.LocalStructure);
        Assert.Equal(expected, uplift.SkinStructure);
    }

    /// <summary>A strength that is not a number becomes the full one rather than nothing.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void A_strength_that_is_not_a_number_becomes_the_whole_of_it(float set)
    {
        Assert.Equal(1f, new NeuralUplift { Intensity = set }.Sane().Intensity);
    }

    /// <summary>Negative one is how the network is told to follow the general strength.</summary>
    [Fact]
    public void Skin_follows_the_general_strength_through_a_negative_one()
    {
        var following = new NeuralUplift { SkinFollowsStructure = true, SkinStructure = 0.25f };

        Assert.Equal(-1f, following.SkinStrength);
        Assert.Equal(0.25f, (following with { SkinFollowsStructure = false }).SkinStrength);
    }

    /// <summary>The sentinel survives being clamped.</summary>
    [Fact]
    public void Making_a_record_sane_does_not_lose_the_sentinel()
    {
        NeuralUplift sane = new NeuralUplift
        {
            SkinFollowsStructure = true,
            SkinStructure = -7f,
        }.Sane();

        Assert.Equal(-1f, sane.SkinStrength);
    }

    /// <summary>A preset or a style is a number, and stays inside the range one can be.</summary>
    [Fact]
    public void A_network_and_a_look_are_numbers_within_range()
    {
        Assert.Equal(0, new NeuralUplift { Preset = -4 }.Sane().Preset);
        Assert.Equal(NeuralUplift.Highest, new NeuralUplift { Style = 99 }.Sane().Style);

        Assert.Equal("Whatever the network prefers", NeuralUplift.Describe(0));
        Assert.Equal("Number 3", NeuralUplift.Describe(3));
    }

    /// <summary>The settings reach the plan, and reach it sane.</summary>
    [Fact]
    public void The_settings_page_reaches_the_renderer()
    {
        var settings = new Settings
        {
            NeuralUplift = true,
            NeuralIntensity = 0.75f,
            NeuralLocalTone = 9f,
            NeuralGlobalTone = 0.25f,
            NeuralLocalStructure = 0.5f,
            NeuralSkinFollowsStructure = false,
            NeuralSkinStructure = 0.125f,
            NeuralAutoSkinMask = false,
            NeuralPreset = 2,
            NeuralStyle = 40,
        };

        NeuralUplift uplift = settings.Upscaling.Neural;

        Assert.True(uplift.Enabled);
        Assert.Equal(0.75f, uplift.Intensity);
        Assert.Equal(1f, uplift.LocalTone);
        Assert.Equal(0.25f, uplift.GlobalTone);
        Assert.Equal(0.5f, uplift.LocalStructure);
        Assert.Equal(0.125f, uplift.SkinStrength);
        Assert.False(uplift.AutoSkinMask);
        Assert.Equal(2, uplift.Preset);
        Assert.Equal(NeuralUplift.Highest, uplift.Style);
    }

    /// <summary>Turning the network on draws the room at the size the window is.</summary>
    [Theory]
    [InlineData(UpscalerQuality.Performance)]
    [InlineData(UpscalerQuality.Quality)]
    [InlineData(UpscalerQuality.UltraPerformance)]
    public void The_network_draws_at_the_size_it_shows(UpscalerQuality asked)
    {
        var plan = new UpscalePlan
        {
            Kind = UpscalerKind.Dlss,
            Quality = asked,
            Neural = new NeuralUplift { Enabled = true },
        }.Sane();

        Assert.Equal(UpscalerQuality.Native, plan.Quality);
        Assert.Equal(1f, plan.Ratio);
        Assert.Equal((1920, 1080), plan.RenderSize(1920, 1080));
    }

    /// <summary>With the network off, the rung the player chose is the rung they get.</summary>
    [Fact]
    public void Without_the_network_the_rung_is_left_alone()
    {
        var plan = new UpscalePlan
        {
            Kind = UpscalerKind.Dlss,
            Quality = UpscalerQuality.Performance,
        }.Sane();

        Assert.Equal(UpscalerQuality.Performance, plan.Quality);
        Assert.Equal((960, 540), plan.RenderSize(1920, 1080));
    }

    /// <summary>A plan with nothing in that field still has something to ask.</summary>
    [Fact]
    public void A_plan_without_a_record_gets_the_empty_one()
    {
        Assert.Same(NeuralUplift.None, new UpscalePlan { Neural = null! }.Sane().Neural);
    }

    /// <summary>
    /// The parameter names reach NGX with the terminating nought it reads to.
    /// </summary>
    [Fact]
    public unsafe void A_parameter_name_is_terminated_where_it_says_it_ends()
    {
        ReadOnlySpan<byte> name = "DLSSNR.Color"u8;

        Assert.Equal(12, name.Length);

        fixed (byte* text = name)
        {
            Assert.Equal(0, text[name.Length]);
        }
    }
}
