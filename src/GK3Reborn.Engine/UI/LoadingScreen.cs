using System.Diagnostics;
using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Rendering;

namespace GK3Reborn.UI;

/// <summary>
/// What the window shows while something slow is being read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The window is drawn into from the moment it exists.</b> A window that has never been
/// presented to shows whatever the compositor last put there — on Windows, a sheet of
/// white — and everything between opening it and the first room being ready is a straight
/// line of blocking reads: the archives, the saves, the sound device, the films, the
/// typeface, and then a scene whose textures are a couple of hundred megabytes. Cold, off a
/// mechanical disc, with the enhanced packs in the way, that is a white rectangle for the
/// better part of a minute and no sign that anything is happening.
/// </para>
/// <para>
/// <b>The bar is only shown once the load has proved slow.</b> A warm walk through a door
/// is a couple of hundred milliseconds, and a progress bar that appears and disappears
/// inside a quarter of a second reads as a flicker rather than as information — so nothing
/// is drawn but black for <see cref="SlowSeconds"/>, and the machine that never needs this
/// never sees it. Once it is up it stays up for the rest of that load, because a bar that
/// came and went twice would be worse than either.
/// </para>
/// <para>
/// <b>It is the same screen for both halves of the problem.</b> Starting up, there is
/// nothing behind it and nothing to fade; going through a door, <see cref="ScreenFade"/>
/// has already taken a photograph of the room being left and darkened it. So this hands
/// the first third of a second to the fade and only takes over when the fade has run out of
/// picture to remove — and it gives the fade back the length it owes, so the next room
/// still arrives out of black rather than being cut to.
/// </para>
/// <para>
/// <b>What is behind it is the caller's business.</b> This dims whatever the renderer is
/// already showing and draws over it: black at startup, and the title screen once the menu
/// has put one up, which is what makes the wait after New Game look like part of the menu
/// rather than like a crash.
/// </para>
/// </remarks>
public sealed class LoadingScreen
{
    /// <summary>
    /// How long a load may take before it is worth saying anything about, in seconds.
    /// </summary>
    /// <remarks>
    /// Half a second. Under it, a room is already up by the time the eye would have found
    /// the bar; over it, the alternative is a still picture the player has no way to tell
    /// from a hung game. It is measured from the start of the load rather than from the last
    /// frame presented, so a transition whose fade covered the first third of a second still
    /// counts that third of a second against this.
    /// </remarks>
    public const double SlowSeconds = 0.5;

    /// <summary>How long the music takes to come up, in seconds.</summary>
    /// <remarks>
    /// Slow enough that it reads as music arriving rather than as a sound effect. It starts
    /// when the bar does, so a load that never needed a bar never plays a note — and the
    /// track is not even read off the disc until then.
    /// </remarks>
    private const double MusicInSeconds = 0.9;

    /// <summary>
    /// How long the whole screen takes to go, in seconds.
    /// </summary>
    /// <remarks>
    /// The bar, the word, the dimming and the music all leave together and at the same rate,
    /// because they arrived as one thing and a bar that snapped off over music still playing
    /// would read as two. Short: what is waiting behind it is the room, and this is time the
    /// player spends looking at a bar that has already reached the end.
    /// </remarks>
    private const double OutSeconds = 0.35;

    /// <summary>How often a frame is presented while the loader works.</summary>
    /// <remarks>
    /// Thirty a second, for the reason <see cref="ScreenFade"/> says: the loader offers a
    /// tick per texture, and presenting on every one of them would put a FIFO swapchain in
    /// front of a read that has hundreds of them to do.
    /// </remarks>
    private const double FrameSeconds = 1.0 / 30.0;

    /// <summary>The menu's own palette, so this looks like the same interface.</summary>
    private static readonly Vector4 Veil = new(0f, 0f, 0f, 0.62f);
    private static readonly Vector4 Track = new(0.16f, 0.18f, 0.22f, 1f);
    private static readonly Vector4 Accent = new(0.85f, 0.68f, 0.36f, 1f);
    private static readonly Vector4 Ink = new(0.86f, 0.87f, 0.90f, 1f);

    private readonly Platform.SilkGameWindow _window;
    private readonly IRenderer _renderer;
    private readonly ScreenFade _fade;

    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _music = new();

    private OverlayAtlas? _blank;
    private OverlayAtlas? _cut;
    private Overlay? _sheet;

    private bool _armed;
    private double _waited;
    private double _through;
    private double _presented;
    private double? _owed;

    private AudioVoice _voice = AudioVoice.None;
    private WavFile? _track;
    private bool _asked;

    /// <summary>Creates a loading screen over a window.</summary>
    /// <param name="window">The window, which still has to be pumped while it waits.</param>
    /// <param name="renderer">What draws it.</param>
    /// <param name="fade">
    /// The transition between rooms, which covers the first part of a load on its own and
    /// which this takes over from. See <see cref="ScreenFade.Faded"/>.
    /// </param>
    public LoadingScreen(
        Platform.SilkGameWindow window, IRenderer renderer, ScreenFade fade)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(fade);

        _window = window;
        _renderer = renderer;
        _fade = fade;
    }

    /// <summary>
    /// The interface's own sheet of letters, once there is one.
    /// </summary>
    /// <remarks>
    /// Null until the archives have been read, which is most of what the first load is:
    /// the typeface comes out of the game's own content, so the screen that covers reading
    /// it cannot be drawn with it. Until then <see cref="OverlayAtlas.Blank"/> stands in and
    /// the word is silently left out — the bar is the part that carries the meaning.
    /// </remarks>
    public OverlayAtlas? Atlas { get; set; }

    /// <summary>What the word is written in.</summary>
    public UiText Text { get; set; } = UiText.English;

    /// <summary>The device the music plays through, or null on a silent run.</summary>
    public IAudioBackend? Sound { get; set; }

    /// <summary>
    /// Where to get the music from, asked once and only if the bar is ever shown.
    /// </summary>
    /// <remarks>
    /// A function rather than the sound itself, so that a machine fast enough never to see
    /// this screen never reads the track either. Null, or a function that returns null, is
    /// a silent loading screen and nothing else changes.
    /// </remarks>
    public Func<WavFile?>? Music { get; set; }

    /// <summary>Whether the bar is on screen.</summary>
    public bool Showing { get; private set; }

    /// <summary>How much of the work is done, from nought to one.</summary>
    public double Through => _through;

    /// <summary>
    /// Says that something slow is starting, and puts a frame up straight away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frame is what stops the window being white: it costs one present and it is the
    /// difference between a game that is starting and a game that has not drawn anything.
    /// Nothing is drawn <em>on</em> it — the bar waits for <see cref="SlowSeconds"/> — so a
    /// load that turns out to be quick shows a black window for a moment and nothing else.
    /// Not presented while the fade is running, which is already presenting frames of its
    /// own and would be cut across by one from here.
    /// </para>
    /// <para>
    /// A screen that is already up stays up. Startup runs straight into the first room on a
    /// run with no menu in between, and hiding the bar for another half second there would
    /// be a blink at the one moment the player is most sure the game has stopped.
    /// </para>
    /// </remarks>
    public void Begin(TimeSpan waited = default)
    {
        // Only if something went wrong: the fade is owed its length back by whoever took it
        // over, and Done is what pays it. Beginning again with one outstanding would lose
        // it, and a room that arrives with no fade at all is a hard cut.
        if (_owed is { } outstanding)
        {
            _owed = null;
            _fade.ArriveOver(outstanding);
        }

        _armed = true;
        _through = 0;
        _presented = double.NegativeInfinity;
        _waited = Math.Max(0, waited.TotalSeconds);
        _clock.Restart();

        // Offered rather than assumed either way: a load the caller says was already slow
        // before this could exist comes up with its bar on the first frame, and one that is
        // only starting gets a black window and nothing else.
        Tick();

        if (!Showing && !_fade.Leaving)
        {
            Present();
        }
    }

    /// <summary>Records how far through the work is, and shows it if it is time.</summary>
    /// <param name="through">Nought at the start, one at the end.</param>
    /// <remarks>
    /// Monotonic within one load: the bar is never allowed to go backwards. The pieces of a
    /// load are not all the same size and where one ends is a measured typical rather than a
    /// promise, so a later piece finishing sooner than expected is ordinary — and a bar that
    /// shrinks is the one thing a player will not read as progress.
    /// </remarks>
    public void At(double through)
    {
        _through = Math.Clamp(Math.Max(_through, through), 0, 1);
        Tick();
    }

    /// <summary>
    /// Offers a frame, at whatever the last <see cref="At"/> said.
    /// </summary>
    /// <remarks>
    /// This is what a loader's progress hook is given. Cheap and rate-limited, so a hook
    /// called once per texture costs one frame every thirtieth of a second and nothing at
    /// all in between.
    /// </remarks>
    public void Tick()
    {
        if (!_armed)
        {
            return;
        }

        // The fade first, and while there is any of it left. It is still showing the room
        // being left, which is a better picture than anything here, and the two of them
        // presenting alternate frames would be a flicker between a photograph and a bar.
        if (_fade.Leaving && !_fade.Faded)
        {
            _fade.Tick();

            return;
        }

        double now = _waited + _clock.Elapsed.TotalSeconds;

        if (!Showing)
        {
            if (now < SlowSeconds)
            {
                // Not slow enough to be worth saying anything about. The fade, if there is
                // one, still wants its frames.
                if (_fade.Leaving)
                {
                    _fade.Tick();
                }

                return;
            }

            Appear();
        }

        if (now - _presented < FrameSeconds * 0.9)
        {
            return;
        }

        _presented = now;

        Cue();
        Level(_music.Elapsed.TotalSeconds / MusicInSeconds);
        Draw(1f);
        Present();
    }

    /// <summary>
    /// Takes the screen down, and gives the fade back whatever this took over from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once the thing being waited for is ready and about to be drawn. The room's own
    /// loop then lets the picture in over a live room, exactly as it would have if the fade
    /// had never been interrupted.
    /// </para>
    /// <para>
    /// <b>It leaves over its own third of a second rather than at once.</b> That is the only
    /// place a fade out can be run from: nothing else presents a frame between here and the
    /// room's first, so a screen that simply stopped would take the music with it in one
    /// step. What it costs is a third of a second at the end of a load that was long enough
    /// to be worth covering, and what it buys is the difference between arriving somewhere
    /// and being cut to it.
    /// </para>
    /// </remarks>
    public void Done()
    {
        if (!_armed)
        {
            return;
        }

        _armed = false;
        _clock.Stop();

        if (Showing)
        {
            Showing = false;

            var going = Stopwatch.StartNew();

            for (double through = 0; through < 1 && !_window.IsClosing;
                 through = going.Elapsed.TotalSeconds / OutSeconds)
            {
                Level(1 - through);
                Draw((float)(1 - through));
                Present();
            }

            Level(0);
            _renderer.SetOverlay(null);
        }

        if (_voice.Exists)
        {
            Sound?.Silence(_voice);
            _voice = AudioVoice.None;
        }

        _music.Reset();

        if (_owed is { } seconds)
        {
            _owed = null;
            _fade.ArriveOver(seconds);
        }
    }

    /// <summary>
    /// Takes the screen over from the fade, and puts the bar and the music up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fade is finished here rather than abandoned. <see cref="ScreenFade.Black"/> is
    /// what takes the photograph of the old room down and says how long the way back should
    /// take; skipping it would leave a still of the room hanging behind everything for the
    /// rest of the load, and would arrive into the next room with a cut.
    /// </para>
    /// <para>
    /// And then the fade is set to nothing, because it is drawn <em>over</em> the interface
    /// — see the renderer's own note on it — so a bar under a fade at full black is a bar
    /// nobody can see. What replaces it is a rectangle this screen draws itself, which is
    /// the same black and is behind the bar rather than in front of it.
    /// </para>
    /// </remarks>
    private void Appear()
    {
        Showing = true;

        if (_fade.Leaving)
        {
            _owed = _fade.Black();
        }

        _renderer.Fade = 0f;
    }

    /// <summary>
    /// Starts the music, once there is a device to start it on and a track to play.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Offered on every frame rather than once when the screen appears, because at startup
    /// the screen is up before the sound device is: opening it is one of the things the wait
    /// is spent on, and a screen that had asked once, early, would be a silent one on
    /// exactly the slow machine this whole thing is for.
    /// </para>
    /// <para>
    /// The track is asked for once and remembered, so a run with no such sound in the
    /// archives costs one lookup rather than one a frame. A run with no device costs
    /// nothing: nothing is read until there is somewhere to play it.
    /// </para>
    /// </remarks>
    private void Cue()
    {
        if (_voice.Exists || Sound is not { } device)
        {
            return;
        }

        if (!_asked)
        {
            _asked = true;
            _track = Music?.Invoke();
        }

        if (_track is not { } wav)
        {
            return;
        }

        // Looped, because a load long enough to be worth covering can outlast a piece of
        // music, and a bar that goes quiet half way is a bar that says the game has stopped.
        _voice = device.Play(wav, AudioBus.Music, repeat: true);
        _music.Restart();
        Level(0);
    }

    /// <summary>How loud the music is, as a share of the level the music bus is at.</summary>
    /// <param name="level">Nought to one; anything outside is clamped.</param>
    private void Level(double level)
    {
        if (_voice.Exists)
        {
            Sound?.SetVoiceGain(_voice, (float)Math.Clamp(level, 0, 1));
        }
    }

    /// <summary>Lays the screen out and hands it to the renderer.</summary>
    /// <param name="opacity">How much of it to draw, for the way out.</param>
    private void Draw(float opacity)
    {
        OverlayAtlas atlas = Atlas ?? (_blank ??= OverlayAtlas.Blank());

        if (_sheet is null || !ReferenceEquals(_cut, atlas))
        {
            _cut = atlas;
            _sheet = new Overlay(atlas);
        }

        // The first sheet has to be handed over rather than left to the display list.
        // SetOverlay uploads whatever atlas the list was cut from — but only once there is
        // an overlay pipeline to upload it to, and on Vulkan there is none until something
        // asks for one. This is the something: nothing else in the game does, until the
        // archives have been read and the interface's own sheet exists, which is precisely
        // what this screen is covering. Without it the bar was drawn on Direct3D and
        // silently missing on Vulkan.
        if (!_renderer.HasOverlay)
        {
            _renderer.SetOverlayAtlas(atlas);
        }

        int width = Math.Max(1, _window.FramebufferWidth);
        int height = Math.Max(1, _window.FramebufferHeight);

        _sheet.Begin(width, height);

        // Over the whole window, whatever is behind it. With nothing behind it this is black
        // over black and costs one rectangle; with the title screen behind it, it is what
        // makes a line of letters readable over a painting.
        _sheet.Rect(0, 0, width, height, Fainter(Veil, opacity));

        // A ninetieth of the window, which is where a bar stops reading as a hairline on a
        // 4K display and stops reading as a loading bar from a different decade on a small
        // one. Never less than three pixels: the track and the fill have to be told apart.
        float thickness = MathF.Max(3f, height / 90f);
        float barWidth = MathF.Round(width * 0.42f);
        float left = MathF.Round((width - barWidth) / 2f);
        float top = MathF.Round(height * 0.62f);

        _sheet.Rect(left, top, barWidth, thickness, Fainter(Track, opacity));
        _sheet.Rect(
            left,
            top,
            MathF.Round(barWidth * (float)_through),
            thickness,
            Fainter(Accent, opacity));

        // The word, over the bar and only where there is a font to write it with. Measured
        // rather than centred by character count, because the atlas may have been cut from
        // an outline at any size the window asked for.
        string word = Text.Say("loading", "Loading");
        float measured = _sheet.Measure(word);

        if (measured > 0)
        {
            _sheet.Text(
                word,
                MathF.Round((width - measured) / 2f),
                MathF.Round(top - (_sheet.LineHeight * 1.75f)),
                Fainter(Ink, opacity));
        }

        _renderer.SetOverlay(_sheet);
    }

    /// <summary>The same colour, drawn at a share of its own alpha.</summary>
    private static Vector4 Fainter(Vector4 colour, float opacity) =>
        colour with { W = colour.W * Math.Clamp(opacity, 0f, 1f) };

    private void Present()
    {
        _window.PumpEvents();
        _window.EndFrame();
        _renderer.DrawFrame(0f, 0f, 0f);
    }
}
