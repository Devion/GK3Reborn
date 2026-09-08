using GK3Reborn.Rendering.Upscaling;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// How a factor a player recognises becomes a count a runtime wants.
/// </summary>
public sealed class FrameGenerationTests
{
    [Theory]
    [InlineData(FrameGeneration.Off, 0)]
    [InlineData(FrameGeneration.Interpolated, 1)]
    [InlineData(FrameGeneration.Triple, 2)]
    [InlineData(FrameGeneration.Quadruple, 3)]
    public void A_factor_is_one_more_than_the_frames_it_generates(
        FrameGeneration generation, int generated)
    {
        Assert.Equal(generated, generation.Generated());
    }

    /// <summary>
    /// Nought is off rather than a count, because the runtime refuses a count of nought.
    /// </summary>
    [Fact]
    public void Off_generates_nothing()
    {
        Assert.Equal(0, FrameGeneration.Off.Generated());
        Assert.Equal("Off", FrameGeneration.Off.Describe());
    }

    [Theory]
    [InlineData(0, FrameGeneration.Off)]
    [InlineData(1, FrameGeneration.Interpolated)]
    [InlineData(2, FrameGeneration.Triple)]
    [InlineData(3, FrameGeneration.Quadruple)]
    [InlineData(9, FrameGeneration.Quadruple)]
    public void What_a_card_will_do_becomes_the_highest_setting_it_reaches(
        int generated, FrameGeneration most)
    {
        Assert.Equal(most, FrameGenerations.Most(generated));
    }

    /// <summary>Every setting is offered, in order, and each says its factor.</summary>
    [Fact]
    public void Every_setting_is_reachable_and_says_what_it_is()
    {
        Assert.Equal(
            [FrameGeneration.Off, FrameGeneration.Interpolated,
             FrameGeneration.Triple, FrameGeneration.Quadruple],
            FrameGenerations.All);

        Assert.Equal(["Off", "2x", "3x", "4x"],
            FrameGenerations.All.Select(g => g.Describe()));
    }

    /// <summary>
    /// A setting written out and read back is the setting that was written.
    /// </summary>
    [Fact]
    public void The_two_times_setting_keeps_the_name_it_was_saved_under()
    {
        Assert.Equal(
            FrameGeneration.Interpolated,
            Enum.Parse<FrameGeneration>("Interpolated"));

        Assert.Equal(1, FrameGeneration.Interpolated.Generated());
    }
}
