using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for the ramp the scene-change fade is driven along.
/// </summary>
/// <remarks>
/// The rest of <see cref="ScreenFade"/> is a device, a window and a clock, and is judged by
/// looking at it. This is the part with a property worth stating: the alpha it produces has
/// to darken the picture the way the eye reads darkening, not the way the blend arithmetic
/// happens to. Driven straight, a fade blended onto an sRGB target sits at three quarters
/// of its brightness when it is half way through and then falls off a cliff — which is what
/// this file exists to stop coming back.
/// </remarks>
public sealed class ScreenFadeTests
{
    /// <summary>What the display does to what the shader writes.</summary>
    private const double Gamma = 2.2;

    /// <summary>It starts at the picture and ends at nothing.</summary>
    [Fact]
    public void RunsFromNothingToBlack()
    {
        Assert.Equal(0f, ScreenFade.Curve(0), 4);
        Assert.Equal(1f, ScreenFade.Curve(1), 4);
    }

    /// <summary>And it never goes backwards, or past either end.</summary>
    [Fact]
    public void OnlyEverDarkens()
    {
        float last = ScreenFade.Curve(-1);

        for (int i = 0; i <= 100; i++)
        {
            float alpha = ScreenFade.Curve(i / 100.0);

            Assert.InRange(alpha, 0f, 1f);
            Assert.True(alpha >= last, $"the fade went back at {i}%: {alpha} after {last}");

            last = alpha;
        }

        Assert.Equal(1f, ScreenFade.Curve(2), 4);
    }

    /// <summary>
    /// Half way through, half the picture is gone — as the eye counts it.
    /// </summary>
    /// <remarks>
    /// The number this asserts is what the screen shows, not the alpha that produces it.
    /// Blending happens in linear light, so what survives an alpha of <c>a</c> is
    /// <c>(1 - a)</c> of the light and <c>(1 - a)^(1/2.2)</c> of the encoded value the
    /// display was sent — and it is the second of those a player is looking at. Smoothstep
    /// puts the middle of the fade exactly at the middle of its length, so this is the one
    /// point on the ramp with an answer that does not depend on the easing.
    /// </remarks>
    [Fact]
    public void IsHalfGoneHalfWayThrough()
    {
        double showing = Math.Pow(1 - ScreenFade.Curve(0.5), 1 / Gamma);

        Assert.Equal(0.5, showing, 2);
    }

    /// <summary>
    /// And what is showing falls in a straight line, once the easing is taken back out.
    /// </summary>
    /// <remarks>
    /// The easing is a smoothstep of how far through the fade is, so undoing it means
    /// asking for the alpha at the eased position rather than at the raw one. What comes
    /// back should be the brightness left, in a straight line — which is the whole claim
    /// the gamma correction makes.
    /// </remarks>
    [Theory]
    [InlineData(0.10)]
    [InlineData(0.25)]
    [InlineData(0.50)]
    [InlineData(0.75)]
    [InlineData(0.90)]
    public void DarkensEvenlyOnceTheEasingIsUndone(double through)
    {
        // Smoothstep, applied to the argument so that Curve's own easing lands where this
        // asks rather than somewhere near it.
        double eased = Solve(through);
        double showing = Math.Pow(1 - ScreenFade.Curve(eased), 1 / Gamma);

        Assert.Equal(1 - through, showing, 2);
    }

    /// <summary>The position whose smoothstep is <paramref name="wanted"/>.</summary>
    /// <param name="wanted">The eased value to hit.</param>
    /// <returns>The raw position, by bisection.</returns>
    private static double Solve(double wanted)
    {
        double low = 0;
        double high = 1;

        for (int i = 0; i < 60; i++)
        {
            double mid = (low + high) / 2;

            if (mid * mid * (3 - (2 * mid)) < wanted)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2;
    }
}
