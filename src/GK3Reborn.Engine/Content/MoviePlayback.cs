using System.Buffers;
using System.Globalization;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Formats.Video;
using GK3Reborn.Formats.Video.Aac;
using GK3Reborn.Formats.Video.H264;
using GK3Reborn.Formats.Video.Mp4;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>What one frame of a movie is.</summary>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="Rgba">Its pixels, four bytes each, top row first. Valid until the next frame is read.</param>
public readonly record struct MovieFrame(int Width, int Height, ReadOnlyMemory<byte> Rgba);

/// <summary>
/// A movie, opened and decoded on demand.
/// </summary>
/// <remarks>
/// <para>
/// Decoded by the engine's own H.264 and AAC decoders, so a movie plays wherever the
/// engine runs — Windows, Linux, a Mac — with nothing installed beside the executable and
/// nothing to version. FFmpeg used to do this; it was sixty megabytes of shared libraries
/// per platform, a different set of names for every generation, and no build at all for
/// Apple silicon. The managed decoders are compared sample for sample against FFmpeg's in
/// the tests, so what is lost is speed, not pictures.
/// </para>
/// <para>
/// Video is decoded ahead of the clock on its own thread and pulled frame by frame as the
/// clock asks; the sound is decoded whole when the movie opens. The two are treated
/// differently because they are used differently: a frame is wanted once and thrown away,
/// and the sound has to be handed to the audio device as one buffer for the device to be
/// the clock. GK3's longest movie is three and a half minutes, which is forty megabytes of
/// PCM — worth spending to have the picture follow the sound rather than the other way
/// round.
/// </para>
/// <para>
/// The decode thread runs a few frames ahead and no further, so a 320x240 movie costs
/// nothing to speak of and a 1440x1080 one keeps one core busy without piling up frames it
/// will never show. A frame that is not ready when the clock reaches it is skipped, which
/// the player treats as leaving the last one on screen.
/// </para>
/// </remarks>
public sealed class Movie : IDisposable
{
    /// <summary>How many decoded frames are kept ahead of the clock.</summary>
    private const int Lookahead = 6;

    private readonly Mp4File _file;
    private readonly Stream _stream;
    private readonly Mp4Track _video;

    /// <summary>
    /// Where the sound is read from: the movie itself, or the language's own track.
    /// </summary>
    /// <remarks>
    /// Two fields rather than a reader that knows about languages, because the difference
    /// is one file handle. Thirteen of GK3's sixteen spoken movies are the same footage in
    /// every language, so a French game plays the shared picture with a French soundtrack
    /// beside it; where the two are the same file these hold the same objects and every
    /// path below is the path it always was. See <see cref="VideoLibrary.OpenSound"/>.
    /// </remarks>
    private readonly Mp4File _soundFile;
    private readonly Stream _soundStream;
    private readonly bool _ownsSound;
    private readonly byte[]? _soundWave;
    private readonly Mp4Track? _audio;
    private readonly H264Decoder _decoder;
    private readonly Queue<(double Seconds, byte[] Rgba)> _ready = new();
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private byte[]? _current;
    private bool _finished;
    private Exception? _failure;

    private Movie(
        Mp4File file,
        Stream stream,
        Mp4Track video,
        Mp4File? soundFile,
        Stream? soundStream,
        byte[]? soundWave,
        Mp4Track? audio,
        H264Decoder decoder,
        string name)
    {
        _file = file;
        _stream = stream;
        _video = video;
        _ownsSound = soundStream is not null;
        _soundFile = soundFile ?? file;
        _soundStream = soundStream ?? stream;
        _soundWave = soundWave;
        _audio = audio;
        _decoder = decoder;
        Name = name;
        Width = decoder.Width;
        Height = decoder.Height;
        Duration = file.Duration > TimeSpan.Zero ? file.Duration : TimeSpan.FromSeconds(video.Seconds(video.Duration));
        FrameRate = Duration > TimeSpan.Zero ? video.Samples.Count / Duration.TotalSeconds : 0;

        _thread = new Thread(DecodeAhead)
        {
            Name = $"Movie {name}",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>The name it was asked for by.</summary>
    public string Name { get; }

    /// <summary>Its width in pixels.</summary>
    public int Width { get; }

    /// <summary>Its height in pixels.</summary>
    public int Height { get; }

    /// <summary>How long it runs.</summary>
    public TimeSpan Duration { get; }

    /// <summary>How many frames a second it was recorded at.</summary>
    public double FrameRate { get; }

    /// <summary>Whether it carries sound.</summary>
    public bool HasAudio => _audio is not null || _soundWave is not null;

    /// <summary>Whether the sound came from somewhere other than the movie.</summary>
    /// <remarks>
    /// True when a language pack supplied the soundtrack for a shared picture. Reported in
    /// the log because it is otherwise invisible: a movie playing in the wrong language
    /// looks exactly like a movie playing in the right one until somebody listens.
    /// </remarks>
    public bool SoundIsSeparate => _ownsSound;

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
            var file = Mp4File.Open(stream, name);
            Mp4Track? video = file.Tracks.Find(t => t.Kind == Mp4TrackKind.Video && t.Codec is "avc1" or "avc3");

            if (video is null || video.SequenceParameterSets.Count == 0 || video.Samples.Count == 0)
            {
                throw new FormatParseException("no H.264 video track");
            }

            // The language's own soundtrack, where the picture is the shared cut and the
            // words are not. Asked for after the picture is known to be shared, because a
            // movie the language re-cut carries its own sound already and laying a second
            // track over it would play the same words twice.
            Stream? separate = videos.IsLocalized(name) ? null : videos.OpenSound(name);
            Mp4File? soundFile = null;
            byte[]? soundWave = null;
            Mp4Track? audio = null;

            if (separate is not null)
            {
                try
                {
                    (soundFile, soundWave, audio) = Soundtrack(separate, name);
                }
                catch (Exception error) when (error is FormatParseException or NotSupportedException or IOException or InvalidDataException)
                {
                    // The movie's own track is the fallback, so a language whose soundtrack
                    // will not open loses the words rather than the scene.
                    separate.Dispose();
                    separate = null;
                    soundFile = null;
                    soundWave = null;

                    diagnostics?.Add(new Diagnostic(
                        "GK3R1163", DiagnosticSeverity.Warning,
                        "A localised soundtrack would not open, so the movie's own is played.",
                        videos.SoundSource(name) ?? name, null,
                        "an MP4, M4A or WAV the engine can decode", error.Message,
                        "Produce it again with `extract-localized --video`."));
                }
            }

            if (separate is null)
            {
                audio = file.Tracks.Find(t => t.Kind == Mp4TrackKind.Audio && t.Codec == "mp4a" && t.AudioSpecificConfig.Length > 0);
            }

            if (soundWave is null && audio is not null &&
                (!AacDecoder.TryParseConfig(audio.AudioSpecificConfig, out _, out _, out _) ||
                 audio.Samples.Count == 0))
            {
                audio = null;
            }

            var decoder = new H264Decoder();
            decoder.Configure(video.SequenceParameterSets, video.PictureParameterSets);

            if (decoder.Width <= 0 || decoder.Height <= 0)
            {
                throw new FormatParseException("the video track's parameter sets do not describe a picture");
            }

            return new Movie(
                file, stream, video, soundFile, separate, soundWave, audio, decoder, name);
        }
        catch (Exception error) when (error is FormatParseException or NotSupportedException or IOException or InvalidDataException)
        {
            stream.Dispose();

            diagnostics?.Add(new Diagnostic(
                "GK3R1162", DiagnosticSeverity.Warning,
                "A movie would not open, so it is skipped.",
                name, null, "an MP4 with H.264 video the engine can decode", error.Message,
                "The file may be truncated, or use a coding tool the decoder does not support " +
                "(interlacing, 4:2:2, high bit depth). Re-import it with the standard settings."));

            return null;
        }
    }

    /// <summary>
    /// Opens a soundtrack that arrived beside a movie rather than inside it.
    /// </summary>
    /// <param name="stream">The soundtrack, positioned at its start.</param>
    /// <param name="name">The movie's name, for diagnostics.</param>
    /// <returns>
    /// The container and its audio track, or the raw bytes when it is a RIFF WAVE.
    /// </returns>
    /// <remarks>
    /// Two forms, decided from the bytes rather than the extension. The import writes an
    /// <c>.m4a</c> — the AAC track copied out of the localised movie without re-encoding —
    /// which is an MP4 with no video in it and reads exactly like one. A RIFF WAVE is the
    /// other thing somebody may reasonably produce by hand, and
    /// <see cref="WavFile"/> already decodes every form GK3 itself uses.
    /// </remarks>
    private static (Mp4File? File, byte[]? Wave, Mp4Track? Audio) Soundtrack(
        Stream stream, string name)
    {
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        stream.Position = 0;

        if (magic is [(byte)'R', (byte)'I', (byte)'F', (byte)'F'])
        {
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);

            return (null, bytes, null);
        }

        var container = Mp4File.Open(stream, name);
        Mp4Track? track = container.Tracks.Find(
            t => t.Kind == Mp4TrackKind.Audio && t.Codec == "mp4a" && t.AudioSpecificConfig.Length > 0);

        if (track is null || track.Samples.Count == 0 ||
            !AacDecoder.TryParseConfig(track.AudioSpecificConfig, out _, out _, out _))
        {
            throw new FormatParseException("no AAC audio track");
        }

        return (container, null, track);
    }

    /// <summary>Reads the frame that should be on screen at a moment.</summary>
    /// <param name="at">How far into the movie the clock is.</param>
    /// <param name="frame">The frame.</param>
    /// <returns>True when there is a new picture for that moment; false when there is none yet, or the movie has run out.</returns>
    /// <remarks>
    /// By time rather than by count, so a dropped frame is a frame skipped rather than the
    /// picture drifting behind the sound for the rest of the movie. The frame handed back
    /// is the latest one whose time has come; earlier ones still waiting are discarded.
    /// </remarks>
    public bool TryReadFrame(TimeSpan at, out MovieFrame frame)
    {
        frame = default;

        if (at >= Duration)
        {
            return false;
        }

        byte[]? chosen = null;

        lock (_gate)
        {
            while (_ready.Count > 0 && _ready.Peek().Seconds <= at.TotalSeconds)
            {
                if (chosen is not null)
                {
                    ArrayPool<byte>.Shared.Return(chosen);
                }

                chosen = _ready.Dequeue().Rgba;
            }

            if (chosen is not null)
            {
                if (_current is not null)
                {
                    ArrayPool<byte>.Shared.Return(_current);
                }

                _current = chosen;
            }

            Monitor.PulseAll(_gate);
        }

        if (chosen is null)
        {
            return false;
        }

        frame = new MovieFrame(Width, Height, chosen.AsMemory(0, Width * Height * 4));
        return true;
    }

    /// <summary>Decodes the whole soundtrack.</summary>
    /// <returns>The sound, or null when the movie is silent or its sound will not decode.</returns>
    /// <remarks>
    /// Whole rather than streamed, so that it can be handed to the audio device as one
    /// buffer and the device can be the clock the picture follows. The import resamples
    /// every movie to the mixer's own rate, so nothing here has to. The encoder's priming
    /// delay, which the file records as an edit, is trimmed so that the first sample is the
    /// first sample of the movie rather than a thousand samples of warm-up.
    /// </remarks>
    public WavFile? ReadSound()
    {
        // A soundtrack somebody supplied as a RIFF WAVE is already what this returns.
        if (_soundWave is not null)
        {
            return WavFile.Read(_soundWave, Name, new DiagnosticBag());
        }

        if (_audio is null)
        {
            return null;
        }

        try
        {
            var decoder = new AacDecoder(_audio.AudioSpecificConfig);
            int channels = decoder.Channels;
            int rate = decoder.SampleRate;
            int perFrame = decoder.FrameSamples * channels;
            long skip = Math.Max(0, _audio.EditOffset) * channels;
            var samples = new List<short>(checked(_audio.Samples.Count * perFrame));
            var pcm = new short[perFrame];
            byte[] buffer = [];

            foreach (Mp4Sample sample in _audio.Samples)
            {
                if (buffer.Length < sample.Size)
                {
                    buffer = new byte[Math.Max(sample.Size, buffer.Length * 2)];
                }

                // The sound's own stream, which is the movie's unless a language supplied a
                // separate track. Locked because the picture is being decoded on another
                // thread out of the movie's stream, and when the two are the same file a
                // seek from either would land the other somewhere it did not ask for.
                lock (_soundStream)
                {
                    _soundFile.Read(sample, buffer);
                }

                int count;

                try
                {
                    count = decoder.Decode(buffer.AsSpan(0, sample.Size), pcm) * channels;
                }
                catch (FormatParseException)
                {
                    // One bad access unit is a click; the rest of the track still plays.
                    decoder.Reset();
                    Array.Clear(pcm);
                    count = perFrame;
                }

                int from = 0;

                if (skip > 0)
                {
                    int take = (int)Math.Min(skip, count);
                    from = take;
                    skip -= take;
                }

                for (int i = from; i < count; i++)
                {
                    samples.Add(pcm[i]);
                }
            }

            return samples.Count > 0 ? WavFile.FromSamples(Name, [.. samples], channels, rate) : null;
        }
        catch (Exception error) when (error is FormatParseException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>Says what it is, for a log line.</summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        string sound = ", silent";
        string where = _ownsSound ? " (localised)" : string.Empty;

        if (_soundWave is not null)
        {
            sound = $", sound from a WAVE{where}";
        }
        else if (_audio is not null && AacDecoder.TryParseConfig(_audio.AudioSpecificConfig, out int rate, out int channels, out _))
        {
            sound = string.Create(
                CultureInfo.InvariantCulture, $", sound {rate} Hz {channels} ch{where}");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Width}x{Height} at {FrameRate:F0} fps, {Duration.TotalSeconds:F1}s{sound}");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stop.Cancel();

        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }

        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        lock (_gate)
        {
            while (_ready.Count > 0)
            {
                ArrayPool<byte>.Shared.Return(_ready.Dequeue().Rgba);
            }

            if (_current is not null)
            {
                ArrayPool<byte>.Shared.Return(_current);
                _current = null;
            }
        }

        _file.Dispose();
        _stream.Dispose();

        // Only when it is a second file. Where the sound is the movie's own these are the
        // objects just disposed, and disposing them again is at best wasted work.
        if (_ownsSound)
        {
            _soundFile.Dispose();
            _soundStream.Dispose();
        }

        _stop.Dispose();
    }

    /// <summary>The decode thread: keeps a few frames ahead of whatever the clock asks for.</summary>
    private void DecodeAhead()
    {
        CancellationToken stop = _stop.Token;
        byte[] buffer = new byte[64 * 1024];
        int frameBytes = Width * Height * 4;

        try
        {
            foreach (Mp4Sample sample in _video.Samples)
            {
                if (stop.IsCancellationRequested)
                {
                    return;
                }

                if (buffer.Length < sample.Size)
                {
                    buffer = new byte[Math.Max(sample.Size, buffer.Length * 2)];
                }

                lock (_stream)
                {
                    _file.Read(sample, buffer);
                }

                _decoder.Decode(buffer.AsMemory(0, sample.Size), _video.NalLengthSize, sample.PresentationTime);

                if (!Deliver(frameBytes, stop))
                {
                    return;
                }
            }

            _decoder.Flush();
            Deliver(frameBytes, stop);
        }
        catch (Exception error) when (error is FormatParseException or NotSupportedException or IOException or IndexOutOfRangeException)
        {
            // A damaged movie stops where the damage is; the player then holds the last
            // picture until the sound ends, which is the least wrong thing to show.
            _failure = error;
        }
        finally
        {
            lock (_gate)
            {
                _finished = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>Converts and queues every frame the decoder has ready, waiting while the queue is full.</summary>
    private bool Deliver(int frameBytes, CancellationToken stop)
    {
        while (_decoder.TryGetFrame(out DecodedFrame decoded))
        {
            double seconds = _video.Seconds(decoded.Tag - _video.EditOffset);
            byte[] rgba = ArrayPool<byte>.Shared.Rent(frameBytes);
            YuvConverter.ToRgba(decoded, rgba);
            decoded.Release();

            lock (_gate)
            {
                while (_ready.Count >= Lookahead && !stop.IsCancellationRequested)
                {
                    Monitor.Wait(_gate);
                }

                if (stop.IsCancellationRequested)
                {
                    ArrayPool<byte>.Shared.Return(rgba);
                    return false;
                }

                _ready.Enqueue((seconds, rgba));
            }
        }

        return true;
    }

    /// <summary>Whether the decoder has stopped, and why if it failed.</summary>
    internal (bool Finished, Exception? Failure) Status
    {
        get
        {
            lock (_gate)
            {
                return (_finished, _failure);
            }
        }
    }
}
