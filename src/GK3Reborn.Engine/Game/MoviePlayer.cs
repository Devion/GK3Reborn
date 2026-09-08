using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Game;

/// <summary>
/// Plays one movie: the picture against a clock, the sound in one piece.
/// </summary>
public sealed class MoviePlayer : IDisposable
{
    private readonly VideoLibrary _videos;
    private readonly IAudioBackend? _audio;

    private Movie? _movie;
    private AudioVoice _voice;
    private double _elapsed;
    private DecodedImage? _frame;
    private IReadOnlyList<Formats.Animation.AnimationCaption> _captions = [];
    private int _rate = Formats.Animation.AnimationFile.FramesPerSecond;

    /// <summary>Creates a player.</summary>
    /// <param name="videos">Where movies come from.</param>
    /// <param name="audio">The device, or null to play them silently.</param>
    public MoviePlayer(VideoLibrary videos, IAudioBackend? audio)
    {
        ArgumentNullException.ThrowIfNull(videos);

        _videos = videos;
        _audio = audio;
    }

    /// <summary>Diagnostics raised while playing.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>
    /// Where a cutscene's subtitles come from, or null to play films without them.
    /// </summary>
    public Func<string, Formats.Animation.AnimationFile?>? Subtitles { get; set; }

    /// <summary>Who is speaking in the film now, or null.</summary>
    public string? Speaker { get; private set; }

    /// <summary>What they are saying, or null.</summary>
    public string? Caption { get; private set; }

    /// <summary>
    /// Whether every film is passed over rather than played.
    /// </summary>
    public bool Skipping { get; set; }

    /// <summary>Whether a movie is on screen.</summary>
    public bool Playing => _movie is not null;

    /// <summary>What is playing, or null.</summary>
    public string? Showing => _movie?.Name;

    /// <summary>How long the movie runs, or zero when none is playing.</summary>
    public double Seconds => _movie?.Duration.TotalSeconds ?? 0;

    /// <summary>How far into it the clock is.</summary>
    public double At => _elapsed;

    /// <summary>The frame that should be on screen, or null when nothing is playing.</summary>
    public DecodedImage? Frame => _frame;

    /// <summary>
    /// Starts a movie.
    /// </summary>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <returns>How long it will run, or zero when it will not play.</returns>
    public double Play(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        Stop();

        if (Skipping)
        {
            return 0;
        }

        _movie = Movie.Open(_videos, name, Diagnostics);

        if (_movie is null)
        {
            return 0;
        }

        _elapsed = 0;
        Speaker = null;
        Caption = null;

        // The film's own subtitles, where it has any. Asked for by the film's name, which
        // is the name of its YAK: 205PEND.bik and 205PEND.YAK.
        Formats.Animation.AnimationFile? written = Subtitles?.Invoke(name);
        _captions = written?.Captions ?? [];
        _rate = written is { Rate: > 0 } ? written.Rate : Formats.Animation.AnimationFile.FramesPerSecond;

        // What is actually about to play, said once. A cutscene running the wrong
        // language's soundtrack looks exactly like one running the right language's, and
        // there is nothing on screen at all to say where either half came from.
        Foundation.Diagnostics.Log.Detail(
            $"film: {name}, {_movie.Describe()}, picture from "
            + $"{_videos.Source(name) ?? "nowhere"}"
            + (_movie.SoundIsSeparate
                ? $", sound from {_videos.SoundSource(name) ?? "a separate track"}"
                : string.Empty));

        // Before the first frame, so the sound and the picture start together rather than
        // the sound starting a decode later.
        if (_audio is not null && _movie.HasAudio && _movie.ReadSound() is { } sound)
        {
            // On the music bus. A movie's track is dialogue, score and effects mixed
            // together by whoever made it, so putting it on the dialogue bus would let a
            // player who turned dialogue down silence the explosions too.
            _voice = _audio.Play(sound, AudioBus.Music);
        }

        Advance(0);

        return _movie.Duration.TotalSeconds;
    }

    /// <summary>
    /// Finds the subtitle that belongs at a moment, if any.
    /// </summary>
    /// <param name="at">How far into the film the clock is.</param>
    private void Written(double at)
    {
        if (_captions.Count == 0)
        {
            return;
        }

        int frame = (int)(at * _rate);
        Formats.Animation.AnimationCaption? showing = null;

        foreach (Formats.Animation.AnimationCaption caption in _captions)
        {
            if (caption.Frame <= frame && frame < caption.EndFrame)
            {
                showing = caption;
            }
        }

        Speaker = showing?.Speaker;
        Caption = showing?.Text;
    }

    /// <summary>Moves the clock on and reads the frame that belongs there.</summary>
    /// <param name="seconds">How long since the last frame.</param>
    /// <returns>True while the movie is still running.</returns>
    public bool Advance(double seconds)
    {
        if (_movie is null)
        {
            return false;
        }

        _elapsed += Math.Max(0, seconds);
        Written(_elapsed);

        if (_movie.TryReadFrame(TimeSpan.FromSeconds(_elapsed), out MovieFrame frame))
        {
            // Kept rather than handed straight on: a frame the decoder could not produce
            // should leave the last one on screen instead of a black flash.
            _frame = new DecodedImage(
                frame.Width, frame.Height, frame.Rgba.ToArray(), HasAlpha: false, _movie.Name);

            return true;
        }

        // Out of picture. The sound may still have a moment to run, and the movie is over
        // when both are.
        if (_elapsed < _movie.Duration.TotalSeconds)
        {
            return true;
        }

        Stop();
        return false;
    }

    /// <summary>Ends the movie now, as a player pressing a key does.</summary>
    public void Stop()
    {
        if (_voice.Exists)
        {
            _audio?.Silence(_voice);
            _voice = AudioVoice.None;
        }

        _movie?.Dispose();
        _movie = null;
        _frame = null;
        _elapsed = 0;
        _captions = [];
        Speaker = null;
        Caption = null;
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();
}
