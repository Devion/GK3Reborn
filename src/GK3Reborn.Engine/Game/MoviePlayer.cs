using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Game;

/// <summary>
/// Plays one movie: the picture against a clock, the sound in one piece.
/// </summary>
/// <remarks>
/// <para>
/// GK3 stops for a movie. <c>PlayFullScreenMovie</c> is waitable, the script that called it
/// does not go on until the movie is over, and the room behind it is not being played — so
/// there is no mixing to do and no world to keep in step with, only a picture to put on
/// screen and a sound to put with it.
/// </para>
/// <para>
/// <b>The sound is handed over whole and the picture chases it.</b> That is the same
/// arrangement as dialogue, and for the same reason: an audio device that has been given a
/// buffer will play it at exactly the right rate whatever the display is doing, and a
/// picture that is a frame late is invisible where a sound that is a frame late is a
/// click. Frames are asked for by time rather than counted out, so a slow frame skips a
/// picture instead of putting the whole movie behind.
/// </para>
/// <para>
/// One movie at a time. Nothing in the game plays two, and the second would have nowhere
/// to go: the screen is the whole of the output.
/// </para>
/// </remarks>
public sealed class MoviePlayer : IDisposable
{
    private readonly VideoLibrary _videos;
    private readonly IAudioBackend? _audio;

    private Movie? _movie;
    private AudioVoice _voice;
    private double _elapsed;
    private DecodedImage? _frame;

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
    /// <remarks>
    /// Zero is the answer a caller needs: a script waiting on a movie that cannot be found
    /// has to carry on rather than wait for something that will never end. The original
    /// does the same — its callback runs whether or not the video played.
    /// </remarks>
    public double Play(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        Stop();

        _movie = Movie.Open(_videos, name, Diagnostics);

        if (_movie is null)
        {
            return 0;
        }

        _elapsed = 0;

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
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();
}
