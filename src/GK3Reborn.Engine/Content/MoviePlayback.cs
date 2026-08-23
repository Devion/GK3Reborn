using System.Globalization;
using System.Runtime.InteropServices;
using FFMediaToolkit;
using FFMediaToolkit.Decoding;
using FFMediaToolkit.Graphics;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// Finding the decoder, once per process.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg is a runtime dependency rather than a bundled one, and a versioned one: the
/// binding is written against <b>FFmpeg 7.1</b> and looks for that generation's shared
/// libraries by name — <c>avcodec-61</c>, <c>avformat-61</c>, <c>avutil-59</c>. A newer
/// FFmpeg on the machine is not a substitute, because its libraries are called something
/// else; that is a property of how FFmpeg versions its ABI rather than a choice made here.
/// </para>
/// <para>
/// Looked for in <c>libs/&lt;rid&gt;</c> first, which is where <c>Plan/01</c> puts native
/// libraries and where <c>NativeLibraryLocator</c> already resolves everything else from.
/// Failing that, the system's own — which is how a Linux box with the distribution's
/// FFmpeg works without anybody copying anything.
/// </para>
/// <para>
/// <b>Not having it is not an error.</b> A machine with no FFmpeg plays the whole game
/// without the cutscenes, which is far better than refusing to start; the diagnostic says
/// what is missing and where to put it, and is said once.
/// </para>
/// </remarks>
public static class MoviePlayback
{
    private static readonly Lock Gate = new();
    private static bool _tried;
    private static string? _from;

    /// <summary>Whether a decoder was found.</summary>
    public static bool Available { get; private set; }

    /// <summary>Where it was found, or null.</summary>
    public static string? LoadedFrom => _from;

    /// <summary>
    /// Finds the decoder, at most once.
    /// </summary>
    /// <param name="nativeRoot">
    /// The run's <c>libs/&lt;rid&gt;</c>, or null to use only what the system has.
    /// </param>
    /// <param name="diagnostics">Receives a diagnostic when there is no decoder.</param>
    /// <returns>True when movies can be played.</returns>
    public static bool Prepare(string? nativeRoot, DiagnosticBag? diagnostics = null)
    {
        lock (Gate)
        {
            if (_tried)
            {
                return Available;
            }

            _tried = true;

            foreach (string? where in Places(nativeRoot))
            {
                if (TryLoad(where))
                {
                    Available = true;
                    _from = where ?? "the system";
                    return true;
                }
            }

            diagnostics?.Add(new Diagnostic(
                "GK3R1160",
                DiagnosticSeverity.Warning,
                "No FFmpeg 7.1 libraries, so the game runs without its cutscenes.",
                "video",
                null,
                "avcodec-61, avformat-61, avutil-59, swscale-8 and swresample-5",
                nativeRoot is { Length: > 0 } ? $"nothing usable in {nativeRoot} or on the system" : "nothing on the system",
                "Put an FFmpeg 7.1 shared build in libs/<rid>, or install one. A newer " +
                "FFmpeg will not do: its libraries carry different names."));

            return false;
        }
    }

    /// <summary>
    /// Where to look, in order.
    /// </summary>
    /// <remarks>
    /// Beside the executable is where an installation keeps it. A development tree keeps it
    /// at the root of the checkout instead, several directories above whichever
    /// <c>bin/Debug</c> is running, and copying sixty megabytes into every project's output
    /// to save walking up a few directories would be the wrong trade. The walk stops as
    /// soon as it finds one, and at the top of the tree if it does not.
    /// </remarks>
    private static IEnumerable<string?> Places(string? nativeRoot)
    {
        if (nativeRoot is { Length: > 0 })
        {
            yield return nativeRoot;
        }

        string rid = RuntimeInformation.RuntimeIdentifier;

        for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            yield return Path.Combine(at.FullName, "libs", rid);
        }

        // And whatever the loader finds on its own, which on Linux is the distribution's.
        yield return null;
    }

    private static bool TryLoad(string? where)
    {
        if (where is { Length: > 0 } && !Directory.Exists(where))
        {
            return false;
        }

        try
        {
            FFmpegLoader.FFmpegPath = where ?? string.Empty;
            FFmpegLoader.LoadFFmpeg();
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException
                                          or FileNotFoundException
                                          or DirectoryNotFoundException
                                          or BadImageFormatException
                                          or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>What one frame of a movie is.</summary>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="Rgba">Its pixels, four bytes each, top row first.</param>
public readonly record struct MovieFrame(int Width, int Height, ReadOnlyMemory<byte> Rgba);

/// <summary>
/// A movie, opened and decoded on demand.
/// </summary>
/// <remarks>
/// <para>
/// Video is pulled frame by frame as the clock asks for it; the sound is decoded whole
/// when the movie opens. The two are treated differently because they are used
/// differently: a frame is wanted once and thrown away, and the sound has to be handed to
/// the audio device as one buffer for the device to be the clock. GK3's longest movie is
/// three and a half minutes, which is forty megabytes of PCM — worth spending to have the
/// picture follow the sound rather than the other way round.
/// </para>
/// <para>
/// The originals are 320x240 at thirty frames a second. Decoding one is about eighty times
/// faster than watching it, so nothing here is on a critical path.
/// </para>
/// </remarks>
public sealed class Movie : IDisposable
{
    private readonly MediaFile _file;
    private readonly Stream _stream;

    private Movie(MediaFile file, Stream stream, string name)
    {
        _file = file;
        _stream = stream;
        Name = name;
    }

    /// <summary>The name it was asked for by.</summary>
    public string Name { get; }

    /// <summary>Its width in pixels.</summary>
    public int Width => _file.Video.Info.FrameSize.Width;

    /// <summary>Its height in pixels.</summary>
    public int Height => _file.Video.Info.FrameSize.Height;

    /// <summary>How long it runs.</summary>
    public TimeSpan Duration => _file.Video.Info.Duration;

    /// <summary>How many frames a second it was recorded at.</summary>
    public double FrameRate => _file.Video.Info.AvgFrameRate;

    /// <summary>Whether it carries sound.</summary>
    public bool HasAudio => _file.HasAudio;

    /// <summary>
    /// Opens a movie.
    /// </summary>
    /// <param name="videos">Where movies come from.</param>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <param name="diagnostics">Receives a diagnostic when it will not open.</param>
    /// <returns>The movie, or null when there is none by that name or it will not open.</returns>
    public static Movie? Open(
        VideoLibrary videos, string name, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(videos);
        ArgumentNullException.ThrowIfNull(name);

        if (!MoviePlayback.Available)
        {
            return null;
        }

        Stream? stream = videos.Open(name);

        if (stream is null)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1161", DiagnosticSeverity.Warning,
                "A script asked for a movie that is not in the packs or the workspace.",
                name, null, "a movie of that name", "none",
                "Check manifests/video.json for what the import produced."));

            return null;
        }

        try
        {
            // RGBA because that is what a texture upload wants, and letting the decoder
            // convert is letting the one piece of code that knows the source format do it.
            MediaFile file = MediaFile.Open(stream, new MediaOptions
            {
                StreamsToLoad = MediaMode.AudioVideo,
                VideoPixelFormat = ImagePixelFormat.Rgba32,
            });

            return new Movie(file, stream, name);
        }
        catch (Exception error)
        {
            stream.Dispose();

            diagnostics?.Add(new Diagnostic(
                "GK3R1162", DiagnosticSeverity.Warning,
                "A movie would not open, so it is skipped.",
                name, null, "a readable video stream", error.Message,
                "The file may be truncated, or in a container the decoder does not know."));

            return null;
        }
    }

    /// <summary>Reads the frame that should be on screen at a moment.</summary>
    /// <param name="at">How far into the movie the clock is.</param>
    /// <param name="frame">The frame.</param>
    /// <returns>True while there is still a picture; false once it has run out.</returns>
    /// <remarks>
    /// By time rather than by count, so a dropped frame is a frame skipped rather than the
    /// picture drifting behind the sound for the rest of the movie.
    /// </remarks>
    public bool TryReadFrame(TimeSpan at, out MovieFrame frame)
    {
        frame = default;

        if (at >= Duration)
        {
            return false;
        }

        try
        {
            if (!_file.Video.TryGetFrame(at, out ImageData image))
            {
                return false;
            }

            frame = new MovieFrame(image.ImageSize.Width, image.ImageSize.Height, Copy(image));
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Decodes the whole soundtrack.</summary>
    /// <returns>The sound, or null when the movie is silent or its sound will not decode.</returns>
    /// <remarks>
    /// Whole rather than streamed, so that it can be handed to the audio device as one
    /// buffer and the device can be the clock the picture follows. The import resamples
    /// every movie to the mixer's own rate, so nothing here has to.
    /// </remarks>
    public WavFile? ReadSound()
    {
        if (!_file.HasAudio)
        {
            return null;
        }

        int channels = _file.Audio.Info.NumChannels;
        int rate = _file.Audio.Info.SampleRate;

        if (channels <= 0 || rate <= 0)
        {
            return null;
        }

        var samples = new List<short>(
            (int)Math.Min(int.MaxValue / 2, (long)(Duration.TotalSeconds * rate * channels) + 1024));

        try
        {
            while (_file.Audio.TryGetNextFrame(out var audio))
            {
                float[][] planes = audio.GetSampleData();

                for (int sample = 0; sample < audio.NumSamples; sample++)
                {
                    for (int channel = 0; channel < channels; channel++)
                    {
                        float[] plane = planes[Math.Min(channel, planes.Length - 1)];

                        // Sixteen bits, which is what the mixer takes, with the clip that
                        // a float track can ask for and an integer one cannot hold.
                        samples.Add((short)Math.Clamp(
                            plane[sample] * short.MaxValue, short.MinValue, short.MaxValue));
                    }
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return samples.Count > 0 ? Build(samples, channels, rate) : null;
        }

        return samples.Count > 0 ? Build(samples, channels, rate) : null;
    }

    /// <summary>Says what it is, for a log line.</summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        string sound = HasAudio
            ? string.Create(
                CultureInfo.InvariantCulture,
                $", sound {_file.Audio.Info.SampleRate} Hz {_file.Audio.Info.NumChannels} ch")
            : ", silent";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Width}x{Height} at {FrameRate:F0} fps, {Duration.TotalSeconds:F1}s{sound}");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _file.Dispose();
        _stream.Dispose();
    }

    private WavFile Build(List<short> samples, int channels, int rate) =>
        WavFile.FromSamples(Name, [.. samples], channels, rate);

    /// <summary>The decoder reuses its buffer, so a frame has to be taken away from it.</summary>
    private static byte[] Copy(ImageData image)
    {
        int width = image.ImageSize.Width;
        int height = image.ImageSize.Height;
        var pixels = new byte[width * height * 4];

        ReadOnlySpan<byte> source = image.Data;

        // Row by row, because a decoded frame's rows are padded to whatever alignment the
        // decoder felt like and a texture upload wants them tight.
        for (int row = 0; row < height; row++)
        {
            source.Slice(row * image.Stride, width * 4)
                .CopyTo(pixels.AsSpan(row * width * 4));
        }

        return pixels;
    }
}
