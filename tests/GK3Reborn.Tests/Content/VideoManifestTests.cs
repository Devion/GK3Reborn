using System.Text.Json;
using GK3Reborn.Content.Manifests;
using Xunit;

namespace GK3Reborn.Tests.Content;

public sealed class VideoManifestTests
{
    private static VideoManifest Sample() => new()
    {
        SchemaVersion = 1,
        Stage = "C7.video",
        ConverterVersion = "1.0.0",
        SourceRoot = "D:/GK3/Data",
        OutputRoot = "D:/ws/build/video",
        Summary = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["total"] = 2,
            ["converted"] = 1,
            ["unreadable-source"] = 1,
        },
        Entries =
        [
            new VideoEntry
            {
                LogicalId = "DAY1-1",
                Status = VideoEntryStatus.UnreadableSource,
                Source = new VideoMedia { File = "day1-1.bik", Bytes = 78452860, Sha256 = "ab" },
                Diagnostic = new VideoDiagnostic
                {
                    ProbeError = "Invalid data found when processing input",
                    Remediation = "Re-acquire the installation.",
                },
            },
            new VideoEntry
            {
                LogicalId = "INTRO",
                Status = VideoEntryStatus.Converted,
                Source = new VideoMedia
                {
                    File = "intro.bik", Bytes = 23274528, Sha256 = "cd",
                    Container = "bink", VideoCodec = "binkvideo",
                    Width = 320, Height = 240, FrameRate = "30/1", DurationSeconds = 213.0,
                    AudioCodec = "binkaudio_rdft", AudioSampleRate = 22050, AudioChannels = 2,
                },
                Output = new VideoMedia
                {
                    File = "video/INTRO.mp4", Bytes = 19900000, Sha256 = "ef",
                    Container = "mov,mp4,m4a,3gp,3g2,mj2", VideoCodec = "h264",
                    Width = 320, Height = 240, FrameRate = "30/1", DurationSeconds = 213.0,
                    AudioCodec = "aac", AudioSampleRate = 48000, AudioChannels = 2,
                },
                Validation = new VideoValidation
                {
                    DimensionsMatch = true,
                    FrameRateMatch = true,
                    DurationDriftSeconds = 0.0,
                    DurationWithinTolerance = true,
                    AudioPreserved = true,
                },
                Recipe = new VideoRecipe
                {
                    Converter = "gk3reborn.video",
                    ConverterVersion = "1.0.0",
                    Arguments = ["-y", "-i", "intro.bik", "<output>"],
                },
            },
        ],
    };

    [Fact]
    public void Round_trips_through_json()
    {
        string json = JsonSerializer.Serialize(Sample(), ManifestJson.Options);
        VideoManifest? back = JsonSerializer.Deserialize<VideoManifest>(json, ManifestJson.Options);

        Assert.NotNull(back);
        Assert.Equal(2, back.Entries.Count);
        Assert.Equal(VideoEntryStatus.UnreadableSource, back.Entries[0].Status);
        Assert.Equal("INTRO", back.Entries[1].LogicalId);
        Assert.Equal(48000, back.Entries[1].Output!.AudioSampleRate);
    }

    [Fact]
    public void Serialization_is_deterministic()
    {
        // Two runs over identical inputs must produce byte-identical manifests.
        string first = JsonSerializer.Serialize(Sample(), ManifestJson.Options);
        string second = JsonSerializer.Serialize(Sample(), ManifestJson.Options);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Status_serializes_as_the_kebab_case_wire_value()
    {
        string json = JsonSerializer.Serialize(Sample(), ManifestJson.Options);
        Assert.Contains("\"unreadable-source\"", json, StringComparison.Ordinal);
        Assert.Contains("\"converted\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_entry_is_not_playable()
    {
        VideoManifest manifest = Sample();
        Assert.False(manifest.Entries[0].IsPlayable);
        Assert.True(manifest.Entries[1].IsPlayable);
    }

    [Fact]
    public void Validation_reports_overall_pass()
    {
        Assert.True(Sample().Entries[1].Validation!.AllPassed);

        var failing = Sample().Entries[1].Validation! with { FrameRateMatch = false };
        Assert.False(failing.AllPassed);
    }
}
