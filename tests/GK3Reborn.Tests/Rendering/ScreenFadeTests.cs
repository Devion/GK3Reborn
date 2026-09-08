using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for the ramp the scene-change fade is driven along.
/// </summary>
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
    [Fact]
    public void IsHalfGoneHalfWayThrough()
    {
        double showing = Math.Pow(1 - ScreenFade.Curve(0.5), 1 / Gamma);

        Assert.Equal(0.5, showing, 2);
    }

    /// <summary>
    /// And what is showing falls in a straight line, once the easing is taken back out.
    /// </summary>
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
