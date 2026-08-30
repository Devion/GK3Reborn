using GK3Reborn.Rendering.Upscaling;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// How a factor a player recognises becomes a count a runtime wants.
/// </summary>
/// <remarks>
/// The two numbers are not the same and never can be: a player picks how many frames they
/// see for each one drawn, and the runtime is told how many to make. Four times is three
/// generated. Everywhere the two are confused the result is off by one in the direction
/// that costs a whole factor.
/// </remarks>
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
    /// <remarks>
    /// The plugin says so by name — "numFramesToGenerate must be greater than 0" — so
    /// turning generation off is a mode rather than a count, and nothing here may hand it a
    /// nought and expect it to mean anything.
    /// </remarks>
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
    /// <remarks>
    /// Settings are stored by name, which is why the two-times value is still called
    /// <c>Interpolated</c>: renaming it would be every existing player's choice failing to
    /// read back. This is the check that keeps somebody from tidying the name later.
    /// </remarks>
    [Fact]
    public void The_two_times_setting_keeps_the_name_it_was_saved_under()
    {
        Assert.Equal(
            FrameGeneration.Interpolated,
            Enum.Parse<FrameGeneration>("Interpolated"));

        Assert.Equal(1, FrameGeneration.Interpolated.Generated());
    }
}
