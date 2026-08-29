using System.Numerics;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Direct3D12;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Inline ray tracing on Direct3D, which is three separate acts of faith until it is looked at.
/// </summary>
/// <remarks>
/// <para>
/// The shaders are GLSL and Direct3D cannot read a word of them; they reach it through
/// SPIRV-Cross and DXC. That chain compiling is not the same as it being right. What these
/// tests check is that a <c>rayQueryEXT</c> translated into a <c>RayQuery</c> traces the
/// same rays, that an acceleration structure built from this engine's matrices puts the
/// geometry where the engine thinks it is, and that an acceleration structure binds
/// correctly through a view made from an address rather than from a resource.
/// </para>
/// <para>
/// Every one of those fails as a plausible wrong picture rather than as an error: a shadow
/// in the wrong place, a room lit as though nothing were in it. So the answer is arranged
/// to have a shape — a square blocker's shadow on a square grid of rays — which is either
/// in the right place or obviously not.
/// </para>
/// </remarks>
public sealed class D3D12RayTracingTests
{
    private static bool CanTrace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            DeviceReport report = D3D12DeviceSelector.Survey();
            return report.Selected?.Tiers.HasFlag(RenderCapabilityTier.RayTracing) == true;
        }
        catch (D3D12Exception)
        {
            return false;
        }
    }

    private static float At(float[] mask, int x, int y) => mask[(y * D3D12TraceProbe.Side) + x];

    [Fact]
    public void A_translated_ray_query_casts_the_shadow_of_what_it_traces()
    {
        Assert.SkipUnless(CanTrace(), "no Direct3D device with inline ray tracing");

        using D3D12TraceProbe probe = D3D12TraceProbe.Create();
        float[] mask = probe.Trace(half: 4f);

        // The blocker covers world -4 to +4; the ray origins run -7.5 to +7.5 in steps of
        // one, so cells 4 through 11 in each direction are under it. Sixty-four blocked
        // rays, in an eight-by-eight square in the middle.
        Assert.Equal(64, mask.Count(v => v > 0.5f));

        for (int y = 0; y < D3D12TraceProbe.Side; y++)
        {
            for (int x = 0; x < D3D12TraceProbe.Side; x++)
            {
                bool under = x is >= 4 and <= 11 && y is >= 4 and <= 11;
                Assert.Equal(under, At(mask, x, y) > 0.5f);
            }
        }
    }

    [Fact]
    public void A_smaller_blocker_casts_a_smaller_shadow()
    {
        Assert.SkipUnless(CanTrace(), "no Direct3D device with inline ray tracing");

        using D3D12TraceProbe probe = D3D12TraceProbe.Create();

        // Not merely a different number: a blocker half as wide has a quarter the shadow.
        // A ray query that reported a constant would pass the first test and fail this one.
        Assert.Equal(64, probe.Trace(half: 4f).Count(v => v > 0.5f));
        Assert.Equal(16, probe.Trace(half: 2f).Count(v => v > 0.5f));
        Assert.Equal(0, probe.Trace(half: 0.2f).Count(v => v > 0.5f));
    }

    [Fact]
    public void The_instance_transform_moves_the_geometry_where_it_says()
    {
        Assert.SkipUnless(CanTrace(), "no Direct3D device with inline ray tracing");

        using D3D12TraceProbe probe = D3D12TraceProbe.Create();

        // Four units along positive X. Direct3D wants a three-by-four instance transform
        // that is the transpose of the four-by-four this engine carries, and a transform
        // written the wrong way round does not fail — it puts the geometry somewhere
        // plausible and wrong. A translation that came out in the wrong axis, or not at
        // all, is exactly what that looks like.
        float[] moved = probe.Trace(half: 4f, offset: new Vector3(4f, 0f, 0f));

        Assert.Equal(64, moved.Count(v => v > 0.5f));

        for (int y = 0; y < D3D12TraceProbe.Side; y++)
        {
            for (int x = 0; x < D3D12TraceProbe.Side; x++)
            {
                bool under = x is >= 8 and <= 15 && y is >= 4 and <= 11;
                Assert.Equal(under, At(moved, x, y) > 0.5f);
            }
        }
    }

    [Fact]
    public void Tracing_says_nothing_to_the_debug_layer()
    {
        Assert.SkipUnless(CanTrace(), "no Direct3D device with inline ray tracing");

        using D3D12TraceProbe probe = D3D12TraceProbe.Create();
        probe.Trace();

        // An acceleration structure has more ways to be built almost correctly than
        // anything else on the device — a scratch buffer in the wrong state, a missing
        // barrier between the two levels, a view made from a resource that should have been
        // an address — and the debug layer is the only thing that says so before the
        // picture does.
        Assert.DoesNotContain(
            probe.Messages,
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }
}
