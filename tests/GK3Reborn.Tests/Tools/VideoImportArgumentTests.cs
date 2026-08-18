using GK3Reborn.Tools.Media;
using GK3Reborn.Tools.Stages;
using Xunit;

namespace GK3Reborn.Tests.Tools;

public sealed class VideoImportArgumentTests
{
    private static MediaProbe Probe(int width, int height, string? audio = "binkaudio_rdft") => new()
    {
        Container = "bink",
        DurationSeconds = 10,
        VideoCodec = "binkvideo",
        Width = width,
        Height = height,
        FrameRate = "30/1",
        AudioCodec = audio,
        AudioSampleRate = audio is null ? null : 22050,
        AudioChannels = audio is null ? null : 2,
    };

    [Fact]
    public void Even_sized_sources_use_4_2_0()
    {
        List<string> args = VideoImportStage.BuildArguments("in.bik", Probe(320, 240));

        Assert.Contains("yuv420p", args, StringComparer.Ordinal);
        Assert.DoesNotContain("yuv444p", args, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(41, 51)]
    [InlineData(389, 424)]
    [InlineData(431, 350)]
    [InlineData(320, 241)]
    public void Odd_sized_sources_use_4_4_4_rather_than_being_padded(int width, int height)
    {
        // H.264 4:2:0 cannot represent odd dimensions. Padding or cropping would
        // shift the UI overlays these Sidney scan clips sit under, so they encode
        // as 4:4:4 instead. Dimensions must survive untouched.
        List<string> args = VideoImportStage.BuildArguments("in.avi", Probe(width, height));

        Assert.Contains("yuv444p", args, StringComparer.Ordinal);
        Assert.DoesNotContain("-vf", args, StringComparer.Ordinal);
        Assert.DoesNotContain("scale", string.Join(' ', args), StringComparison.Ordinal);
        Assert.DoesNotContain("pad", string.Join(' ', args), StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_is_resampled_once_to_the_mixer_rate()
    {
        List<string> args = VideoImportStage.BuildArguments("in.bik", Probe(320, 240));

        int index = args.IndexOf("-ar");
        Assert.True(index >= 0);
        Assert.Equal("48000", args[index + 1]);
    }

    [Fact]
    public void Sources_without_audio_get_no_audio_flags()
    {
        List<string> args = VideoImportStage.BuildArguments("in.avi", Probe(320, 240, audio: null));

        Assert.DoesNotContain("-c:a", args, StringComparer.Ordinal);
        Assert.DoesNotContain("0:a:0", args, StringComparer.Ordinal);
    }

    [Fact]
    public void Frame_timing_is_passed_through_rather_than_resampled()
    {
        List<string> args = VideoImportStage.BuildArguments("in.bik", Probe(320, 240));

        int index = args.IndexOf("-fps_mode");
        Assert.True(index >= 0);
        Assert.Equal("passthrough", args[index + 1]);
        Assert.Contains("+faststart", args, StringComparer.Ordinal);
    }

    [Fact]
    public void Odd_dimension_detection_matches_the_probe()
    {
        Assert.True(Probe(41, 51).HasOddDimensions);
        Assert.True(Probe(389, 424).HasOddDimensions);
        Assert.False(Probe(320, 240).HasOddDimensions);
    }
}
