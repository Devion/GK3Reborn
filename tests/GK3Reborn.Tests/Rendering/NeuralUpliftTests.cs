using GK3Reborn.Game;
using GK3Reborn.Rendering.Upscaling;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// What the neural rendering settings mean by the time they reach the network.
/// </summary>
/// <remarks>
/// <para>
/// The network is <c>nvngx_dlssnr.dll</c>, which the driver will not load until the blank
/// entry in its feature table is filled in — see <c>NgxFeatureTable</c>. Nothing here can
/// talk to it: that needs a GeForce, the file, a driver and a frame. What is tested is
/// everything on this side of the boundary — that a settings file somebody edited cannot put
/// a value into the network meaning something other than what it says, that the one sentinel
/// the network defines survives the trip, and that the rung it is given is the only one it
/// will accept.
/// </para>
/// <para>
/// Worth testing for the reason the rest of this integration is documented so heavily: every
/// number below is passed by name into somebody else's DLL, which reads it, believes it and
/// says nothing. A strength that arrives as a not-a-number comes back as a frame of nothing.
/// </para>
/// </remarks>
public sealed class NeuralUpliftTests
{
    /// <summary>Nothing is on until somebody turns it on.</summary>
    /// <remarks>
    /// The one default worth asserting. This changes how the game looks rather than fixing
    /// how it looks, and a port whose business is the 1999 picture must not restyle it for
    /// somebody who never asked.
    /// </remarks>
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
    /// <remarks>
    /// Full, because nought is not "leave the picture alone" for this network — it is asking
    /// it to do none of what it does, which it does not answer by passing the frame through.
    /// A settings file that has gone wrong should land on the value somebody who set nothing
    /// would have had.
    /// </remarks>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void A_strength_that_is_not_a_number_becomes_the_whole_of_it(float set)
    {
        Assert.Equal(1f, new NeuralUplift { Intensity = set }.Sane().Intensity);
    }

    /// <summary>Negative one is how the network is told to follow the general strength.</summary>
    /// <remarks>
    /// The sentinel is the network's, not this project's, and it is the reason the page has a
    /// toggle rather than a slider that runs below nought: "follow the other setting" and
    /// "none at all" are different answers and one slider cannot say both.
    /// </remarks>
    [Fact]
    public void Skin_follows_the_general_strength_through_a_negative_one()
    {
        var following = new NeuralUplift { SkinFollowsStructure = true, SkinStructure = 0.25f };

        Assert.Equal(-1f, following.SkinStrength);
        Assert.Equal(0.25f, (following with { SkinFollowsStructure = false }).SkinStrength);
    }

    /// <summary>The sentinel survives being clamped.</summary>
    /// <remarks>
    /// <see cref="NeuralUplift.SkinStructure"/> is clamped to the ordinary range and the
    /// sentinel is produced by the toggle, so sanity checking a record cannot destroy it —
    /// which it would if the two shared one field.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Not a preference: the network refuses every frame it is asked to scale. The plugin
    /// that drives it sets no scaling ratio for it — the parameter names for one are not even
    /// in the plugin — so it is handed an input the size it was not built for and answers
    /// with an invalid-parameter error, once a frame, for ever. What the player would see is
    /// the small picture stretched.
    /// </para>
    /// <para>
    /// Pinned here rather than in the renderer so that the size the room is drawn at, the
    /// size the settings page reports, and the size the network is given cannot disagree.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <b>This is the assumption the whole parameter block rests on.</b> Names are UTF-8
    /// literals so that taking their address costs nothing — the alternative is marshalling a
    /// managed string forty-odd times a frame — and that is only safe because such a literal
    /// is laid down with a nought after it that its length does not count. NGX is handed the
    /// address and reads until it finds one.
    /// </para>
    /// <para>
    /// If a future language version stopped appending it, nothing would fail to compile and
    /// nothing would warn: NGX would read whatever followed in the assembly's data and look
    /// up a parameter under a name nobody set, which is a network that quietly ignores every
    /// texture it was given.
    /// </para>
    /// </remarks>
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
