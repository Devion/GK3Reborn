using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

public sealed class FrameClockTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void Recording_shader_time_depends_on_frames_not_render_duration(int fps)
    {
        FrameClock first = FrameClock.At(0, fps);
        FrameClock later = FrameClock.At(8 * fps, fps);
        Assert.Equal(0f, first.Seconds);
        Assert.Equal(8f, later.Seconds);
        Assert.Equal(1f / fps, later.DeltaSeconds);
        Assert.Equal(later, FrameClock.At(8 * fps, fps));
    }

    [Fact]
    public void Invalid_recording_clock_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameClock.At(-1, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameClock.At(0, 0));
    }
}
