using System.Numerics;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Upscaling;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for what the renderer is asked to upscale, and where it samples while it does.
/// </summary>
/// <remarks>
/// None of this needs a device, which is the point of it living outside
/// <c>Rendering/Vulkan</c>: the ratios, the clamping and the jitter sequence are arithmetic,
/// and arithmetic that is wrong here is wrong on every machine rather than on somebody's.
/// </remarks>
public sealed class UpscalePlanTests
{
    [Theory]
    [InlineData(UpscalerQuality.Native, 1920, 1080)]
    [InlineData(UpscalerQuality.UltraQuality, 1477, 831)]
    [InlineData(UpscalerQuality.Quality, 1280, 720)]
    [InlineData(UpscalerQuality.Balanced, 1129, 635)]
    [InlineData(UpscalerQuality.Performance, 960, 540)]
    [InlineData(UpscalerQuality.UltraPerformance, 640, 360)]
    public void Each_rung_draws_the_room_at_the_size_its_name_promises(
        UpscalerQuality quality, int width, int height)
    {
        var plan = new UpscalePlan { Kind = UpscalerKind.Fsr, Quality = quality };

        Assert.Equal((width, height), plan.RenderSize(1920, 1080));
    }

    [Fact]
    public void Nothing_is_scaled_when_no_upscaler_is_chosen()
    {
        // Off means the room is drawn at the size of the window whatever the quality row
        // says, because the quality row is not on the page at all when it is off.
        var plan = new UpscalePlan
        {
            Kind = UpscalerKind.Off,
            Quality = UpscalerQuality.UltraPerformance,
        };

        Assert.Equal((1920, 1080), plan.RenderSize(1920, 1080));
        Assert.False(plan.Active);
        Assert.False(plan.Temporal);
    }

    [Fact]
    public void A_window_dragged_to_nothing_still_gives_a_size_a_swapchain_will_take()
    {
        // A zero-extent image is a device loss rather than a small picture, and a window
        // being dragged smaller passes through every size on the way.
        var plan = new UpscalePlan
        {
            Kind = UpscalerKind.Spatial,
            Quality = UpscalerQuality.UltraPerformance,
        };

        (int width, int height) = plan.RenderSize(1, 1);

        Assert.True(width >= 32 && height >= 32, "a render target must not be zero-sized");
    }

    [Fact]
    public void Only_the_two_that_accumulate_are_temporal()
    {
        // What the renderer decides whether to jitter the camera by. The spatial upscaler
        // has no history, and jittering a frame nothing accumulates is a wobble.
        Assert.False(new UpscalePlan { Kind = UpscalerKind.Spatial }.Temporal);
        Assert.True(new UpscalePlan { Kind = UpscalerKind.Fsr }.Temporal);
        Assert.True(new UpscalePlan { Kind = UpscalerKind.Dlss }.Temporal);
    }

    [Fact]
    public void A_hand_edited_plan_is_brought_back_inside_its_range()
    {
        var mangled = new UpscalePlan
        {
            Kind = (UpscalerKind)99,
            Quality = (UpscalerQuality)(-4),
            Sharpness = float.NaN,
            FrameGeneration = (FrameGeneration)7,
            DlssPreset = 900,
        }.Sane();

        Assert.Equal(UpscalerKind.Off, mangled.Kind);
        Assert.Equal(UpscalerQuality.Quality, mangled.Quality);
        Assert.Equal(0.5f, mangled.Sharpness);
        Assert.Equal(FrameGeneration.Off, mangled.FrameGeneration);
        Assert.Equal(DlssPresets.Highest, mangled.DlssPreset);
    }

    [Fact]
    public void The_page_says_the_two_resolutions_rather_than_the_ratio()
    {
        // "Quality" and "1.5x" both have to be converted before they mean anything. Two
        // resolutions are the thing itself.
        var plan = new UpscalePlan { Kind = UpscalerKind.Dlss, Quality = UpscalerQuality.Quality };

        Assert.Equal("1280x720 to 1920x1080", plan.Describe(1920, 1080));
        Assert.Equal("1920x1080", UpscalePlan.None.Describe(1920, 1080));
    }

    [Fact]
    public void The_presets_are_letters_and_run_past_the_ones_with_names()
    {
        // NVIDIA keeps adding letters. A build from before a preset existed still has to be
        // able to ask for it once somebody drops in a newer runtime.
        Assert.Equal("Whatever the runtime prefers", DlssPresets.Describe(0));
        Assert.StartsWith("Preset J", DlssPresets.Describe(10), StringComparison.Ordinal);
        Assert.StartsWith("Preset K", DlssPresets.Describe(11), StringComparison.Ordinal);
        Assert.Equal("Preset Q", DlssPresets.Describe(17));
        Assert.Equal("Preset Z", DlssPresets.Describe(DlssPresets.Highest));
    }
}

/// <summary>Tests for where inside its pixel each frame samples.</summary>
public sealed class JitterSequenceTests
{
    [Fact]
    public void The_sequence_lengthens_with_the_square_of_the_ratio()
    {
        // Four render pixels to a screen pixel's worth of area needs four times as many
        // samples to cover it, or the accumulation converges to a picture with holes.
        Assert.Equal(8, JitterSequence.PhaseCount(1920, 1920));
        Assert.Equal(18, JitterSequence.PhaseCount(1280, 1920));
        Assert.Equal(32, JitterSequence.PhaseCount(960, 1920));
        Assert.Equal(72, JitterSequence.PhaseCount(640, 1920));
    }

    [Fact]
    public void Every_offset_stays_inside_its_own_pixel()
    {
        for (long i = 0; i < 400; i++)
        {
            Vector2 offset = JitterSequence.Offset(i, 32);

            Assert.InRange(offset.X, -0.5f, 0.5f);
            Assert.InRange(offset.Y, -0.5f, 0.5f);
        }
    }

    [Fact]
    public void The_sequence_covers_the_pixel_rather_than_clustering_in_it()
    {
        // The property that makes a low-discrepancy sequence worth having: the mean is the
        // middle of the pixel, so a long accumulation is centred where the pixel is rather
        // than pulled to one side of it.
        Vector2 total = Vector2.Zero;

        for (long i = 0; i < 32; i++)
        {
            total += JitterSequence.Offset(i, 32);
        }

        Assert.True(Math.Abs(total.X / 32f) < 0.05f, "the sequence should be centred across");
        Assert.True(Math.Abs(total.Y / 32f) < 0.05f, "the sequence should be centred down");
    }

    [Fact]
    public void The_first_frame_does_not_sample_the_pixel_centre()
    {
        // Halton is one-based: element nought of both bases is zero, and starting there
        // would spend the first frame of every sequence sampling exactly the point the
        // jitter exists to move away from.
        Assert.NotEqual(Vector2.Zero, JitterSequence.Offset(0, 16));
    }

    [Fact]
    public void A_frame_counter_that_never_resets_is_a_valid_index()
    {
        // The renderer counts frames for the length of a session and does not restart the
        // count at a scene change, so the sequence has to take the modulus itself.
        Assert.Equal(JitterSequence.Offset(3, 16), JitterSequence.Offset(19, 16));
        Assert.Equal(JitterSequence.Offset(3, 16), JitterSequence.Offset(1_000_003, 16));
    }

    [Fact]
    public void A_pixel_offset_becomes_a_clip_offset_of_the_same_direction()
    {
        // The whole frame is two units across in clip space, so half a pixel of a
        // 1000-pixel-wide target is a thousandth of it.
        Vector2 clip = JitterSequence.ToClip(new Vector2(0.5f, -0.5f), 1000, 500);

        Assert.Equal(0.001f, clip.X, 6);
        Assert.Equal(-0.002f, clip.Y, 6);
    }
}

/// <summary>Tests for what the end of the frame does with the picture.</summary>
public sealed class OutputPlanTests
{
    [Fact]
    public void Standard_range_gives_nothing_any_headroom()
    {
        // In SDR a lamp and a white wall are both one, because one is all an 8-bit target
        // holds. Every gain has to be exactly one or the picture changes for everybody.
        OutputPlan plan = OutputPlan.Standard;

        Assert.Equal(1f, plan.EmissiveGain);
        Assert.Equal(1f, plan.SunGain);
        Assert.Equal(1f, plan.Headroom);
    }

    [Fact]
    public void The_gains_are_the_luminances_measured_against_paper_white()
    {
        var plan = new OutputPlan
        {
            HighDynamicRange = true,
            PaperWhiteNits = 200f,
            PeakNits = 1000f,
            SunNits = 800f,
            LightNits = 1000f,
        };

        Assert.Equal(4f, plan.SunGain);
        Assert.Equal(5f, plan.EmissiveGain);
        Assert.Equal(5f, plan.Headroom);
    }

    [Fact]
    public void The_luminances_are_clamped_against_each_other_rather_than_singly()
    {
        // A peak below paper white would ask the encoder for negative headroom, and a sun
        // dimmer than a wall is not a sun. Their bounds depend on each other, which is why
        // there is one place that applies them.
        OutputPlan plan = new OutputPlan
        {
            HighDynamicRange = true,
            PaperWhiteNits = 400f,
            PeakNits = 100f,
            SunNits = 10f,
            LightNits = float.NaN,
        }.Sane();

        Assert.Equal(400f, plan.PaperWhiteNits);
        Assert.True(plan.PeakNits >= plan.PaperWhiteNits, "the peak cannot be below paper white");
        Assert.True(plan.SunNits >= plan.PaperWhiteNits, "the sun cannot be dimmer than a wall");
        Assert.True(float.IsFinite(plan.LightNits), "a lamp needs a number");
    }

    [Fact]
    public void The_gains_cannot_run_away_however_the_file_was_edited()
    {
        OutputPlan plan = new OutputPlan
        {
            HighDynamicRange = true,
            PaperWhiteNits = 40f,
            PeakNits = 10_000f,
            SunNits = 10_000f,
            LightNits = 10_000f,
        }.Sane();

        // Clamped at sixty-four, which is far past anything a display can show and far
        // short of a number that would overflow a half-float target.
        Assert.True(plan.SunGain <= 64f);
        Assert.True(plan.EmissiveGain <= 64f);
        Assert.True(plan.Headroom <= 100f);
    }
}
