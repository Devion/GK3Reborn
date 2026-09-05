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
    /// <remarks>
    /// <para>
    /// <b>GK3 wrote its cutscene subtitles down and then never showed them.</b> Fourteen of
    /// the films have a <c>.YAK</c> of the film's own name — <c>205PEND.YAK</c> — whose
    /// <c>[GK3]</c> section is 232 <c>SpeakerCaption</c> nodes: a start frame, an end frame,
    /// who is speaking and what they say. They are translated in every release, and
    /// <c>[OPTIONS] FRAMERATE 30</c> in each one says what the frames are counted in.
    /// </para>
    /// <para>
    /// <b>Which makes them worth more here than anywhere else.</b> Spanish and Portuguese
    /// never dubbed their cutscenes — every recording in both is byte-identical to English —
    /// so those two releases are a Spanish game whose films are spoken in English. The
    /// subtitles are the whole of what those languages have, and Sierra shipped them.
    /// </para>
    /// <para>
    /// Read through <see cref="AnimationLibrary"/>, which reads through the archives, which
    /// read through the language pack — so the film gets the player's language for free and
    /// nothing here knows what a language is.
    /// </para>
    /// </remarks>
    public Func<string, Formats.Animation.AnimationFile?>? Subtitles { get; set; }

    /// <summary>Who is speaking in the film now, or null.</summary>
    public string? Speaker { get; private set; }

    /// <summary>What they are saying, or null.</summary>
    /// <remarks>
    /// Null for most of most films: these are subtitles for spoken lines, not a running
    /// commentary, and the gaps between them are gaps on screen.
    /// </remarks>
    public string? Caption { get; private set; }

    /// <summary>
    /// Whether every film is passed over rather than played.
    /// </summary>
    /// <remarks>
    /// <c>--no-movies</c>. A film is three minutes of a run that is not the room, and a
    /// room reached by walking into a cutscene — TE4 is entered through
    /// <c>DAY3-5.bik</c>, 144 seconds of it — cannot be looked at at all without sitting
    /// through the film first. Skipped as if it had played: <see cref="Play"/> answers
    /// zero, so the script waiting on it carries straight on and the story reaches the
    /// same state, which is the same answer a movie that cannot be found gives.
    /// </remarks>
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
    /// <remarks>
    /// Zero is the answer a caller needs: a script waiting on a movie that cannot be found
    /// has to carry on rather than wait for something that will never end. The original
    /// does the same — its callback runs whether or not the video played.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// A scan rather than an index, because a film has between five and forty-five of these
    /// and the loop runs once a frame. Forty-five comparisons of two integers is nothing
    /// against decoding a picture, and an index would have to be reset on every seek.
    /// </para>
    /// <para>
    /// <b>The last one that has started wins.</b> GK3's captions overlap — one speaker's
    /// line often ends after the next has begun, because that is how people talk — and
    /// drawing both would need two rows and a rule about which goes where. Showing whoever
    /// spoke most recently is what a subtitle does.
    /// </para>
    /// </remarks>
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
