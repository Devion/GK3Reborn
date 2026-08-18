using System.Globalization;
using System.Text.Json;

namespace GK3Reborn.Tools.Media;

/// <summary>Stream and format facts extracted from an ffprobe result.</summary>
public sealed record MediaProbe
{
    /// <summary>Container format name.</summary>
    public required string Container { get; init; }

    /// <summary>Duration in seconds.</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>Video codec name.</summary>
    public required string VideoCodec { get; init; }

    /// <summary>Frame width.</summary>
    public required int Width { get; init; }

    /// <summary>Frame height.</summary>
    public required int Height { get; init; }

    /// <summary>Frame rate as an exact rational, e.g. "30/1".</summary>
    public required string FrameRate { get; init; }

    /// <summary>Audio codec name, or null when the file has no audio.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Audio sample rate in Hz.</summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>Audio channel count.</summary>
    public int? AudioChannels { get; init; }

    /// <summary>True when either dimension is odd.</summary>
    public bool HasOddDimensions => (Width % 2) != 0 || (Height % 2) != 0;

    /// <summary>Reads a probe result, or returns null when it has no video stream.</summary>
    public static MediaProbe? FromJson(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("streams", out JsonElement streams))
        {
            return null;
        }

        JsonElement? video = null;
        JsonElement? audio = null;
        foreach (JsonElement s in streams.EnumerateArray())
        {
            string? type = s.TryGetProperty("codec_type", out JsonElement t) ? t.GetString() : null;
            if (type == "video" && video is null)
            {
                video = s;
            }
            else if (type == "audio" && audio is null)
            {
                audio = s;
            }
        }

        if (video is not { } v)
        {
            return null;
        }

        double duration = 0;
        if (root.TryGetProperty("format", out JsonElement format) &&
            format.TryGetProperty("duration", out JsonElement d) &&
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            duration = parsed;
        }

        return new MediaProbe
        {
            Container = root.TryGetProperty("format", out JsonElement f) &&
                        f.TryGetProperty("format_name", out JsonElement fn)
                ? fn.GetString() ?? "unknown"
                : "unknown",
            DurationSeconds = Math.Round(duration, 4),
            VideoCodec = v.GetProperty("codec_name").GetString() ?? "unknown",
            Width = v.GetProperty("width").GetInt32(),
            Height = v.GetProperty("height").GetInt32(),
            FrameRate = v.TryGetProperty("r_frame_rate", out JsonElement r) ? r.GetString() ?? "0/0" : "0/0",
            AudioCodec = audio?.GetProperty("codec_name").GetString(),
            AudioSampleRate = audio is { } a && a.TryGetProperty("sample_rate", out JsonElement sr) &&
                              int.TryParse(sr.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rate)
                ? rate
                : null,
            AudioChannels = audio is { } a2 && a2.TryGetProperty("channels", out JsonElement ch)
                ? ch.GetInt32()
                : null,
        };
    }
}
